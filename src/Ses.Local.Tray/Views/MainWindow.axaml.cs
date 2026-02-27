using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Ses.Local.Core.Models;
using Ses.Local.Tray.ViewModels;

namespace Ses.Local.Tray.Views;

public partial class MainWindow : Window
{
    private MainWindowViewModel? _vm;

    public MainWindow() => AvaloniaXamlLoader.Load(this);

    public MainWindow(MainWindowViewModel vm) : this()
    {
        _vm = vm;
        DataContext = vm;
        PositionNearTray();
    }

    private void PositionNearTray()
    {
        // Position bottom-right of screen
        var screen = Screens.Primary;
        if (screen is null) return;
        var workArea = screen.WorkingArea;
        Position = new Avalonia.PixelPoint(
            workArea.Right - 410,
            workArea.Bottom - 610);
    }

    private async void OnSignOutClick(object? sender, RoutedEventArgs e)
    {
        if (_vm is not null) await _vm.SignOutAsync();
    }

    private void OnTroubleshootClick(object? sender, RoutedEventArgs e) =>
        MainWindowViewModel.OpenTroubleshoot();

    private void OnQuitClick(object? sender, RoutedEventArgs e) =>
        (Avalonia.Application.Current?.ApplicationLifetime as
         Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)
        ?.Shutdown();

    private async void OnFeatureToggled(object? sender, RoutedEventArgs e)
    {
        if (_vm is null || sender is not ToggleSwitch ts) return;
        if (ts.DataContext is FeatureStatus feature)
            await _vm.ToggleFeatureAsync(feature, ts.IsChecked ?? false);
    }
}
