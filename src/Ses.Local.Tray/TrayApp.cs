using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Ses.Local.Core.Interfaces;
using Ses.Local.Tray.Converters;
using Ses.Local.Tray.Services;
using Ses.Local.Tray.ViewModels;
using Ses.Local.Tray.Views;

namespace Ses.Local.Tray;

public partial class TrayApp : Application
{
    private MainWindow?         _mainWindow;
    private LicenseWindow?      _licenseWindow;
    private IServiceProvider?   _services;
    private NativeMenuItem?     _statusItem;
    private NativeMenuItem?     _signInItem;
    private NativeMenuItem?     _licenseItem;
    private DispatcherTimer?    _statusTimer;

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

            _statusItem = new NativeMenuItem("○ Not connected") { IsEnabled = false };
            menu.Items.Add(_statusItem);
            menu.Items.Add(new NativeMenuItemSeparator());

            _signInItem = new NativeMenuItem("Sign In...");
            _signInItem.Click += OnSignInClicked;
            menu.Items.Add(_signInItem);

            _licenseItem = new NativeMenuItem("Enter License Key...");
            _licenseItem.Click += OnLicenseKeyClicked;
            _licenseItem.IsVisible = false;
            menu.Items.Add(_licenseItem);

            var openItem = new NativeMenuItem("Open ses-local");
            openItem.Click += (_, _) => ShowWindow();
            menu.Items.Add(openItem);

            menu.Items.Add(new NativeMenuItemSeparator());

            var stopDaemonItem = new NativeMenuItem("Stop Daemon");
            stopDaemonItem.Click += OnStopDaemonClicked;
            menu.Items.Add(stopDaemonItem);

            var quitItem = new NativeMenuItem("Quit Tray");
            quitItem.Click += (_, _) => desktop.Shutdown();
            menu.Items.Add(quitItem);

            trayIcon.Menu = menu;
            SetValue(TrayIcon.IconsProperty, new TrayIcons { trayIcon });

            _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            _statusTimer.Tick += async (_, _) => await UpdateStatusAsync();
            _statusTimer.Start();
        }

        base.OnFrameworkInitializationCompleted();
    }

    public void SetServiceProvider(IServiceProvider services)
    {
        _services = services;
        Dispatcher.UIThread.InvokeAsync(UpdateStatusAsync);
    }

    private async Task UpdateStatusAsync()
    {
        if (_statusItem is null || _services is null) return;
        try
        {
            var auth  = _services.GetRequiredService<IAuthService>();
            var state = await auth.GetStateAsync();

            if (state.IsAuthenticated)
            {
                _statusItem.Header      = "● Connected";
                _signInItem!.IsVisible  = false;
                _licenseItem!.IsVisible = false;
            }
            else if (state.LicenseValid)
            {
                _statusItem.Header      = "● Licensed (Tier 1)";
                _signInItem!.IsVisible  = false;
                _licenseItem!.IsVisible = false;
            }
            else if (state.NeedsReauth)
            {
                _statusItem.Header      = "⚠ Session expired";
                _signInItem!.Header     = "Sign In Again...";
                _signInItem!.IsVisible  = true;
                _licenseItem!.IsVisible = true;
            }
            else
            {
                _statusItem.Header      = "○ Not activated";
                _signInItem!.Header     = "Sign In...";
                _signInItem!.IsVisible  = true;
                _licenseItem!.IsVisible = true;

                // Auto-open license prompt on first run (no license, no OAuth)
                if (_licenseWindow is null)
                    await Dispatcher.UIThread.InvokeAsync(ShowLicenseWindow);
            }
        }
        catch
        {
            _statusItem.Header      = "✕ Daemon not running";
            _signInItem!.IsVisible  = false;
            _licenseItem!.IsVisible = false;
        }
    }

    private async void OnSignInClicked(object? sender, EventArgs e)
    {
        if (_services is null) return;
        var auth = _services.GetRequiredService<IAuthService>();
        await auth.TriggerReauthAsync();
    }

    private void OnLicenseKeyClicked(object? sender, EventArgs e) => ShowLicenseWindow();

    private void ShowLicenseWindow()
    {
        if (_licenseWindow is { IsVisible: true })
        {
            _licenseWindow.Activate();
            return;
        }

        var proxy = _services?.GetRequiredService<DaemonAuthProxy>();
        if (proxy is null) return;

        _licenseWindow = new LicenseWindow(proxy);
        _licenseWindow.Closed += (_, _) =>
        {
            _licenseWindow = null;
            // Refresh status after window closes (may have activated a license)
            Dispatcher.UIThread.InvokeAsync(UpdateStatusAsync);
        };
        _licenseWindow.Show();
    }

    private async void OnStopDaemonClicked(object? sender, EventArgs e)
    {
        if (_services is null) return;
        var proxy = _services.GetRequiredService<DaemonAuthProxy>();
        await proxy.ShutdownAsync();
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
