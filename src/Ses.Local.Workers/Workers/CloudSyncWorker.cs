using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Ses.Local.Core.Interfaces;
using Ses.Local.Workers.Services;
using Ses.Local.Workers.Telemetry;

namespace Ses.Local.Workers.Workers;

/// <summary>
/// Syncs pending sessions from local SQLite to:
/// 1. TaskMaster DocumentService (transcripts)
/// 2. Cloud memory service (key observations)
///
/// Runs every 2 minutes when work is pending, backs off to 10 minutes when idle.
/// Fails gracefully on all network errors — never crashes the app.
/// </summary>
public sealed partial class CloudSyncWorker : BackgroundService
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
        LogStarted(_logger);
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
                LogSyncPassFailed(_logger, ex);
                interval = IdleInterval; // back off on error
            }
        }
    }

    private async Task<int> RunSyncPassAsync(CancellationToken ct)
    {
        using var activity = SesLocalMetrics.ActivitySource.StartActivity("CloudSyncWorker.SyncPass");

        var pat = await _auth.GetAccessTokenAsync(ct);
        if (string.IsNullOrEmpty(pat))
        {
            LogNoAccessToken(_logger);
            return 0;
        }

        var pending = await _db.GetPendingSyncAsync(BatchSize, ct);
        if (pending.Count == 0) return 0;

        LogSyncingCount(_logger, pending.Count);
        int synced = 0;

        foreach (var session in pending)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                // Get messages for this session
                var messages = await _db.GetMessagesAsync(session.Id, ct);

                SesLocalMetrics.UploadsAttempted.Add(1);

                // Upload to DocumentService
                var docId = await _docUploader.UploadAsync(session, messages, pat, ct);

                // Retain to cloud memory (best-effort, don't fail sync on memory error)
                _ = await _memRetainer.RetainAsync(session, messages, pat, ct);

                // Mark synced regardless of memory result — document upload is the primary
                await _db.MarkSyncedAsync(session.Id, docId, ct);

                SesLocalMetrics.UploadsSucceeded.Add(1);
                synced++;
            }
            catch (Exception ex)
            {
                SesLocalMetrics.UploadsFailed.Add(1);
                LogSessionSyncFailed(_logger, session.Id, ex);
                // Continue with next session
            }
        }

        LogSyncPassComplete(_logger, synced, pending.Count);
        activity?.SetTag("sessions.synced", synced);
        activity?.SetTag("sessions.total", pending.Count);
        return synced;
    }

    // ── LoggerMessage source generators (high-perf structured logging) ────────

    [LoggerMessage(Level = LogLevel.Information, Message = "CloudSyncWorker started")]
    private static partial void LogStarted(ILogger logger);

    [LoggerMessage(Level = LogLevel.Debug, Message = "CloudSyncWorker: no access token available — skipping")]
    private static partial void LogNoAccessToken(ILogger logger);

    [LoggerMessage(Level = LogLevel.Debug, Message = "CloudSyncWorker: syncing {Count} sessions")]
    private static partial void LogSyncingCount(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "CloudSyncWorker: failed to sync session {SessionId}")]
    private static partial void LogSessionSyncFailed(ILogger logger, long sessionId, Exception ex);

    [LoggerMessage(Level = LogLevel.Debug, Message = "CloudSyncWorker: sync pass complete — {Synced}/{Total}")]
    private static partial void LogSyncPassComplete(ILogger logger, int synced, int total);

    [LoggerMessage(Level = LogLevel.Warning, Message = "CloudSyncWorker: sync pass failed (non-fatal)")]
    private static partial void LogSyncPassFailed(ILogger logger, Exception ex);
}
