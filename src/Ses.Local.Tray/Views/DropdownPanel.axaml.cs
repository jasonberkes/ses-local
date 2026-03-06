using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Ses.Local.Core.Models;
using Ses.Local.Tray.ViewModels;

namespace Ses.Local.Tray.Views;

public partial class DropdownPanel : Window
{
    private DropdownPanelViewModel? _vm;

    public DropdownPanel() => AvaloniaXamlLoader.Load(this);

    public DropdownPanel(DropdownPanelViewModel vm) : this()
    {
        _vm = vm;
        DataContext = vm;
        Deactivated += (_, _) => Hide();
        PositionBelowMenuBar();
    }

    private void PositionBelowMenuBar()
    {
        var screen = Screens.Primary;
        if (screen is null) return;
        var x = screen.WorkingArea.Right - (int)Width - 8;
        var y = screen.WorkingArea.Y;
        Position = new PixelPoint(x, y);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Hide();
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }

    /// <summary>Applies a pre-fetched auth state — avoids a redundant daemon call.</summary>
    public void RefreshStatus(SesAuthState state) => _vm?.ApplyState(state);

    // ── tab clicks ──────────────────────────────────────────────────────────

    private void OnStatusTabClick(object? sender, RoutedEventArgs e)    => _vm?.SelectTab(PanelTab.Status);
    private void OnCcConfigTabClick(object? sender, RoutedEventArgs e)  => _vm?.SelectTab(PanelTab.CcConfig);
    private void OnImportTabClick(object? sender, RoutedEventArgs e)    => _vm?.SelectTab(PanelTab.Import);
    private void OnSettingsTabClick(object? sender, RoutedEventArgs e)  => _vm?.SelectTab(PanelTab.Settings);

    // ── feature toggles ─────────────────────────────────────────────────────

    private void OnFeatureToggled(object? sender, RoutedEventArgs e)
    {
        if (_vm is null || sender is not ToggleSwitch ts) return;
        if (ts.DataContext is FeatureStatus feature)
            _vm.ToggleFeature(feature, ts.IsChecked ?? false);
    }

    // ── footer ──────────────────────────────────────────────────────────────

    private void OnTroubleshootClick(object? sender, RoutedEventArgs e) => _vm?.OpenTroubleshoot();

    private void OnQuitClick(object? sender, RoutedEventArgs e) =>
        (Avalonia.Application.Current?.ApplicationLifetime as
         Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)
        ?.Shutdown();

    // ── settings ────────────────────────────────────────────────────────────

    private async void OnSignOutClick(object? sender, RoutedEventArgs e)
    {
        if (_vm is not null) await _vm.SignOutAsync();
    }
}
