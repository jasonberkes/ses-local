namespace Ses.Local.Core.Models;

/// <summary>MCP host applications that accept claude_desktop_config.json-style MCP server configs.</summary>
public enum McpHost
{
    /// <summary>Claude Desktop app — ~/Library/Application Support/Claude/claude_desktop_config.json</summary>
    ClaudeDesktop,

    /// <summary>Claude Code CLI — ~/.claude.json (mcpServers key)</summary>
    ClaudeCode,

    /// <summary>Cursor IDE — ~/.cursor/mcp.json</summary>
    Cursor,

    /// <summary>VS Code with the Continue extension — ~/.continue/config.json</summary>
    VsCodeContinue,
}

/// <summary>A detected MCP host with its resolved config file path.</summary>
public sealed class McpHostInfo
{
    public McpHost Host       { get; init; }
    public string  ConfigPath { get; init; } = string.Empty;
}

/// <summary>An MCP server to add to a host config.</summary>
public sealed class McpServerConfig
{
    /// <summary>Key name used in the mcpServers dictionary.</summary>
    public string             Name    { get; init; } = string.Empty;
    public string             Command { get; init; } = string.Empty;
    public List<string>       Args    { get; init; } = [];
    public Dictionary<string, string>? Env { get; init; }
}
