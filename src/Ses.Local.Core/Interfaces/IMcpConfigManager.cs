using Ses.Local.Core.Models;

namespace Ses.Local.Core.Interfaces;

/// <summary>
/// Manages MCP server entries across all supported host config files.
/// All write operations back up the original file before modifying it.
/// </summary>
public interface IMcpConfigManager
{
    /// <summary>Returns the MCP hosts installed on this machine (config dir or file exists).</summary>
    Task<IReadOnlyList<McpHostInfo>> DetectInstalledHostsAsync(CancellationToken ct = default);

    /// <summary>Returns the current mcpServers entries for the given host.</summary>
    Task<Dictionary<string, McpServerEntry>> ReadConfigAsync(McpHostInfo host, CancellationToken ct = default);

    /// <summary>
    /// Adds (or replaces) a server entry in the host config.
    /// Backs up the original file before writing.
    /// </summary>
    Task AddServerAsync(McpHostInfo host, McpServerConfig server, CancellationToken ct = default);

    /// <summary>
    /// Removes a server entry from the host config if it exists.
    /// Backs up the original file before writing.
    /// </summary>
    Task RemoveServerAsync(McpHostInfo host, string serverName, CancellationToken ct = default);

    /// <summary>
    /// Provisions ses-mcp for every detected host.
    /// Returns the list of hosts that were successfully updated.
    /// </summary>
    Task<IReadOnlyList<McpHostInfo>> ProvisionSesMcpAsync(CancellationToken ct = default);
}
