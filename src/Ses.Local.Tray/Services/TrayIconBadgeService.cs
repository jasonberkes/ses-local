using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace Ses.Local.Tray.Services;

/// <summary>
/// Composites a small colored dot onto the base tray icon to indicate notification priority.
/// Icons are cached per color — rendered once, reused on every poll cycle.
/// </summary>
public sealed class TrayIconBadgeService
{
    private readonly Bitmap                        _baseIcon;
    private readonly Dictionary<Color, WindowIcon> _cache = new();
    private readonly WindowIcon                    _defaultIcon;
    private readonly int                           _width;
    private readonly int                           _height;

    public TrayIconBadgeService(Bitmap baseIcon)
    {
        _baseIcon    = baseIcon;
        _defaultIcon = new WindowIcon(baseIcon);
        _width       = (int)baseIcon.Size.Width;
        _height      = (int)baseIcon.Size.Height;
    }

    public WindowIcon GetBadgedIcon(Color dotColor)
    {
        if (_cache.TryGetValue(dotColor, out var cached))
            return cached;

        var rtb = new RenderTargetBitmap(new PixelSize(_width, _height), new Vector(96, 96));
        using (var ctx = rtb.CreateDrawingContext())
        {
            ctx.DrawImage(_baseIcon, new Rect(0, 0, _width, _height));

            // White halo behind dot for contrast on any icon background
            var center = new Point(_width - 6, 6);
            ctx.DrawEllipse(Brushes.White, null, center, 5, 5);
            ctx.DrawEllipse(new SolidColorBrush(dotColor), null, center, 4, 4);
        }

        using var ms = new MemoryStream();
        rtb.Save(ms);
        ms.Position = 0;

        var icon = new WindowIcon(ms);
        _cache[dotColor] = icon;
        return icon;
    }

    public WindowIcon GetDefaultIcon() => _defaultIcon;

    /// <summary>Maps notification priority to badge dot color. Returns Transparent when no notifications.</summary>
    public static Color GetDotColor(NotificationService.NotificationCategory? priority) => priority switch
    {
        NotificationService.NotificationCategory.Auth   => Colors.Red,
        NotificationService.NotificationCategory.Daemon => Colors.Red,
        NotificationService.NotificationCategory.Sync   => Colors.Orange,
        NotificationService.NotificationCategory.Update => Colors.DodgerBlue,
        _                                               => Colors.Transparent,
    };

    public static TrayIconBadgeService? TryCreate(Stream baseIconStream)
    {
        try
        {
            var bitmap = new Bitmap(baseIconStream);
            return new TrayIconBadgeService(bitmap);
        }
        catch
        {
            return null;
        }
    }
}
