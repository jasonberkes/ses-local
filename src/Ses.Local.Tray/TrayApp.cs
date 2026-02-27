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
                Icon  = new WindowIcon(GetTrayIconStream()),
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

    private static Stream GetTrayIconStream()
    {
        // Embedded 16x16 PNG resource — fallback to a generated icon if resource missing
        var assembly = typeof(TrayApp).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("tray-icon.png"));

        if (resourceName is not null)
            return assembly.GetManifestResourceStream(resourceName)!;

        // Generate a minimal 1x1 PNG as fallback (transparent)
        return new MemoryStream([
            0x89,0x50,0x4E,0x47,0x0D,0x0A,0x1A,0x0A, // PNG signature
            0x00,0x00,0x00,0x0D,0x49,0x48,0x44,0x52, // IHDR chunk
            0x00,0x00,0x00,0x01,0x00,0x00,0x00,0x01,
            0x08,0x06,0x00,0x00,0x00,0x1F,0x15,0xC4,
            0x89,0x00,0x00,0x00,0x0B,0x49,0x44,0x41, // IDAT chunk
            0x54,0x08,0xD7,0x63,0x60,0x00,0x00,0x00,
            0x02,0x00,0x01,0xE2,0x21,0xBC,0x33,0x00,
            0x00,0x00,0x00,0x49,0x45,0x4E,0x44,0xAE, // IEND chunk
            0x42,0x60,0x82
        ]);
    }
}
