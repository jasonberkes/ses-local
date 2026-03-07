using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Ses.Local.Core.Models;
using Ses.Local.Tray.Services;
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

        // Wire the native file picker into the import wizard ViewModel
        if (vm.ImportWizard is { } wizard)
            wizard.FilePicker = PickImportFileAsync;

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

    // ── notifications ────────────────────────────────────────────────────────

    private void OnNotificationActionClick(object? sender, RoutedEventArgs e)
    {
        if (_vm is null || sender is not Button { DataContext: NotificationEntry entry }) return;
        _vm.TriggerNotificationAction(entry);
        e.Handled = true;
    }

    private void OnDismissNotificationClick(object? sender, RoutedEventArgs e)
    {
        if (_vm is null || sender is not Button { DataContext: NotificationEntry entry }) return;
        _vm.DismissNotification(entry);
        e.Handled = true;
    }

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

    private async void OnQuitClick(object? sender, RoutedEventArgs e)
    {
        if (_vm is not null) await _vm.QuitAsync();
    }

    private async void OnStopDaemonClick(object? sender, RoutedEventArgs e)
    {
        if (_vm is not null) await _vm.StopDaemonAsync();
    }

    private async void OnQuitSettingsClick(object? sender, RoutedEventArgs e)
    {
        if (_vm is not null) await _vm.QuitAsync();
    }

    // ── settings ────────────────────────────────────────────────────────────

    private async void OnSignOutClick(object? sender, RoutedEventArgs e)
    {
        if (_vm is not null) await _vm.SignOutAsync();
    }

    // ── CC Config tab ────────────────────────────────────────────────────────

    private void OnModelSelectionChanged(object? sender, Avalonia.Controls.SelectionChangedEventArgs e)
    {
        if (_vm is null || sender is not Avalonia.Controls.ComboBox cb) return;
        if (cb.SelectedItem is string model)
            _vm.ChangeCcModel(model);
    }

    private void OnOpenCcSettingsClick(object? sender, RoutedEventArgs e) =>
        _vm?.OpenCcSettingsFile();

    // ── MCP server management ────────────────────────────────────────────────

    private void OnMcpToggled(object? sender, RoutedEventArgs e)
    {
        if (_vm is null || sender is not ToggleSwitch ts) return;
        if (ts.DataContext is McpServerViewModel server)
            _vm.ToggleMcpServer(server);
    }

    private void OnRemoveMcpClick(object? sender, RoutedEventArgs e)
    {
        if (_vm is null || sender is not Button btn) return;
        if (btn.DataContext is McpServerViewModel server)
            _vm.RequestRemoveMcpServer(server);
    }

    private void OnCancelRemoveMcpClick(object? sender, RoutedEventArgs e)
    {
        if (_vm is null || sender is not Button btn) return;
        if (btn.DataContext is McpServerViewModel server)
            _vm.CancelRemoveMcpServer(server);
    }

    private void OnConfirmRemoveMcpClick(object? sender, RoutedEventArgs e)
    {
        if (_vm is null || sender is not Button btn) return;
        if (btn.DataContext is McpServerViewModel server)
            _vm.ConfirmRemoveMcpServer(server);
    }

    private void OnAddMcpServerClick(object? sender, RoutedEventArgs e) =>
        _vm?.ShowAddForm();

    private void OnCancelAddMcpClick(object? sender, RoutedEventArgs e) =>
        _vm?.CancelAddForm();

    private void OnConfirmAddMcpClick(object? sender, RoutedEventArgs e) =>
        _vm?.ConfirmAddServer();

    private async void OnRestartAllMcpClick(object? sender, RoutedEventArgs e)
    {
        if (_vm is not null) await _vm.RestartAllMcpAsync();
    }

    private async void OnToggleHooksClick(object? sender, RoutedEventArgs e)
    {
        if (_vm is not null) await _vm.ToggleHooksAsync();
    }

    private async void OnViewLogsClick(object? sender, RoutedEventArgs e)
    {
        if (_vm is not null) await _vm.ToggleLogsExpandedAsync();
    }

    // ── Import wizard ────────────────────────────────────────────────────────

    private void OnImportSourceClaudeClick(object? sender, RoutedEventArgs e)  => _vm?.ImportWizard?.SelectSource(ImportSource.Claude);
    private void OnImportSourceChatGptClick(object? sender, RoutedEventArgs e) => _vm?.ImportWizard?.SelectSource(ImportSource.ChatGPT);
    private void OnImportSourceGeminiClick(object? sender, RoutedEventArgs e)  => _vm?.ImportWizard?.SelectSource(ImportSource.Gemini);
    private void OnImportBackClick(object? sender, RoutedEventArgs e)          => _vm?.ImportWizard?.Reset();

    private async void OnPickFileClick(object? sender, RoutedEventArgs e)
    {
        if (_vm?.ImportWizard is { } wizard)
            await wizard.PickFileAsync();
    }

    private async void OnStartImportClick(object? sender, RoutedEventArgs e)
    {
        if (_vm?.ImportWizard is { } wizard)
            await wizard.StartImportAsync();
    }

    private async void OnCancelImportClick(object? sender, RoutedEventArgs e)
    {
        if (_vm?.ImportWizard is { } wizard)
            await wizard.CancelAsync();
    }

    private void OnImportMoreClick(object? sender, RoutedEventArgs e) => _vm?.ImportWizard?.ImportMore();

    private async void OnImportDoneClick(object? sender, RoutedEventArgs e)
    {
        _vm?.ImportWizard?.Reset();
        if (_vm is not null)
            await _vm.RefreshImportHistoryAsync();
    }

    private void OnReImportClick(object? sender, RoutedEventArgs e)
    {
        if (_vm is null || sender is not Button btn) return;
        if (btn.DataContext is ImportHistoryRecord entry)
            _vm.StartReImport(entry);
    }

    private async Task<string?> PickImportFileAsync(string[] extensions)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return null;

        var filter = new FilePickerFileType("Export files") { Patterns = extensions };
        var files  = await topLevel.StorageProvider.OpenFilePickerAsync(
            new FilePickerOpenOptions
            {
                Title          = "Select AI Conversation Export",
                AllowMultiple  = false,
                FileTypeFilter = [filter]
            });

        return files.Count > 0 ? files[0].Path.LocalPath : null;
    }

    // ── CLAUDE.md viewer (TRAY-4) ─────────────────────────────────────────────

    private async void OnClaudeMdRefreshClick(object? sender, RoutedEventArgs e)
    {
        if (_vm is not null) await _vm.RefreshClaudeMdProjectsAsync();
    }

    private void OnClaudeMdRowPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_vm is null || sender is not Border border) return;
        if (border.DataContext is ProjectClaudeMdViewModel project)
            _vm.ToggleClaudeMdRow(project);
    }

    private async void OnCopyClaudeMdPathClick(object? sender, RoutedEventArgs e)
    {
        if (_vm is null || sender is not Button btn) return;
        if (btn.DataContext is ProjectClaudeMdViewModel project)
        {
            var path = project.ClaudeMdPath;
            if (path is not null)
            {
                try { await (TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(path) ?? Task.CompletedTask); }
                catch { /* clipboard unavailable */ }
            }
        }
    }

    // ── Updates (TRAY-10) ────────────────────────────────────────────────────

    private async void OnCheckUpdatesClick(object? sender, RoutedEventArgs e)
    {
        if (_vm is not null) await _vm.CheckUpdatesAsync(force: true);
    }

    private async void OnApplyUpdateClick(object? sender, RoutedEventArgs e)
    {
        if (_vm is null || sender is not Button btn) return;
        if (btn.DataContext is ComponentUpdateViewModel item)
            await _vm.ApplyUpdateAsync(item);
    }

    // ── Active CC sessions (TRAY-10) ─────────────────────────────────────────

    private void OnSessionRowClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.DataContext is ActiveSessionViewModel vm)
            vm.IsExpanded = !vm.IsExpanded;
    }

    private void OnOpenTerminalClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        if (btn.DataContext is ProjectClaudeMdViewModel project)
            _vm?.OpenTerminalHere(project);
        else if (btn.DataContext is ActiveSessionViewModel session)
            session.OpenTerminal();
    }

    private void OnOpenInEditorClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        if (btn.DataContext is ProjectClaudeMdViewModel project)
            _vm?.OpenClaudeMdInEditor(project);
        else if (btn.DataContext is ActiveSessionViewModel session)
            session.OpenInEditor();
    }
}
