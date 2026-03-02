using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Ses.Local.Core.Interfaces;
using Ses.Local.Core.Options;
using Ses.Local.Workers.Telemetry;

namespace Ses.Local.Workers.Workers;

/// <summary>
/// Background worker that polls for sessions with uncompressed observations and
/// runs the Layer 1 rule-based compressor to produce <c>conv_session_summaries</c> rows.
/// When vector search is enabled, also embeds new summaries for semantic search.
/// Polls every 60 seconds and processes up to 10 sessions per pass.
/// </summary>
public sealed partial class CompressionWorker : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(60);
    private const int BatchSize = 10;

    private readonly ILocalDbService _db;
    private readonly IObservationCompressor _compressor;
    private readonly IVectorSearchService? _vectorSearch;
    private readonly bool _vectorSearchEnabled;
    private readonly ILogger<CompressionWorker> _logger;

    public CompressionWorker(
        ILocalDbService db,
        IObservationCompressor compressor,
        IOptions<SesLocalOptions> options,
        ILogger<CompressionWorker> logger,
        IVectorSearchService? vectorSearch = null)
    {
        _db         = db;
        _compressor = compressor;
        _vectorSearch = vectorSearch;
        _vectorSearchEnabled = options.Value.EnableVectorSearch;
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
        {
            // Even if no new summaries, check for un-embedded summaries when vector search is on
            if (_vectorSearchEnabled && _vectorSearch is not null)
                await EmbedPendingSessionsAsync(ct);
            return;
        }

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

                // Embed the new summary for vector search if enabled
                if (_vectorSearchEnabled && _vectorSearch is not null)
                {
                    try
                    {
                        await _vectorSearch.IndexSessionAsync(sessionId, ct);
                    }
                    catch (Exception embedEx) when (embedEx is not OperationCanceledException)
                    {
                        LogEmbeddingFailed(_logger, sessionId, embedEx);
                    }
                }

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

    private async Task EmbedPendingSessionsAsync(CancellationToken ct)
    {
        var pending = await _db.GetSessionsWithoutEmbeddingAsync(BatchSize, ct);
        if (pending.Count == 0)
            return;

        LogFoundSessionsToEmbed(_logger, pending.Count);

        foreach (var sessionId in pending)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await _vectorSearch!.IndexSessionAsync(sessionId, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                LogEmbeddingFailed(_logger, sessionId, ex);
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

    [LoggerMessage(Level = LogLevel.Debug, Message = "CompressionWorker: found {Count} session(s) to embed")]
    private static partial void LogFoundSessionsToEmbed(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Session {SessionId} has no observations; skipping")]
    private static partial void LogSkippingNoObservations(ILogger logger, long sessionId);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Session {SessionId} compressed (layer={Layer}, category={Category}, tools={Tools}, errors={Errors})")]
    private static partial void LogSessionCompressed(ILogger logger, long sessionId, int layer,
        string? category, int tools, int errors);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to compress session {SessionId}")]
    private static partial void LogSessionCompressionFailed(ILogger logger, long sessionId, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to embed session {SessionId} for vector search")]
    private static partial void LogEmbeddingFailed(ILogger logger, long sessionId, Exception ex);
}
