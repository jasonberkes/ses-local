using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Ses.Local.Core.Interfaces;
using Ses.Local.Core.Models;
using Ses.Local.Core.Options;
using Ses.Local.Workers.Telemetry;

namespace Ses.Local.Workers.Workers;

/// <summary>
/// Pulls new or updated conversation transcripts from the cloud into the local SQLite database,
/// enabling multi-device support (Tier 2 — requires OAuth authentication).
///
/// Behaviour:
/// - Gated by <see cref="SesLocalOptions.EnableCloudPull"/> (default: false).
/// - On startup and every <see cref="SesLocalOptions.CloudPullIntervalMinutes"/> minutes,
///   queries DocumentService for documents updated since the last pull.
/// - Filters out documents that originated on this device (identified by stored device_id).
/// - Merges cloud sessions into local DB idempotently: pulling the same data twice is a no-op.
/// - Cloud wins for session metadata (title, content_hash); local wins for observations.
/// - Fails gracefully on all network/parse errors — never crashes the application.
/// </summary>
public sealed partial class CloudPullWorker : BackgroundService
{
    private const string LastPullAtKey = "last_pull_at";
    private const string DeviceIdKey   = "device_id";

    private readonly ILocalDbService _db;
    private readonly IAuthService _auth;
    private readonly IDocumentServiceDownloader _downloader;
    private readonly SesLocalOptions _options;
    private readonly ILogger<CloudPullWorker> _logger;

    public CloudPullWorker(
        ILocalDbService db,
        IAuthService auth,
        IDocumentServiceDownloader downloader,
        IOptions<SesLocalOptions> options,
        ILogger<CloudPullWorker> logger)
    {
        _db         = db;
        _auth       = auth;
        _downloader = downloader;
        _options    = options.Value;
        _logger     = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.EnableCloudPull)
        {
            LogDisabled(_logger);
            return;
        }

        LogStarted(_logger);

