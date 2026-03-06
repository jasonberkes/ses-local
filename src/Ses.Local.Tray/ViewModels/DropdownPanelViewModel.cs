using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Options;
using Ses.Local.Core.Interfaces;
using Ses.Local.Core.Models;
using Ses.Local.Core.Options;
using Ses.Local.Tray.Services;

namespace Ses.Local.Tray.ViewModels;

public enum PanelTab { Status, CcConfig, Import, Settings }

public sealed class DropdownPanelViewModel : INotifyPropertyChanged
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

    public DropdownPanelViewModel(IAuthService auth, DaemonAuthProxy daemonProxy, IOptions<SesLocalOptions> options)
    {
        _auth            = auth;
        _daemonProxy     = daemonProxy;
        _troubleshootUrl = options.Value.DocsBaseUrl.TrimEnd('/') + "/ses-local/troubleshoot";
        _appVersion      = GetAppVersion();
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
        ConvSyncFeatures.Add(new FeatureStatus { Name = "ChatGPT",        Key = "chatgpt_sync",        IsComingSoon = true, IsEnabled = false });

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

        await Task.WhenAll(UpdateStatusAsync(ct), RefreshComponentsAsync(ct));
    }

    /// <summary>Fetches current auth state from daemon and applies it to all UI properties.</summary>
    public async Task UpdateStatusAsync(CancellationToken ct = default)
    {
        try
        {
            var state = await _auth.GetStateAsync(ct);
            ApplyState(state);
        }
        catch
        {
            StatusText     = "Daemon not running";
            StatusDotColor = StatusDot.Red;
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

    private void UpdateFeatureDots(bool isAuthenticated)
    {
        foreach (var f in _allFeatures)
        {
            f.Dot = f.IsComingSoon
                ? StatusDot.Grey
                : f.IsEnabled && isAuthenticated
                    ? StatusDot.Green
                    : StatusDot.Grey;

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

    public void SelectTab(PanelTab tab) => SelectedTab = tab;

    public void OpenTroubleshoot() => OpenUrl(_troubleshootUrl);

    private static void OpenUrl(string url)
    {
        try
        {
            if (OperatingSystem.IsMacOS())
                System.Diagnostics.Process.Start("open", url);
            else if (OperatingSystem.IsWindows())
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch { }
    }

    private static string GetAppVersion()
    {
        var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        return v is null ? "1.0.0" : $"{v.Major}.{v.Minor}.{v.Build}";
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
