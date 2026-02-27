using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Ses.Local.Core.Events;
using Ses.Local.Core.Interfaces;
using Ses.Local.Core.Options;
using Ses.Local.Workers.Services;

namespace Ses.Local.Workers.Workers;

/// <summary>
/// Watches Claude Desktop's Local Storage LevelDB for changes.
/// On change: scans .ldb files for LSS-{uuid} keys, extracts all
/// conversation UUIDs (Desktop + iPhone sync + web sync), and notifies
/// IDesktopActivityNotifier with the full UUID list.
///
/// The WI-941 ClaudeDesktopSyncWorker subscribes and fetches content
/// for any UUIDs not yet in local.db.
/// </summary>
public sealed class LevelDbWatcher : BackgroundService
{
    private readonly LevelDbUuidExtractor _extractor;
    private readonly IDesktopActivityNotifier _notifier;
    private readonly ILogger<LevelDbWatcher> _logger;
    private readonly SesLocalOptions _options;

    // Debounce: LDB files get written in bursts — batch within 3s window
    private readonly TimeSpan _debounceInterval = TimeSpan.FromSeconds(3);
    private DateTime _lastNotification = DateTime.MinValue;
    private readonly SemaphoreSlim _debounceLock = new(1, 1);

    public LevelDbWatcher(
        LevelDbUuidExtractor extractor,
        IDesktopActivityNotifier notifier,
        ILogger<LevelDbWatcher> logger,
        IOptions<SesLocalOptions> options)
    {
        _extractor = extractor;
        _notifier  = notifier;
        _logger    = logger;
        _options   = options.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.EnableClaudeDesktopSync)
        {
            _logger.LogInformation("LevelDbWatcher disabled via options");
            return;
        }

        var watchPath = LevelDbUuidExtractor.GetLevelDbPath();
        if (!Directory.Exists(watchPath))
        {
            _logger.LogInformation(
                "Claude Desktop Local Storage not found: {Path}. Watcher idle.", watchPath);
            return;
        }

        _logger.LogInformation("LevelDbWatcher watching: {Path}", watchPath);

        // Do an initial scan immediately on startup
        await ScanAndNotifyAsync(stoppingToken);

        using var watcher = new FileSystemWatcher(watchPath)
        {
            NotifyFilter          = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            Filter                = "*.ldb",
            IncludeSubdirectories = false,
            EnableRaisingEvents   = true
        };

        watcher.Changed += OnFileChanged;
        watcher.Created += OnFileChanged;

        // Polling fallback — scan on interval regardless
        var pollInterval = TimeSpan.FromSeconds(
            _options.PollingIntervalSeconds > 0 ? _options.PollingIntervalSeconds : 300);

        using var timer = new PeriodicTimer(pollInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                _logger.LogDebug("LevelDbWatcher: periodic poll");
                await ScanAndNotifyAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) { }
    }

    private async void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await ScanAndNotifyDebounced(cts.Token);
    }

    private async Task ScanAndNotifyDebounced(CancellationToken ct)
    {
        await _debounceLock.WaitAsync(ct);
        try
        {
            var now = DateTime.UtcNow;
            if (now - _lastNotification < _debounceInterval) return;
            _lastNotification = now;
        }
        finally
        {
            _debounceLock.Release();
        }

        await ScanAndNotifyAsync(ct);
    }

    private Task ScanAndNotifyAsync(CancellationToken ct)
    {
        var path  = LevelDbUuidExtractor.GetLevelDbPath();
        var uuids = _extractor.ExtractUuids(path);

        if (uuids.Count > 0)
        {
            _logger.LogDebug("LevelDbWatcher: {Count} UUIDs found, notifying", uuids.Count);
            _notifier.NotifyActivity(new DesktopActivityEvent
            {
                ConversationUuids = uuids
            });
        }

        return Task.CompletedTask;
    }
}
