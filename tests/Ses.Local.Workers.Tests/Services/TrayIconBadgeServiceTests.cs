using Avalonia.Media;
using Ses.Local.Tray.Services;
using Xunit;

namespace Ses.Local.Workers.Tests.Services;

public sealed class TrayIconBadgeServiceTests
{
    [Theory]
    [InlineData(NotificationService.NotificationCategory.Auth,   "Red")]
    [InlineData(NotificationService.NotificationCategory.Daemon, "Red")]
    [InlineData(NotificationService.NotificationCategory.Sync,   "Orange")]
    [InlineData(NotificationService.NotificationCategory.Update, "DodgerBlue")]
    public void GetDotColor_MapsCategory_ToExpectedColor(
        NotificationService.NotificationCategory category, string expectedColorName)
    {
        var color    = TrayIconBadgeService.GetDotColor(category);
        var expected = (Color)typeof(Colors).GetProperty(expectedColorName)!.GetValue(null)!;

        Assert.Equal(expected, color);
    }

    [Fact]
    public void GetDotColor_Null_ReturnsTransparent()
    {
        var color = TrayIconBadgeService.GetDotColor(null);
        Assert.Equal(Colors.Transparent, color);
    }

    [Fact]
    public void GetDotColor_AuthAndDaemon_BothReturnRed()
    {
        Assert.Equal(
            TrayIconBadgeService.GetDotColor(NotificationService.NotificationCategory.Auth),
            TrayIconBadgeService.GetDotColor(NotificationService.NotificationCategory.Daemon));
    }
}
