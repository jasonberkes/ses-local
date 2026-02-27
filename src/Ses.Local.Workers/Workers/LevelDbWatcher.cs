using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Ses.Local.Workers.Workers;

/// <summary>
/// Watches the Claude Desktop LevelDB UUID cache for new/updated conversations.
/// Mac: ~/Library/Application Support/Claude/https_claude.ai_0.indexeddb.leveldb/
/// Windows: %APPDATA%\Claude\https_claude.ai_0.indexeddb.leveldb\
/// Implementation: WI-940.
/// </summary>
public sealed class LevelDbWatcher : BackgroundService
{
    private readonly ILogger<LevelDbWatcher> _logger;
    public LevelDbWatcher(ILogger<LevelDbWatcher> logger) => _logger = logger;
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("LevelDbWatcher started (stub — WI-940)");
        return Task.CompletedTask;
    }
}
