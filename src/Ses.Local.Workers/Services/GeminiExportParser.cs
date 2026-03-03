using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Ses.Local.Core.Enums;
using Ses.Local.Core.Interfaces;
using Ses.Local.Core.Models;
using Ses.Local.Core.Options;
using Ses.Local.Core.Services;

namespace Ses.Local.Workers.Services;

/// <summary>
/// Parses Google Takeout Gemini conversation exports into local.db.
///
/// Google Takeout produces a ZIP archive with the path:
///   Takeout/Gemini Apps Activity/My Activity.json
///
/// The JSON is an array of activity entries.  Each entry typically represents a
/// single conversation turn (prompt + response pair), stored in a "subtitles" array:
///   [{"name": "Prompt", "value": "..."}, {"name": "Response", "value": "..."}]
///
/// Because Google does not group turns into named conversations in this format, each
/// activity entry is stored as its own two-message ConversationSession.  The
/// session title is the first ~80 characters of the prompt.
///
/// Deduplication: keyed on a hash of (time + prompt text) since the export has no
/// stable UUID.
///
/// Also supports:
///   • Plain JSON file (extracted from the takeout archive manually)
///   • Full ZIP archive (looks for My Activity.json under any path segment ending in
///     "Gemini Apps Activity")
/// </summary>
public sealed class GeminiExportParser : IConversationImporter
{
    private readonly ILocalDbService _db;
    private readonly ILogger<GeminiExportParser> _logger;
    private readonly SesLocalOptions _options;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public GeminiExportParser(
        ILocalDbService db,
        ILogger<GeminiExportParser> logger,
        IOptions<SesLocalOptions> options)
    {
        _db      = db;
        _logger  = logger;
        _options = options.Value;
    }

    /// <inheritdoc/>
    public async Task<ImportResult> ImportAsync(
        string filePath,
        IProgress<ImportProgress>? progress = null,
        CancellationToken ct = default,
        ImportFilterOptions? importOptions = null)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException("Export file not found.", filePath);

        _logger.LogInformation("Starting Gemini export import from: {Path}", filePath);

        var result = new ImportResult();

        try
        {
            var (jsonPath, isTempFile) = await ResolveJsonPathAsync(filePath, ct);
            try
            {
                await ImportJsonFileAsync(jsonPath, result, progress, ct, importOptions);
            }
            finally
            {
                if (isTempFile && File.Exists(jsonPath))
                    File.Delete(jsonPath);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Gemini import cancelled after {Count} conversations", result.SessionsImported);
            throw;
        }
        catch (JsonException ex)
        {
            result.Errors++;
            _logger.LogError(ex, "Invalid JSON in Gemini export file: {Path}", filePath);
        }
        catch (InvalidDataException ex)
        {
            result.Errors++;
            _logger.LogError(ex, "Invalid ZIP archive: {Path}", filePath);
        }

        _logger.LogInformation(
            "Gemini import complete — {Sessions} sessions, {Messages} messages, {Dupes} duplicates, {Errors} errors",
            result.SessionsImported, result.MessagesImported, result.Duplicates, result.Errors);

        return result;
    }

    // ── ZIP extraction ────────────────────────────────────────────────────────

    /// <summary>
    /// Locates My Activity.json inside a Google Takeout ZIP and extracts it to a temp file.
    /// If <paramref name="filePath"/> is already a JSON file, it is returned as-is.
    /// </summary>
    internal static async Task<(string Path, bool IsTempFile)> ResolveJsonPathAsync(
        string filePath, CancellationToken ct)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        if (ext != ".zip")
            return (filePath, false);

        using var zip = ZipFile.OpenRead(filePath);

        // Look for "My Activity.json" under any "Gemini Apps Activity" directory
        var entry = zip.Entries.FirstOrDefault(e =>
            e.FullName.Contains("Gemini Apps Activity", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(e.Name, "My Activity.json", StringComparison.OrdinalIgnoreCase));

        // Broader fallback: any My Activity.json in the archive
        entry ??= zip.Entries.FirstOrDefault(e =>
            string.Equals(e.Name, "My Activity.json", StringComparison.OrdinalIgnoreCase));

        if (entry is null)
            throw new InvalidDataException(
                "ZIP archive does not contain 'My Activity.json'. " +
                "Ensure this is a valid Google Takeout archive that includes Gemini Apps Activity.");

        var tempPath = Path.Combine(Path.GetTempPath(), $"gemini-import-{Guid.NewGuid():N}.json");
        await using var entryStream = entry.Open();
        await using var fileStream  = new FileStream(tempPath, FileMode.Create, FileAccess.Write,
            FileShare.None, bufferSize: 65_536, useAsync: true);
        await entryStream.CopyToAsync(fileStream, ct);
        return (tempPath, true);
    }

    // ── Import ────────────────────────────────────────────────────────────────

    private async Task ImportJsonFileAsync(
        string jsonPath,
        ImportResult result,
        IProgress<ImportProgress>? progress,
        CancellationToken ct,
        ImportFilterOptions? filter)
    {
        await using var stream = new FileStream(
            jsonPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 65_536, useAsync: true);

        await foreach (var entry in JsonSerializer.DeserializeAsyncEnumerable<GeminiActivityEntry>(
            stream, JsonOptions, ct))
        {
            if (entry is null) continue;
            ct.ThrowIfCancellationRequested();

            if (ShouldExclude(entry, filter)) { result.Filtered++; continue; }
            await ImportOneAsync(entry, result, ct);
            progress?.Report(new ImportProgress(result.SessionsImported, BuildTitle(entry)));
        }
    }

