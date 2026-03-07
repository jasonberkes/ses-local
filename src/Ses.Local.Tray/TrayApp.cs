using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Ses.Local.Core.Interfaces;
using Ses.Local.Core.Models;
using Ses.Local.Core.Options;
using Ses.Local.Tray.Converters;
using Ses.Local.Tray.Services;
using Ses.Local.Tray.ViewModels;
using Ses.Local.Tray.Views;
using Microsoft.Extensions.Logging;

namespace Ses.Local.Tray;

public partial class TrayApp : Application
{
    private DropdownPanel?      _dropdownPanel;
    private LicenseWindow?      _licenseWindow;
    private IServiceProvider?   _services;
    private DispatcherTimer?    _statusTimer;
    private DaemonSupervisor?   _supervisor;
    private IClassicDesktopStyleApplicationLifetime? _desktop;
    private TrayIcon?            _trayIcon;
    private NotificationService  _notifications = new();
    private TrayIconBadgeService? _badgeService;

    public static readonly DotColorConverter DotColorConverterInstance = new();

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _desktop = desktop;
            desktop.MainWindow = null;
            // Prevent window-close from triggering app exit (tray-only mode)
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            _badgeService = TryCreateBadgeService();
            _trayIcon = new TrayIcon
            {
                Icon        = _badgeService?.GetDefaultIcon(),
                ToolTipText = "ses-local",
                Menu        = null  // NO menu — any click opens the dropdown panel
            };
            _trayIcon.Clicked += OnTrayIconClicked;

            SetValue(TrayIcon.IconsProperty, new TrayIcons { _trayIcon });

            _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            _statusTimer.Tick += async (_, _) => await UpdateStatusAsync();
            _statusTimer.Start();
        }

        base.OnFrameworkInitializationCompleted();
    }

    public void SetServiceProvider(IServiceProvider services)
    {
        _services = services;

        // Wire DaemonSupervisor — subscribe to status changes and start supervision
        _supervisor = services.GetRequiredService<DaemonSupervisor>();
        _supervisor.StatusChanged += status =>
        {
            // Running is acknowledged via AcknowledgeDaemonRunning() inside UpdateStatusAsync.
            // Skip here to avoid a redundant IPC call immediately after the IPC that just succeeded.
            if (status == DaemonStatus.Running) return;
            Dispatcher.UIThread.InvokeAsync(() => UpdateStatusAsync());
        };

        _supervisor.Start();

        Dispatcher.UIThread.InvokeAsync(() => UpdateStatusAsync());
    }

    // ── status update ─────────────────────────────────────────────────────────

    private async Task UpdateStatusAsync(CancellationToken ct = default)
    {
        if (_services is null) return;

        SesAuthState? authState = null;
        try
        {
            // Try IPC first — daemon may be running even before the supervisor detects it.
            var auth = _services.GetRequiredService<IAuthService>();
            authState = await auth.GetStateAsync(ct);

            // IPC succeeded — tell the supervisor if it didn't know.
            _supervisor?.AcknowledgeDaemonRunning();
            _notifications.Dismiss(NotificationService.NotificationCategory.Daemon);

            if (authState.LoginTimedOut || authState.NeedsReauth)
            {
                _notifications.Add(NotificationService.NotificationCategory.Auth,
                    "Session expired — click to sign in",
                    () => _ = _services.GetRequiredService<IAuthService>().TriggerReauthAsync());
            }
            else
            {
                _notifications.Dismiss(NotificationService.NotificationCategory.Auth);
            }

            // Auto-open license prompt on first run (no license, no OAuth)
            if (!authState.IsAuthenticated && !authState.LicenseValid &&
                !authState.LoginTimedOut && !authState.NeedsReauth && _licenseWindow is null)
            {
                await Dispatcher.UIThread.InvokeAsync(ShowLicenseWindow);
            }
        }
        catch
        {
            // Daemon not reachable via IPC
            _notifications.Dismiss(NotificationService.NotificationCategory.Auth);

            if (_supervisor?.Status == DaemonStatus.Crashed)
            {
                _notifications.Add(NotificationService.NotificationCategory.Daemon,
                    "Daemon crashed — click to restart",
                    () => _ = _supervisor.RestartAsync());
            }
            else
            {
                _notifications.Dismiss(NotificationService.NotificationCategory.Daemon);
            }
        }

        UpdateTrayBadge();

        // Forward pre-fetched state to panel — avoids a second daemon round-trip
        if (authState is not null && _dropdownPanel is { IsVisible: true } panel)
            panel.RefreshStatus(authState);
    }

    private void UpdateTrayBadge()
    {
        if (_trayIcon is null || _badgeService is null) return;
        var priority = _notifications.HighestPriority;
        _trayIcon.Icon = priority is null
            ? _badgeService.GetDefaultIcon()
            : _badgeService.GetBadgedIcon(TrayIconBadgeService.GetDotColor(priority));
    }

    // ── license ───────────────────────────────────────────────────────────────

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
            Dispatcher.UIThread.InvokeAsync(() => UpdateStatusAsync());
        };
        _licenseWindow.Show();
    }

    // ── dropdown panel ────────────────────────────────────────────────────────

    private void OnTrayIconClicked(object? sender, EventArgs e)
    {
        if (_dropdownPanel is { IsVisible: true })
            _dropdownPanel.Hide();
        else
            ShowDropdownPanel();
    }

    private void ShowDropdownPanel()
    {
        if (_services is null) return;

        var auth        = _services.GetRequiredService<IAuthService>();
        var daemonProxy = _services.GetRequiredService<DaemonAuthProxy>();
        var opts        = _services.GetRequiredService<IOptions<SesLocalOptions>>();

        if (_dropdownPanel is null)
        {
            var importWizard = new ImportWizardViewModel(daemonProxy);
            var vm = new DropdownPanelViewModel(
                auth, daemonProxy, opts,
                importWizard: importWizard,
                notifications: _notifications,
                stopDaemon: async () => { if (_supervisor is not null) await _supervisor.StopAsync(); },
                quitApp: async () =>
                {
                    if (_supervisor is not null) await _supervisor.StopAsync();
                    _desktop?.Shutdown();
                });
            _dropdownPanel = new DropdownPanel(vm);
            _dropdownPanel.Closed += (_, _) => { vm.Dispose(); _dropdownPanel = null; };
        }

        _dropdownPanel.Show();
        _dropdownPanel.Activate();
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static TrayIconBadgeService? TryCreateBadgeService()
    {
        try
        {
            var assembly     = typeof(TrayApp).Assembly;
            var resourceName = assembly.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("tray-icon.png"));
            if (resourceName is not null)
            {
                using var stream = assembly.GetManifestResourceStream(resourceName)!;
                return TrayIconBadgeService.TryCreate(stream);
            }
        }
        catch { /* run without badge service */ }
        return null;
    }
}
