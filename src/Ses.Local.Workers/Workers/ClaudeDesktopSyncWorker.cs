using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Ses.Local.Core.Events;
using Ses.Local.Core.Interfaces;
using Ses.Local.Core.Options;
using Ses.Local.Workers.Services;
using System.Threading.Channels;

namespace Ses.Local.Workers.Workers;

/// <summary>
/// Subscribes to DesktopActivityEvents (from LevelDbWatcher) and
/// drives ClaudeAiSyncService to fetch conversation content.
/// Also runs periodic incremental sync as fallback.
/// </summary>
public sealed class ClaudeDesktopSyncWorker : BackgroundService
{
    private readonly ClaudeAiSyncService _syncService;
    private readonly IDesktopActivityNotifier _notifier;
    private readonly ILogger<ClaudeDesktopSyncWorker> _logger;
    private readonly SesLocalOptions _options;

    // Bounded channel: if 5 events queue up, drop oldest (Desktop won't generate more than this)
    private readonly Channel<DesktopActivityEvent> _queue =
        Channel.CreateBounded<DesktopActivityEvent>(
            new BoundedChannelOptions(5) { FullMode = BoundedChannelFullMode.DropOldest });

    public ClaudeDesktopSyncWorker(
        ClaudeAiSyncService syncService,
        IDesktopActivityNotifier notifier,
        ILogger<ClaudeDesktopSyncWorker> logger,
        IOptions<SesLocalOptions> options)
    {
        _syncService = syncService;
        _notifier    = notifier;
        _logger      = logger;
        _options     = options.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.EnableClaudeDesktopSync)
        {
            _logger.LogInformation("ClaudeDesktopSyncWorker disabled via options");
            return;
        }

        _notifier.DesktopActivityDetected += OnActivity;

        // Initial bulk sync
        await _syncService.SyncAsync(null, stoppingToken);

        // Event-driven + periodic fallback
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));
        try
        {
            Task<bool>? pollTask = null;
            while (!stoppingToken.IsCancellationRequested)
            {
                // Only create a new poll task if the previous one completed
                pollTask ??= timer.WaitForNextTickAsync(stoppingToken).AsTask();
                var channelTask = _queue.Reader.WaitToReadAsync(stoppingToken).AsTask();

                var completed = await Task.WhenAny(pollTask, channelTask);
                if (completed == pollTask) pollTask = null; // allow next tick to be awaited

                // Collect all queued events and merge UUID lists
                var allUuids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                while (_queue.Reader.TryRead(out var evt))
                    foreach (var id in evt.ConversationUuids)
                        allUuids.Add(id);

                if (!stoppingToken.IsCancellationRequested)
                {
                    var uuids = allUuids.Count > 0 ? (IReadOnlyList<string>)allUuids.ToList() : null;
                    await _syncService.SyncAsync(uuids, stoppingToken);
                }
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            _notifier.DesktopActivityDetected -= OnActivity;
        }
    }

    private void OnActivity(object? sender, DesktopActivityEvent e) =>
        _queue.Writer.TryWrite(e);
}
