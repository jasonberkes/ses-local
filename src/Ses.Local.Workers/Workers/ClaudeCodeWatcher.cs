using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Ses.Local.Workers.Workers;

/// <summary>
/// Watches ~/.claude/projects/**/*.jsonl for new CC sessions.
/// Implementation: WI-943.
/// </summary>
public sealed class ClaudeCodeWatcher : BackgroundService
{
    private readonly ILogger<ClaudeCodeWatcher> _logger;
    public ClaudeCodeWatcher(ILogger<ClaudeCodeWatcher> logger) => _logger = logger;
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("ClaudeCodeWatcher started (stub — WI-943)");
        return Task.CompletedTask;
    }
}
