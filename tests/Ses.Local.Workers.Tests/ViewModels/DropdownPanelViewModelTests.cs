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
        Assert.Contains("ChatGPT",        names);
    }

    [Fact]
    public void ChatGptFeature_IsMarkedComingSoon()
    {
        var vm     = CreateVm();
        var chatGpt = vm.ConvSyncFeatures.First(f => f.Name == "ChatGPT");

        Assert.True(chatGpt.IsComingSoon);
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
}
