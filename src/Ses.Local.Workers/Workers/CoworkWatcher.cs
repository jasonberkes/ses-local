using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Ses.Local.Workers.Workers;

/// <summary>
/// Watches Cowork local session directory.
/// Implementation: WI-944.
/// </summary>
public sealed class CoworkWatcher : BackgroundService
{
    private readonly ILogger<CoworkWatcher> _logger;
    public CoworkWatcher(ILogger<CoworkWatcher> logger) => _logger = logger;
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("CoworkWatcher started (stub — WI-944)");
        return Task.CompletedTask;
    }
}
