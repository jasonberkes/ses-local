using Ses.Local.Tray.Services;
using Xunit;

namespace Ses.Local.Workers.Tests.Services;

public sealed class NotificationServiceTests
{
    private static NotificationService Create() => new();

    [Fact]
    public void Add_SetsHasNotificationsTrue()
    {
        var svc = Create();
        svc.Add(NotificationService.NotificationCategory.Auth, "test");
        Assert.True(svc.HasNotifications);
    }

    [Fact]
    public void Add_SameCategory_ReplacesExisting()
    {
        var svc = Create();
        svc.Add(NotificationService.NotificationCategory.Auth, "first");
        svc.Add(NotificationService.NotificationCategory.Auth, "second");

        Assert.Equal(1, svc.Count);
        Assert.Equal("second", svc.GetAll()[0].Message);
    }

    [Fact]
    public void Add_DifferentCategories_KeepsBoth()
    {
        var svc = Create();
        svc.Add(NotificationService.NotificationCategory.Auth,   "auth msg");
        svc.Add(NotificationService.NotificationCategory.Daemon, "daemon msg");

        Assert.Equal(2, svc.Count);
    }

    [Fact]
    public void Dismiss_RemovesByCategory()
    {
        var svc = Create();
        svc.Add(NotificationService.NotificationCategory.Auth, "test");
        svc.Dismiss(NotificationService.NotificationCategory.Auth);

        Assert.False(svc.HasNotifications);
    }

    [Fact]
    public void Dismiss_NonExistentCategory_DoesNotThrow()
    {
        var svc = Create();
        svc.Dismiss(NotificationService.NotificationCategory.Sync);
        Assert.False(svc.HasNotifications);
    }

    [Fact]
    public void DismissAll_ClearsAllNotifications()
    {
        var svc = Create();
        svc.Add(NotificationService.NotificationCategory.Auth,   "a");
        svc.Add(NotificationService.NotificationCategory.Daemon, "b");
        svc.Add(NotificationService.NotificationCategory.Update, "c");

        svc.DismissAll();

        Assert.Equal(0, svc.Count);
        Assert.False(svc.HasNotifications);
    }

    [Fact]
    public void HighestPriority_ReturnsAuthOverDaemon()
    {
        var svc = Create();
        svc.Add(NotificationService.NotificationCategory.Daemon, "d");
        svc.Add(NotificationService.NotificationCategory.Auth,   "a");

        Assert.Equal(NotificationService.NotificationCategory.Auth, svc.HighestPriority);
    }

    [Fact]
    public void HighestPriority_ReturnsDaemonOverSync()
    {
        var svc = Create();
        svc.Add(NotificationService.NotificationCategory.Sync,   "s");
        svc.Add(NotificationService.NotificationCategory.Daemon, "d");

        Assert.Equal(NotificationService.NotificationCategory.Daemon, svc.HighestPriority);
    }

    [Fact]
    public void HighestPriority_NullWhenEmpty()
    {
        var svc = Create();
        Assert.Null(svc.HighestPriority);
    }

    [Fact]
    public void GetAll_OrderedByPriority()
    {
        var svc = Create();
        svc.Add(NotificationService.NotificationCategory.Update, "update");
        svc.Add(NotificationService.NotificationCategory.Auth,   "auth");
        svc.Add(NotificationService.NotificationCategory.Sync,   "sync");

        var all = svc.GetAll();

        Assert.Equal(NotificationService.NotificationCategory.Auth,   all[0].Category);
        Assert.Equal(NotificationService.NotificationCategory.Sync,   all[1].Category);
        Assert.Equal(NotificationService.NotificationCategory.Update, all[2].Category);
    }

    [Fact]
    public void NotificationsChanged_FiredOnAdd()
    {
        var svc   = Create();
        var fired = 0;
        svc.NotificationsChanged += (_, _) => fired++;

        svc.Add(NotificationService.NotificationCategory.Sync, "s");

        Assert.Equal(1, fired);
    }

    [Fact]
    public void NotificationsChanged_FiredOnDismiss()
    {
        var svc = Create();
        svc.Add(NotificationService.NotificationCategory.Sync, "s");
        var fired = 0;
        svc.NotificationsChanged += (_, _) => fired++;

        svc.Dismiss(NotificationService.NotificationCategory.Sync);

        Assert.Equal(1, fired);
    }

    [Fact]
    public void NotificationsChanged_NotFiredWhenDismissingNonExistent()
    {
        var svc   = Create();
        var fired = 0;
        svc.NotificationsChanged += (_, _) => fired++;

        svc.Dismiss(NotificationService.NotificationCategory.Auth);

        Assert.Equal(0, fired);
    }

    [Fact]
    public void Action_StoredAndRetrievable()
    {
        var svc     = Create();
        var called  = false;
        svc.Add(NotificationService.NotificationCategory.Auth, "test", () => called = true);

        svc.GetAll()[0].Action?.Invoke();

        Assert.True(called);
    }
}
