using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Ses.Local.Core.Interfaces;
using Ses.Local.Workers.Services;

namespace Ses.Local.Workers.Workers;

/// <summary>
/// Syncs pending sessions from local SQLite to:
/// 1. TaskMaster DocumentService (transcripts)
/// 2. Cloud memory service (key observations)
///
/// Runs every 2 minutes when work is pending, backs off to 10 minutes when idle.
/// Fails gracefully on all network errors — never crashes the app.
/// </summary>
public sealed class CloudSyncWorker : BackgroundService
{
    private readonly ILocalDbService _db;
    private readonly IAuthService _auth;
    private readonly DocumentServiceUploader _docUploader;
    private readonly CloudMemoryRetainer _memRetainer;
    private readonly ILogger<CloudSyncWorker> _logger;

    private const int BatchSize = 10;
    private static readonly TimeSpan ActiveInterval = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan IdleInterval   = TimeSpan.FromMinutes(10);

    public CloudSyncWorker(
        ILocalDbService db,
        IAuthService auth,
        DocumentServiceUploader docUploader,
        CloudMemoryRetainer memRetainer,
        ILogger<CloudSyncWorker> logger)
    {
        _db          = db;
        _auth        = auth;
        _docUploader = docUploader;
        _memRetainer = memRetainer;
        _logger      = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("CloudSyncWorker started");
        var interval = ActiveInterval;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, stoppingToken);
                int synced = await RunSyncPassAsync(stoppingToken);
                interval = synced > 0 ? ActiveInterval : IdleInterval;
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "CloudSyncWorker: sync pass failed (non-fatal)");
                interval = IdleInterval; // back off on error
            }
        }
    }

    private async Task<int> RunSyncPassAsync(CancellationToken ct)
    {
        var pat = await _auth.GetAccessTokenAsync(ct);
        if (string.IsNullOrEmpty(pat))
        {
            _logger.LogDebug("CloudSyncWorker: no access token available — skipping");
            return 0;
        }

        var pending = await _db.GetPendingSyncAsync(BatchSize, ct);
        if (pending.Count == 0) return 0;

        _logger.LogDebug("CloudSyncWorker: syncing {Count} sessions", pending.Count);
        int synced = 0;

        foreach (var session in pending)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                // Get messages for this session
                var messages = await _db.GetMessagesAsync(session.Id, ct);

                // Upload to DocumentService
                var docId = await _docUploader.UploadAsync(session, messages, pat, ct);

                // Retain to cloud memory (best-effort, don't fail sync on memory error)
                _ = await _memRetainer.RetainAsync(session, messages, pat, ct);

                // Mark synced regardless of memory result — document upload is the primary
                await _db.MarkSyncedAsync(session.Id, docId, ct);
                synced++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "CloudSyncWorker: failed to sync session {Id}", session.Id);
                // Continue with next session
            }
        }

        _logger.LogDebug("CloudSyncWorker: sync pass complete — {Synced}/{Total}", synced, pending.Count);
        return synced;
    }
}
