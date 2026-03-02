using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Ses.Local.Core.Interfaces;

namespace Ses.Local.Workers.Workers;

/// <summary>
/// Background worker that polls for sessions with uncompressed observations and
/// runs the Layer 1 rule-based compressor to produce <c>conv_session_summaries</c> rows.
/// Polls every 60 seconds and processes up to 10 sessions per pass.
/// </summary>
public sealed class CompressionWorker : BackgroundService
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
        _logger.LogInformation("CompressionWorker started (poll interval: {Interval}s, batch: {Batch})",
            PollInterval.TotalSeconds, BatchSize);

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
                _logger.LogError(ex, "CompressionWorker encountered an unhandled error");
            }

            await Task.Delay(PollInterval, stoppingToken).ConfigureAwait(false);
        }

        _logger.LogInformation("CompressionWorker stopped");
    }

    private async Task ProcessBatchAsync(CancellationToken ct)
    {
        var sessionIds = await _db.GetSessionsWithoutSummaryAsync(BatchSize, ct);
        if (sessionIds.Count == 0)
            return;

        _logger.LogDebug("CompressionWorker: found {Count} session(s) to compress", sessionIds.Count);

        foreach (var sessionId in sessionIds)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var observations = await _db.GetObservationsAsync(sessionId, ct);
                if (observations.Count == 0)
                {
                    _logger.LogDebug("Session {SessionId} has no observations; skipping", sessionId);
                    continue;
                }

                var summary = await _compressor.CompressAsync(sessionId, observations, ct);
                await _db.UpsertSessionSummaryAsync(summary, ct);

                _logger.LogInformation(
                    "Session {SessionId} compressed (layer={Layer}, category={Category}, tools={Tools}, errors={Errors})",
                    sessionId, summary.CompressionLayer, summary.Category, summary.ToolUseCount, summary.ErrorCount);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to compress session {SessionId}", sessionId);
            }
        }
    }
}
