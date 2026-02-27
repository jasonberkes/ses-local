using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Ses.Local.Core.Enums;
using Ses.Local.Core.Interfaces;
using Ses.Local.Core.Models;

namespace Ses.Local.Workers.Services;

/// <summary>
/// Fetches conversation content from claude.ai API and stores in local SQLite.
///
/// Two modes:
/// - BulkSync: on first startup, paginate ALL conversations
/// - TargetedSync: called with specific UUIDs from LevelDbWatcher events
/// </summary>
public sealed class ClaudeAiSyncService
{
    private readonly ClaudeSessionCookieExtractor _cookieExtractor;
    private readonly ILocalDbService _db;
    private readonly ILogger<ClaudeAiSyncService> _logger;
    private bool _initialSyncDone;

    public ClaudeAiSyncService(
        ClaudeSessionCookieExtractor cookieExtractor,
        ILocalDbService db,
        ILogger<ClaudeAiSyncService> logger)
    {
        _cookieExtractor = cookieExtractor;
        _db              = db;
        _logger          = logger;
    }

    /// <summary>
    /// On first call: bulk sync all conversations.
    /// On subsequent calls with UUIDs: fetch only those UUIDs.
    /// On subsequent calls without UUIDs: incremental 24h sync.
    /// </summary>
    public async Task SyncAsync(IReadOnlyList<string>? targetUuids = null, CancellationToken ct = default)
    {
        var cookie = _cookieExtractor.Extract();
        if (string.IsNullOrEmpty(cookie))
        {
            _logger.LogDebug("No Claude Desktop session cookie — skipping sync (browser extension will supplement)");
            return;
        }

        using var client = new ClaudeAiClient(cookie,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ClaudeAiClient>.Instance);

        try
        {
            if (!_initialSyncDone)
            {
                await BulkSyncAsync(client, ct);
                _initialSyncDone = true;
                return;
            }

            if (targetUuids is { Count: > 0 })
            {
                await TargetedSyncAsync(client, targetUuids, ct);
            }
            else
            {
                await IncrementalSyncAsync(client, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _logger.LogWarning(ex, "Claude.ai sync pass failed (non-fatal)"); }
    }

    private async Task BulkSyncAsync(ClaudeAiClient client, CancellationToken ct)
    {
        _logger.LogInformation("Claude.ai: starting initial bulk sync");
        int count = 0;
        await foreach (var meta in client.ListConversationsAsync(ct))
        {
            await SyncOneAsync(client, meta.Uuid, ct);
            count++;
        }
        _logger.LogInformation("Claude.ai: bulk sync complete — {Count} conversations", count);
    }

    private async Task TargetedSyncAsync(ClaudeAiClient client, IReadOnlyList<string> uuids, CancellationToken ct)
    {
        _logger.LogDebug("Claude.ai: targeted sync for {Count} UUIDs", uuids.Count);
        foreach (var uuid in uuids)
        {
            ct.ThrowIfCancellationRequested();
            await SyncOneAsync(client, uuid, ct);
        }
    }

    private async Task IncrementalSyncAsync(ClaudeAiClient client, CancellationToken ct)
    {
        var cutoff = DateTime.UtcNow.AddHours(-24);
        int count = 0;
        await foreach (var meta in client.ListConversationsAsync(ct))
        {
            if (meta.UpdatedAt < cutoff) break;
            await SyncOneAsync(client, meta.Uuid, ct);
            count++;
        }
        if (count > 0)
            _logger.LogDebug("Claude.ai: incremental sync — {Count} conversations", count);
    }

    private async Task SyncOneAsync(ClaudeAiClient client, string uuid, CancellationToken ct)
    {
        var full = await client.GetConversationAsync(uuid, ct);
        if (full is null) return;

        var hash    = ComputeHash(full);
        var session = BuildSession(full, hash);
        await _db.UpsertSessionAsync(session, ct);
        // UpsertSessionAsync syncs back session.Id after insert/conflict resolution

        if (full.Messages.Count > 0 && session.Id > 0)
        {
            var messages = full.Messages.Select(m => BuildMessage(m, session.Id));
            await _db.UpsertMessagesAsync(messages, ct);
        }
    }

    private static ConversationSession BuildSession(ClaudeConversation conv, string hash) =>
        new()
        {
            Source      = ConversationSource.ClaudeChat,
            ExternalId  = conv.Uuid,
            Title       = conv.Name,
            ContentHash = hash,
            CreatedAt   = conv.CreatedAt,
            UpdatedAt   = conv.UpdatedAt
        };

    private static ConversationMessage BuildMessage(ClaudeMessage m, long sessionId) =>
        new()
        {
            SessionId = sessionId,
            Role      = m.Sender == "human" ? "user" : "assistant",
            Content   = m.Text,
            CreatedAt = m.CreatedAt
        };

    private static string ComputeHash(ClaudeConversation conv)
    {
        var key   = $"{conv.Uuid}:{conv.UpdatedAt:O}:{conv.Messages.Count}";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return Convert.ToHexString(bytes)[..16];
    }
}
