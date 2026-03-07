using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Ses.Local.Core.Interfaces;
using Ses.Local.Core.Models;
using Ses.Local.Tray.Services;
using Ses.Local.Tray.Views;

namespace Ses.Local.Tray;

public partial class TrayApp : Application
{
    private LicenseWindow?      _licenseWindow;
    private IServiceProvider?   _services;
    private DispatcherTimer?    _statusTimer;
    private DaemonSupervisor?   _supervisor;
    private IClassicDesktopStyleApplicationLifetime? _desktop;
    private TrayIcon?            _trayIcon;
    private NotificationService  _notifications = new();
    private TrayIconBadgeService? _badgeService;
    private readonly ClaudeCodeSettingsService _ccSettings = new();

    /// <summary>
    /// Set by Program.Main before StartWithClassicDesktopLifetime.
    /// Read by OnFrameworkInitializationCompleted to wire up services.
    /// app.Instance is null until the event loop starts, so we can't call
    /// SetServiceProvider from Main directly.
    /// </summary>
    internal static IServiceProvider? PendingServices { get; set; }

    // Last known auth state — cached by UpdateMenuItemsAsync, read by click handlers
    private SesAuthState? _lastAuthState;

    // Dynamic menu items — updated every 5 s by _statusTimer
    private NativeMenuItem _statusItem           = null!;
    private NativeMenuItem _uptimeItem           = null!;
    private NativeMenuItem _claudeDesktopItem    = null!;
    private NativeMenuItem _claudeCodeItem       = null!;
    private NativeMenuItem _claudeAiItem         = null!;
    private NativeMenuItem _chatGptItem          = null!;
    private NativeMenuItem _modelItem            = null!;
    private NativeMenuItem _hooksItem            = null!;
    private NativeMenuItem _mcpCountItem         = null!;
    private NativeMenuItem _lastImportItem       = null!;
    private NativeMenuItem _signInItem           = null!;
    private NativeMenuItem _daemonComponentItem  = null!;
    private NativeMenuItem _sesMcpComponentItem  = null!;
    private NativeMenuItem _sesHooksComponentItem = null!;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _desktop = desktop;
            desktop.MainWindow = null;
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            _badgeService = TryCreateBadgeService();
            _trayIcon = new TrayIcon
            {
                Icon        = _badgeService?.GetDefaultIcon(),
                ToolTipText = "ses-local",
                Menu        = BuildMenu()
            };

            SetValue(TrayIcon.IconsProperty, new TrayIcons { _trayIcon });

            _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            _statusTimer.Tick += async (_, _) => await UpdateStatusAsync();
            _statusTimer.Start();

