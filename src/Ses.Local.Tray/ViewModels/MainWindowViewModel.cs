using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Ses.Local.Core.Interfaces;
using Ses.Local.Core.Models;

namespace Ses.Local.Tray.ViewModels;

public sealed class MainWindowViewModel : INotifyPropertyChanged
{
    private readonly IAuthService _auth;
    private string _userDisplayName = "Loading...";
    private bool _isFirstRun;
    private readonly string _appVersion;

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<FeatureStatus> ConvSyncFeatures { get; } = [];
    public ObservableCollection<FeatureStatus> MemoryFeatures { get; } = [];

    public string UserDisplayName
    {
        get => _userDisplayName;
        set { _userDisplayName = value; OnPropertyChanged(); }
    }

    public bool IsFirstRun
    {
        get => _isFirstRun;
        set { _isFirstRun = value; OnPropertyChanged(); }
    }

    public string AppVersion => _appVersion;

    public MainWindowViewModel(IAuthService auth)
    {
        _auth = auth;
        _appVersion = GetAppVersion();
        InitFeatures();
        _ = LoadAsync();
    }

    private void InitFeatures()
    {
        ConvSyncFeatures.Add(new FeatureStatus { Name = "Claude.ai",      Key = "claude_ai_sync",      IsEnabled = true });
        ConvSyncFeatures.Add(new FeatureStatus { Name = "Claude Desktop", Key = "claude_desktop_sync", IsEnabled = true });
        ConvSyncFeatures.Add(new FeatureStatus { Name = "Claude Code",    Key = "claude_code_sync",    IsEnabled = true });
        ConvSyncFeatures.Add(new FeatureStatus { Name = "Cowork",         Key = "cowork_sync",         IsEnabled = false });
        ConvSyncFeatures.Add(new FeatureStatus { Name = "ChatGPT",        Key = "chatgpt_sync",        IsComingSoon = true, IsEnabled = false });

        MemoryFeatures.Add(new FeatureStatus { Name = "ses-mcp tools",  Key = "mcp_memory_tools",  IsEnabled = true });
        MemoryFeatures.Add(new FeatureStatus { Name = "CC hooks",        Key = "cc_hooks",           IsEnabled = false });
        MemoryFeatures.Add(new FeatureStatus { Name = "Cloud sync",      Key = "cloud_memory_sync", IsEnabled = true });
        MemoryFeatures.Add(new FeatureStatus { Name = "Local cache",     Key = "local_memory_cache", IsEnabled = true });
    }

    private async Task LoadAsync()
    {
        var config = SesConfig.Load();
        IsFirstRun = config.IsFirstRun;
        UserDisplayName = config.UserDisplayName ?? "User";

        // Apply saved feature flags
        foreach (var feature in ConvSyncFeatures.Concat(MemoryFeatures))
        {
            if (config.FeatureFlags.TryGetValue(feature.Key, out var enabled))
                feature.IsEnabled = enabled;
        }

        // Set status dots based on detection
        await UpdateStatusDotsAsync();
    }

    private async Task UpdateStatusDotsAsync()
    {
        var state = await _auth.GetStateAsync();

        foreach (var f in ConvSyncFeatures.Concat(MemoryFeatures))
        {
            f.Dot = f.IsComingSoon
                ? StatusDot.Grey
                : f.IsEnabled && state.IsAuthenticated
                    ? StatusDot.Green
                    : StatusDot.Grey;

            f.LastActivity = f.IsEnabled ? "Active" : "Disabled";
        }
    }

    public async Task ToggleFeatureAsync(FeatureStatus feature, bool enabled)
    {
        feature.IsEnabled = enabled;
        var config = SesConfig.Load();
        config.FeatureFlags[feature.Key] = enabled;
        config.Save();
        await UpdateStatusDotsAsync();
        // Cloud sync of feature flag happens in WI-947 (ses-mcp manager)
    }

    public async Task SignOutAsync()
    {
        await _auth.SignOutAsync();
        UserDisplayName = "Signed out";
    }

    public static void OpenTroubleshoot() =>
        OpenUrl("https://docs.supereasysoftware.com/ses-local/troubleshoot");

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
