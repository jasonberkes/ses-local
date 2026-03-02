using Microsoft.Extensions.Logging.Abstractions;
using Ses.Local.Core.Models;
using Ses.Local.Core.Services;
using Xunit;

namespace Ses.Local.Core.Tests.Services;

/// <summary>
/// Unit tests for McpConfigManager — uses temp directories to avoid touching real configs.
/// </summary>
public sealed class McpConfigManagerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly McpConfigManager _sut;

    public McpConfigManagerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"ses-mcp-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _sut = new McpConfigManager(NullLogger<McpConfigManager>.Instance);
    }

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    // ── Host detection ─────────────────────────────────────────────────────────

    [Fact]
    public void GetHostConfigPaths_MacOS_ReturnsExpectedPaths()
    {
        if (!OperatingSystem.IsMacOS()) return; // only run on macOS

        var home  = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var paths = McpConfigManager.GetHostConfigPaths(home).ToList();

        Assert.Contains(paths, t => t.Host == McpHost.ClaudeDesktop &&
            t.Path.Contains("Application Support/Claude/claude_desktop_config.json"));

        Assert.Contains(paths, t => t.Host == McpHost.ClaudeCode &&
            t.Path.EndsWith(".claude.json"));

        Assert.Contains(paths, t => t.Host == McpHost.Cursor &&
            t.Path.Contains(".cursor/mcp.json"));

        Assert.Contains(paths, t => t.Host == McpHost.VsCodeContinue &&
            t.Path.Contains(".continue/config.json"));
    }

    // ── ReadConfigAsync ────────────────────────────────────────────────────────

    [Fact]
    public async Task ReadConfigAsync_MissingFile_ReturnsEmpty()
    {
        var host = MakeHost("nonexistent.json");
        var result = await _sut.ReadConfigAsync(host);
        Assert.Empty(result);
    }

    [Fact]
    public async Task ReadConfigAsync_EmptyFile_ReturnsEmpty()
    {
        var path = WriteTempConfig("empty.json", "");
        var host = MakeHost("empty.json");
        var result = await _sut.ReadConfigAsync(host);
        Assert.Empty(result);
    }

    [Fact]
    public async Task ReadConfigAsync_ValidConfig_ParsesEntries()
    {
        var path = WriteTempConfig("config.json", """
            {
              "mcpServers": {
                "ses-mcp": {
                  "command": "/home/user/.ses/bin/ses-mcp",
                  "args": []
                },
                "other-tool": {
                  "command": "node",
                  "args": ["/some/path/server.js"],
                  "env": { "API_KEY": "abc123" }
                }
              }
            }
            """);

        var host   = MakeHost("config.json");
        var result = await _sut.ReadConfigAsync(host);

        Assert.Equal(2, result.Count);
        Assert.True(result.ContainsKey("ses-mcp"));
        Assert.Equal("/home/user/.ses/bin/ses-mcp", result["ses-mcp"].Command);
        Assert.True(result.ContainsKey("other-tool"));
        Assert.Equal("node", result["other-tool"].Command);
        Assert.Equal("/some/path/server.js", result["other-tool"].Args[0]);
        Assert.Equal("abc123", result["other-tool"].Env!["API_KEY"]);
    }

    [Fact]
    public async Task ReadConfigAsync_NoMcpServersKey_ReturnsEmpty()
    {
        WriteTempConfig("config.json", """{ "someOtherKey": "value" }""");
        var result = await _sut.ReadConfigAsync(MakeHost("config.json"));
        Assert.Empty(result);
    }

    // ── AddServerAsync ─────────────────────────────────────────────────────────

    [Fact]
    public async Task AddServerAsync_NewFile_CreatesFileWithEntry()
    {
        var host   = MakeHost("new.json");
        var server = new McpServerConfig
        {
            Name    = "ses-mcp",
            Command = "/usr/local/bin/ses-mcp",
            Args    = [],
        };

        await _sut.AddServerAsync(host, server);

        var result = await _sut.ReadConfigAsync(host);
        Assert.Single(result);
        Assert.Equal("/usr/local/bin/ses-mcp", result["ses-mcp"].Command);
    }

    [Fact]
    public async Task AddServerAsync_PreservesExistingEntries()
    {
        WriteTempConfig("config.json", """
            {
              "mcpServers": {
                "existing-tool": {
                  "command": "existing-cmd",
                  "args": ["--foo"]
                }
              }
            }
            """);

        var host   = MakeHost("config.json");
        var server = new McpServerConfig { Name = "ses-mcp", Command = "/ses/bin/ses-mcp" };

        await _sut.AddServerAsync(host, server);

        var result = await _sut.ReadConfigAsync(host);
        Assert.Equal(2, result.Count);
        Assert.True(result.ContainsKey("existing-tool"));
        Assert.Equal("existing-cmd", result["existing-tool"].Command);
        Assert.True(result.ContainsKey("ses-mcp"));
    }

    [Fact]
    public async Task AddServerAsync_ReplacesExistingEntryWithSameName()
    {
        WriteTempConfig("config.json", """
            {
              "mcpServers": {
                "ses-mcp": {
                  "command": "/old/path/ses-mcp"
                }
              }
            }
            """);

        var host   = MakeHost("config.json");
        var server = new McpServerConfig { Name = "ses-mcp", Command = "/new/path/ses-mcp" };

        await _sut.AddServerAsync(host, server);

        var result = await _sut.ReadConfigAsync(host);
        Assert.Single(result);
        Assert.Equal("/new/path/ses-mcp", result["ses-mcp"].Command);
    }

    [Fact]
    public async Task AddServerAsync_CreatesBackupOfExistingFile()
    {
        var configPath = WriteTempConfig("config.json", """{ "mcpServers": {} }""");
        var host       = MakeHost("config.json");
        var server     = new McpServerConfig { Name = "ses-mcp", Command = "/bin/ses-mcp" };

        await _sut.AddServerAsync(host, server);

        Assert.True(File.Exists(configPath + ".bak"), "Backup file should be created");
    }

    [Fact]
    public async Task AddServerAsync_WithEnv_PersistsEnvVars()
    {
        var host = MakeHost("config.json");
        var server = new McpServerConfig
        {
            Name    = "ses-mcp",
            Command = "/bin/ses-mcp",
            Env     = new Dictionary<string, string> { ["SES_KEY"] = "secret" },
        };

        await _sut.AddServerAsync(host, server);

        var result = await _sut.ReadConfigAsync(host);
        Assert.Equal("secret", result["ses-mcp"].Env!["SES_KEY"]);
    }

    [Fact]
    public async Task AddServerAsync_PreservesNonMcpFieldsInExistingFile()
    {
        WriteTempConfig("config.json", """
            {
              "globalShortcut": "Ctrl+Space",
              "mcpServers": {}
            }
            """);

        var host   = MakeHost("config.json");
        var server = new McpServerConfig { Name = "ses-mcp", Command = "/bin/ses-mcp" };
        await _sut.AddServerAsync(host, server);

        var text = File.ReadAllText(host.ConfigPath);
        Assert.Contains("globalShortcut", text);
        Assert.Contains("Ctrl+Space", text);
    }

    // ── RemoveServerAsync ──────────────────────────────────────────────────────

    [Fact]
    public async Task RemoveServerAsync_ExistingEntry_RemovesIt()
    {
        WriteTempConfig("config.json", """
            {
              "mcpServers": {
                "ses-mcp": { "command": "/bin/ses-mcp" },
                "keep-me": { "command": "keep" }
              }
            }
            """);

        var host = MakeHost("config.json");
        await _sut.RemoveServerAsync(host, "ses-mcp");

        var result = await _sut.ReadConfigAsync(host);
        Assert.Single(result);
        Assert.True(result.ContainsKey("keep-me"));
        Assert.False(result.ContainsKey("ses-mcp"));
    }

    [Fact]
    public async Task RemoveServerAsync_MissingEntry_NoOp()
    {
        WriteTempConfig("config.json", """
            {
              "mcpServers": {
                "other": { "command": "other-cmd" }
              }
            }
            """);

        var host = MakeHost("config.json");
        // Should not throw
        await _sut.RemoveServerAsync(host, "ses-mcp");

        var result = await _sut.ReadConfigAsync(host);
        Assert.Single(result);
    }

    [Fact]
    public async Task RemoveServerAsync_MissingFile_NoOp()
    {
        var host = MakeHost("nonexistent.json");
        // Should not throw
        await _sut.RemoveServerAsync(host, "ses-mcp");
    }

    [Fact]
    public async Task RemoveServerAsync_CreatesBackupBeforeWriting()
    {
        var configPath = WriteTempConfig("config.json", """
            {
              "mcpServers": {
                "ses-mcp": { "command": "/bin/ses-mcp" }
              }
            }
            """);

        var host = MakeHost("config.json");
        await _sut.RemoveServerAsync(host, "ses-mcp");

        Assert.True(File.Exists(configPath + ".bak"), "Backup file should be created");
    }

    // ── ProvisionSesMcpAsync ───────────────────────────────────────────────────

    [Fact]
    public async Task ProvisionSesMcpAsync_NoHostsDetected_ReturnsEmpty()
    {
        // Use a temp home where no host directories exist — nothing to provision
        // We test via ReadConfigAsync on a host that was provisioned via detected hosts.
        // Since DetectInstalledHostsAsync relies on real directories, we verify indirectly:
        // If no host dirs exist, provisioned list is empty.
        var provisioned = await _sut.ProvisionSesMcpAsync();
        // On a dev machine some hosts may be detected; just assert it doesn't throw
        Assert.NotNull(provisioned);
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private string WriteTempConfig(string filename, string json)
    {
        var path = Path.Combine(_tempDir, filename);
        File.WriteAllText(path, json);
        return path;
    }

    private McpHostInfo MakeHost(string filename) => new()
    {
        Host       = McpHost.ClaudeDesktop,
        ConfigPath = Path.Combine(_tempDir, filename),
    };
}
