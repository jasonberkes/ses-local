using Ses.Local.Core.Models;

namespace Ses.Local.Tray.Services;

/// <summary>Reads MCP server configuration from Claude Desktop's config file (read-only).</summary>
public sealed class ClaudeDesktopConfigService
{
    private readonly string _configPath;

    public ClaudeDesktopConfigService()
    {
        _configPath = OperatingSystem.IsMacOS()
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Library", "Application Support", "Claude", "claude_desktop_config.json")
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Claude", "claude_desktop_config.json");
    }

    internal ClaudeDesktopConfigService(string configPath)
    {
        _configPath = configPath;
    }

    public IReadOnlyList<McpServerInfo> ReadMcpServers()
    {
        var config = ClaudeDesktopConfig.Load(_configPath);
        return config.McpServers
            .Select(kvp =>
            {
                var target    = kvp.Value.Command ?? "";
                var available = !Path.IsPathRooted(target) || File.Exists(target);
                return new McpServerInfo(kvp.Key, "stdio", target, available);
            })
            .ToList();
    }
}