    // ── Per-entry processing ──────────────────────────────────────────────────

    private async Task ImportOneAsync(
        GeminiActivityEntry entry, ImportResult result, CancellationToken ct)
    {
        var prompt   = FindSubtitleValue(entry, "Prompt");
        var response = FindSubtitleValue(entry, "Response");

        if (string.IsNullOrWhiteSpace(prompt) && string.IsNullOrWhiteSpace(response))
        {
            // Empty entry — skip silently
            return;
        }

        var externalId = ComputeExternalId(entry);
        var createdAt  = ParseTime(entry.Time);
        var title      = BuildTitle(entry);
        var hash       = ComputeHash(externalId, entry.Time);

        try
        {
            var session = new ConversationSession
            {
                Source      = ConversationSource.Gemini,
                ExternalId  = externalId,
                Title       = title,
                ContentHash = hash,
                CreatedAt   = createdAt,
                UpdatedAt   = createdAt
            };

            await _db.UpsertSessionAsync(session, ct);

            var messages = new List<ConversationMessage>(2);

            if (!string.IsNullOrWhiteSpace(prompt))
            {
                var content = _options.EnablePrivateTagStripping
                    ? PrivateTagStripper.Strip(prompt)
                    : prompt;
                messages.Add(new ConversationMessage
                {
                    SessionId = session.Id,
                    Role      = "user",
                    Content   = content,
                    CreatedAt = createdAt
                });
            }

            if (!string.IsNullOrWhiteSpace(response))
            {
                var content = _options.EnablePrivateTagStripping
                    ? PrivateTagStripper.Strip(response)
                    : response;
                messages.Add(new ConversationMessage
                {
                    SessionId = session.Id,
                    Role      = "assistant",
                    Content   = content,
                    CreatedAt = createdAt
                });
            }

            if (messages.Count > 0)
            {
                await _db.UpsertMessagesAsync(messages, ct);
                result.MessagesImported += messages.Count;
            }

            result.SessionsImported++;
            _logger.LogDebug("Imported Gemini entry {Id} — {Count} messages", externalId, messages.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            result.Errors++;
            _logger.LogWarning(ex, "Failed to import Gemini entry {Id}", externalId);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    internal static bool ShouldExclude(GeminiActivityEntry entry, ImportFilterOptions? filter)
    {
        if (filter is null) return false;

        if (filter.ExcludeTitlePatterns is { Count: > 0 })
        {
            var title = BuildTitle(entry);
            foreach (var pattern in filter.ExcludeTitlePatterns)
            {
                if (ClaudeExportParser.MatchesGlobPattern(title, pattern))
                    return true;
            }
        }

        if (filter.ExcludeBefore.HasValue)
        {
            if (ParseTime(entry.Time) < filter.ExcludeBefore.Value) return true;
        }

        if (filter.ExcludeAfter.HasValue)
        {
            if (ParseTime(entry.Time) > filter.ExcludeAfter.Value) return true;
        }

        return false;
    }

    internal static string? FindSubtitleValue(GeminiActivityEntry entry, string name)
    {
        if (entry.Subtitles is null) return null;
        foreach (var subtitle in entry.Subtitles)
        {
            if (string.Equals(subtitle.Name, name, StringComparison.OrdinalIgnoreCase))
                return subtitle.Value;
        }
        return null;
    }

    internal static string BuildTitle(GeminiActivityEntry entry)
    {
        var prompt = FindSubtitleValue(entry, "Prompt");
        if (!string.IsNullOrWhiteSpace(prompt))
        {
            var trimmed = prompt.Trim();
            return trimmed.Length <= 80 ? trimmed : trimmed[..80] + "…";
        }
        return entry.Title ?? "Gemini conversation";
    }

    internal static string ComputeExternalId(GeminiActivityEntry entry)
    {
        var key   = $"{entry.Time}:{FindSubtitleValue(entry, "Prompt")}";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return "gemini-" + Convert.ToHexString(bytes)[..16].ToLowerInvariant();
    }

    internal static string ComputeHash(string externalId, string? time)
    {
        var key   = $"{externalId}:{time}";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return Convert.ToHexString(bytes)[..16];
    }

    internal static DateTime ParseTime(string? value)
    {
        if (string.IsNullOrEmpty(value)) return DateTime.UtcNow;
        return DateTime.TryParse(value, null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt)
            ? dt.ToUniversalTime()
            : DateTime.UtcNow;
    }
}

// ── Internal DTOs (mirroring Google Takeout My Activity.json structure) ────────

internal sealed class GeminiActivityEntry
{
    [JsonPropertyName("header")]    public string?              Header    { get; set; }
    [JsonPropertyName("title")]     public string?              Title     { get; set; }
    [JsonPropertyName("time")]      public string?              Time      { get; set; }
    [JsonPropertyName("products")]  public List<string>?        Products  { get; set; }
    [JsonPropertyName("subtitles")] public List<GeminiSubtitle>? Subtitles { get; set; }
}

internal sealed class GeminiSubtitle
{
    [JsonPropertyName("name")]  public string? Name  { get; set; }
    [JsonPropertyName("value")] public string? Value { get; set; }
}
