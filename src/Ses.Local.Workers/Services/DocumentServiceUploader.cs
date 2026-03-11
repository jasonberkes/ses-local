using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Ses.Local.Core.Models;

namespace Ses.Local.Workers.Services;

/// <summary>
/// Uploads conversation transcripts to TaskMaster DocumentService as Transcript documents.
/// Posts directly to the API (bypassing SDK v1.2.0 which has a TenantId int/Guid mismatch).
/// </summary>
public sealed class DocumentServiceUploader
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<DocumentServiceUploader> _logger;

    private const int TranscriptTypeId = 4;

    // Super Easy Software tenant (docs.Tenants)
    private const string DefaultTenantId = "73fd3e56-67ae-4389-a398-1f2b796b10d1";

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public DocumentServiceUploader(IHttpClientFactory httpClientFactory, ILogger<DocumentServiceUploader> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger            = logger;
    }

    /// <summary>
    /// Uploads a conversation transcript. Returns the document ID, or null on failure.
    /// </summary>
    /// <param name="session">The session to upload.</param>
    /// <param name="messages">Messages belonging to the session.</param>
    /// <param name="pat">Bearer token for DocumentService auth.</param>
    /// <param name="deviceId">Optional device UUID — embedded in metadata so pull workers can skip own uploads.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<string?> UploadAsync(
        ConversationSession session,
        IReadOnlyList<ConversationMessage> messages,
        string pat,
        string? deviceId = null,
        CancellationToken ct = default)
    {
        try
        {
            var http = _httpClientFactory.CreateClient(DependencyInjection.DocumentServiceClientName);
            http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", pat);

            var transcript = FormatTranscript(session, messages);
            var metadataJson = JsonSerializer.Serialize(new
            {
                source       = session.Source.ToString(),
                externalId   = session.ExternalId,
                messageCount = messages.Count,
                deviceId,
                transcript
            });

            var payload = new
            {
                tenantId       = DefaultTenantId,
                documentTypeId = TranscriptTypeId,
                title          = session.Title ?? $"Conversation {session.ExternalId}",
                description    = $"{session.Source} conversation — {session.UpdatedAt:yyyy-MM-dd}",
                contentHash    = session.ContentHash,
                mimeType       = "application/json",
                metadata       = metadataJson,
                tags           = $"{session.Source},conversation,sync",
                createdBy      = "ses-local"
            };

            var json = JsonSerializer.Serialize(payload, s_jsonOptions);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var response = await http.PostAsync("api/v1/documents", content, ct);

            if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                throw new UnauthorizedAccessException(
                    "DocumentService returned 403 — PAT missing TenantId claim or docs:write scope");
            }

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                _logger.LogWarning(
                    "DocumentService returned {Status} for session {Id}: {Body}",
                    (int)response.StatusCode, session.Id, body);
                return null;
            }

            var responseJson = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(responseJson);
            var docId = doc.RootElement.GetProperty("id").ToString();

            _logger.LogDebug("Uploaded transcript for session {Id} → doc {DocId}", session.Id, docId);
            return docId;
        }
        catch (UnauthorizedAccessException)
        {
            throw; // 403 — propagate to CloudSyncWorker to stop the batch
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to upload transcript for session {Id}", session.Id);
            return null;
        }
    }

    internal static string FormatTranscript(ConversationSession session, IReadOnlyList<ConversationMessage> messages)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {session.Title ?? "Untitled"}");
        sb.AppendLine($"Source: {session.Source} | Created: {session.CreatedAt:yyyy-MM-dd HH:mm}");
        sb.AppendLine();

        foreach (var msg in messages.OrderBy(m => m.CreatedAt))
        {
            var role = msg.Role == "user" ? "Human" : "Assistant";
            sb.AppendLine($"**{role}** ({msg.CreatedAt:HH:mm}):");
            sb.AppendLine(msg.Content);
            sb.AppendLine();
        }

        return sb.ToString();
    }
}
