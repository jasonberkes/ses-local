using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Ses.Local.Core.Interfaces;
using Ses.Local.Core.Options;
using Ses.Local.Tray.Converters;
using Ses.Local.Tray.Services;
using Ses.Local.Tray.ViewModels;
using Ses.Local.Tray.Views;
using Microsoft.Extensions.Logging;

namespace Ses.Local.Tray;

public partial class TrayApp : Application
{
    private MainWindow?         _mainWindow;
    private LicenseWindow?      _licenseWindow;
    private IServiceProvider?   _services;
    private NativeMenuItem?     _statusItem;
    private NativeMenuItem?     _signInItem;
    private NativeMenuItem?     _licenseItem;
    private NativeMenuItem?     _importItem;
    private NativeMenuItem?     _mcpItem;
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

            _importItem = new NativeMenuItem("Import Conversations...");
            _importItem.Click += OnImportConversationsClicked;
            menu.Items.Add(_importItem);

            _mcpItem = new NativeMenuItem("Configure MCP Servers...");
            _mcpItem.Click += OnConfigureMcpClicked;
            menu.Items.Add(_mcpItem);

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
        Dispatcher.UIThread.InvokeAsync(() => UpdateStatusAsync());
    }

    private async Task UpdateStatusAsync(CancellationToken ct = default)
    {
        if (_statusItem is null || _services is null) return;
        try
        {
            var auth  = _services.GetRequiredService<IAuthService>();
            var state = await auth.GetStateAsync(ct);

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
            Dispatcher.UIThread.InvokeAsync(() => UpdateStatusAsync());
        };
        _licenseWindow.Show();
    }

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
                ShowInTaskbar = false,
                WindowState   = WindowState.Normal,
                Width         = 1,
                Height        = 1,
                Opacity       = 0
            };
            owner.Show();

            var topLevel = TopLevel.GetTopLevel(owner);
            if (topLevel is null) { owner.Close(); return null; }

            var files = await topLevel.StorageProvider.OpenFilePickerAsync(
                new Avalonia.Platform.Storage.FilePickerOpenOptions
                {
                    Title         = "Select Claude Export JSON",
                    AllowMultiple = false,
                    FileTypeFilter =
                    [
                        new Avalonia.Platform.Storage.FilePickerFileType("JSON files")
                        {
                            Patterns = ["*.json"]
                        }
                    ]
                });

            owner.Close();
            return files.Count > 0 ? files[0].Path.LocalPath : null;
        });

        if (filePath is null) return;

        // Update menu item to show progress
        _importItem.Header   = "Importing...";
        _importItem.IsEnabled = false;

        try
        {
            var result = await proxy.ImportConversationsAsync(filePath);

            _importItem.Header = result is not null
                ? $"✓ Imported {result.SessionsImported} conversations"
                : "✕ Import failed (daemon unavailable)";

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
            var opts = _services!.GetRequiredService<IOptions<SesLocalOptions>>();
            var vm = new MainWindowViewModel(auth, opts);
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
