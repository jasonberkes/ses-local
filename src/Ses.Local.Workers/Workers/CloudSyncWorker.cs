using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Ses.Local.Workers.Workers;

/// <summary>
/// Syncs pending sessions from ~/.ses/local.db to TaskMaster cloud.
/// Implementation: WI-945.
/// </summary>
public sealed class CloudSyncWorker : BackgroundService
{
    private readonly ILogger<CloudSyncWorker> _logger;
    public CloudSyncWorker(ILogger<CloudSyncWorker> logger) => _logger = logger;
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("CloudSyncWorker started (stub — WI-945)");
        return Task.CompletedTask;
    }
}
