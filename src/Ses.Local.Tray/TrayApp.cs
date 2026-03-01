using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using Ses.Local.Core.Interfaces;
using Ses.Local.Tray.Converters;
using Ses.Local.Tray.ViewModels;
using Ses.Local.Tray.Views;

namespace Ses.Local.Tray;

public partial class TrayApp : Application
{
    private MainWindow? _mainWindow;
    private IServiceProvider? _services;

    public static readonly DotColorConverter DotColorConverterInstance = new();

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Don't show main window on startup — tray icon controls visibility
            desktop.MainWindow = null;

            // Build tray icon
            var trayIcon = new TrayIcon
            {
                Icon        = TryGetTrayIcon(),
                ToolTipText = "ses-local"
            };
            trayIcon.Clicked += OnTrayIconClicked;

            var menu = new NativeMenu();
            var showItem = new NativeMenuItem("Open ses-local");
            showItem.Click += (_, _) => ShowWindow();
            var quitItem = new NativeMenuItem("Quit");
            quitItem.Click += (_, _) => desktop.Shutdown();
            menu.Items.Add(showItem);
            menu.Items.Add(new NativeMenuItemSeparator());
            menu.Items.Add(quitItem);
            trayIcon.Menu = menu;

            var icons = new TrayIcons { trayIcon };
            SetValue(TrayIcon.IconsProperty, icons);
        }

        base.OnFrameworkInitializationCompleted();
    }

    public void SetServiceProvider(IServiceProvider services) => _services = services;

    private void OnTrayIconClicked(object? sender, EventArgs e) => ShowWindow();

    private void ShowWindow()
    {
        if (_mainWindow is null || !_mainWindow.IsVisible)
        {
            var auth = _services?.GetRequiredService<IAuthService>();
            if (auth is null) return;
            var vm = new MainWindowViewModel(auth);
            _mainWindow = new MainWindow(vm);
            _mainWindow.Closed += (_, _) => _mainWindow = null;
            _mainWindow.Show();
        }
        else
        {
            _mainWindow.Activate();
        }
    }

    private static WindowIcon? TryGetTrayIcon()
    {
        try
        {
            var assembly = typeof(TrayApp).Assembly;
            var resourceName = assembly.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("tray-icon.png"));

            if (resourceName is not null)
            {
                using var stream = assembly.GetManifestResourceStream(resourceName)!;
                return new WindowIcon(stream);
            }
        }
        catch { /* icon load failed — run without icon */ }
        return null;
    }
}
