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
/// Parses ChatGPT conversation exports into local.db.
/// Supports two input forms:
///   • ZIP archive (the standard ChatGPT export) — extracts conversations.json automatically.
///   • Raw conversations.json file — if the user has already extracted the archive.
///
/// ChatGPT export format:
///   An array of conversations, each with a flat "mapping" dictionary that encodes
///   a message tree (nodes reference parent/children IDs).  We linearise the tree
///   by following parent links from current_node back to the root, then reversing,
///   which yields the canonical branch the user last viewed and faithfully matches
///   what ChatGPT renders in its UI (i.e. the selected regeneration branch).
///
/// Deduplication: keyed on conversation "id" (UUID).
/// Streaming JSON: the inner conversations.json is loaded into a temporary file so
/// that JsonSerializer.DeserializeAsyncEnumerable can stream it without buffering
/// the whole ZIP entry in memory.
/// </summary>
public sealed class ChatGptExportParser : IConversationImporter
{
    private readonly ILocalDbService _db;
    private readonly ILogger<ChatGptExportParser> _logger;
    private readonly SesLocalOptions _options;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    public ChatGptExportParser(
        ILocalDbService db,
        ILogger<ChatGptExportParser> logger,
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

        _logger.LogInformation("Starting ChatGPT export import from: {Path}", filePath);

        var result = new ImportResult();

        try
        {
            // Resolve the actual JSON path — may need to extract from ZIP
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
            _logger.LogInformation("ChatGPT import cancelled after {Count} conversations", result.SessionsImported);
            throw;
        }
        catch (JsonException ex)
        {
            result.Errors++;
            _logger.LogError(ex, "Invalid JSON in ChatGPT export file: {Path}", filePath);
        }
        catch (InvalidDataException ex)
        {
            result.Errors++;
            _logger.LogError(ex, "Invalid ZIP archive: {Path}", filePath);
        }

        _logger.LogInformation(
            "ChatGPT import complete — {Sessions} sessions, {Messages} messages, {Dupes} duplicates, {Errors} errors",
            result.SessionsImported, result.MessagesImported, result.Duplicates, result.Errors);

        return result;
    }

    // ── ZIP extraction ────────────────────────────────────────────────────────

    /// <summary>
    /// If <paramref name="filePath"/> is a ZIP, extracts conversations.json to a temp file
    /// and returns <c>(tempPath, true)</c>.  If it is already a JSON file, returns
    /// <c>(filePath, false)</c>.
    /// </summary>
    internal static async Task<(string Path, bool IsTempFile)> ResolveJsonPathAsync(
        string filePath, CancellationToken ct)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        if (ext != ".zip")
            return (filePath, false);

        using var zip = ZipFile.OpenRead(filePath);
        var entry = zip.Entries.FirstOrDefault(e =>
            string.Equals(e.Name, "conversations.json", StringComparison.OrdinalIgnoreCase));

        if (entry is null)
            throw new InvalidDataException(
                "ZIP archive does not contain conversations.json. " +
                "Ensure this is a valid ChatGPT data export.");

