using Moq;
using Ses.Local.Core.Interfaces;
using Ses.Local.Core.Models;
using Ses.Local.Tray.ViewModels;
using Xunit;

namespace Ses.Local.Workers.Tests.ViewModels;

public sealed class MainWindowViewModelTests
{
    [Fact]
    public void Constructor_InitializesFeatureRows()
    {
        var auth = new Mock<IAuthService>();
        auth.Setup(x => x.GetStateAsync(default)).ReturnsAsync(SesAuthState.Unauthenticated);

        var vm = new MainWindowViewModel(auth.Object);

        Assert.Equal(5, vm.ConvSyncFeatures.Count);
        Assert.Equal(4, vm.MemoryFeatures.Count);
    }

    [Fact]
    public void ConvSyncFeatures_ContainsExpectedNames()
    {
        var auth = new Mock<IAuthService>();
        auth.Setup(x => x.GetStateAsync(default)).ReturnsAsync(SesAuthState.Unauthenticated);

        var vm = new MainWindowViewModel(auth.Object);
        var names = vm.ConvSyncFeatures.Select(f => f.Name).ToList();

        Assert.Contains("Claude.ai", names);
        Assert.Contains("Claude Desktop", names);
        Assert.Contains("Claude Code", names);
        Assert.Contains("Cowork", names);
        Assert.Contains("ChatGPT", names);
    }

    [Fact]
    public void ChatGptFeature_IsMarkedComingSoon()
    {
        var auth = new Mock<IAuthService>();
        auth.Setup(x => x.GetStateAsync(default)).ReturnsAsync(SesAuthState.Unauthenticated);

        var vm = new MainWindowViewModel(auth.Object);
        var chatGpt = vm.ConvSyncFeatures.First(f => f.Name == "ChatGPT");

        Assert.True(chatGpt.IsComingSoon);
    }

    [Fact]
    public async Task SignOutAsync_CallsAuthService()
    {
        var auth = new Mock<IAuthService>();
        auth.Setup(x => x.GetStateAsync(default)).ReturnsAsync(SesAuthState.Unauthenticated);
        auth.Setup(x => x.SignOutAsync(default)).Returns(Task.CompletedTask);

        var vm = new MainWindowViewModel(auth.Object);
        await vm.SignOutAsync();

        auth.Verify(x => x.SignOutAsync(default), Times.Once);
    }

    [Fact]
    public async Task ToggleFeatureAsync_UpdatesFeatureEnabledState()
    {
        var auth = new Mock<IAuthService>();
        auth.Setup(x => x.GetStateAsync(default)).ReturnsAsync(SesAuthState.Unauthenticated);

        var vm = new MainWindowViewModel(auth.Object);
        var feature = vm.ConvSyncFeatures.First(f => f.Key == "claude_code_sync");

        await vm.ToggleFeatureAsync(feature, false);

        Assert.False(feature.IsEnabled);
    }
}
