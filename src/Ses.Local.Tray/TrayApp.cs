using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Ses.Local.Core.Interfaces;
using Ses.Local.Tray.Converters;
using Ses.Local.Tray.ViewModels;
using Ses.Local.Tray.Views;

namespace Ses.Local.Tray;

public partial class TrayApp : Application
{
    private MainWindow?     _mainWindow;
    private IServiceProvider? _services;
    private NativeMenuItem? _statusItem;
    private DispatcherTimer? _statusTimer;

    public static readonly DotColorConverter DotColorConverterInstance = new();

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = null;

            var trayIcon = new TrayIcon
            {
                Icon        = TryGetTrayIcon(),
                ToolTipText = "ses-local"
            };
            trayIcon.Clicked += OnTrayIconClicked;

            var menu = new NativeMenu();

            // Status line (non-interactive, updated periodically)
            _statusItem = new NativeMenuItem("● Connecting...") { IsEnabled = false };
            menu.Items.Add(_statusItem);
            menu.Items.Add(new NativeMenuItemSeparator());

            var openItem = new NativeMenuItem("Open ses-local");
            openItem.Click += (_, _) => ShowWindow();
            menu.Items.Add(openItem);

            menu.Items.Add(new NativeMenuItemSeparator());

            var quitItem = new NativeMenuItem("Quit");
            quitItem.Click += (_, _) => desktop.Shutdown();
            menu.Items.Add(quitItem);

            trayIcon.Menu = menu;
            SetValue(TrayIcon.IconsProperty, new TrayIcons { trayIcon });

            // Poll status every 5 seconds
            _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            _statusTimer.Tick += async (_, _) => await UpdateStatusAsync();
            _statusTimer.Start();
        }

        base.OnFrameworkInitializationCompleted();
    }

    public void SetServiceProvider(IServiceProvider services)
    {
        _services = services;
        // Update immediately once services are available
        Dispatcher.UIThread.InvokeAsync(UpdateStatusAsync);
    }

    private async Task UpdateStatusAsync()
    {
        if (_statusItem is null || _services is null) return;
        try
        {
            var auth  = _services.GetRequiredService<IAuthService>();
            var state = await auth.GetStateAsync();
            _statusItem.Header = state.IsAuthenticated
                ? "● Connected"
                : state.NeedsReauth
                    ? "⚠ Sign in required"
                    : "○ Not connected";
        }
        catch
        {
            _statusItem.Header = "○ Not connected";
        }
    }

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
            var assembly     = typeof(TrayApp).Assembly;
            var resourceName = assembly.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("tray-icon.png"));
            if (resourceName is not null)
            {
                using var stream = assembly.GetManifestResourceStream(resourceName)!;
                return new WindowIcon(stream);
            }
        }
        catch { /* run without icon */ }
        return null;
    }
}
