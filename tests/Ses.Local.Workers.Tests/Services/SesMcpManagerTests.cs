using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Ses.Local.Core.Interfaces;
using Ses.Local.Core.Models;
using Ses.Local.Workers.Services;
using Xunit;

namespace Ses.Local.Workers.Tests.Services;

public sealed class SesMcpManagerTests
{
    [Fact]
    public void GetClaudeDesktopConfigPath_ReturnsNonEmptyPath()
    {
        var path = SesMcpManager.GetClaudeDesktopConfigPath();
        Assert.False(string.IsNullOrEmpty(path));
        Assert.EndsWith("claude_desktop_config.json", path);
    }

    [Fact]
    public async Task CheckAndRepair_WhenBinaryMissingAndManifestUnavailable_HandlesGracefully()
    {
        var keychain = new Mock<ICredentialStore>();
        keychain.Setup(x => x.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);

        var updater = BuildUpdater();
        var auth    = new Mock<IAuthService>();

        var manager = new SesMcpManager(keychain.Object, updater, auth.Object,
            NullLogger<SesMcpManager>.Instance);

        // Should not throw even if binary missing and network unavailable
        var ex = await Record.ExceptionAsync(() => manager.CheckAndRepairAsync());
        Assert.Null(ex);
    }

    [Fact]
    public async Task CheckAndRepair_WhenPatInKeychainProvided_DoesNotThrow()
    {
        var keychain = new Mock<ICredentialStore>();
        keychain.Setup(x => x.GetAsync("ses-local-pat", It.IsAny<CancellationToken>()))
            .ReturnsAsync("tm_pat_testtoken");
        keychain.Setup(x => x.SetAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var updater = BuildUpdater();
        var auth    = new Mock<IAuthService>();

        var manager = new SesMcpManager(keychain.Object, updater, auth.Object,
            NullLogger<SesMcpManager>.Instance);

        // Should not throw regardless of whether Claude Desktop is installed
        var ex = await Record.ExceptionAsync(() => manager.CheckAndRepairAsync());
        Assert.Null(ex);
    }

    [Fact]
    public void ClaudeDesktopConfig_LoadFromMissingPath_ReturnsEmpty()
    {
        var config = ClaudeDesktopConfig.Load("/definitely/does/not/exist.json");
        Assert.NotNull(config);
        Assert.Empty(config.McpServers);
    }

    [Fact]
    public void ClaudeDesktopConfig_RoundTrip_PreservesData()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.json");
        try
        {
            var config = new ClaudeDesktopConfig();
            config.McpServers["ses-local"] = new McpServerEntry
            {
                Command = "/Users/test/.ses/ses-mcp",
                Args    = ["--transport", "stdio"],
                Env     = new Dictionary<string, string> { ["MCP_HEADERS"] = "Authorization: Bearer tm_pat_test" }
            };
            config.Save(tempPath);

            var loaded = ClaudeDesktopConfig.Load(tempPath);
            Assert.True(loaded.McpServers.ContainsKey("ses-local"));
            Assert.Equal("/Users/test/.ses/ses-mcp", loaded.McpServers["ses-local"].Command);
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    private static SesMcpUpdater BuildUpdater()
    {
        var handler = new FakeHttpHandler();
        var http    = new HttpClient(handler);
        return new SesMcpUpdater(NullLogger<SesMcpUpdater>.Instance, http);
    }

    private sealed class FakeHttpHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable));
    }
}
