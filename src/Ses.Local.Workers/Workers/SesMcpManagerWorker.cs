using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Ses.Local.Workers.Services;

namespace Ses.Local.Workers.Workers;

/// <summary>
/// Runs ses-mcp health check on startup and every 30 minutes.
/// </summary>
public sealed class SesMcpManagerWorker : BackgroundService
{
    private readonly SesMcpManager _manager;
    private readonly ILogger<SesMcpManagerWorker> _logger;
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(30);

    public SesMcpManagerWorker(SesMcpManager manager, ILogger<SesMcpManagerWorker> logger)
    {
        _manager = manager;
        _logger  = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Run on startup
        await RunCheckAsync(stoppingToken);

        using var timer = new PeriodicTimer(CheckInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
                await RunCheckAsync(stoppingToken);
        }
        catch (OperationCanceledException) { }
    }

    private async Task RunCheckAsync(CancellationToken ct)
    {
        try
        {
            var status = await _manager.CheckAndRepairAsync(ct);
            _logger.LogDebug(
                "ses-mcp health: installed={Installed}, configured={Configured}, drift={Drift}",
                status.IsInstalled, status.IsConfigured, status.HasConfigDrift);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ses-mcp health check failed (non-fatal)");
        }
    }
}
