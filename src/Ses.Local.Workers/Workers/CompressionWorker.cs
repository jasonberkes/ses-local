using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Ses.Local.Core.Interfaces;
using Ses.Local.Workers.Telemetry;

namespace Ses.Local.Workers.Workers;

/// <summary>
/// Background worker that polls for sessions with uncompressed observations and
/// runs the Layer 1 rule-based compressor to produce <c>conv_session_summaries</c> rows.
/// Polls every 60 seconds and processes up to 10 sessions per pass.
/// </summary>
public sealed partial class CompressionWorker : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(60);
    private const int BatchSize = 10;

    private readonly ILocalDbService _db;
    private readonly IObservationCompressor _compressor;
    private readonly ILogger<CompressionWorker> _logger;

    public CompressionWorker(
        ILocalDbService db,
        IObservationCompressor compressor,
        ILogger<CompressionWorker> logger)
    {
        _db         = db;
        _compressor = compressor;
        _logger     = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogStarted(_logger, (int)PollInterval.TotalSeconds, BatchSize);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessBatchAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                LogUnhandledError(_logger, ex);
            }

            await Task.Delay(PollInterval, stoppingToken).ConfigureAwait(false);
        }

        LogStopped(_logger);
    }

    private async Task ProcessBatchAsync(CancellationToken ct)
    {
        var sessionIds = await _db.GetSessionsWithoutSummaryAsync(BatchSize, ct);
        if (sessionIds.Count == 0)
            return;

        LogFoundSessionsToCompress(_logger, sessionIds.Count);

        foreach (var sessionId in sessionIds)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var observations = await _db.GetObservationsAsync(sessionId, ct);
                if (observations.Count == 0)
                {
                    LogSkippingNoObservations(_logger, sessionId);
                    continue;
                }

                using var activity = SesLocalMetrics.ActivitySource.StartActivity("CompressionWorker.Compress");
                activity?.SetTag("session.id", sessionId);

                var summary = await _compressor.CompressAsync(sessionId, observations, ct);
                await _db.UpsertSessionSummaryAsync(summary, ct);

                SesLocalMetrics.SessionsCompressed.Add(1,
                    new KeyValuePair<string, object?>("layer", summary.CompressionLayer));

                activity?.SetTag("compression.layer", summary.CompressionLayer);
                activity?.SetTag("compression.category", summary.Category);

                LogSessionCompressed(_logger, sessionId, summary.CompressionLayer, summary.Category,
                    summary.ToolUseCount, summary.ErrorCount);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                LogSessionCompressionFailed(_logger, sessionId, ex);
            }
        }
    }

    // ── LoggerMessage source generators (high-perf structured logging) ────────

    [LoggerMessage(Level = LogLevel.Information,
        Message = "CompressionWorker started (poll interval: {IntervalSeconds}s, batch: {BatchSize})")]
    private static partial void LogStarted(ILogger logger, int intervalSeconds, int batchSize);

    [LoggerMessage(Level = LogLevel.Information, Message = "CompressionWorker stopped")]
    private static partial void LogStopped(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "CompressionWorker encountered an unhandled error")]
    private static partial void LogUnhandledError(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Debug, Message = "CompressionWorker: found {Count} session(s) to compress")]
    private static partial void LogFoundSessionsToCompress(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Session {SessionId} has no observations; skipping")]
    private static partial void LogSkippingNoObservations(ILogger logger, long sessionId);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Session {SessionId} compressed (layer={Layer}, category={Category}, tools={Tools}, errors={Errors})")]
    private static partial void LogSessionCompressed(ILogger logger, long sessionId, int layer,
        string? category, int tools, int errors);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to compress session {SessionId}")]
    private static partial void LogSessionCompressionFailed(ILogger logger, long sessionId, Exception ex);
}
