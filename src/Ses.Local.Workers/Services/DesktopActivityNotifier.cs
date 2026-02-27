using Ses.Local.Core.Events;
using Ses.Local.Core.Interfaces;

namespace Ses.Local.Workers.Services;

public sealed class DesktopActivityNotifier : IDesktopActivityNotifier
{
    public event EventHandler<DesktopActivityEvent>? DesktopActivityDetected;
    public void NotifyActivity(DesktopActivityEvent e) =>
        DesktopActivityDetected?.Invoke(this, e);
}
