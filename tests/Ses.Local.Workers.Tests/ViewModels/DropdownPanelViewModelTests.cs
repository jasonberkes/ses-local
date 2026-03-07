using Microsoft.Extensions.Options;
using Moq;
using Ses.Local.Core.Interfaces;
using Ses.Local.Core.Models;
using Ses.Local.Core.Options;
using Ses.Local.Tray.Services;
using Ses.Local.Tray.ViewModels;
using Xunit;

namespace Ses.Local.Workers.Tests.ViewModels;

public sealed class DropdownPanelViewModelTests
{
    // DaemonAuthProxy constructor only registers a connect callback — no actual socket connection until first request.
    private static readonly DaemonAuthProxy s_fakeProxy = new(Options.Create(new SesLocalOptions()));

    private static DropdownPanelViewModel CreateVm(IAuthService? auth = null)
    {
        if (auth is null)
        {
            var mock = new Mock<IAuthService>();
            mock.Setup(x => x.GetStateAsync(default)).ReturnsAsync(SesAuthState.Unauthenticated);
            auth = mock.Object;
        }
        return new DropdownPanelViewModel(auth, s_fakeProxy, Options.Create(new SesLocalOptions()));
    }

    [Fact]
    public void Constructor_InitializesFeatureRows()
    {
        var vm = CreateVm();

        Assert.Equal(5, vm.ConvSyncFeatures.Count);
        Assert.Equal(4, vm.MemoryFeatures.Count);
    }

    [Fact]
    public void ConvSyncFeatures_ContainsExpectedNames()
    {
        var vm    = CreateVm();
        var names = vm.ConvSyncFeatures.Select(f => f.Name).ToList();

        Assert.Contains("Claude.ai",      names);
        Assert.Contains("Claude Desktop", names);
        Assert.Contains("Claude Code",    names);
        Assert.Contains("Cowork",         names);
        Assert.Contains("ChatGPT Desktop", names);
    }

    [Fact]
    public void ChatGptFeature_IsNotComingSoon()
    {
        var vm      = CreateVm();
        var chatGpt = vm.ConvSyncFeatures.First(f => f.Name == "ChatGPT Desktop");

        Assert.False(chatGpt.IsComingSoon);
    }

    [Fact]
    public async Task SignOutAsync_CallsAuthService()
    {
        var auth = new Mock<IAuthService>();
        auth.Setup(x => x.GetStateAsync(default)).ReturnsAsync(SesAuthState.Unauthenticated);
        auth.Setup(x => x.SignOutAsync(default)).Returns(Task.CompletedTask);

        var vm = new DropdownPanelViewModel(auth.Object, s_fakeProxy, Options.Create(new SesLocalOptions()));
        await vm.SignOutAsync();

        auth.Verify(x => x.SignOutAsync(default), Times.Once);
    }

    [Fact]
    public void ToggleFeature_UpdatesFeatureEnabledState()
    {
        var vm      = CreateVm();
        var feature = vm.ConvSyncFeatures.First(f => f.Key == "claude_code_sync");

        vm.ToggleFeature(feature, false);

        Assert.False(feature.IsEnabled);
    }

    [Fact]
    public void DefaultSelectedTab_IsStatus()
    {
        var vm = CreateVm();

        Assert.Equal(PanelTab.Status, vm.SelectedTab);
        Assert.True(vm.IsStatusTab);
        Assert.False(vm.IsCcConfigTab);
        Assert.False(vm.IsImportTab);
        Assert.False(vm.IsSettingsTab);
    }

    [Fact]
    public void SelectTab_SwitchesActiveTab()
    {
        var vm = CreateVm();

        vm.SelectTab(PanelTab.CcConfig);

        Assert.Equal(PanelTab.CcConfig, vm.SelectedTab);
        Assert.False(vm.IsStatusTab);
        Assert.True(vm.IsCcConfigTab);
        Assert.False(vm.IsImportTab);
        Assert.False(vm.IsSettingsTab);
    }

    [Fact]
    public void SelectTab_Import_SetsImportTabActive()
    {
        var vm = CreateVm();

        vm.SelectTab(PanelTab.Import);

        Assert.True(vm.IsImportTab);
        Assert.False(vm.IsStatusTab);
        Assert.False(vm.IsCcConfigTab);
        Assert.False(vm.IsSettingsTab);
    }

