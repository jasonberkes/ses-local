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
/// Parses Claude.ai JSON conversation exports and imports them into local.db.
/// Supports both array-rooted exports (most common) and object-rooted exports
/// with a top-level "conversations" key.
/// Uses streaming JSON deserialization for memory-efficient handling of large files.
/// Deduplication is handled via ExternalId (uuid) — existing conversations with
/// identical UUIDs are updated rather than duplicated.
/// </summary>
public sealed class ClaudeExportParser : IConversationImporter
{
    private readonly ILocalDbService _db;
    private readonly ILogger<ClaudeExportParser> _logger;
    private readonly SesLocalOptions _options;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public ClaudeExportParser(ILocalDbService db, ILogger<ClaudeExportParser> logger, IOptions<SesLocalOptions> options)
    {
        _db      = db;
        _logger  = logger;
        _options = options.Value;
    }

    /// <summary>
    /// Parses and imports a Claude.ai JSON export file.
    /// </summary>
    /// <param name="filePath">Absolute path to the .json export file.</param>
    /// <param name="progress">Optional progress reporter — called after each conversation is processed.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="importOptions">Optional filtering options for the import.</param>
    /// <returns>Summary of the import operation.</returns>
    public async Task<ImportResult> ImportAsync(
        string filePath,
        IProgress<ImportProgress>? progress = null,
        CancellationToken ct = default,
        ImportFilterOptions? importOptions = null)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException("Export file not found.", filePath);

        _logger.LogInformation("Starting Claude export import from: {Path}", filePath);

        var result = new ImportResult();

        try
        {
            var format = DetectFormat(filePath);

            if (format == ExportFormat.Array)
                await ImportArrayRootAsync(filePath, result, progress, ct, importOptions);
            else
                await ImportObjectRootAsync(filePath, result, progress, ct, importOptions);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Import cancelled after {Count} conversations", result.SessionsImported);
            throw;
        }
        catch (JsonException ex)
        {
            result.Errors++;
            _logger.LogError(ex, "Invalid JSON in export file: {Path}", filePath);
        }

        _logger.LogInformation(
            "Import complete — {Sessions} sessions, {Messages} messages, {Dupes} duplicates, {Errors} errors",
            result.SessionsImported, result.MessagesImported, result.Duplicates, result.Errors);

