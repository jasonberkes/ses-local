using System.ComponentModel;
using System.Runtime.CompilerServices;
using Ses.Local.Core.Models;

namespace Ses.Local.Tray.Services;

public enum ComponentState { Checking, Downloading, Installed, Error, NotNeeded }

public sealed class ComponentStatus : INotifyPropertyChanged
{
    private ComponentState _state = ComponentState.Checking;
    private string _statusText = "Checking...";
    private string? _version;

    public string Name { get; init; } = "";

    public ComponentState State
    {
        get => _state;
        set
        {
            _state = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Dot));
        }
    }

    public string StatusText
    {
        get => _statusText;
        set { _statusText = value; OnPropertyChanged(); }
    }

    public string? Version
    {
        get => _version;
        set { _version = value; OnPropertyChanged(); }
    }

    public StatusDot Dot => State switch
    {
        ComponentState.Installed    => StatusDot.Green,
        ComponentState.Downloading  => StatusDot.Green,
        ComponentState.Error        => StatusDot.Red,
        _                           => StatusDot.Grey
    };

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