    [Fact]
    public void SelectTab_Settings_SetsSettingsTabActive()
    {
        var vm = CreateVm();

        vm.SelectTab(PanelTab.Settings);

        Assert.True(vm.IsSettingsTab);
        Assert.False(vm.IsStatusTab);
    }

    [Fact]
    public async Task UpdateStatusAsync_WhenAuthenticated_SetsGreenDot()
    {
        var auth = new Mock<IAuthService>();
        auth.Setup(x => x.GetStateAsync(default))
            .ReturnsAsync(new SesAuthState { IsAuthenticated = true });

        var vm = new DropdownPanelViewModel(auth.Object, s_fakeProxy, Options.Create(new SesLocalOptions()));
        await vm.UpdateStatusAsync();

        Assert.Equal(StatusDot.Green, vm.StatusDotColor);
        Assert.Equal("Connected",     vm.StatusText);
    }

    [Fact]
    public async Task UpdateStatusAsync_WhenUnauthenticated_SetsGreyDot()
    {
        var auth = new Mock<IAuthService>();
        auth.Setup(x => x.GetStateAsync(default)).ReturnsAsync(SesAuthState.Unauthenticated);

        var vm = new DropdownPanelViewModel(auth.Object, s_fakeProxy, Options.Create(new SesLocalOptions()));
        await vm.UpdateStatusAsync();

        Assert.Equal(StatusDot.Grey,   vm.StatusDotColor);
        Assert.Equal("Not activated", vm.StatusText);
    }

    [Fact]
    public async Task UpdateStatusAsync_WhenDaemonThrows_SetsRedDot()
    {
        var auth = new Mock<IAuthService>();
        auth.Setup(x => x.GetStateAsync(default)).ThrowsAsync(new Exception("daemon down"));

        var vm = new DropdownPanelViewModel(auth.Object, s_fakeProxy, Options.Create(new SesLocalOptions()));
        await vm.UpdateStatusAsync();

        Assert.Equal(StatusDot.Red,        vm.StatusDotColor);
        Assert.Equal("Daemon not running", vm.StatusText);
    }

    [Fact]
    public async Task SignOutAsync_ClearsStatusToGrey()
    {
        var callCount = 0;
        var auth = new Mock<IAuthService>();
        auth.Setup(x => x.GetStateAsync(default))
            .ReturnsAsync(() =>
            {
                callCount++;
                // First call (LoadAsync) returns authenticated; subsequent calls (after sign-out) unauthenticated
                return callCount == 1
                    ? new SesAuthState { IsAuthenticated = true }
                    : SesAuthState.Unauthenticated;
            });
        auth.Setup(x => x.SignOutAsync(default)).Returns(Task.CompletedTask);

        var vm = new DropdownPanelViewModel(auth.Object, s_fakeProxy, Options.Create(new SesLocalOptions()));
        await vm.SignOutAsync();

        Assert.Equal(StatusDot.Grey,  vm.StatusDotColor);
        Assert.Equal("Not activated", vm.StatusText);
        Assert.Equal("Signed out",    vm.UserDisplayName);
    }

    [Fact]
    public void Constructor_InitializesComponents()
    {
        var vm = CreateVm();

        Assert.Equal(3, vm.Components.Count);
        Assert.Equal("ses-local-daemon", vm.Components[0].Name);
        Assert.Equal("ses-mcp",          vm.Components[1].Name);
        Assert.Equal("ses-hooks",        vm.Components[2].Name);
    }

    [Fact]
    public async Task RefreshComponentsAsync_WhenDaemonUnreachable_SetsErrorState()
    {
        // DaemonAuthProxy will fail to connect since no daemon is running — GetComponentsAsync returns null
        var vm = CreateVm();
        await vm.RefreshComponentsAsync();

        Assert.All(vm.Components, c => Assert.Equal(ComponentState.Error, c.State));
    }

    // ── MCP management ────────────────────────────────────────────────────────

