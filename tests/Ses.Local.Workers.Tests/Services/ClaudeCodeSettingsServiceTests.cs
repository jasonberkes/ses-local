using Ses.Local.Tray.Services;
using Xunit;

namespace Ses.Local.Workers.Tests.Services;

public sealed class ClaudeCodeSettingsServiceTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"cc-settings-test-{Guid.NewGuid():N}");
    private readonly string _settingsPath;
    private readonly string _localPath;

    public ClaudeCodeSettingsServiceTests()
    {
        Directory.CreateDirectory(_tempDir);
        _settingsPath = Path.Combine(_tempDir, "settings.json");
        _localPath    = Path.Combine(_tempDir, "settings.local.json");
    }

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    private ClaudeCodeSettingsService Create() =>
        new(_settingsPath, _localPath);

    // ── ReadSettings ──────────────────────────────────────────────────────────

    [Fact]
    public void ReadSettings_WhenNoFilesExist_ReturnsDefaults()
    {
        var svc  = Create();
        var info = svc.ReadSettings();

        Assert.Equal("default", info.ModelName);
        Assert.Empty(info.PermissionsAllow);
        Assert.Empty(info.PermissionsDeny);
        Assert.Empty(info.McpServers);
        Assert.Empty(info.RegisteredHooks);
        Assert.Null(info.SettingsLastModified);
        Assert.Null(info.LocalSettingsLastModified);
    }

    [Fact]
    public void ReadSettings_WithValidSettingsJson_ParsesModelName()
    {
        File.WriteAllText(_settingsPath, """
            {
              "env": { "ANTHROPIC_MODEL": "claude-opus-4-6" }
            }
            """);

        var info = Create().ReadSettings();

        Assert.Equal("claude-opus-4-6", info.ModelName);
    }

    [Fact]
    public void ReadSettings_WithNoEnvSection_ReturnsDefault()
    {
        File.WriteAllText(_settingsPath, """{ "permissions": {} }""");

        var info = Create().ReadSettings();

        Assert.Equal("default", info.ModelName);
    }

    [Fact]
    public void ReadSettings_WithPermissions_ParsesAllowAndDeny()
    {
        File.WriteAllText(_settingsPath, """
            {
              "permissions": {
                "allow": ["Bash(*)", "Read(*)"],
                "deny": ["Write(~/secrets)"]
              }
            }
            """);

        var info = Create().ReadSettings();

        Assert.Equal(["Bash(*)", "Read(*)"], info.PermissionsAllow);
        Assert.Equal(["Write(~/secrets)"], info.PermissionsDeny);
    }

    [Fact]
    public void ReadSettings_WithMcpServers_ParsesStdioAndHttp()
    {
        File.WriteAllText(_settingsPath, """
            {
              "mcpServers": {
                "ses-local": { "command": "/usr/local/bin/ses-mcp", "args": [] },
                "taskmaster": { "url": "https://mcp.tm.supereasysoftware.com" },
                "context7": { "command": "npx", "args": ["context7"] }
              }
            }
            """);

        var info = Create().ReadSettings();

        Assert.Equal(3, info.McpServers.Count);

        var sesLocal  = info.McpServers.First(s => s.Name == "ses-local");
        var taskmaster = info.McpServers.First(s => s.Name == "taskmaster");
        var context7   = info.McpServers.First(s => s.Name == "context7");

        Assert.Equal("stdio", sesLocal.ConnectionType);
        Assert.Equal("/usr/local/bin/ses-mcp", sesLocal.Target);

        Assert.Equal("http", taskmaster.ConnectionType);
        Assert.Equal("https://mcp.tm.supereasysoftware.com", taskmaster.Target);
        Assert.True(taskmaster.IsAvailable);

        Assert.Equal("stdio", context7.ConnectionType);
        Assert.Equal("npx", context7.Target);
        Assert.True(context7.IsAvailable); // non-rooted path → assumed available
    }

    [Fact]
    public void ReadSettings_WithHooks_ParsesHookNames()
    {
        File.WriteAllText(_settingsPath, """
            {
              "hooks": {
                "PostToolUse": [{ "matcher": "Write", "hooks": [] }],
                "PreToolUse": []
              }
            }
            """);

        var info = Create().ReadSettings();

        Assert.Contains("PostToolUse", info.RegisteredHooks);
        Assert.Contains("PreToolUse", info.RegisteredHooks);
    }

    [Fact]
    public void ReadSettings_WithMalformedJson_ReturnsDefaults()
    {
        File.WriteAllText(_settingsPath, "{ this is not valid json }");

        var info = Create().ReadSettings();

        Assert.Equal("default", info.ModelName);
        Assert.Empty(info.McpServers);
    }

    [Fact]
    public void ReadSettings_WithLocalOverride_LocalModelWins()
    {
        File.WriteAllText(_settingsPath,    """{ "env": { "ANTHROPIC_MODEL": "claude-haiku-4-5" } }""");
        File.WriteAllText(_localPath, """{ "env": { "ANTHROPIC_MODEL": "claude-opus-4-6" } }""");

        var info = Create().ReadSettings();

        Assert.Equal("claude-opus-4-6", info.ModelName);
    }

    [Fact]
    public void ReadSettings_WhenSettingsExists_ReportsLastModified()
    {
        File.WriteAllText(_settingsPath, "{}");

        var info = Create().ReadSettings();

        Assert.NotNull(info.SettingsLastModified);
        Assert.Null(info.LocalSettingsLastModified);
    }

    // ── WriteModelName ────────────────────────────────────────────────────────

    [Fact]
    public void WriteModelName_CreatesEnvSection_WhenMissing()
    {
        File.WriteAllText(_settingsPath, """{ "permissions": {} }""");

        var svc = Create();
        svc.WriteModelName("claude-opus-4-6");

        var info = svc.ReadSettings();
        Assert.Equal("claude-opus-4-6", info.ModelName);
    }

    [Fact]
    public void WriteModelName_OverwritesExistingModel()
    {
        File.WriteAllText(_settingsPath, """{ "env": { "ANTHROPIC_MODEL": "claude-haiku-4-5" } }""");

        var svc = Create();
        svc.WriteModelName("claude-sonnet-4-6");

        var info = svc.ReadSettings();
        Assert.Equal("claude-sonnet-4-6", info.ModelName);
    }

    [Fact]
    public void WriteModelName_PreservesUnknownKeys()
    {
        File.WriteAllText(_settingsPath, """
            {
              "permissions": { "allow": ["Bash(*)"] },
              "env": { "ANTHROPIC_MODEL": "old" },
              "customKey": "customValue"
            }
            """);

        var svc = Create();
        svc.WriteModelName("claude-opus-4-6");

        var json = File.ReadAllText(_settingsPath);
        Assert.Contains("customValue", json);
        Assert.Contains("Bash(*)", json);
        Assert.Contains("claude-opus-4-6", json);
    }

    // ── MergeSettings ─────────────────────────────────────────────────────────

    [Fact]
    public void MergeSettings_BothNull_ReturnsEmpty()
    {
        var result = ClaudeCodeSettingsService.MergeSettings(null, null);
        Assert.Empty(result);
    }

    [Fact]
    public void MergeSettings_OnlyMain_ReturnsMain()
    {
        var main = ClaudeCodeSettingsService.TryReadJson(_settingsPath);
        File.WriteAllText(_settingsPath, """{ "env": { "KEY": "main" } }""");
        main = ClaudeCodeSettingsService.TryReadJson(_settingsPath);

        var result = ClaudeCodeSettingsService.MergeSettings(main, null);

        Assert.Equal("main", result["env"]!["KEY"]!.GetValue<string>());
    }
}
