using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Ses.Local.Core.Interfaces;
using Ses.Local.Core.Models;
using Ses.Local.Core.Options;
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

        var factory = BuildFailingFactory();
        var updater = BuildUpdater();
        var auth    = new Mock<IAuthService>();

        var manager = new SesMcpManager(factory, keychain.Object, updater, auth.Object,
            NullLogger<SesMcpManager>.Instance, Options.Create(new SesLocalOptions()));

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

        var factory = BuildFailingFactory();
        var updater = BuildUpdater();
        var auth    = new Mock<IAuthService>();

        var manager = new SesMcpManager(factory, keychain.Object, updater, auth.Object,
            NullLogger<SesMcpManager>.Instance, Options.Create(new SesLocalOptions()));

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
    public void ClaudeDesktopConfig_RemovesSesCloud_WhenPresent()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.json");
        try
        {
            // Arrange: config has ses-cloud entry
            var config = new ClaudeDesktopConfig();
            config.McpServers["ses-cloud"] = new McpServerEntry
            {
                Command = "npx",
                Args    = ["-y", "@anthropic-ai/mcp-proxy", "https://mcp.tm.supereasysoftware.com/mcp"]
            };
            config.McpServers["ses-local"] = new McpServerEntry
            {
                Command = "/Users/test/.ses/ses-mcp",
                Args    = ["--transport", "stdio", "--skip-update"]
            };
            config.Save(tempPath);

            // Act: simulate the cleanup step
            var loaded = ClaudeDesktopConfig.Load(tempPath);
            var removed = loaded.McpServers.Remove("ses-cloud");
            loaded.Save(tempPath);

            // Assert: ses-cloud gone, ses-local preserved
            Assert.True(removed);
            var reloaded = ClaudeDesktopConfig.Load(tempPath);
            Assert.False(reloaded.McpServers.ContainsKey("ses-cloud"));
            Assert.True(reloaded.McpServers.ContainsKey("ses-local"));
        }
        finally
        {
            File.Delete(tempPath);
        }
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
        return new SesMcpUpdater(NullLogger<SesMcpUpdater>.Instance, http,
            () => "/nonexistent/path/ses-mcp");
    }

    private static IHttpClientFactory BuildFailingFactory()
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>()))
               .Returns(() => new HttpClient(new FakeHttpHandler()));
        return factory.Object;
    }

    private sealed class FakeHttpHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable));
    }
}