            // Wire up services now that the Application instance exists
            if (PendingServices is not null)
            {
                SetServiceProvider(PendingServices);
                PendingServices = null;
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    public void SetServiceProvider(IServiceProvider services)
    {
        _services = services;

        _supervisor = services.GetRequiredService<DaemonSupervisor>();
        _supervisor.StatusChanged += status =>
        {
            if (status == DaemonStatus.Running) return;
            Dispatcher.UIThread.InvokeAsync(async () => await UpdateStatusAsync());
        };

        _supervisor.Start();
        Dispatcher.UIThread.InvokeAsync(() => UpdateStatusAsync());
    }

    // ── menu construction ─────────────────────────────────────────────────────

    private NativeMenu BuildMenu()
    {
        var menu = new NativeMenu();

        // Status section
        _statusItem = new NativeMenuItem("○ Not connected") { IsEnabled = false };
        _uptimeItem = new NativeMenuItem("") { IsEnabled = false };
        menu.Items.Add(_statusItem);
        menu.Items.Add(_uptimeItem);
        menu.Items.Add(new NativeMenuItemSeparator());

        // Sync stats
        _claudeDesktopItem = new NativeMenuItem("Claude Desktop   —") { IsEnabled = false };
        _claudeCodeItem    = new NativeMenuItem("Claude Code      —") { IsEnabled = false };
        _claudeAiItem      = new NativeMenuItem("Claude.ai        —") { IsEnabled = false };
        _chatGptItem       = new NativeMenuItem("ChatGPT          Not synced") { IsEnabled = false };
        menu.Items.Add(_claudeDesktopItem);
        menu.Items.Add(_claudeCodeItem);
        menu.Items.Add(_claudeAiItem);
        menu.Items.Add(_chatGptItem);
        menu.Items.Add(new NativeMenuItemSeparator());

        // CC Config submenu
        menu.Items.Add(BuildCcConfigMenu());
        menu.Items.Add(new NativeMenuItemSeparator());

        // Import
        var importItem = new NativeMenuItem("Import Conversations...");
        importItem.Click += (_, _) => _ = OnImportConversationsClickedAsync();
        menu.Items.Add(importItem);
        _lastImportItem = new NativeMenuItem("") { IsEnabled = false };
        menu.Items.Add(_lastImportItem);
        menu.Items.Add(new NativeMenuItemSeparator());

        // Components submenu
        menu.Items.Add(BuildComponentsMenu());
        menu.Items.Add(new NativeMenuItemSeparator());

        // Actions
        _signInItem = new NativeMenuItem("Sign In...");
        _signInItem.Click += (_, _) => _ = OnSignInOrOutClickedAsync();
        menu.Items.Add(_signInItem);

        var stopItem = new NativeMenuItem("Stop Daemon");
        stopItem.Click += (_, _) => _ = OnStopDaemonClicked();
        menu.Items.Add(stopItem);

        var quitItem = new NativeMenuItem("Quit");
        quitItem.Click += (_, _) => _ = OnQuitClicked();
        menu.Items.Add(quitItem);

        return menu;
    }

    private NativeMenuItem BuildCcConfigMenu()
    {
        var ccMenu = new NativeMenu();

        _modelItem = new NativeMenuItem("Model: default") { IsEnabled = false };
        ccMenu.Items.Add(_modelItem);

        var changeModelMenu = new NativeMenu();
        foreach (var model in ClaudeCodeSettingsService.CommonModels)
        {
            var captured = model;
            var modelMenuItem = new NativeMenuItem(model);
            modelMenuItem.Click += (_, _) => OnChangeModelClicked(captured);
            changeModelMenu.Items.Add(modelMenuItem);
        }
        ccMenu.Items.Add(new NativeMenuItem("Change Model") { Menu = changeModelMenu });
        ccMenu.Items.Add(new NativeMenuItemSeparator());

        _mcpCountItem = new NativeMenuItem("MCP Servers") { IsEnabled = false };
        ccMenu.Items.Add(_mcpCountItem);

        var manageMcpItem = new NativeMenuItem("Edit MCP Servers in settings.json...");
        manageMcpItem.Click += (_, _) => OnOpenSettingsClicked();
        ccMenu.Items.Add(manageMcpItem);
        ccMenu.Items.Add(new NativeMenuItemSeparator());

        _hooksItem = new NativeMenuItem("Hooks: Loading...") { IsEnabled = false };
        ccMenu.Items.Add(_hooksItem);

        var toggleHooksItem = new NativeMenuItem("Toggle Hooks");
        toggleHooksItem.Click += (_, _) => _ = OnToggleHooksClicked();
        ccMenu.Items.Add(toggleHooksItem);
        ccMenu.Items.Add(new NativeMenuItemSeparator());

        var openSettingsItem = new NativeMenuItem("Open settings.json");
        openSettingsItem.Click += (_, _) => OnOpenSettingsClicked();
        ccMenu.Items.Add(openSettingsItem);

        return new NativeMenuItem("CC Config") { Menu = ccMenu };
    }

    private NativeMenuItem BuildComponentsMenu()
    {
        var compMenu = new NativeMenu();

        _daemonComponentItem   = new NativeMenuItem("⚪ Daemon     Loading...") { IsEnabled = false };
        _sesMcpComponentItem   = new NativeMenuItem("⚪ ses-mcp    Loading...") { IsEnabled = false };
        _sesHooksComponentItem = new NativeMenuItem("⚪ ses-hooks  Loading...") { IsEnabled = false };
        compMenu.Items.Add(_daemonComponentItem);
        compMenu.Items.Add(_sesMcpComponentItem);
        compMenu.Items.Add(_sesHooksComponentItem);
        compMenu.Items.Add(new NativeMenuItemSeparator());

        var checkUpdatesItem = new NativeMenuItem("Check for Updates");
        checkUpdatesItem.Click += (_, _) => _ = OnCheckForUpdatesClicked();
        compMenu.Items.Add(checkUpdatesItem);

        return new NativeMenuItem("Components") { Menu = compMenu };
    }

    // ── status update ─────────────────────────────────────────────────────────

    private async Task UpdateStatusAsync(CancellationToken ct = default)
    {
        if (_services is null) return;

        SesAuthState? authState = null;
        try
        {
            var auth = _services.GetRequiredService<IAuthService>();
            authState = await auth.GetStateAsync(ct);

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

        }
        catch
        {
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
        await UpdateMenuItemsAsync(authState, ct);
    }

    private async Task UpdateMenuItemsAsync(SesAuthState? authState, CancellationToken ct)
    {
        // Auth-dependent items
        _lastAuthState        = authState;
        _signInItem.IsEnabled = true;
        if (authState is not null)
        {
            _statusItem.Header = authState.IsAuthenticated                           ? "🟢 Connected"
                               : authState.LicenseValid                              ? "🟢 Licensed (Tier 1)"
                               : authState.LoginTimedOut || authState.NeedsReauth    ? "🟡 Session expired"
                               :                                                       "🔴 Not activated";
            _signInItem.Header = authState.IsAuthenticated || authState.LicenseValid
                ? "Sign Out"
                : "Sign In...";
        }
        else
        {
            _statusItem.Header = "🔴 Not connected";
        }

        if (_services is null) return;

        var proxy = _services.GetRequiredService<DaemonAuthProxy>();
        var uptime = proxy.LastKnownUptime;
        _uptimeItem.Header = string.IsNullOrEmpty(uptime) ? "" : $"Uptime: {FormatUptime(uptime)}";

        // Local CC settings (fast, no IPC)
        try
        {
            var info = _ccSettings.ReadSettings();
            _modelItem.Header    = $"Model: {info.ModelName}";
            var activeCount      = info.McpServers.Count(s => !s.IsDisabled);
            _mcpCountItem.Header = $"MCP Servers ({activeCount} active)";
            _hooksItem.Header    = info.RegisteredHooks.Count > 0 ? "Hooks: Active" : "Hooks: Disabled";
        }
        catch { /* non-fatal */ }

        // Parallel IPC calls — individual faults are tolerated: partial updates beat total freeze
        var syncStatsTask     = proxy.GetSyncStatsAsync(ct);
        var componentsTask    = proxy.GetComponentsAsync(ct);
        var importHistoryTask = proxy.GetImportHistoryAsync(ct);

        try { await Task.WhenAll(syncStatsTask, componentsTask, importHistoryTask); }
        catch { /* check IsCompletedSuccessfully below */ }

        // Sync stats
        var stats = syncStatsTask.IsCompletedSuccessfully ? syncStatsTask.Result : null;
        if (stats is not null)
        {
            // ClaudeChat covers both Claude Desktop (LevelDB) and Claude.ai (browser extension)
            _claudeDesktopItem.Header = stats.ClaudeChat.Count > 0
                ? $"🟢 Claude Desktop   {FormatSyncCount(stats.ClaudeChat.Count, "conv")}"
                : "⚪ Claude Desktop   —";
            _claudeCodeItem.Header = stats.ClaudeCode.Count > 0
                ? $"🟢 Claude Code      {FormatSyncCount(stats.ClaudeCode.Count, "session")}"
                : "⚪ Claude Code      —";
            _claudeAiItem.Header = stats.ClaudeChat.Count > 0
                ? $"🟢 Claude.ai        {FormatSyncCount(stats.ClaudeChat.Count, "conv")}"
                : "⚪ Claude.ai        —";
            _chatGptItem.Header = stats.ChatGpt.Count > 0
                ? $"🟢 ChatGPT          {FormatSyncCount(stats.ChatGpt.Count, "conv")}"
                : "⚪ ChatGPT          —";
        }

        // Component status
        var components = componentsTask.IsCompletedSuccessfully ? componentsTask.Result : null;
        if (components is not null)
        {
            _daemonComponentItem.Header   = $"🟢 Daemon     {FormatVersion(components.Daemon.Version)} Running";
            _sesMcpComponentItem.Header   = components.SesMcp.Installed
                ? $"🟢 ses-mcp    {FormatVersion(components.SesMcp.Version)} Installed"
                : "🔴 ses-mcp    Not installed";
            _sesHooksComponentItem.Header = components.SesHooks.Installed
                ? $"🟢 ses-hooks  Registered"
                : "⚪ ses-hooks  Not found";
        }

        // Import history
        var history = importHistoryTask.IsCompletedSuccessfully ? importHistoryTask.Result : null;
        if (history?.Count > 0)
            _lastImportItem.Header = $"Last import: {FormatAge(history[0].ImportedAt)}";
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
            Dispatcher.UIThread.InvokeAsync(() => UpdateStatusAsync());
        };
        _licenseWindow.Show();
    }

    // ── menu action handlers ──────────────────────────────────────────────────

    private async Task OnSignInOrOutClickedAsync()
    {
        if (_services is null) return;
        var auth = _services.GetRequiredService<IAuthService>();
        if (_lastAuthState is { } s && (s.IsAuthenticated || s.LicenseValid))
        {
            await auth.SignOutAsync();
            await UpdateStatusAsync();
        }
        else
        {
            await auth.TriggerReauthAsync();
        }
    }

    private async Task OnStopDaemonClicked()
    {
        if (_supervisor is not null)
            await _supervisor.StopAsync();
    }

    private async Task OnQuitClicked()
    {
        if (_supervisor is not null)
            await _supervisor.StopAsync();
        _desktop?.Shutdown();
    }

    private void OnChangeModelClicked(string model)
    {
        try
        {
            _ccSettings.WriteModelName(model);
            _modelItem.Header = $"Model: {model}";
        }
        catch { /* non-fatal */ }
    }

    private void OnOpenSettingsClicked()
    {
        var path = _ccSettings.SettingsFilePath;
        if (!File.Exists(path)) return;
        OpenFileInDefaultEditor(path);
    }

    private static void OpenFileInDefaultEditor(string path) => OsOpen.Launch(path);

    private async Task OnToggleHooksClicked()
    {
        try
        {
            var info = _ccSettings.ReadSettings();
            if (info.RegisteredHooks.Count > 0)
            {
                _ccSettings.DisableHooks();
                _hooksItem.Header = "Hooks: Disabled";
            }
            else
            {
                var restored = _ccSettings.EnableHooks();
                if (!restored && _services is not null)
                {
                    var proxy = _services.GetRequiredService<DaemonAuthProxy>();
                    await proxy.EnableHooksAsync();
                }
                _hooksItem.Header = "Hooks: Active";
            }
        }
        catch { /* non-fatal */ }
    }

    private async Task OnImportConversationsClickedAsync()
    {
        string? filePath = null;
        try
        {
            filePath = await Dispatcher.UIThread.InvokeAsync(PickImportFileAsync);
        }
        catch { /* file picker dismissed or unavailable */ }

        if (filePath is null || _services is null) return;

        var proxy = _services.GetRequiredService<DaemonAuthProxy>();
        _lastImportItem.Header = "Importing...";
        try
        {
            var started = await proxy.StartImportAsync(filePath);
            _lastImportItem.Header = started ? "Import started" : "Import failed to start";
        }
        catch
        {
            _lastImportItem.Header = "Import error";
        }
    }

    private static async Task<string?> PickImportFileAsync()
    {
        // Invisible helper window provides a TopLevel for the native file picker
        var helperWindow = new Window
        {
            IsVisible         = false,
            Width             = 0,
            Height            = 0,
            ShowInTaskbar     = false,
            SystemDecorations = SystemDecorations.None,
            Position          = new PixelPoint(-32000, -32000)
        };
        helperWindow.Show();

        try
        {
            var topLevel = TopLevel.GetTopLevel(helperWindow);
            if (topLevel is null) return null;

            var filter = new FilePickerFileType("Export files") { Patterns = ["*.zip", "*.json"] };
            var files = await topLevel.StorageProvider.OpenFilePickerAsync(
                new FilePickerOpenOptions
                {
                    Title          = "Select AI Conversation Export",
                    AllowMultiple  = false,
                    FileTypeFilter = [filter]
                });

            return files.Count > 0 ? files[0].Path.LocalPath : null;
        }
        finally
        {
            helperWindow.Close();
        }
    }

    private async Task OnCheckForUpdatesClicked()
    {
        if (_services is null) return;
        var proxy = _services.GetRequiredService<DaemonAuthProxy>();
        try
        {
            var updates = await proxy.CheckUpdatesAsync();
            if (updates is null) return;

            foreach (var u in updates)
            {
                var info = u.UpdateAvailable && u.LatestVersion is not null
                    ? $"Update available: v{u.LatestVersion}"
                    : "Up to date";

                if (u.Name.Contains("daemon", StringComparison.OrdinalIgnoreCase))
                    _daemonComponentItem.Header = $"🟢 Daemon     {info}";
                else if (u.Name.Contains("ses-mcp", StringComparison.OrdinalIgnoreCase))
                    _sesMcpComponentItem.Header = $"🟢 ses-mcp    {info}";
                else if (u.Name.Contains("ses-hooks", StringComparison.OrdinalIgnoreCase))
                    _sesHooksComponentItem.Header = $"🟢 ses-hooks  {info}";
            }
        }
        catch { /* non-fatal */ }
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static string FormatUptime(string raw)
    {
        if (TimeSpan.TryParse(raw, out var ts))
        {
            if (ts.TotalMinutes < 1) return "just started";
            if (ts.TotalHours   < 1) return $"{(int)ts.TotalMinutes}m";
            if (ts.TotalDays    < 1) return $"{(int)ts.TotalHours}h {ts.Minutes}m";
            return $"{(int)ts.TotalDays}d {ts.Hours}h";
        }
        return raw;
    }

    private static string FormatSyncCount(int count, string unit) =>
        count == 0 ? "—" : $"{count:N0} {unit}{(count == 1 ? "" : "s")}";

    private static string FormatVersion(string? version) =>
        version is null ? "" : $"v{version} ";

    private static string FormatAge(DateTime dt)
    {
        var age = DateTime.UtcNow - dt.ToUniversalTime();
        return age.TotalSeconds < 60   ? "just now"
             : age.TotalMinutes < 60   ? $"{(int)age.TotalMinutes} min ago"
             : age.TotalHours   < 24   ? $"{(int)age.TotalHours}h ago"
             : age.TotalDays    < 7    ? $"{(int)age.TotalDays}d ago"
             :                           dt.ToLocalTime().ToString("MMM d");
    }

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
