using Avalonia;
using Avalonia.Themes.Fluent;

namespace Ses.Local.Tray;

/// <summary>Root Avalonia application. Tray icon and dashboard implemented in WI-937.</summary>
public sealed class TrayApp : Application
{
    public override void Initialize() => Styles.Add(new FluentTheme());
    public override void OnFrameworkInitializationCompleted() => base.OnFrameworkInitializationCompleted();
}
