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

    // ── DisableHooks / EnableHooks / AreHooksDisabled ─────────────────────────

    [Fact]
    public void DisableHooks_MovesHooksToDisabledKey()
    {
        File.WriteAllText(_settingsPath, """
            {
              "hooks": {
                "PostToolUse": [{ "hooks": [{ "type": "command", "command": "ses-hooks PostToolUse" }] }]
              }
            }
            """);

        var svc = Create();
        svc.DisableHooks();

        var json = File.ReadAllText(_settingsPath);
        Assert.Contains("_hooksDisabled", json);
        Assert.Contains("PostToolUse", json);
        // Active hooks should be empty
        var info = svc.ReadSettings();
        Assert.Empty(info.RegisteredHooks);
    }

    [Fact]
    public void DisableHooks_WhenHooksAlreadyEmpty_IsNoOp()
    {
        File.WriteAllText(_settingsPath, """{ "hooks": {} }""");

        var svc = Create();
        svc.DisableHooks(); // Should not throw

        var json = File.ReadAllText(_settingsPath);
        Assert.DoesNotContain("_hooksDisabled", json);
    }

    [Fact]
    public void EnableHooks_RestoresFromDisabledKey_ReturnsTrue()
    {
        File.WriteAllText(_settingsPath, """
            {
              "hooks": {},
              "_hooksDisabled": {
                "PostToolUse": [{ "hooks": [{ "type": "command", "command": "ses-hooks PostToolUse" }] }]
              }
            }
            """);

        var svc = Create();
        var restored = svc.EnableHooks();

        Assert.True(restored);
        var json = File.ReadAllText(_settingsPath);
        Assert.DoesNotContain("_hooksDisabled", json);
        var info = svc.ReadSettings();
        Assert.Contains("PostToolUse", info.RegisteredHooks);
    }

    [Fact]
    public void EnableHooks_WhenNoDisabledKey_ReturnsFalse()
    {
        File.WriteAllText(_settingsPath, """{ "hooks": {} }""");

        var svc = Create();
        var restored = svc.EnableHooks();

        Assert.False(restored);
    }

    [Fact]
    public void AreHooksDisabled_ReturnsTrueWhenDisabledKeyExists()
    {
        File.WriteAllText(_settingsPath, """
            {
              "_hooksDisabled": {
                "PostToolUse": [{ "hooks": [{ "type": "command", "command": "ses-hooks PostToolUse" }] }]
              }
            }
            """);

        Assert.True(Create().AreHooksDisabled());
    }

    [Fact]
    public void AreHooksDisabled_ReturnsFalseWhenNoDisabledKey()
    {
        File.WriteAllText(_settingsPath, """{ "hooks": { "PostToolUse": [] } }""");

        Assert.False(Create().AreHooksDisabled());
    }

    [Fact]
    public void DisableHooks_ThenEnableHooks_RoundtripsCorrectly()
    {
        File.WriteAllText(_settingsPath, """
            {
              "hooks": {
                "PostToolUse": [{ "hooks": [{ "type": "command", "command": "ses-hooks PostToolUse" }] }],
                "SessionStart": [{ "hooks": [{ "type": "command", "command": "ses-hooks SessionStart" }] }]
              }
            }
            """);

        var svc = Create();
        svc.DisableHooks();

        var midInfo = svc.ReadSettings();
        Assert.Empty(midInfo.RegisteredHooks);
        Assert.True(svc.AreHooksDisabled());

        var restored = svc.EnableHooks();
        Assert.True(restored);
        Assert.False(svc.AreHooksDisabled());

        var info = svc.ReadSettings();
        Assert.Contains("PostToolUse", info.RegisteredHooks);
        Assert.Contains("SessionStart", info.RegisteredHooks);
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

    // ── ToggleMcpServer ───────────────────────────────────────────────────────

    [Fact]
    public void ToggleMcpServer_DisablesServer_MovesToMcpDisabled()
    {
        File.WriteAllText(_settingsPath, """
            {
              "mcpServers": {
                "context7": { "command": "npx", "args": ["context7"] }
              }
            }
            """);

        var svc = Create();
        svc.ToggleMcpServer("context7", enable: false);

        var info = svc.ReadSettings();
        var disabled = info.McpServers.FirstOrDefault(s => s.Name == "context7");

        Assert.NotNull(disabled);
        Assert.True(disabled.IsDisabled);
        var json = File.ReadAllText(_settingsPath);
        Assert.Contains("_mcpDisabled", json);
        // "context7" should only appear under _mcpDisabled, not under mcpServers
        var compact = json.Replace(" ", "").Replace("\n", "").Replace("\r", "");
        Assert.DoesNotContain("\"mcpServers\":{\"context7\"", compact);
    }

    [Fact]
    public void ToggleMcpServer_ReEnablesServer_MovesBackToMcpServers()
    {
        File.WriteAllText(_settingsPath, """
            {
              "mcpServers": {},
              "_mcpDisabled": {
                "context7": { "command": "npx", "args": ["context7"] }
              }
            }
            """);

        var svc = Create();
        svc.ToggleMcpServer("context7", enable: true);

        var info = svc.ReadSettings();
        var active = info.McpServers.FirstOrDefault(s => s.Name == "context7" && !s.IsDisabled);

        Assert.NotNull(active);
        var json = File.ReadAllText(_settingsPath);
        Assert.DoesNotContain("_mcpDisabled", json);
    }

    [Fact]
    public void ToggleMcpServer_Disable_PreservesOtherServers()
    {
        File.WriteAllText(_settingsPath, """
            {
              "mcpServers": {
                "ses-local": { "command": "/usr/local/bin/ses-mcp", "args": [] },
                "context7": { "command": "npx", "args": ["context7"] }
              }
            }
            """);

        var svc = Create();
        svc.ToggleMcpServer("context7", enable: false);

        var info = svc.ReadSettings();
        Assert.Contains(info.McpServers, s => s.Name == "ses-local" && !s.IsDisabled);
        Assert.Contains(info.McpServers, s => s.Name == "context7" && s.IsDisabled);
    }

    // ── AddMcpServer ──────────────────────────────────────────────────────────

    [Fact]
    public void AddStdioMcpServer_WritesToMcpServers()
    {
        File.WriteAllText(_settingsPath, "{}");

        var svc = Create();
        svc.AddStdioMcpServer("myserver", "npx", ["some-mcp-package"]);

        var info = svc.ReadSettings();
        var server = info.McpServers.FirstOrDefault(s => s.Name == "myserver");

        Assert.NotNull(server);
        Assert.Equal("stdio", server.ConnectionType);
        Assert.Equal("npx", server.Target);
        Assert.False(server.IsDisabled);
    }

    [Fact]
    public void AddHttpMcpServer_WritesToMcpServers()
    {
        File.WriteAllText(_settingsPath, "{}");

        var svc = Create();
        svc.AddHttpMcpServer("cloud-mcp", "https://mcp.example.com");

        var info = svc.ReadSettings();
        var server = info.McpServers.FirstOrDefault(s => s.Name == "cloud-mcp");

        Assert.NotNull(server);
        Assert.Equal("http", server.ConnectionType);
        Assert.Equal("https://mcp.example.com", server.Target);
    }

    [Fact]
    public void AddStdioMcpServer_PreservesExistingServers()
    {
        File.WriteAllText(_settingsPath, """
            { "mcpServers": { "ses-local": { "command": "/bin/ses-mcp", "args": [] } } }
            """);

        var svc = Create();
        svc.AddStdioMcpServer("new-server", "npx", []);

        var info = svc.ReadSettings();
        Assert.Equal(2, info.McpServers.Count);
        Assert.Contains(info.McpServers, s => s.Name == "ses-local");
        Assert.Contains(info.McpServers, s => s.Name == "new-server");
    }

    // ── RemoveMcpServer ───────────────────────────────────────────────────────

    [Fact]
    public void RemoveMcpServer_RemovesFromMcpServers()
    {
        File.WriteAllText(_settingsPath, """
            {
              "mcpServers": {
                "ses-local": { "command": "/bin/ses-mcp", "args": [] },
                "context7": { "command": "npx", "args": ["context7"] }
              }
            }
            """);

        var svc = Create();
        svc.RemoveMcpServer("context7");

        var info = svc.ReadSettings();
        Assert.DoesNotContain(info.McpServers, s => s.Name == "context7");
        Assert.Contains(info.McpServers, s => s.Name == "ses-local");
    }

    [Fact]
    public void RemoveMcpServer_AlsoRemovesFromMcpDisabled()
    {
        File.WriteAllText(_settingsPath, """
            {
              "mcpServers": {},
              "_mcpDisabled": { "context7": { "command": "npx", "args": [] } }
            }
            """);

        var svc = Create();
        svc.RemoveMcpServer("context7");

        var info = svc.ReadSettings();
        Assert.DoesNotContain(info.McpServers, s => s.Name == "context7");
    }

    // ── McpServerExists ───────────────────────────────────────────────────────

    [Fact]
    public void McpServerExists_ReturnsTrueForActiveServer()
    {
        File.WriteAllText(_settingsPath, """
            { "mcpServers": { "ses-local": { "command": "/bin/ses-mcp", "args": [] } } }
            """);

        Assert.True(Create().McpServerExists("ses-local"));
    }

    [Fact]
    public void McpServerExists_ReturnsTrueForDisabledServer()
    {
        File.WriteAllText(_settingsPath, """
            { "_mcpDisabled": { "context7": { "command": "npx", "args": [] } } }
            """);

        Assert.True(Create().McpServerExists("context7"));
    }

    [Fact]
    public void McpServerExists_ReturnsFalseForUnknownServer()
    {
        File.WriteAllText(_settingsPath, "{}");

        Assert.False(Create().McpServerExists("nonexistent"));
    }

    // ── ReadSettings (disabled servers) ───────────────────────────────────────

    [Fact]
    public void ReadSettings_IncludesDisabledServers_WithIsDisabledTrue()
    {
        File.WriteAllText(_settingsPath, """
            {
              "mcpServers": { "ses-local": { "command": "/bin/ses-mcp", "args": [] } },
              "_mcpDisabled": { "context7": { "command": "npx", "args": [] } }
            }
            """);

        var info = Create().ReadSettings();

        Assert.Equal(2, info.McpServers.Count);
        Assert.Contains(info.McpServers, s => s.Name == "ses-local"  && !s.IsDisabled);
        Assert.Contains(info.McpServers, s => s.Name == "context7" && s.IsDisabled);
    }
}