        return result;
    }

    // ── Streaming import (array-rooted export) ────────────────────────────────

    private async Task ImportArrayRootAsync(
        string filePath,
        ImportResult result,
        IProgress<ImportProgress>? progress,
        CancellationToken ct,
        ImportFilterOptions? filter)
    {
        await using var stream = new FileStream(
            filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 65_536, useAsync: true);

        await foreach (var conv in JsonSerializer.DeserializeAsyncEnumerable<ExportConversation>(
            stream, JsonOptions, ct))
        {
            if (conv is null) continue;
            ct.ThrowIfCancellationRequested();
            if (ShouldExclude(conv, filter)) { result.Filtered++; continue; }
            await ImportOneAsync(conv, result, ct);
            progress?.Report(new ImportProgress(result.SessionsImported, conv.Name));
        }
    }

    // ── Full-file import (object-rooted export: { "conversations": [...] }) ───

    private async Task ImportObjectRootAsync(
        string filePath,
        ImportResult result,
        IProgress<ImportProgress>? progress,
        CancellationToken ct,
        ImportFilterOptions? filter)
    {
        await using var stream = new FileStream(
            filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 65_536, useAsync: true);

        var file = await JsonSerializer.DeserializeAsync<ExportFile>(stream, JsonOptions, ct);
        var conversations = file?.Conversations;

        if (conversations is null || conversations.Count == 0) return;

        foreach (var conv in conversations)
        {
            ct.ThrowIfCancellationRequested();
            if (ShouldExclude(conv, filter)) { result.Filtered++; continue; }
            await ImportOneAsync(conv, result, ct);
            progress?.Report(new ImportProgress(result.SessionsImported, conv.Name));
        }
    }

    // ── Per-conversation processing ───────────────────────────────────────────

    private async Task ImportOneAsync(
        ExportConversation conv,
        ImportResult result,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(conv.Uuid))
        {
            result.Errors++;
            _logger.LogWarning("Skipping conversation with empty UUID");
            return;
        }

        try
        {
            var createdAt = ParseDate(conv.CreatedAt);
            var updatedAt = ParseDate(conv.UpdatedAt);
            var hash      = ComputeHash(conv);

            var session = new ConversationSession
            {
                Source      = ConversationSource.ClaudeChat,
                ExternalId  = conv.Uuid,
                Title       = string.IsNullOrWhiteSpace(conv.Name)
                              ? conv.Uuid[..Math.Min(8, conv.Uuid.Length)]
                              : conv.Name,
                ContentHash = hash,
                CreatedAt   = createdAt,
                UpdatedAt   = updatedAt
            };

            await _db.UpsertSessionAsync(session, ct);

            var messages     = BuildMessages(conv, session.Id);
            var messageCount = messages.Count;

            if (messageCount > 0)
            {
                await _db.UpsertMessagesAsync(messages, ct);
                result.MessagesImported += messageCount;
            }

            result.SessionsImported++;
            _logger.LogDebug("Imported conversation {Uuid} — {Count} messages", conv.Uuid, messageCount);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            result.Errors++;
            _logger.LogWarning(ex, "Failed to import conversation {Uuid}", conv.Uuid);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private List<ConversationMessage> BuildMessages(ExportConversation conv, long sessionId)
    {
        var messages = new List<ConversationMessage>(conv.ChatMessages.Count);

        foreach (var m in conv.ChatMessages)
        {
            var content = ExtractContent(m);
            if (string.IsNullOrWhiteSpace(content)) continue;

            if (_options.EnablePrivateTagStripping)
                content = PrivateTagStripper.Strip(content);

            messages.Add(new ConversationMessage
            {
                SessionId = sessionId,
                Role      = m.Sender == "human" ? "user" : "assistant",
                Content   = content,
                CreatedAt = ParseDate(m.CreatedAt)
            });
        }

        return messages;
    }

    /// <summary>
    /// Returns true if the conversation should be excluded from import based on filter options.
    /// </summary>
    internal static bool ShouldExclude(ExportConversation conv, ImportFilterOptions? filter)
    {
        if (filter is null) return false;

        // Exclude by title pattern (glob-style with * wildcard)
        if (filter.ExcludeTitlePatterns is { Count: > 0 })
        {
            foreach (var pattern in filter.ExcludeTitlePatterns)
            {
                if (MatchesGlobPattern(conv.Name, pattern))
                    return true;
            }
        }

        // Exclude by date range
        if (filter.ExcludeBefore.HasValue)
        {
            var createdAt = ParseDate(conv.CreatedAt);
            if (createdAt < filter.ExcludeBefore.Value)
                return true;
        }

        if (filter.ExcludeAfter.HasValue)
        {
            var createdAt = ParseDate(conv.CreatedAt);
            if (createdAt > filter.ExcludeAfter.Value)
                return true;
        }

        return false;
    }

    /// <summary>Simple glob pattern matching with * wildcard (case-insensitive).</summary>
    internal static bool MatchesGlobPattern(string text, string pattern)
    {
        if (string.IsNullOrEmpty(pattern)) return false;

        // Convert glob pattern to regex: escape everything except *, then replace * with .*
        var regexPattern = "^" + System.Text.RegularExpressions.Regex.Escape(pattern)
            .Replace("\\*", ".*") + "$";
        return System.Text.RegularExpressions.Regex.IsMatch(
            text, regexPattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    /// <summary>
    /// Extracts plain text from a message. Prefers the top-level <c>text</c> field;
    /// falls back to concatenating text blocks from the structured <c>content</c> array.
    /// </summary>
    internal static string ExtractContent(ExportMessage m)
    {
        if (!string.IsNullOrWhiteSpace(m.Text))
            return m.Text.Trim();

        if (m.Content is { Count: > 0 })
        {
            var parts = new List<string>(m.Content.Count);
            foreach (var block in m.Content)
            {
                if (block.Type == "text" && !string.IsNullOrWhiteSpace(block.Text))
                    parts.Add(block.Text.Trim());
            }
            return string.Join("\n", parts);
        }

        return string.Empty;
    }

    /// <summary>Computes a short content hash for deduplication based on uuid, updated_at, and message count.</summary>
    internal static string ComputeHash(ExportConversation conv)
    {
        var key   = $"{conv.Uuid}:{conv.UpdatedAt}:{conv.ChatMessages.Count}";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return Convert.ToHexString(bytes)[..16];
    }

    private static DateTime ParseDate(string? value)
    {
        if (string.IsNullOrEmpty(value)) return DateTime.UtcNow;
        return DateTime.TryParse(value, null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt)
            ? dt.ToUniversalTime()
            : DateTime.UtcNow;
    }

    /// <summary>
    /// Peeks at the first non-whitespace character to determine if the root is an array or object.
    /// Uses <see cref="StreamReader"/> so that UTF-8 BOM bytes are automatically consumed.
    /// </summary>
    internal static ExportFormat DetectFormat(string filePath)
    {
        // StreamReader with detectEncodingFromByteOrderMarks=true handles UTF-8 BOM automatically.
        using var reader = new StreamReader(filePath, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        int c;
        while ((c = reader.Read()) != -1)
        {
            if (c is ' ' or '\t' or '\n' or '\r') continue;
            return (char)c == '[' ? ExportFormat.Array : ExportFormat.Object;
        }
        return ExportFormat.Array;
    }

    internal enum ExportFormat { Array, Object }
}

// ── Result / progress types ───────────────────────────────────────────────────

/// <summary>Progress snapshot emitted after each conversation is processed.</summary>
/// <param name="Processed">Number of conversations successfully imported so far.</param>
/// <param name="CurrentTitle">Title of the most recently imported conversation.</param>
public sealed record ImportProgress(int Processed, string? CurrentTitle);

/// <summary>Aggregated results returned after an import operation completes.</summary>
public sealed class ImportResult
{
    public int SessionsImported { get; set; }
    public int MessagesImported { get; set; }

    /// <summary>Conversations that already existed in the DB and were updated in-place.</summary>
    public int Duplicates { get; set; }

    /// <summary>Conversations that could not be parsed or stored.</summary>
    public int Errors { get; set; }

    /// <summary>Conversations excluded by import filter options.</summary>
    public int Filtered { get; set; }
}

/// <summary>Filtering options for import operations.</summary>
public sealed class ImportFilterOptions
{
    /// <summary>
    /// Exclude conversations whose title matches any of these glob patterns (case-insensitive, * wildcard).
    /// Example: "personal*" excludes all conversations starting with "personal".
    /// </summary>
    public IReadOnlyList<string>? ExcludeTitlePatterns { get; init; }

    /// <summary>Exclude conversations created before this date.</summary>
    public DateTime? ExcludeBefore { get; init; }

    /// <summary>Exclude conversations created after this date.</summary>
    public DateTime? ExcludeAfter { get; init; }
}

// ── Internal DTOs (mirroring Claude.ai export JSON structure) ─────────────────

internal sealed class ExportConversation
{
    [JsonPropertyName("uuid")]          public string Uuid         { get; set; } = string.Empty;
    [JsonPropertyName("name")]          public string Name         { get; set; } = string.Empty;
    [JsonPropertyName("created_at")]    public string? CreatedAt   { get; set; }
    [JsonPropertyName("updated_at")]    public string? UpdatedAt   { get; set; }
    [JsonPropertyName("chat_messages")] public List<ExportMessage> ChatMessages { get; set; } = [];
}

internal sealed class ExportMessage
{
    [JsonPropertyName("uuid")]       public string Uuid       { get; set; } = string.Empty;
    [JsonPropertyName("sender")]     public string Sender     { get; set; } = string.Empty;
    [JsonPropertyName("text")]       public string Text       { get; set; } = string.Empty;
    [JsonPropertyName("created_at")] public string? CreatedAt { get; set; }

    /// <summary>
    /// Structured content blocks. Present when the message has rich content
    /// (e.g. assistant replies with code blocks). May be null for simple text messages.
    /// </summary>
    [JsonPropertyName("content")] public List<ExportContentBlock>? Content { get; set; }
}

internal sealed class ExportContentBlock
{
    [JsonPropertyName("type")] public string  Type { get; set; } = string.Empty;
    [JsonPropertyName("text")] public string? Text { get; set; }
}

/// <summary>Object-rooted export format: <c>{ "conversations": [...] }</c>.</summary>
internal sealed class ExportFile
{
    [JsonPropertyName("conversations")] public List<ExportConversation>? Conversations { get; set; }
}
