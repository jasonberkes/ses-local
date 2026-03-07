using System.ComponentModel;
using System.Runtime.CompilerServices;
using Ses.Local.Core.Models;
using Ses.Local.Tray.Services;

namespace Ses.Local.Tray.ViewModels;

/// <summary>ViewModel for an interactive MCP server row in the CC Config tab.</summary>
public sealed class McpServerViewModel : INotifyPropertyChanged
{
    // Protected servers cannot be removed via the UI.
    private static readonly HashSet<string> s_protectedNames =
        new(StringComparer.OrdinalIgnoreCase) { "ses-local", "taskmaster" };

    public string Name           { get; }
    public string ConnectionType { get; }
    public string Target         { get; }
    public bool   IsProtected    { get; }
    public bool   ShowRemoveButton => !IsProtected;

    private bool _isEnabled;
    public bool IsEnabled
    {
        get => _isEnabled;
        set { _isEnabled = value; OnPropertyChanged(); OnPropertyChanged(nameof(Dot)); }
    }

    private bool _isAvailable;
    public bool IsAvailable
    {
        get => _isAvailable;
        set { _isAvailable = value; OnPropertyChanged(); OnPropertyChanged(nameof(Dot)); }
    }

    // Green = enabled + available, Red = enabled but binary missing, Grey = disabled.
    public StatusDot Dot =>
        !_isEnabled  ? StatusDot.Grey :
        _isAvailable ? StatusDot.Green :
                       StatusDot.Red;

    private bool _showRemoveConfirm;
    public bool ShowRemoveConfirm
    {
        get => _showRemoveConfirm;
        set { _showRemoveConfirm = value; OnPropertyChanged(); OnPropertyChanged(nameof(ShowNormalState)); }
    }

    public bool ShowNormalState => !_showRemoveConfirm;

    public McpServerViewModel(McpServerInfo info)
    {
        Name           = info.Name;
        ConnectionType = info.ConnectionType;
        Target         = info.Target;
        IsProtected    = s_protectedNames.Contains(info.Name);
        _isEnabled     = !info.IsDisabled;
        _isAvailable   = info.IsAvailable;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
