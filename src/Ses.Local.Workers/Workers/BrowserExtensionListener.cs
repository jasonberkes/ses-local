using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Ses.Local.Workers.Workers;

/// <summary>
/// Localhost HTTP listener on port 37780 for browser extension.
/// Implementation: WI-942.
/// </summary>
public sealed class BrowserExtensionListener : BackgroundService
{
    private readonly ILogger<BrowserExtensionListener> _logger;
    public BrowserExtensionListener(ILogger<BrowserExtensionListener> logger) => _logger = logger;
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("BrowserExtensionListener started (stub — WI-942)");
        return Task.CompletedTask;
    }
}