        var tempPath = Path.Combine(Path.GetTempPath(), $"chatgpt-import-{Guid.NewGuid():N}.json");
        await using var entryStream = entry.Open();
        await using var fileStream  = new FileStream(tempPath, FileMode.Create, FileAccess.Write,
            FileShare.None, bufferSize: 65_536, useAsync: true);
        await entryStream.CopyToAsync(fileStream, ct);
        return (tempPath, true);
    }

    // ── Streaming import ──────────────────────────────────────────────────────

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

        await foreach (var conv in JsonSerializer.DeserializeAsyncEnumerable<GptConversation>(
            stream, JsonOptions, ct))
        {
            if (conv is null) continue;
            ct.ThrowIfCancellationRequested();

            if (ShouldExclude(conv, filter)) { result.Filtered++; continue; }
            await ImportOneAsync(conv, result, ct);
            progress?.Report(new ImportProgress(result.SessionsImported, conv.Title));
        }
    }

    // ── Per-conversation ──────────────────────────────────────────────────────

    private async Task ImportOneAsync(GptConversation conv, ImportResult result, CancellationToken ct)
    {
        var id = string.IsNullOrWhiteSpace(conv.Id) ? conv.ConversationId : conv.Id;
        if (string.IsNullOrWhiteSpace(id))
        {
            result.Errors++;
            _logger.LogWarning("Skipping ChatGPT conversation with empty ID");
            return;
        }

        try
        {
            var createdAt = UnixToUtc(conv.CreateTime);
            var updatedAt = UnixToUtc(conv.UpdateTime);
            var messages  = FlattenTree(conv);
            var hash      = ComputeHash(id, conv.UpdateTime, messages.Count);

            var session = new ConversationSession
            {
                Source      = ConversationSource.ChatGpt,
                ExternalId  = id,
                Title       = string.IsNullOrWhiteSpace(conv.Title)
                              ? id[..Math.Min(8, id.Length)]
                              : conv.Title,
                ContentHash = hash,
                CreatedAt   = createdAt,
                UpdatedAt   = updatedAt
            };

            await _db.UpsertSessionAsync(session, ct);

            if (messages.Count > 0)
            {
                foreach (var m in messages) m.SessionId = session.Id;
                await _db.UpsertMessagesAsync(messages, ct);
                result.MessagesImported += messages.Count;
            }

            result.SessionsImported++;
            _logger.LogDebug("Imported ChatGPT conversation {Id} — {Count} messages", id, messages.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            result.Errors++;
            _logger.LogWarning(ex, "Failed to import ChatGPT conversation {Id}", id);
        }
    }

    // ── Tree flattening ───────────────────────────────────────────────────────

    /// <summary>
    /// Flattens the ChatGPT mapping tree into a chronologically-ordered list of messages.
    /// Follows parent links from current_node back to the root to pick the canonical branch,
    /// then reverses for ascending chronological order.  Falls back to sorting all messages
    /// by create_time if current_node is absent or can't be resolved.
    /// </summary>
    internal List<ConversationMessage> FlattenTree(GptConversation conv)
    {
        if (conv.Mapping is not { Count: > 0 })
            return [];

        var orderedNodes = ResolveCanonicalBranch(conv);

        var messages = new List<ConversationMessage>(orderedNodes.Count);
        foreach (var node in orderedNodes)
        {
            var msg = node.Message;
            if (msg is null) continue;

            var role = msg.Author?.Role ?? string.Empty;
            if (role is not ("user" or "assistant")) continue;  // skip system/tool

            var content = ExtractContent(msg);
            if (string.IsNullOrWhiteSpace(content)) continue;

            if (_options.EnablePrivateTagStripping)
                content = PrivateTagStripper.Strip(content);

            messages.Add(new ConversationMessage
            {
                Role      = role,
                Content   = content,
                CreatedAt = UnixToUtc(msg.CreateTime)
            });
        }
        return messages;
    }

    private static List<GptMappingNode> ResolveCanonicalBranch(GptConversation conv)
    {
        var mapping = conv.Mapping!;

        // Walk from current_node → root via parent links
        if (!string.IsNullOrWhiteSpace(conv.CurrentNode) && mapping.ContainsKey(conv.CurrentNode))
        {
            var branch = new List<GptMappingNode>();
            var nodeId = conv.CurrentNode;

            while (nodeId is not null && mapping.TryGetValue(nodeId, out var node))
            {
                branch.Add(node);
                nodeId = node.Parent;
            }
            branch.Reverse(); // root → current_node
            return branch;
        }

        // Fallback: sort all nodes by message create_time
        return mapping.Values
            .Where(n => n.Message is not null)
            .OrderBy(n => n.Message!.CreateTime)
            .ToList();
    }

    // ── Content extraction ────────────────────────────────────────────────────

    internal static string ExtractContent(GptMessage msg)
    {
        var contentObj = msg.Content;
        if (contentObj is null) return string.Empty;

        // "text" / "tether_quote" / "multimodal_text" — parts array
        if (contentObj.Parts is { Count: > 0 })
        {
            var parts = new List<string>(contentObj.Parts.Count);
            foreach (var part in contentObj.Parts)
            {
                // Parts are JsonElement — may be string or object
                if (part.ValueKind == JsonValueKind.String)
                {
                    var s = part.GetString();
                    if (!string.IsNullOrWhiteSpace(s)) parts.Add(s.Trim());
                }
                else if (part.ValueKind == JsonValueKind.Object &&
                         part.TryGetProperty("text", out var textProp))
                {
                    var s = textProp.GetString();
                    if (!string.IsNullOrWhiteSpace(s)) parts.Add(s.Trim());
                }
            }
            return string.Join("\n", parts);
        }

        return string.Empty;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    internal static bool ShouldExclude(GptConversation conv, ImportFilterOptions? filter)
    {
        if (filter is null) return false;

        if (filter.ExcludeTitlePatterns is { Count: > 0 })
        {
            foreach (var pattern in filter.ExcludeTitlePatterns)
            {
                if (ClaudeExportParser.MatchesGlobPattern(conv.Title ?? string.Empty, pattern))
                    return true;
            }
        }

        if (filter.ExcludeBefore.HasValue)
        {
            var createdAt = UnixToUtc(conv.CreateTime);
            if (createdAt < filter.ExcludeBefore.Value) return true;
        }

        if (filter.ExcludeAfter.HasValue)
        {
            var createdAt = UnixToUtc(conv.CreateTime);
            if (createdAt > filter.ExcludeAfter.Value) return true;
        }

        return false;
    }

    internal static DateTime UnixToUtc(double? unixSeconds)
    {
        if (unixSeconds is null or 0) return DateTime.UtcNow;
        return DateTimeOffset.FromUnixTimeMilliseconds((long)(unixSeconds.Value * 1000))
            .UtcDateTime;
    }

    internal static string ComputeHash(string id, double? updatedAt, int messageCount)
    {
        var key   = $"{id}:{updatedAt}:{messageCount}";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return Convert.ToHexString(bytes)[..16];
    }
}

// ── Internal DTOs (mirroring ChatGPT conversations.json structure) ─────────────

internal sealed class GptConversation
{
    [JsonPropertyName("id")]               public string?  Id             { get; set; }
    [JsonPropertyName("conversation_id")]  public string?  ConversationId { get; set; }
    [JsonPropertyName("title")]            public string?  Title          { get; set; }
    [JsonPropertyName("create_time")]      public double?  CreateTime     { get; set; }
    [JsonPropertyName("update_time")]      public double?  UpdateTime     { get; set; }
    [JsonPropertyName("current_node")]     public string?  CurrentNode    { get; set; }
    [JsonPropertyName("mapping")]          public Dictionary<string, GptMappingNode>? Mapping { get; set; }
}

internal sealed class GptMappingNode
{
    [JsonPropertyName("id")]       public string?     Id       { get; set; }
    [JsonPropertyName("parent")]   public string?     Parent   { get; set; }
    [JsonPropertyName("children")] public List<string>? Children { get; set; }
    [JsonPropertyName("message")]  public GptMessage? Message  { get; set; }
}

internal sealed class GptMessage
{
    [JsonPropertyName("id")]          public string?     Id          { get; set; }
    [JsonPropertyName("author")]      public GptAuthor?  Author      { get; set; }
    [JsonPropertyName("create_time")] public double?     CreateTime  { get; set; }
    [JsonPropertyName("content")]     public GptContent? Content     { get; set; }
}

internal sealed class GptAuthor
{
    [JsonPropertyName("role")] public string? Role { get; set; }
}

internal sealed class GptContent
{
    [JsonPropertyName("content_type")] public string?              ContentType { get; set; }
    [JsonPropertyName("parts")]        public List<JsonElement>?   Parts       { get; set; }
}
