using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Ses.Local.Tray.Services;

namespace Ses.Local.Tray.ViewModels;

public sealed class ActiveSessionViewModel : INotifyPropertyChanged
{
    private bool _isExpanded;

    public string  ProjectName      { get; }
    public string? FullPath         { get; }
    public string  LastActivityText { get; }

    public string DisplayPath
    {
        get
        {
            if (FullPath is null) return ProjectName;
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return FullPath.StartsWith(home, StringComparison.OrdinalIgnoreCase)
                ? "~" + FullPath[home.Length..]
                : FullPath;
        }
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set { _isExpanded = value; OnPropertyChanged(); }
    }

    public bool HasFullPath => FullPath is not null;

    public ActiveSessionViewModel(ActiveSessionInfo info)
    {
        ProjectName      = info.ProjectName;
        FullPath         = info.FullPath;
        LastActivityText = FormatAge(info.LastActivity);
    }

    public void OpenTerminal()
    {
        if (FullPath is null) return;
        try
        {
            if (OperatingSystem.IsMacOS())
                Process.Start("open", $"-a Terminal \"{FullPath}\"");
            else if (OperatingSystem.IsWindows())
                Process.Start(new ProcessStartInfo("cmd", $"/c start cmd /k cd /d \"{FullPath}\"") { UseShellExecute = true });
        }
        catch { }
    }

    public void OpenInEditor()
    {
        if (FullPath is null) return;
        try { Process.Start(new ProcessStartInfo("code", $"\"{FullPath}\"") { UseShellExecute = true }); }
        catch { }
    }

    private static string FormatAge(DateTime dt)
    {
        var age = DateTime.UtcNow - dt;
        return age.TotalSeconds < 60   ? "just now"
             : age.TotalMinutes < 60   ? $"{(int)age.TotalMinutes} min ago"
             : age.TotalHours   < 24   ? $"{(int)age.TotalHours}h ago"
             : age.TotalDays    < 7    ? $"{(int)age.TotalDays}d ago"
             :                           dt.ToLocalTime().ToString("MMM d");
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
