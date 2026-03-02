using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Ses.Local.Core.Models;
using TaskMaster.DocumentService.SDK.Clients;
using TaskMaster.DocumentService.SDK.DTOs;

namespace Ses.Local.Workers.Services;

/// <summary>
/// Uploads conversation transcripts to TaskMaster DocumentService as Transcript documents.
/// </summary>
public sealed class DocumentServiceUploader
{
    private readonly ILogger<DocumentServiceUploader> _logger;
    private const string DocServiceUrl =
        "https://tm-documentservice-prod-eus2.redhill-040b1667.eastus2.azurecontainerapps.io";

    // DocumentTypeId 4 = Transcript (from docs schema)
    private const int TranscriptTypeId = 4;

    // Tenant ID for ses-local (int, not Guid)
    private const int DefaultTenantId = 1;

    public DocumentServiceUploader(ILogger<DocumentServiceUploader> logger)
        => _logger = logger;

    /// <summary>
    /// Uploads a conversation transcript. Returns the document ID, or null on failure.
    /// </summary>
    public async Task<string?> UploadAsync(
        ConversationSession session,
        IReadOnlyList<ConversationMessage> messages,
        string pat,
        CancellationToken ct = default)
    {
        try
        {
            using var http = BuildHttpClient(pat);
            var client     = new DocumentServiceClient(http);

            var transcript = FormatTranscript(session, messages);
            var metadataJson = JsonSerializer.Serialize(new
            {
                source       = session.Source.ToString(),
                externalId   = session.ExternalId,
                messageCount = messages.Count,
                transcript   // embed in metadata until blob upload is wired
            });

            var request = new CreateDocumentRequest
            {
                TenantId       = DefaultTenantId,
                DocumentTypeId = TranscriptTypeId,
                Title          = session.Title ?? $"Conversation {session.ExternalId}",
                Description    = $"{session.Source} conversation — {session.UpdatedAt:yyyy-MM-dd}",
                ContentHash    = session.ContentHash,
                MimeType       = "application/json",
                Metadata       = metadataJson,
                Tags           = $"{session.Source},conversation,sync",
                CreatedBy      = "ses-local"
            };

            var doc = await client.Documents.CreateAsync(request, ct);
            _logger.LogDebug("Uploaded transcript for session {Id} → doc {DocId}", session.Id, doc.Id);
            return doc.Id.ToString();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to upload transcript for session {Id}", session.Id);
            return null;
        }
    }

    private static string FormatTranscript(ConversationSession session, IReadOnlyList<ConversationMessage> messages)
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

    private static System.Net.Http.HttpClient BuildHttpClient(string pat)
    {
        var http = new System.Net.Http.HttpClient
        {
            BaseAddress = new Uri(DocServiceUrl),
            Timeout     = TimeSpan.FromSeconds(30)
        };
        http.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", pat);
        return http;
    }
}
