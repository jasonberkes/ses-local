using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Ses.Local.Workers.Services;

namespace Ses.Local.Workers.Workers;

/// <summary>
/// Runs update checks on startup and every 4 hours.
/// Failures never crash the app — always degrade gracefully.
/// </summary>
public sealed class AutoUpdateWorker : BackgroundService
{
    private readonly SesLocalUpdater _sesLocalUpdater;
    private readonly SesMcpUpdater _sesMcpUpdater;
    private readonly ILogger<AutoUpdateWorker> _logger;
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(4);

    public AutoUpdateWorker(
        SesLocalUpdater sesLocalUpdater,
        SesMcpUpdater sesMcpUpdater,
        ILogger<AutoUpdateWorker> logger)
    {
        _sesLocalUpdater = sesLocalUpdater;
        _sesMcpUpdater = sesMcpUpdater;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Check immediately on startup
        await RunChecksAsync(stoppingToken);

        // Then every 4 hours
        using var timer = new PeriodicTimer(CheckInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
                await RunChecksAsync(stoppingToken);
        }
        catch (OperationCanceledException) { }
    }

    private async Task RunChecksAsync(CancellationToken ct)
    {
        _logger.LogDebug("Running update checks...");

        // ses-local update — check first, apply if available
        try
        {
            var result = await _sesLocalUpdater.CheckAndApplyAsync(ct);
            if (result.UpdateApplied)
                _logger.LogInformation("ses-local updated to {Version}. Will take effect on next restart.", result.NewVersion);
            else if (result.Message is not null)
                _logger.LogDebug("ses-local: {Message}", result.Message);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ses-local update check error (non-fatal)");
        }

        // ses-mcp update — independent channel
        try
        {
            var result = await _sesMcpUpdater.CheckAndApplyAsync(ct);
            if (result.UpdateApplied)
                _logger.LogInformation("ses-mcp updated to {Version}.", result.NewVersion);
            else if (result.Message is not null)
                _logger.LogDebug("ses-mcp: {Message}", result.Message);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ses-mcp update check error (non-fatal)");
        }
    }
}
