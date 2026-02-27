using Ses.Local.Core.Events;

namespace Ses.Local.Core.Interfaces;

/// <summary>
/// Event bus between LevelDbWatcher (producer) and ClaudeDesktopSyncWorker (consumer, WI-941).
/// </summary>
public interface IDesktopActivityNotifier
{
    event EventHandler<DesktopActivityEvent>? DesktopActivityDetected;
    void NotifyActivity(DesktopActivityEvent e);
}
