using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Ses.Local.Tray.Services;

public sealed class NotificationService : INotifyPropertyChanged
{
    public enum NotificationCategory { Auth, Sync, Daemon, Update }

    private readonly Dictionary<NotificationCategory, NotificationEntry> _notifications = new();

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler?               NotificationsChanged;

    public bool HasNotifications                   => _notifications.Count > 0;
    public int  Count                              => _notifications.Count;
    public NotificationCategory? HighestPriority
    {
        get
        {
            if (_notifications.ContainsKey(NotificationCategory.Auth))   return NotificationCategory.Auth;
            if (_notifications.ContainsKey(NotificationCategory.Daemon)) return NotificationCategory.Daemon;
            if (_notifications.ContainsKey(NotificationCategory.Sync))   return NotificationCategory.Sync;
            if (_notifications.ContainsKey(NotificationCategory.Update)) return NotificationCategory.Update;
            return null;
        }
    }

    public void Add(NotificationCategory category, string message, Action? action = null)
    {
        _notifications[category] = new NotificationEntry
        {
            Category  = category,
            Message   = message,
            CreatedAt = DateTime.UtcNow,
            Action    = action,
        };
        RaiseChanged();
    }

    public void Dismiss(NotificationCategory category)
    {
        if (!_notifications.Remove(category)) return;
        RaiseChanged();
    }

    public void DismissAll()
    {
        if (_notifications.Count == 0) return;
        _notifications.Clear();
        RaiseChanged();
    }

    public IReadOnlyList<NotificationEntry> GetAll() =>
        [.. _notifications.Values.OrderBy(n => PriorityOf(n.Category))];

    private static int PriorityOf(NotificationCategory c) => c switch
    {
        NotificationCategory.Auth   => 0,
        NotificationCategory.Daemon => 1,
        NotificationCategory.Sync   => 2,
        NotificationCategory.Update => 3,
        _                           => 4,
    };

    private void RaiseChanged()
    {
        OnPropertyChanged(nameof(HasNotifications));
        OnPropertyChanged(nameof(Count));
        OnPropertyChanged(nameof(HighestPriority));
        NotificationsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class NotificationEntry
{
    public required NotificationService.NotificationCategory Category { get; init; }
    public required string                                   Message  { get; init; }
    public required DateTime                                 CreatedAt { get; init; }
    public          Action?                                  Action   { get; init; }

    public string Icon => Category switch
    {
        NotificationService.NotificationCategory.Auth   => "⚠",
        NotificationService.NotificationCategory.Daemon => "✕",
        NotificationService.NotificationCategory.Sync   => "⚠",
        NotificationService.NotificationCategory.Update => "↑",
        _                                               => "•",
    };
}
