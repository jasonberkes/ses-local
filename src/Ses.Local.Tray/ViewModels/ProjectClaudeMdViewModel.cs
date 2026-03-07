using System.ComponentModel;
using System.Runtime.CompilerServices;
using Ses.Local.Core.Models;
using Ses.Local.Tray.Services;

namespace Ses.Local.Tray.ViewModels;

/// <summary>ViewModel for a single project row in the CLAUDE.md viewer section.</summary>
public sealed class ProjectClaudeMdViewModel : INotifyPropertyChanged
{
    private bool _isExpanded;
    private string _claudeMdContent = string.Empty;

    public string ProjectName   { get; }
    public string ProjectPath   { get; }
    public string? ClaudeMdPath { get; }
    public bool HasClaudeMd     { get; }
    public StatusDot Dot        { get; }
    public string LastModifiedText { get; }
    public string TruncatedPath { get; }

    public bool IsExpanded
    {
        get => _isExpanded;
        set { _isExpanded = value; OnPropertyChanged(); }
    }

    public string ClaudeMdContent
    {
        get => _claudeMdContent;
        set { _claudeMdContent = value; OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ProjectClaudeMdViewModel(ProjectClaudeMd model)
    {
        ProjectName      = model.ProjectName;
        ProjectPath      = model.ProjectPath;
        ClaudeMdPath     = model.ClaudeMdPath;
        HasClaudeMd      = model.HasClaudeMd;
        Dot              = model.HasClaudeMd ? StatusDot.Green : StatusDot.Grey;
        TruncatedPath    = TruncatePath(model.ClaudeMdPath ?? model.ProjectPath);
        LastModifiedText = model.LastModified is null ? string.Empty : DropdownPanelViewModel.FormatAge(model.LastModified.Value);
    }

    /// <summary>Loads CLAUDE.md content from disk (truncated to 4 KB for display).</summary>
    public void LoadContent()
    {
        if (ClaudeMdPath is null || ClaudeMdContent.Length > 0) return;
        try
        {
            var buf = new char[4097];
            int read;
            using (var sr = new StreamReader(ClaudeMdPath))
                read = sr.ReadBlock(buf, 0, buf.Length);
            ClaudeMdContent = read < buf.Length
                ? new string(buf, 0, read)
                : new string(buf, 0, 4096) + "\n\n[truncated…]";
        }
        catch (Exception ex)
        {
            ClaudeMdContent = $"Error reading file: {ex.Message}";
        }
    }

    private static string TruncatePath(string path)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (path.StartsWith(home, StringComparison.Ordinal))
            path = "~" + path[home.Length..];

        if (path.Length <= 48) return path;

        // Keep last 40 chars with "..." prefix
        return "…" + path[^40..];
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