        // Run an initial pull shortly after startup, then repeat on the configured interval
        var interval = TimeSpan.FromMinutes(_options.CloudPullIntervalMinutes);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, stoppingToken);
                await RunPullPassAsync(stoppingToken);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                LogPullPassFailed(_logger, ex);
                // Back off on unexpected errors — don't spin-loop
            }
        }
    }

    internal async Task<int> RunPullPassAsync(CancellationToken ct)
    {
        using var activity = SesLocalMetrics.ActivitySource.StartActivity("CloudPullWorker.PullPass");

        var pat = await _auth.GetAccessTokenAsync(ct);
        if (string.IsNullOrEmpty(pat))
        {
            LogNoAccessToken(_logger);
            return 0;
        }

        // Resolve or generate our device_id (may have been set by CloudSyncWorker already)
        var deviceId = await _db.GetSyncMetadataAsync(DeviceIdKey, ct);
        if (deviceId is null)
        {
            deviceId = Guid.NewGuid().ToString();
            await _db.SetSyncMetadataAsync(DeviceIdKey, deviceId, ct);
            LogNewDeviceId(_logger, deviceId);
        }

        // Determine pull window — default to 30 days on first run
        var lastPullStr  = await _db.GetSyncMetadataAsync(LastPullAtKey, ct);
        var updatedAfter = lastPullStr is not null
            ? DateTime.Parse(lastPullStr, null, System.Globalization.DateTimeStyles.RoundtripKind)
            : DateTime.UtcNow.AddDays(-30);

        LogPullPass(_logger, updatedAfter);

        var documents = await _downloader.GetDocumentsAsync(pat, updatedAfter, deviceId, ct);

        int imported = 0;
        int skipped  = 0;

        foreach (var doc in documents)
        {
            ct.ThrowIfCancellationRequested();
            SesLocalMetrics.PullsAttempted.Add(1);

            try
            {
                bool wasNew = await MergeDocumentAsync(doc, ct);
                if (wasNew)
                {
                    SesLocalMetrics.PullsImported.Add(1);
                    imported++;
                }
                else
                {
                    SesLocalMetrics.PullsSkipped.Add(1);
                    skipped++;
                }
            }
            catch (Exception ex)
            {
                SesLocalMetrics.PullsSkipped.Add(1);
                LogMergeFailed(_logger, doc.Session.ExternalId, ex);
                skipped++;
            }
        }

        // Advance the pull cursor so next pass only fetches genuinely newer documents
        await _db.SetSyncMetadataAsync(LastPullAtKey, DateTime.UtcNow.ToString("O"), ct);

        LogPullPassComplete(_logger, imported, skipped, documents.Count);
        activity?.SetTag("sessions.imported", imported);
        activity?.SetTag("sessions.skipped",  skipped);
        activity?.SetTag("sessions.total",    documents.Count);

        return imported;
    }

    /// <summary>
    /// Merges a pulled document into the local DB.
    /// Returns true if the session was new or had updated content; false if it was already present
    /// with the same ContentHash (idempotent no-op).
    ///
    /// Conflict resolution:
    ///   - Cloud wins for session metadata (title, content_hash, updated_at).
    ///   - Local wins for observations (not touched here — only messages are merged).
    /// </summary>
    private async Task<bool> MergeDocumentAsync(PulledDocument doc, CancellationToken ct)
    {
        var session  = doc.Session;
        var messages = doc.Messages;

        // Look up any existing local session by its DB id (synced back via upsert).
        // We read it first to capture the current content_hash for change detection.
        var existing = await _db.GetSessionBySourceAndExternalIdAsync(
            session.Source.ToString(), session.ExternalId, ct);

        if (existing is not null &&
            existing.ContentHash is not null &&
            existing.ContentHash == session.ContentHash)
        {
            LogSkippedNoChange(_logger, session.ExternalId);
            return false;
        }

        // Cloud wins for session metadata; UpsertSessionAsync syncs back session.Id.
        await _db.UpsertSessionAsync(session, ct);

        // Merge messages if we received any (ON CONFLICT in UpsertMessagesAsync is idempotent).
        if (messages.Count > 0)
        {
            foreach (var msg in messages)
                msg.SessionId = session.Id;

            await _db.UpsertMessagesAsync(messages, ct);
        }

        LogImported(_logger, session.ExternalId, session.Source.ToString(), messages.Count);
        return true;
    }

    // ── LoggerMessage source generators ───────────────────────────────────────

    [LoggerMessage(Level = LogLevel.Information, Message = "CloudPullWorker started")]
    private static partial void LogStarted(ILogger logger);

    [LoggerMessage(Level = LogLevel.Debug, Message = "CloudPullWorker is disabled (EnableCloudPull = false) — not running")]
    private static partial void LogDisabled(ILogger logger);

    [LoggerMessage(Level = LogLevel.Debug, Message = "CloudPullWorker: no access token — skipping pull pass")]
    private static partial void LogNoAccessToken(ILogger logger);

    [LoggerMessage(Level = LogLevel.Debug, Message = "CloudPullWorker: pulling documents updated after {UpdatedAfter:O}")]
    private static partial void LogPullPass(ILogger logger, DateTime updatedAfter);

    [LoggerMessage(Level = LogLevel.Information, Message = "CloudPullWorker: new device_id generated: {DeviceId}")]
    private static partial void LogNewDeviceId(ILogger logger, string deviceId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "CloudPullWorker: skipped {ExternalId} — content hash unchanged")]
    private static partial void LogSkippedNoChange(ILogger logger, string externalId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "CloudPullWorker: imported session {ExternalId} (source={Source}, messages={Count})")]
    private static partial void LogImported(ILogger logger, string externalId, string source, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "CloudPullWorker: failed to merge session {ExternalId}")]
    private static partial void LogMergeFailed(ILogger logger, string externalId, Exception ex);

    [LoggerMessage(Level = LogLevel.Debug, Message = "CloudPullWorker: pull pass complete — {Imported} imported, {Skipped} skipped of {Total}")]
    private static partial void LogPullPassComplete(ILogger logger, int imported, int skipped, int total);

    [LoggerMessage(Level = LogLevel.Warning, Message = "CloudPullWorker: pull pass failed (non-fatal)")]
    private static partial void LogPullPassFailed(ILogger logger, Exception ex);
}
