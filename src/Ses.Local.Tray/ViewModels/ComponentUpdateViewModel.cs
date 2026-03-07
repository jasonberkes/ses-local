using System.ComponentModel;
using System.Runtime.CompilerServices;
using Ses.Local.Tray.Services;

namespace Ses.Local.Tray.ViewModels;

public sealed class ComponentUpdateViewModel : INotifyPropertyChanged
{
    private bool _isUpdating;
    private string _updateMessage = string.Empty;

    public string  Name             { get; }
    public string  InstalledVersion { get; }
    public string? LatestVersion    { get; }
    public bool    UpdateAvailable  { get; }

    public bool IsUpdating
    {
        get => _isUpdating;
        set { _isUpdating = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanUpdate)); }
    }

    public string UpdateMessage
    {
        get => _updateMessage;
        set { _updateMessage = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasUpdateMessage)); }
    }

    public bool HasUpdateMessage => !string.IsNullOrEmpty(_updateMessage);

    public bool CanUpdate => UpdateAvailable && !_isUpdating;

    public string StatusText
    {
        get
        {
            if (_isUpdating)                                   return "Updating...";
            if (!string.IsNullOrEmpty(_updateMessage))         return _updateMessage;
            if (UpdateAvailable && LatestVersion is not null)  return $"Update available: v{LatestVersion}";
            if (InstalledVersion != "unknown")                 return "Up to date";
            return "Not installed";
        }
    }

    /// <summary>Accent blue when update is available, grey otherwise.</summary>
    public string StatusColor => UpdateAvailable ? "#0A84FF" : "#888888";

    public ComponentUpdateViewModel(ComponentUpdateInfo info)
    {
        Name             = info.Name;
        InstalledVersion = info.InstalledVersion ?? "unknown";
        LatestVersion    = info.LatestVersion;
        UpdateAvailable  = info.UpdateAvailable;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
