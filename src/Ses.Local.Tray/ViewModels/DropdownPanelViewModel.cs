using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Options;
using Ses.Local.Core.Interfaces;
using Ses.Local.Core.Models;
using Ses.Local.Core.Options;
using Ses.Local.Tray.Services;

namespace Ses.Local.Tray.ViewModels;

public enum PanelTab { Status, CcConfig, Import, Settings }

public sealed class DropdownPanelViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly IAuthService _auth;
    private readonly DaemonAuthProxy _daemonProxy;
    private readonly string _troubleshootUrl;
    private string _userDisplayName = "Loading...";
    private string _statusText = "Connecting...";
    private StatusDot _statusDot = StatusDot.Grey;
    private PanelTab _selectedTab = PanelTab.Status;
    private bool _isFirstRun;
    private readonly string _appVersion;
    private FeatureStatus[] _allFeatures = [];
    private SesConfig? _config;
    private readonly ClaudeCodeSettingsService _ccSettings;
    private readonly ClaudeDesktopConfigService _desktopSettings;
    private readonly bool _ownsCcSettings;
    private readonly EventHandler _onSettingsChanged;

    // ── Dashboard status properties ───────────────────────────────────────────
    private string _daemonUptime = "—";
    private string _totalConversationsText = "—";
    private string _totalMessagesText = "—";
    private string _localDbSizeText = "—";
    private string _oldestConversationText = "—";
    private string _newestConversationText = "—";
    private volatile bool _syncStatsRefreshPending;

    public string DaemonUptime
    {
        get => _daemonUptime;
        set { _daemonUptime = value; OnPropertyChanged(); }
    }

    public string TotalConversationsText
    {
        get => _totalConversationsText;
        set { _totalConversationsText = value; OnPropertyChanged(); }
    }

    public string TotalMessagesText
    {
        get => _totalMessagesText;
        set { _totalMessagesText = value; OnPropertyChanged(); }
    }

    public string LocalDbSizeText
    {
        get => _localDbSizeText;
        set { _localDbSizeText = value; OnPropertyChanged(); }
    }

    public string OldestConversationText
    {
        get => _oldestConversationText;
        set { _oldestConversationText = value; OnPropertyChanged(); }
    }

    public string NewestConversationText
    {
        get => _newestConversationText;
        set { _newestConversationText = value; OnPropertyChanged(); }
    }

    // ── CC Config tab state ───────────────────────────────────────────────────
    private string _ccModelName = "default";
    private string _ccPermissionsAllow = "(none)";
    private string _ccPermissionsDeny = "(none)";
    private string _ccSettingsFileAge = "—";
    private string _ccLocalSettingsFileAge = "—";
    private string _ccHooksSummary = "(none)";

    // ── MCP management state ──────────────────────────────────────────────────
    private bool _isAddFormVisible;
    private bool _addIsStdio = true;
    private string _addName = "";
    private string _addCommand = "";
    private string _addArgs = "";
    private string _addUrl = "";
    private string _addValidationError = "";
    private string _restartStatus = "";
    private bool _isRestarting;

    // ── Hooks state (TRAY-3) ───────────────────────────────────────────────────
    private StatusDot _hooksStatusDot = StatusDot.Grey;
    private string _hooksStatusText = "Loading...";
    private string _hooksLastActivity = string.Empty;
    private bool _isLogsExpanded;

    public string CcModelName
    {
        get => _ccModelName;
        set { _ccModelName = value; OnPropertyChanged(); }
    }

    public string CcPermissionsAllow
    {
        get => _ccPermissionsAllow;
        set { _ccPermissionsAllow = value; OnPropertyChanged(); }
    }

    public string CcPermissionsDeny
    {
        get => _ccPermissionsDeny;
        set { _ccPermissionsDeny = value; OnPropertyChanged(); }
    }

    public string CcSettingsFileAge
    {
        get => _ccSettingsFileAge;
        set { _ccSettingsFileAge = value; OnPropertyChanged(); }
    }

    public string CcLocalSettingsFileAge
    {
        get => _ccLocalSettingsFileAge;
        set { _ccLocalSettingsFileAge = value; OnPropertyChanged(); }
    }

    public string CcHooksSummary
    {
        get => _ccHooksSummary;
        set { _ccHooksSummary = value; OnPropertyChanged(); OnPropertyChanged(nameof(HooksEnabled)); OnPropertyChanged(nameof(HooksToggleLabel)); }
    }

    public StatusDot HooksStatusDot
    {
        get => _hooksStatusDot;
        set { _hooksStatusDot = value; OnPropertyChanged(); }
    }

    public string HooksStatusText
    {
        get => _hooksStatusText;
        set { _hooksStatusText = value; OnPropertyChanged(); }
    }

    public string HooksLastActivity
    {
        get => _hooksLastActivity;
        set { _hooksLastActivity = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasLastActivity)); }
    }

    public bool HasLastActivity => !string.IsNullOrEmpty(_hooksLastActivity);

    /// <summary>Derived from CcHooksSummary — true when any hooks are registered in settings.json.</summary>
    public bool HooksEnabled => _ccHooksSummary != "(none)";

    public string HooksToggleLabel => HooksEnabled ? "Disable" : "Enable";

    public bool IsLogsExpanded
    {
        get => _isLogsExpanded;
        set { _isLogsExpanded = value; OnPropertyChanged(); }
    }

    public ObservableCollection<HookLogEntry> RecentLogs { get; } = [];

    public ObservableCollection<McpServerViewModel> CcMcpServers      { get; } = [];
    public ObservableCollection<McpServerViewModel> DesktopMcpServers { get; } = [];
    public string[] CcAvailableModels { get; } = ClaudeCodeSettingsService.CommonModels;

    // ── MCP add-form properties ───────────────────────────────────────────────

    public bool IsAddFormVisible
    {
        get => _isAddFormVisible;
        set { _isAddFormVisible = value; OnPropertyChanged(); }
    }

    public bool AddIsStdio
    {
        get => _addIsStdio;
        set { _addIsStdio = value; OnPropertyChanged(); OnPropertyChanged(nameof(AddIsHttp)); }
    }

    public bool AddIsHttp => !_addIsStdio;

    public string AddName
    {
        get => _addName;
        set { _addName = value; OnPropertyChanged(); }
    }

    public string AddCommand
    {
        get => _addCommand;
        set { _addCommand = value; OnPropertyChanged(); }
    }

    public string AddArgs
    {
        get => _addArgs;
        set { _addArgs = value; OnPropertyChanged(); }
    }

    public string AddUrl
    {
        get => _addUrl;
        set { _addUrl = value; OnPropertyChanged(); }
    }

    public string AddValidationError
    {
        get => _addValidationError;
        set { _addValidationError = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasAddValidationError)); }
    }

    public bool HasAddValidationError => !string.IsNullOrEmpty(_addValidationError);

    public string RestartStatus
    {
        get => _restartStatus;
        set { _restartStatus = value; OnPropertyChanged(); OnPropertyChanged(nameof(ShowRestartStatus)); }
    }

    public bool ShowRestartStatus => !string.IsNullOrEmpty(_restartStatus);

    public bool IsRestarting
    {
        get => _isRestarting;
        set { _isRestarting = value; OnPropertyChanged(); }
    }

    // Direct refs to fixed component items — avoids FirstOrDefault string searches
    private ComponentStatus _daemonStatus  = null!;
    private ComponentStatus _sesMcpStatus  = null!;
    private ComponentStatus _sesHooksStatus = null!;

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<FeatureStatus> ConvSyncFeatures { get; } = [];
    public ObservableCollection<FeatureStatus> MemoryFeatures { get; } = [];
    public ObservableCollection<ComponentStatus> Components { get; } = [];

    public string UserDisplayName
    {
        get => _userDisplayName;
        set { _userDisplayName = value; OnPropertyChanged(); }
    }

    public string StatusText
    {
        get => _statusText;
        set { _statusText = value; OnPropertyChanged(); }
    }

    public StatusDot StatusDotColor
    {
        get => _statusDot;
        set { _statusDot = value; OnPropertyChanged(); }
    }

    public PanelTab SelectedTab
    {
        get => _selectedTab;
        set
        {
            _selectedTab = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsStatusTab));
            OnPropertyChanged(nameof(IsCcConfigTab));
            OnPropertyChanged(nameof(IsImportTab));
            OnPropertyChanged(nameof(IsSettingsTab));
        }
    }

    public bool IsStatusTab    => _selectedTab == PanelTab.Status;
    public bool IsCcConfigTab  => _selectedTab == PanelTab.CcConfig;
    public bool IsImportTab    => _selectedTab == PanelTab.Import;
    public bool IsSettingsTab  => _selectedTab == PanelTab.Settings;

    public bool IsFirstRun
    {
        get => _isFirstRun;
        set { _isFirstRun = value; OnPropertyChanged(); }
    }

    public string AppVersion => _appVersion;

    public DropdownPanelViewModel(IAuthService auth, DaemonAuthProxy daemonProxy, IOptions<SesLocalOptions> options,
        ClaudeCodeSettingsService? ccSettings = null, ClaudeDesktopConfigService? desktopSettings = null)
    {
        _auth            = auth;
        _daemonProxy     = daemonProxy;
        _troubleshootUrl = options.Value.DocsBaseUrl.TrimEnd('/') + "/ses-local/troubleshoot";
        _appVersion      = GetAppVersion();
        _ownsCcSettings      = ccSettings is null;
        _ccSettings          = ccSettings ?? new ClaudeCodeSettingsService();
        _desktopSettings     = desktopSettings ?? new ClaudeDesktopConfigService();
        _onSettingsChanged   = (_, _) => RefreshCcConfig();
        _ccSettings.SettingsChanged += _onSettingsChanged;
        InitFeatures();
        InitComponents();
        _ = LoadAsync();
    }

    private void InitFeatures()
    {
        ConvSyncFeatures.Add(new FeatureStatus { Name = "Claude.ai",      Key = "claude_ai_sync",      IsEnabled = true });
        ConvSyncFeatures.Add(new FeatureStatus { Name = "Claude Desktop", Key = "claude_desktop_sync", IsEnabled = true });
        ConvSyncFeatures.Add(new FeatureStatus { Name = "Claude Code",    Key = "claude_code_sync",    IsEnabled = true });
        ConvSyncFeatures.Add(new FeatureStatus { Name = "Cowork",         Key = "cowork_sync",         IsEnabled = false });
        ConvSyncFeatures.Add(new FeatureStatus { Name = "ChatGPT Desktop", Key = "chatgpt_desktop_sync", IsEnabled = false });

        MemoryFeatures.Add(new FeatureStatus { Name = "ses-mcp tools", Key = "mcp_memory_tools",  IsEnabled = true });
        MemoryFeatures.Add(new FeatureStatus { Name = "CC hooks",       Key = "cc_hooks",           IsEnabled = false });
        MemoryFeatures.Add(new FeatureStatus { Name = "Cloud sync",     Key = "cloud_memory_sync", IsEnabled = true });
        MemoryFeatures.Add(new FeatureStatus { Name = "Local cache",    Key = "local_memory_cache", IsEnabled = true });

        _allFeatures = [.. ConvSyncFeatures, .. MemoryFeatures];
    }

    private void InitComponents()
    {
        _daemonStatus   = new ComponentStatus { Name = "ses-local-daemon" };
        _sesMcpStatus   = new ComponentStatus { Name = "ses-mcp" };
        _sesHooksStatus = new ComponentStatus { Name = "ses-hooks" };

        Components.Add(_daemonStatus);
        Components.Add(_sesMcpStatus);
        Components.Add(_sesHooksStatus);
    }

    public async Task RefreshComponentsAsync(CancellationToken ct = default)
    {
        var resp = await _daemonProxy.GetComponentsAsync(ct);
        if (resp is null)
        {
            foreach (var c in Components)
            {
                c.State      = ComponentState.Error;
                c.StatusText = "Daemon not reachable";
            }
            return;
        }

        _daemonStatus.State      = ComponentState.Installed;
        _daemonStatus.StatusText = "Running";
        _daemonStatus.Version    = resp.Daemon.Version;

        _sesMcpStatus.State      = resp.SesMcp.Installed ? ComponentState.Installed : ComponentState.Error;
        _sesMcpStatus.StatusText = resp.SesMcp.Installed ? "Installed" : "Not installed";
        _sesMcpStatus.Version    = resp.SesMcp.Version;

        _sesHooksStatus.State      = resp.SesHooks.Installed ? ComponentState.Installed : ComponentState.Error;
        _sesHooksStatus.StatusText = resp.SesHooks.Installed ? "Registered" : "Not found";
    }

    private async Task LoadAsync(CancellationToken ct = default)
    {
        _config = SesConfig.Load();
        IsFirstRun = _config.IsFirstRun;
        UserDisplayName = _config.UserDisplayName ?? "User";

        foreach (var feature in _allFeatures)
        {
            if (_config.FeatureFlags.TryGetValue(feature.Key, out var enabled))
                feature.IsEnabled = enabled;
        }

        await Task.WhenAll(UpdateStatusAsync(ct), RefreshComponentsAsync(ct), RefreshSyncStatsAsync(ct));
    }

    /// <summary>Fetches current auth state from daemon and applies it to all UI properties.</summary>
    public async Task UpdateStatusAsync(CancellationToken ct = default)
    {
        try
        {
            var state = await _auth.GetStateAsync(ct);
            DaemonUptime = _daemonProxy.LastKnownUptime;
            ApplyState(state);

            // Opportunistically refresh sync stats (deduplicated via volatile flag)
            if (!_syncStatsRefreshPending)
            {
                _syncStatsRefreshPending = true;
                _ = Task.Run(() => RefreshSyncStatsAsync(CancellationToken.None));
            }
        }
        catch
        {
            StatusText     = "Daemon not running";
            StatusDotColor = StatusDot.Red;
            DaemonUptime   = "—";
        }
    }

    /// <summary>Applies a pre-fetched auth state (avoids a second daemon call when TrayApp already has it).</summary>
    public void ApplyState(SesAuthState state)
    {
        if (state.IsAuthenticated)
        {
            StatusText     = "Connected";
            StatusDotColor = StatusDot.Green;
        }
        else if (state.LicenseValid)
        {
            StatusText     = "Licensed (Tier 1)";
            StatusDotColor = StatusDot.Green;
        }
        else if (state.LoginTimedOut || state.NeedsReauth)
        {
            StatusText     = "Session expired";
            StatusDotColor = StatusDot.Red;
        }
        else
        {
            StatusText     = "Not activated";
            StatusDotColor = StatusDot.Grey;
        }

        UpdateFeatureDots(state.IsAuthenticated);
    }

    /// <summary>Fetches sync stats from the daemon and updates dashboard properties.</summary>
    public async Task RefreshSyncStatsAsync(CancellationToken ct = default)
    {
        try
        {
            var stats = await _daemonProxy.GetSyncStatsAsync(ct);
            if (stats is not null)
                ApplySyncStats(stats);
        }
        finally
        {
            _syncStatsRefreshPending = false;
        }
    }

    /// <summary>Applies fetched sync stats to feature rows and totals display properties.</summary>
    public void ApplySyncStats(SyncStats stats)
    {
        // Update conversation sync feature rows with real counts + last activity
        foreach (var f in ConvSyncFeatures)
        {
            if (f.IsComingSoon) continue;

            var surface = f.Key switch
            {
                "claude_ai_sync"      => stats.ClaudeChat,
                "claude_desktop_sync" => stats.ClaudeChat,
                "claude_code_sync"    => stats.ClaudeCode,
                "cowork_sync"         => stats.Cowork,
                _                    => null
            };

            if (surface is not null)
                f.LastActivity = FormatSurfaceStats(surface, f.IsEnabled);
        }

        // Totals
        TotalConversationsText = stats.TotalConversations.ToString("N0") + " conversations";
        TotalMessagesText      = stats.TotalMessages.ToString("N0") + " messages";
        LocalDbSizeText        = FormatBytes(stats.LocalDbSizeBytes);
        OldestConversationText = stats.OldestConversation.HasValue
            ? stats.OldestConversation.Value.ToLocalTime().ToString("MMM d, yyyy")
            : "—";
        NewestConversationText = stats.NewestConversation.HasValue
            ? stats.NewestConversation.Value.ToLocalTime().ToString("MMM d, yyyy")
            : "—";
    }

    private static string FormatSurfaceStats(SurfaceStats surface, bool enabled)
    {
        if (!enabled) return "Disabled";
        if (surface.Count == 0) return "No conversations synced";

        var count = surface.Count.ToString("N0") + (surface.Count == 1 ? " conversation" : " conversations");
        if (!surface.LastActivity.HasValue) return count;

        var age = DateTime.UtcNow - surface.LastActivity.Value;
        var timeStr = age.TotalSeconds < 60   ? "just now"
                    : age.TotalMinutes < 60   ? $"{(int)age.TotalMinutes} min ago"
                    : age.TotalHours < 24     ? $"{(int)age.TotalHours}h ago"
                    : age.TotalDays < 7       ? $"{(int)age.TotalDays}d ago"
                    :                            surface.LastActivity.Value.ToLocalTime().ToString("MMM d");

        return $"{count} · {timeStr}";
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "0 B";
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        return $"{bytes / (1024.0 * 1024.0):F1} MB";
    }

    private void UpdateFeatureDots(bool isAuthenticated)
    {
        foreach (var f in _allFeatures)
        {
            f.Dot = f.IsComingSoon
                ? StatusDot.Grey
                : f.IsEnabled && isAuthenticated
                    ? StatusDot.Green
                    : StatusDot.Grey;

            if (!f.IsComingSoon && f.LastActivity == "Never")
                f.LastActivity = f.IsEnabled ? "Active" : "Disabled";
        }
    }

    public void ToggleFeature(FeatureStatus feature, bool enabled)
    {
        feature.IsEnabled = enabled;

        _config ??= SesConfig.Load();
        _config.FeatureFlags[feature.Key] = enabled;
        _config.Save();

        // Re-derive dots from cached auth state without a daemon round-trip.
        // StatusDotColor is Green iff authenticated (Green) or licensed (Green).
        UpdateFeatureDots(StatusDotColor == StatusDot.Green);
    }

    public async Task SignOutAsync(CancellationToken ct = default)
    {
        await _auth.SignOutAsync(ct);
        UserDisplayName = "Signed out";
        await UpdateStatusAsync(ct);
    }

    public void SelectTab(PanelTab tab)
    {
        SelectedTab = tab;
        if (tab == PanelTab.CcConfig)
        {
            RefreshCcConfig();
            _ = RefreshHooksStatusAsync();
        }
    }

    public void RefreshCcConfig()
    {
        try
        {
            var info = _ccSettings.ReadSettings();
            CcModelName         = info.ModelName;
            CcPermissionsAllow  = info.PermissionsAllow.Count > 0
                ? string.Join(", ", info.PermissionsAllow) : "(none)";
            CcPermissionsDeny   = info.PermissionsDeny.Count > 0
                ? string.Join(", ", info.PermissionsDeny) : "(none)";
            CcHooksSummary      = info.RegisteredHooks.Count > 0
                ? string.Join(", ", info.RegisteredHooks) : "(none)";
            CcSettingsFileAge      = FormatAge(info.SettingsLastModified);
            CcLocalSettingsFileAge = FormatAge(info.LocalSettingsLastModified);

            CcMcpServers.Clear();
            foreach (var s in info.McpServers)
                CcMcpServers.Add(new McpServerViewModel(s));
        }
        catch { /* read error — leave existing values */ }

        try
        {
            DesktopMcpServers.Clear();
            foreach (var s in _desktopSettings.ReadMcpServers())
                DesktopMcpServers.Add(new McpServerViewModel(s));
        }
        catch { /* read error — leave existing values */ }
    }

    /// <summary>Refreshes hooks health status from the daemon (binary check + last activity).</summary>
    public async Task RefreshHooksStatusAsync(CancellationToken ct = default)
    {
        // Fetch extended status from daemon (includes binary check + last activity)
        var status = await _daemonProxy.GetHooksStatusAsync(ct);
        if (status is null)
        {
            HooksStatusDot  = StatusDot.Grey;
            HooksStatusText = "Daemon not reachable";
            return;
        }

        if (!status.BinaryExists)
        {
            HooksStatusDot  = StatusDot.Red;
            HooksStatusText = "Binary not found — reinstall needed";
        }
        else if (!status.Registered)
        {
            HooksStatusDot  = StatusDot.Grey;
            HooksStatusText = _ccSettings.AreHooksDisabled() ? "Disabled" : "Not configured";
        }
        else if (status.LastActivity is not null && (DateTime.UtcNow - status.LastActivity.Value) < TimeSpan.FromHours(1))
        {
            HooksStatusDot  = StatusDot.Green;
            HooksStatusText = "Active";
        }
        else
        {
            HooksStatusDot  = StatusDot.Yellow;
            HooksStatusText = "Registered (no recent activity)";
        }

        HooksLastActivity = status.LastActivity is null
            ? string.Empty
            : FormatAge(status.LastActivity.Value.ToLocalTime());
    }

    /// <summary>Toggles hooks on or off. If enabling and no saved hooks exist, asks daemon to register fresh.</summary>
    public async Task ToggleHooksAsync(CancellationToken ct = default)
    {
        if (HooksEnabled)
        {
            _ccSettings.DisableHooks();
        }
        else
        {
            var restored = _ccSettings.EnableHooks();
            if (!restored)
                await _daemonProxy.EnableHooksAsync(ct);
        }

        RefreshCcConfig();
        await RefreshHooksStatusAsync(ct);
    }

    /// <summary>Loads the last 20 hook observations and toggles the log panel.</summary>
    public async Task ToggleLogsExpandedAsync(CancellationToken ct = default)
    {
        if (_isLogsExpanded)
        {
            IsLogsExpanded = false;
            return;
        }

        var logs = await _daemonProxy.GetHookLogsAsync(ct);
        RecentLogs.Clear();
        if (logs is not null)
        {
            foreach (var entry in logs)
                RecentLogs.Add(entry);
        }
        IsLogsExpanded = true;
    }

    public void ChangeCcModel(string model)
    {
        if (string.IsNullOrWhiteSpace(model)) return;
        _ccSettings.WriteModelName(model);
        CcModelName = model;
    }

    // ── MCP management ────────────────────────────────────────────────────────

    public void ToggleMcpServer(McpServerViewModel server)
    {
        _ccSettings.ToggleMcpServer(server.Name, !server.IsEnabled);
        RefreshCcConfig();
    }

    public void RequestRemoveMcpServer(McpServerViewModel server)
    {
        if (server.IsProtected) return;
        server.ShowRemoveConfirm = true;
    }

    public void CancelRemoveMcpServer(McpServerViewModel server) =>
        server.ShowRemoveConfirm = false;

    public void ConfirmRemoveMcpServer(McpServerViewModel server)
    {
        _ccSettings.RemoveMcpServer(server.Name);
        CcMcpServers.Remove(server);
    }

    public void ShowAddForm()
    {
        AddName            = "";
        AddCommand         = "";
        AddArgs            = "";
        AddUrl             = "";
        AddIsStdio         = true;
        AddValidationError = "";
        IsAddFormVisible   = true;
    }

    public void CancelAddForm() => IsAddFormVisible = false;

    public void ConfirmAddServer()
    {
        var name = AddName.Trim();
        if (string.IsNullOrEmpty(name))
        {
            AddValidationError = "Name is required.";
            return;
        }
        if (_ccSettings.McpServerExists(name))
        {
            AddValidationError = $"A server named \"{name}\" already exists.";
            return;
        }

        if (_addIsStdio)
        {
            var command = AddCommand.Trim();
            if (string.IsNullOrEmpty(command))
            {
                AddValidationError = "Command is required for stdio servers.";
                return;
            }
            var trimmedArgs = AddArgs.Trim();
            var args = trimmedArgs.Length > 0
                ? trimmedArgs.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                : [];
            _ccSettings.AddStdioMcpServer(name, command, args);
        }
        else
        {
            var url = AddUrl.Trim();
            if (string.IsNullOrEmpty(url))
            {
                AddValidationError = "URL is required for HTTP servers.";
                return;
            }
            _ccSettings.AddHttpMcpServer(name, url);
        }

        IsAddFormVisible = false;
        RefreshCcConfig();
    }

    public async Task RestartAllMcpAsync()
    {
        if (_isRestarting) return;
        IsRestarting  = true;
        RestartStatus = "Restarting...";
        try
        {
            try
            {
                Process.Start(new ProcessStartInfo("/bin/sh",
                    "-c \"pkill -f ses-mcp; pkill -f mcp-remote; pkill -f mcp-proxy; exit 0\"")
                {
                    UseShellExecute = false,
                    CreateNoWindow  = true
                });
            }
            catch { /* pkill not available or no processes — non-fatal */ }
            // Give MCP processes time to exit before Claude Code restarts them.
            await Task.Delay(3000);
        }
        finally
        {
            IsRestarting  = false;
            RestartStatus = "";
            RefreshCcConfig();
        }
    }

    public void OpenCcSettingsFile()
    {
        var path = _ccSettings.SettingsFilePath;
        if (!File.Exists(path)) return;

        try
        {
            if (OperatingSystem.IsMacOS())
            {
                // Try VS Code first, fall back to TextEdit
                var vscode = Process.Start(
                    new ProcessStartInfo("code", path) { UseShellExecute = true });
                if (vscode is null)
                    Process.Start("open", $"-a TextEdit \"{path}\"");
            }
            else if (OperatingSystem.IsWindows())
            {
                Process.Start(
                    new ProcessStartInfo("notepad", path) { UseShellExecute = true });
            }
        }
        catch { /* non-fatal */ }
    }

    private static string FormatAge(DateTime? dt)
    {
        if (dt is null) return "not found";
        var age = DateTime.Now - dt.Value;
        return age.TotalSeconds < 60  ? "just now" :
               age.TotalMinutes < 60  ? $"{(int)age.TotalMinutes} minutes ago" :
               age.TotalHours < 24    ? $"{(int)age.TotalHours} hours ago" :
               age.TotalDays < 7     ? $"{(int)age.TotalDays} days ago" :
                                        dt.Value.ToString("yyyy-MM-dd");
    }

    public void OpenTroubleshoot() => OpenUrl(_troubleshootUrl);

    private static void OpenUrl(string url)
    {
        try
        {
            if (OperatingSystem.IsMacOS())
                Process.Start("open", url);
            else if (OperatingSystem.IsWindows())
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch { }
    }

    private static string GetAppVersion()
    {
        var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        return v is null ? "1.0.0" : $"{v.Major}.{v.Minor}.{v.Build}";
    }

    public void Dispose()
    {
        _ccSettings.SettingsChanged -= _onSettingsChanged;
        if (_ownsCcSettings)
            _ccSettings.Dispose();
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
