using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Ses.Local.Core.Enums;
using Ses.Local.Core.Interfaces;
using Ses.Local.Core.Models;

namespace Ses.Local.Workers.Services;

/// <summary>
/// Downloads conversation transcripts from TaskMaster DocumentService and parses them
/// back into <see cref="ConversationSession"/> + <see cref="ConversationMessage"/> objects.
/// Used by <see cref="CloudPullWorker"/> for multi-device cloud-to-local sync (WI-991).
/// </summary>
public sealed class DocumentServiceDownloader : IDocumentServiceDownloader
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<DocumentServiceDownloader> _logger;

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public DocumentServiceDownloader(
        IHttpClientFactory httpClientFactory,
        ILogger<DocumentServiceDownloader> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger            = logger;
    }

    /// <summary>
    /// Queries DocumentService for documents created by ses-local and updated after the given timestamp.
    /// Returns parsed documents that were uploaded from a different device (own uploads are filtered out).
    /// </summary>
    public async Task<IReadOnlyList<PulledDocument>> GetDocumentsAsync(
        string pat,
        DateTime updatedAfter,
        string ownDeviceId,
        CancellationToken ct = default)
    {
        try
        {
            var http = _httpClientFactory.CreateClient(DependencyInjection.DocumentServiceClientName);
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", pat);

            var updatedAfterStr = Uri.EscapeDataString(updatedAfter.ToString("O"));
            var url = $"api/v1/documents?updatedAfter={updatedAfterStr}&tags=conversation%2Csync&createdBy=ses-local&pageSize=50";

            using var response = await http.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                _logger.LogWarning(
                    "DocumentService pull returned {Status}: {Body}",
                    (int)response.StatusCode, body);
                return [];
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            var dtos = ParseDocumentList(json);

            var results = new List<PulledDocument>();
            foreach (var dto in dtos)
            {
                var pulled = TryParseDocument(dto, ownDeviceId);
                if (pulled is not null)
                    results.Add(pulled);
            }

            return results;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch documents from DocumentService");
            return [];
        }
    }

    // ── Parsing helpers ───────────────────────────────────────────────────────

    private IReadOnlyList<DocumentDto> ParseDocumentList(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Handle both array root and object-with-items wrapper
            JsonElement itemsEl = root.ValueKind == JsonValueKind.Array
                ? root
                : root.TryGetProperty("items", out var items) ? items
                  : root.TryGetProperty("data", out var data) ? data
                  : default;

            if (itemsEl.ValueKind != JsonValueKind.Array)
            {
                _logger.LogDebug("Unexpected DocumentService list response shape");
                return [];
            }

            return itemsEl.Deserialize<List<DocumentDto>>(s_jsonOptions) ?? [];
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse DocumentService list response");
            return [];
        }
    }

    private PulledDocument? TryParseDocument(DocumentDto dto, string ownDeviceId)
    {
        try
        {
            if (string.IsNullOrEmpty(dto.Metadata))
                return null;

            using var metaDoc = JsonDocument.Parse(dto.Metadata);
            var meta = metaDoc.RootElement;

            // Skip documents uploaded from this device (already local)
            if (meta.TryGetProperty("deviceId", out var deviceIdEl) &&
                deviceIdEl.GetString() == ownDeviceId)
                return null;

            var sourceStr = meta.TryGetProperty("source", out var src) ? src.GetString() : null;
            var externalId = meta.TryGetProperty("externalId", out var extId) ? extId.GetString() : null;
            var transcript = meta.TryGetProperty("transcript", out var tx) ? tx.GetString() : null;

            if (string.IsNullOrEmpty(externalId) || string.IsNullOrEmpty(transcript))
                return null;

            if (!Enum.TryParse<ConversationSource>(sourceStr, out var source))
                source = ConversationSource.ClaudeCode;

            var messages = ParseTranscriptMessages(transcript);
            var createdAt = messages.Count > 0 ? messages[0].CreatedAt : (dto.UpdatedAt ?? DateTime.UtcNow);

            var session = new ConversationSession
            {
                Source      = source,
                ExternalId  = externalId,
                Title       = dto.Title ?? $"Conversation {externalId}",
                CreatedAt   = createdAt,
                UpdatedAt   = dto.UpdatedAt ?? DateTime.UtcNow,
                ContentHash = dto.ContentHash
            };

            return new PulledDocument(session, messages, dto.Id);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Skipping malformed document {DocId}", dto.Id);
            return null;
        }
    }

    /// <summary>
    /// Parses the markdown transcript format produced by <see cref="DocumentServiceUploader.FormatTranscript"/>.
    /// Format per message block:
    ///   **Human** (HH:mm):       or   **Assistant** (HH:mm):
    ///   {content}
    ///   (blank line)
    /// </summary>
    internal static List<ConversationMessage> ParseTranscriptMessages(string transcript)
    {
        var messages = new List<ConversationMessage>();
        var lines = transcript.Split('\n');

        string? currentRole    = null;
        DateTime currentTime   = DateTime.MinValue;
        var contentLines       = new List<string>();
        bool inHeader          = true; // skip the title and Source line

        void FlushMessage()
        {
            if (currentRole is null) return;
            // Remove trailing blank lines from content
            while (contentLines.Count > 0 && string.IsNullOrWhiteSpace(contentLines[^1]))
                contentLines.RemoveAt(contentLines.Count - 1);
            if (contentLines.Count == 0) return;
            messages.Add(new ConversationMessage
            {
                Role      = currentRole,
                Content   = string.Join("\n", contentLines),
                CreatedAt = currentTime
            });
            contentLines.Clear();
            currentRole = null;
        }

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd('\r');

            // Skip title line (starts with #) and Source line
            if (inHeader)
            {
                if (line.StartsWith("Source:", StringComparison.Ordinal) ||
                    line.StartsWith("# ", StringComparison.Ordinal) ||
                    string.IsNullOrWhiteSpace(line))
                    continue;
                inHeader = false;
            }

            // Detect message header: **Human** (HH:mm): or **Assistant** (HH:mm):
            if (line.StartsWith("**Human**", StringComparison.Ordinal) ||
                line.StartsWith("**Assistant**", StringComparison.Ordinal))
            {
                FlushMessage();

                currentRole = line.StartsWith("**Human**") ? "user" : "assistant";
                currentTime = ParseTimeFromHeader(line);
                continue;
            }

            if (currentRole is not null)
                contentLines.Add(line);
        }

        FlushMessage();
        return messages;
    }

    private static DateTime ParseTimeFromHeader(string headerLine)
    {
        // Format: **Human** (HH:mm):
        var start = headerLine.IndexOf('(');
        var end   = headerLine.IndexOf(')');
        if (start < 0 || end <= start) return DateTime.UtcNow;

        var timeStr = headerLine.Substring(start + 1, end - start - 1);
        if (DateTime.TryParseExact(timeStr, "HH:mm", null,
                System.Globalization.DateTimeStyles.None, out var t))
            return DateTime.UtcNow.Date.Add(t.TimeOfDay);

        return DateTime.UtcNow;
    }

    // ── DTOs ──────────────────────────────────────────────────────────────────

    private sealed class DocumentDto
    {
        [JsonPropertyName("id")]          public string? Id          { get; set; }
        [JsonPropertyName("title")]       public string? Title       { get; set; }
        [JsonPropertyName("contentHash")] public string? ContentHash { get; set; }
        [JsonPropertyName("metadata")]    public string? Metadata    { get; set; }
        [JsonPropertyName("updatedAt")]   public DateTime? UpdatedAt { get; set; }
    }
}