    private static DropdownPanelViewModel CreateVmWithSettings(
        string settingsPath, string localSettingsPath)
    {
        var mock = new Mock<IAuthService>();
        mock.Setup(x => x.GetStateAsync(default)).ReturnsAsync(SesAuthState.Unauthenticated);
        var svc = new ClaudeCodeSettingsService(settingsPath, localSettingsPath);
        return new DropdownPanelViewModel(mock.Object, s_fakeProxy,
            Options.Create(new SesLocalOptions()), ccSettings: svc);
    }

    [Fact]
    public void ConfirmAddServer_Stdio_AddsServerToCollection()
    {
        var dir  = Path.Combine(Path.GetTempPath(), $"vm-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var settings = Path.Combine(dir, "settings.json");
        var local    = Path.Combine(dir, "settings.local.json");
        File.WriteAllText(settings, "{}");

        try
        {
            var vm = CreateVmWithSettings(settings, local);
            vm.AddName    = "myserver";
            vm.AddIsStdio = true;
            vm.AddCommand = "npx";
            vm.AddArgs    = "some-pkg";

            vm.ConfirmAddServer();

            var json = File.ReadAllText(settings);
            Assert.Contains("myserver", json);
            Assert.Contains("npx", json);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void ConfirmAddServer_EmptyName_SetsValidationError()
    {
        var vm = CreateVm();
        vm.AddName    = "  ";
        vm.AddIsStdio = true;
        vm.AddCommand = "npx";

        vm.ConfirmAddServer();

        Assert.True(vm.HasAddValidationError);
        Assert.False(string.IsNullOrEmpty(vm.AddValidationError));
    }

    [Fact]
    public void ConfirmAddServer_EmptyCommand_SetsValidationError()
    {
        var dir  = Path.Combine(Path.GetTempPath(), $"vm-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var settings = Path.Combine(dir, "settings.json");
        var local    = Path.Combine(dir, "settings.local.json");
        File.WriteAllText(settings, "{}");

        try
        {
            var vm = CreateVmWithSettings(settings, local);
            vm.AddName    = "myserver";
            vm.AddIsStdio = true;
            vm.AddCommand = "";

            vm.ConfirmAddServer();

            Assert.True(vm.HasAddValidationError);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void ConfirmAddServer_DuplicateName_SetsValidationError()
    {
        var dir  = Path.Combine(Path.GetTempPath(), $"vm-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var settings = Path.Combine(dir, "settings.json");
        var local    = Path.Combine(dir, "settings.local.json");
        File.WriteAllText(settings, """
            { "mcpServers": { "existing": { "command": "npx", "args": [] } } }
            """);

        try
        {
            var vm = CreateVmWithSettings(settings, local);
            vm.AddName    = "existing";
            vm.AddIsStdio = true;
            vm.AddCommand = "npx";

            vm.ConfirmAddServer();

            Assert.True(vm.HasAddValidationError);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void ConfirmRemoveMcpServer_RemovesFromCollectionAndFile()
    {
        var dir  = Path.Combine(Path.GetTempPath(), $"vm-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var settings = Path.Combine(dir, "settings.json");
        var local    = Path.Combine(dir, "settings.local.json");
        File.WriteAllText(settings, """
            { "mcpServers": { "context7": { "command": "npx", "args": ["context7"] } } }
            """);

        try
        {
            var vm = CreateVmWithSettings(settings, local);
            vm.SelectTab(PanelTab.CcConfig);

            var server = vm.CcMcpServers.First(s => s.Name == "context7");
            vm.ConfirmRemoveMcpServer(server);

            Assert.DoesNotContain(vm.CcMcpServers, s => s.Name == "context7");
            var json = File.ReadAllText(settings);
            Assert.DoesNotContain("context7", json);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void RequestRemoveMcpServer_ProtectedServer_DoesNothing()
    {
        var vm     = CreateVm();
        var server = new McpServerViewModel(
            new Ses.Local.Tray.Services.McpServerInfo("ses-local", "stdio", "/bin/ses-mcp", true));

        vm.RequestRemoveMcpServer(server);

        Assert.False(server.ShowRemoveConfirm);
    }

    [Fact]
    public void ShowAddForm_ResetsFormFields()
    {
        var vm = CreateVm();
        vm.AddName    = "old";
        vm.AddCommand = "old-cmd";

        vm.ShowAddForm();

        Assert.Empty(vm.AddName);
        Assert.Empty(vm.AddCommand);
        Assert.True(vm.IsAddFormVisible);
    }

    [Fact]
    public void CancelAddForm_HidesForm()
    {
        var vm = CreateVm();
        vm.ShowAddForm();

        vm.CancelAddForm();

        Assert.False(vm.IsAddFormVisible);
    }

    // ── Hooks (TRAY-3) ────────────────────────────────────────────────────────

    [Fact]
    public void HooksToggleLabel_WhenHooksRegistered_IsDisable()
    {
        var vm = CreateVm();
        vm.CcHooksSummary = "SessionStart, PostToolUse"; // non-(none) → enabled

        Assert.True(vm.HooksEnabled);
        Assert.Equal("Disable", vm.HooksToggleLabel);
    }

    [Fact]
    public void HooksToggleLabel_WhenNoHooksRegistered_IsEnable()
    {
        var vm = CreateVm();
        vm.CcHooksSummary = "(none)"; // disabled

        Assert.False(vm.HooksEnabled);
        Assert.Equal("Enable", vm.HooksToggleLabel);
    }

    [Fact]
    public void HasLastActivity_ReturnsFalseWhenEmpty()
    {
        var vm = CreateVm();
        vm.HooksLastActivity = string.Empty;

        Assert.False(vm.HasLastActivity);
    }

    [Fact]
    public void HasLastActivity_ReturnsTrueWhenSet()
    {
        var vm = CreateVm();
        vm.HooksLastActivity = "2 minutes ago";

        Assert.True(vm.HasLastActivity);
    }

    [Fact]
    public async Task RefreshHooksStatusAsync_WhenDaemonUnreachable_SetsGreyAndMessage()
    {
        var vm = CreateVm();
        await vm.RefreshHooksStatusAsync();

        Assert.Equal(StatusDot.Grey, vm.HooksStatusDot);
        Assert.Equal("Daemon not reachable", vm.HooksStatusText);
    }

    [Fact]
    public async Task ToggleLogsExpandedAsync_TogglesTwice_CollapsesOnSecondCall()
    {
        var vm = CreateVm();

        // First call: daemon unreachable → logs empty but panel expands
        await vm.ToggleLogsExpandedAsync();
        Assert.True(vm.IsLogsExpanded);

        // Second call: collapses
        await vm.ToggleLogsExpandedAsync();
        Assert.False(vm.IsLogsExpanded);
    }

    // ── SyncStats / Dashboard (TRAY-8) ────────────────────────────────────────

    [Fact]
    public void ApplySyncStats_UpdatesFeatureRowLastActivity()
    {
        var vm = CreateVm();

        // Ensure known enabled state regardless of persisted SesConfig on disk
        var claudeAi      = vm.ConvSyncFeatures.First(f => f.Key == "claude_ai_sync");
        var claudeDesktop = vm.ConvSyncFeatures.First(f => f.Key == "claude_desktop_sync");
        var claudeCode    = vm.ConvSyncFeatures.First(f => f.Key == "claude_code_sync");
        claudeAi.IsEnabled      = true;
        claudeDesktop.IsEnabled = true;
        claudeCode.IsEnabled    = true;

        vm.ApplySyncStats(new SyncStats
        {
            ClaudeChat         = new SurfaceStats { Count = 847, LastActivity = DateTime.UtcNow.AddMinutes(-3) },
            ClaudeCode         = new SurfaceStats { Count = 234, LastActivity = DateTime.UtcNow.AddMinutes(-1) },
            TotalConversations = 1081,
            TotalMessages      = 45000,
            LocalDbSizeBytes   = 47_185_920,
            OldestConversation = new DateTime(2023, 3, 15, 0, 0, 0, DateTimeKind.Utc),
            NewestConversation = new DateTime(2026, 3, 6, 0, 0, 0, DateTimeKind.Utc),
        });

        // Both Claude.ai and Claude Desktop map to ClaudeChat
        Assert.Contains("847", claudeAi.LastActivity);
        Assert.Contains("847", claudeDesktop.LastActivity);
        Assert.Contains("234", claudeCode.LastActivity);
    }

    [Fact]
    public void ApplySyncStats_UpdatesTotalsProperties()
    {
        var vm = CreateVm();
        var stats = new SyncStats
        {
            TotalConversations = 2284,
            TotalMessages      = 156432,
            LocalDbSizeBytes   = 47_424_512,
            OldestConversation = new DateTime(2023, 3, 15, 0, 0, 0, DateTimeKind.Utc),
            NewestConversation = new DateTime(2026, 3, 6, 0, 0, 0, DateTimeKind.Utc),
        };

        vm.ApplySyncStats(stats);

        Assert.Contains("2,284", vm.TotalConversationsText);
        Assert.Contains("156,432", vm.TotalMessagesText);
        Assert.Contains("MB", vm.LocalDbSizeText);
        Assert.NotEqual("—", vm.OldestConversationText);
        Assert.NotEqual("—", vm.NewestConversationText);
    }

    [Fact]
    public void ApplySyncStats_ChatGptDesktopFeature_HasCorrectKey()
    {
        var vm = CreateVm();
        var chatGpt = vm.ConvSyncFeatures.First(f => f.Key == "chatgpt_desktop_sync");

        Assert.Equal("ChatGPT Desktop", chatGpt.Name);
        Assert.False(chatGpt.IsComingSoon);
    }

    [Fact]
    public void ApplySyncStats_DisabledFeature_ShowsDisabled()
    {
        var vm = CreateVm();
        var cowork = vm.ConvSyncFeatures.First(f => f.Key == "cowork_sync");
        cowork.IsEnabled = false;

        vm.ApplySyncStats(new SyncStats
        {
            Cowork = new SurfaceStats { Count = 5, LastActivity = DateTime.UtcNow }
        });

        Assert.Equal("Disabled", cowork.LastActivity);
    }

    [Fact]
    public void ToggleFeature_PersistsToSesConfig()
    {
        var vm      = CreateVm();
        var feature = vm.ConvSyncFeatures.First(f => f.Key == "claude_code_sync");

        vm.ToggleFeature(feature, false);

        // Reload config to verify persistence
        var config = SesConfig.Load();
        Assert.True(config.FeatureFlags.TryGetValue("claude_code_sync", out var val) && val == false);
    }

    // ── Import History (TRAY-6) ────────────────────────────────────────────────

    [Fact]
    public void Constructor_ImportHistoryIsEmpty()
    {
        var vm = CreateVm();
        Assert.Empty(vm.ImportHistory);
        Assert.False(vm.HasImportHistory);
    }

    [Fact]
    public void StartReImport_WhenFileMissing_SetsReImportMessage()
    {
        var vm    = CreateVm();
        var entry = new ImportHistoryRecord
        {
            Source   = "claude",
            FilePath = "/nonexistent/path/export.json",
        };

        vm.StartReImport(entry);

        Assert.NotEmpty(vm.ImportReImportMessage);
        Assert.True(vm.HasReImportMessage);
    }

    [Fact]
    public void StartReImport_WhenFileExists_ClearsReImportMessage()
    {
        var tmpFile = Path.GetTempFileName();
        try
        {
            var wizard = new ImportWizardViewModel(
                (_, _) => Task.FromResult(true),
                _ => Task.FromResult<ImportStatusResponse?>(null),
                _ => Task.CompletedTask);

            var mock = new Mock<IAuthService>();
            mock.Setup(x => x.GetStateAsync(default)).ReturnsAsync(SesAuthState.Unauthenticated);
            var vm = new DropdownPanelViewModel(mock.Object, s_fakeProxy, Options.Create(new SesLocalOptions()),
                importWizard: wizard);

            var entry = new ImportHistoryRecord { Source = "claude", FilePath = tmpFile };

            vm.StartReImport(entry);

            Assert.Empty(vm.ImportReImportMessage);
            Assert.False(vm.HasReImportMessage);
            // Wizard should be on instructions step with pre-populated file
            Assert.True(wizard.IsInstructionsStep);
            Assert.Equal(tmpFile, wizard.FilePath);
        }
        finally
        {
            File.Delete(tmpFile);
        }
    }

    [Fact]
    public void SelectTab_Import_SetsIsImportTab()
    {
        var vm = CreateVm();

        vm.SelectTab(PanelTab.Import);

        Assert.True(vm.IsImportTab);
    }
}
