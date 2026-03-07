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
    private NativeMenuItem?     _statusItem;
    private NativeMenuItem?     _signInItem;
    private NativeMenuItem?     _licenseItem;
    private NativeMenuItem?     _importItem;
    private NativeMenuItem?     _mcpItem;
    private NativeMenuItem?     _daemonControlItem;
    private DispatcherTimer?    _statusTimer;
    private DaemonSupervisor?   _supervisor;
    private IClassicDesktopStyleApplicationLifetime? _desktop;

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

            _importItem = new NativeMenuItem("Import Conversations...");
            _importItem.Click += OnImportConversationsClicked;
            menu.Items.Add(_importItem);

            _mcpItem = new NativeMenuItem("Configure MCP Servers...");
            _mcpItem.Click += OnConfigureMcpClicked;
            menu.Items.Add(_mcpItem);

            menu.Items.Add(new NativeMenuItemSeparator());

            _daemonControlItem = new NativeMenuItem("Stop Daemon");
            _daemonControlItem.Click += OnDaemonControlClicked;
            menu.Items.Add(_daemonControlItem);

            var quitItem = new NativeMenuItem("Quit Tray");
            quitItem.Click += OnQuitClicked;
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

        // Wire DaemonSupervisor — subscribe to status changes and start supervision
        _supervisor = services.GetRequiredService<DaemonSupervisor>();
        _supervisor.StatusChanged += status =>
            Dispatcher.UIThread.InvokeAsync(() =>
            {
                UpdateDaemonMenuItem(status);
                _ = UpdateStatusAsync();
            });

        _supervisor.Start();

        Dispatcher.UIThread.InvokeAsync(() => UpdateStatusAsync());
    }

    // ── status update ─────────────────────────────────────────────────────────

    private async Task UpdateStatusAsync(CancellationToken ct = default)
    {
        if (_statusItem is null || _services is null) return;

        // Supervisor process state takes priority over auth state
        if (_supervisor is not null)
        {
            switch (_supervisor.Status)
            {
                case DaemonStatus.Starting:
                    _statusItem.Header      = "↻ Starting daemon...";
                    _signInItem!.IsVisible  = false;
                    _licenseItem!.IsVisible = false;
                    return;

                case DaemonStatus.Restarting:
                    _statusItem.Header      = $"↻ Restarting daemon (attempt {_supervisor.RetryAttempt}/{DaemonSupervisor.MaxRetries})...";
                    _signInItem!.IsVisible  = false;
                    _licenseItem!.IsVisible = false;
                    return;

                case DaemonStatus.Crashed:
                    _statusItem.Header      = "✕ Daemon crashed — click Restart";
                    _signInItem!.IsVisible  = false;
                    _licenseItem!.IsVisible = false;
                    return;

                case DaemonStatus.Stopped:
                    _statusItem.Header      = "■ Daemon stopped";
                    _signInItem!.IsVisible  = false;
                    _licenseItem!.IsVisible = false;
                    return;
            }
        }

        // Daemon is Running (or supervisor unknown) — check auth state
        SesAuthState? authState = null;
        try
        {
            var auth = _services.GetRequiredService<IAuthService>();
            authState = await auth.GetStateAsync(ct);

            if (authState.IsAuthenticated)
            {
                _statusItem.Header      = "● Connected";
                _signInItem!.IsVisible  = false;
                _licenseItem!.IsVisible = false;
            }
            else if (authState.LicenseValid)
            {
                _statusItem.Header      = "● Licensed (Tier 1)";
                _signInItem!.IsVisible  = false;
                _licenseItem!.IsVisible = false;
            }
            else if (authState.LoginTimedOut)
            {
                _statusItem.Header      = "⚠ Login timed out — click to retry";
                _signInItem!.Header     = "Sign In Again...";
                _signInItem!.IsVisible  = true;
                _licenseItem!.IsVisible = true;
            }
            else if (authState.NeedsReauth)
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

        // Forward pre-fetched state to panel — avoids a second daemon round-trip
        if (authState is not null && _dropdownPanel is { IsVisible: true } panel)
            panel.RefreshStatus(authState);
    }

    // ── daemon control menu item ──────────────────────────────────────────────

    private void UpdateDaemonMenuItem(DaemonStatus status)
    {
        if (_daemonControlItem is null) return;

        (_daemonControlItem.Header, _daemonControlItem.IsEnabled) = status switch
        {
            DaemonStatus.Running    => ("Stop Daemon", true),
            DaemonStatus.Crashed    => ("Restart Daemon", true),
            DaemonStatus.Stopped    => ("Start Daemon", true),
            DaemonStatus.Starting   => ("Starting Daemon...", false),
            DaemonStatus.Restarting => ($"↻ Restarting ({_supervisor!.RetryAttempt}/{DaemonSupervisor.MaxRetries})...", false),
            _                       => ("Stop Daemon", true),
        };
    }

    private async void OnDaemonControlClicked(object? sender, EventArgs e)
    {
        if (_supervisor is null) return;

        switch (_supervisor.Status)
        {
            case DaemonStatus.Running:
            case DaemonStatus.Starting:
                await _supervisor.StopAsync();
                break;

            case DaemonStatus.Crashed:
            case DaemonStatus.Stopped:
                await _supervisor.RestartAsync();
                break;
        }
    }

    // ── quit ─────────────────────────────────────────────────────────────────

    private async void OnQuitClicked(object? sender, EventArgs e)
    {
        if (_supervisor is not null)
            await _supervisor.StopAsync();

        _desktop?.Shutdown();
    }

    // ── sign-in / license ─────────────────────────────────────────────────────

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
            Dispatcher.UIThread.InvokeAsync(() => UpdateStatusAsync());
        };
        _licenseWindow.Show();
    }

    // ── import conversations ──────────────────────────────────────────────────

    private async void OnImportConversationsClicked(object? sender, EventArgs e)
    {
        if (_services is null || _importItem is null) return;

        var proxy = _services.GetRequiredService<DaemonAuthProxy>();

        // Show file picker on the UI thread via a temporary host window
        var filePath = await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            // Avalonia 11 requires a TopLevel to access StorageProvider
            var owner = new Window
            {
                ShowInTaskbar      = false,
                ShowActivated      = false,
                SystemDecorations  = SystemDecorations.None,
                WindowState        = WindowState.Normal,
                Width              = 1,
                Height             = 1,
                Opacity            = 0
            };
            owner.Show();

            var topLevel = TopLevel.GetTopLevel(owner);
            if (topLevel is null) { owner.Close(); return null; }

            var files = await topLevel.StorageProvider.OpenFilePickerAsync(
                new Avalonia.Platform.Storage.FilePickerOpenOptions
                {
                    Title         = "Select AI Conversation Export",
                    AllowMultiple = false,
                    FileTypeFilter =
                    [
                        new Avalonia.Platform.Storage.FilePickerFileType("Conversation export files")
                        {
                            Patterns = ["*.json", "*.zip"]
                        },
                        new Avalonia.Platform.Storage.FilePickerFileType("JSON files")
                        {
                            Patterns = ["*.json"]
                        },
                        new Avalonia.Platform.Storage.FilePickerFileType("ZIP archives")
                        {
                            Patterns = ["*.zip"]
                        }
                    ]
                });

            owner.Close();
            return files.Count > 0 ? files[0].Path.LocalPath : null;
        });

        if (filePath is null) return;

        // Show format-aware progress label
        var formatLabel = GuessFormatLabel(filePath);
        _importItem.Header    = $"Importing {formatLabel} conversations...";
        _importItem.IsEnabled = false;

        try
        {
            var started = await proxy.StartImportAsync(filePath);

            _importItem.Header = started
                ? "Importing... (check Import tab for progress)"
                : "✕ Import failed (daemon unavailable or import already running)";

            // Reset header after a few seconds
            _ = Task.Delay(TimeSpan.FromSeconds(6)).ContinueWith(_ =>
                Dispatcher.UIThread.InvokeAsync(() =>
                {
                    _importItem.Header    = "Import Conversations...";
                    _importItem.IsEnabled = true;
                }));
        }
        catch (Exception)
        {
            _importItem.Header    = "Import Conversations...";
            _importItem.IsEnabled = true;
        }
    }

    // ── MCP ──────────────────────────────────────────────────────────────────

    private async void OnConfigureMcpClicked(object? sender, EventArgs e)
    {
        if (_services is null || _mcpItem is null) return;

        var mcp = _services.GetRequiredService<IMcpConfigManager>();

        _mcpItem.Header    = "Configuring MCP Servers...";
        _mcpItem.IsEnabled = false;

        try
        {
            var provisioned = await mcp.ProvisionSesMcpAsync();

            if (provisioned.Count == 0)
            {
                _mcpItem.Header = "✕ No supported MCP hosts detected";
            }
            else
            {
                var names = string.Join(", ", provisioned.Select(h => h.Host switch
                {
                    Core.Models.McpHost.ClaudeDesktop  => "Claude Desktop",
                    Core.Models.McpHost.ClaudeCode     => "Claude Code",
                    Core.Models.McpHost.Cursor         => "Cursor",
                    Core.Models.McpHost.VsCodeContinue => "VS Code/Continue",
                    _                                  => h.Host.ToString()
                }));
                _mcpItem.Header = $"✓ ses-mcp configured for {names}";
            }

            _ = Task.Delay(TimeSpan.FromSeconds(6)).ContinueWith(_ =>
                Dispatcher.UIThread.InvokeAsync(() =>
                {
                    _mcpItem.Header    = "Configure MCP Servers...";
                    _mcpItem.IsEnabled = true;
                }));
        }
        catch (Exception ex)
        {
            _services.GetService<ILogger<TrayApp>>()
                ?.LogWarning(ex, "MCP provisioning failed");
            _mcpItem.Header    = "Configure MCP Servers...";
            _mcpItem.IsEnabled = true;
        }
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
            var vm = new DropdownPanelViewModel(auth, daemonProxy, opts, importWizard: importWizard);
            _dropdownPanel = new DropdownPanel(vm);
            _dropdownPanel.Closed += (_, _) => { vm.Dispose(); _dropdownPanel = null; };
        }

        _dropdownPanel.Show();
        _dropdownPanel.Activate();
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns a human-readable format label based on a quick peek at the file name/extension.
    /// Used for the "Importing {format} conversations..." progress label only — the daemon
    /// performs authoritative content-based detection.
    /// </summary>
    private static string GuessFormatLabel(string filePath)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        if (ext == ".zip")
        {
            try
            {
                using var zip = System.IO.Compression.ZipFile.OpenRead(filePath);
                if (zip.Entries.Any(e =>
                        string.Equals(e.Name, "conversations.json", StringComparison.OrdinalIgnoreCase)))
                    return "ChatGPT";
                if (zip.Entries.Any(e =>
                        string.Equals(e.Name, "My Activity.json", StringComparison.OrdinalIgnoreCase)))
                    return "Gemini";
            }
            catch { /* ignore — daemon will report real format */ }
            return "AI";
        }
        return "Claude";
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
