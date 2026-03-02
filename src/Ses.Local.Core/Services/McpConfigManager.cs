using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Ses.Local.Core.Interfaces;
using Ses.Local.Core.Models;

namespace Ses.Local.Core.Services;

/// <summary>
/// Manages MCP server entries across all supported host config files.
/// Uses JsonNode for surgical edits — preserves all unknown fields.
/// </summary>
public sealed class McpConfigManager : IMcpConfigManager
{
    private static readonly JsonSerializerOptions s_writeOpts =
        new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly ILogger<McpConfigManager> _logger;

    public McpConfigManager(ILogger<McpConfigManager> logger) => _logger = logger;

    // ── Host detection ─────────────────────────────────────────────────────────

    public Task<IReadOnlyList<McpHostInfo>> DetectInstalledHostsAsync(CancellationToken ct = default)
    {
        var home   = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var result = new List<McpHostInfo>();

        foreach (var (host, path) in GetHostConfigPaths(home))
        {
            // Detect by directory presence (even if config file doesn't exist yet,
            // the app is installed if the parent directory exists).
            var dir = Path.GetDirectoryName(path);
            if (dir is not null && Directory.Exists(dir))
                result.Add(new McpHostInfo { Host = host, ConfigPath = path });
        }

        return Task.FromResult<IReadOnlyList<McpHostInfo>>(result);
    }

    // ── Read ───────────────────────────────────────────────────────────────────

    public Task<Dictionary<string, McpServerEntry>> ReadConfigAsync(
        McpHostInfo host, CancellationToken ct = default)
    {
        var servers = ReadMcpServers(host.ConfigPath);
        return Task.FromResult(servers);
    }

    // ── Add ────────────────────────────────────────────────────────────────────

    public Task AddServerAsync(McpHostInfo host, McpServerConfig server, CancellationToken ct = default)
    {
        var root = LoadOrCreateRoot(host.ConfigPath);
        var mcp  = EnsureMcpServersObject(root);

        var entry = new JsonObject { ["command"] = server.Command };

        if (server.Args.Count > 0)
        {
            var arr = new JsonArray();
            foreach (var a in server.Args) arr.Add(JsonValue.Create(a));
            entry["args"] = arr;
        }

        if (server.Env is { Count: > 0 })
        {
            var envObj = new JsonObject();
            foreach (var kv in server.Env)
                envObj[kv.Key] = kv.Value;
            entry["env"] = envObj;
        }

        mcp[server.Name] = entry;

        WriteWithBackup(host.ConfigPath, root);
        _logger.LogInformation("Added MCP server '{Name}' to {Host}", server.Name, host.Host);
        return Task.CompletedTask;
    }

    // ── Remove ─────────────────────────────────────────────────────────────────

    public Task RemoveServerAsync(McpHostInfo host, string serverName, CancellationToken ct = default)
    {
        if (!File.Exists(host.ConfigPath))
            return Task.CompletedTask;

        var root = LoadOrCreateRoot(host.ConfigPath);
        var mcp  = GetMcpServersObject(root);
        if (mcp is null || !mcp.ContainsKey(serverName))
            return Task.CompletedTask;

        mcp.Remove(serverName);
        WriteWithBackup(host.ConfigPath, root);
        _logger.LogInformation("Removed MCP server '{Name}' from {Host}", serverName, host.Host);
        return Task.CompletedTask;
    }

    // ── Provision ses-mcp ──────────────────────────────────────────────────────

    public async Task<IReadOnlyList<McpHostInfo>> ProvisionSesMcpAsync(CancellationToken ct = default)
    {
        var hosts       = await DetectInstalledHostsAsync(ct);
        var sesMcpPath  = GetSesMcpBinaryPath();
        var server      = new McpServerConfig
        {
            Name    = "ses-mcp",
            Command = sesMcpPath,
            Args    = [],
        };

        var provisioned = new List<McpHostInfo>();
        foreach (var host in hosts)
        {
            try
            {
                await AddServerAsync(host, server, ct);
                provisioned.Add(host);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to provision ses-mcp for {Host}", host.Host);
            }
        }

        return provisioned;
    }

    // ── Static helpers ─────────────────────────────────────────────────────────

    /// <summary>Returns the expected config path for each known MCP host.</summary>
    public static IEnumerable<(McpHost Host, string Path)> GetHostConfigPaths(string home)
    {
        if (OperatingSystem.IsMacOS())
        {
            yield return (
                McpHost.ClaudeDesktop,
                Path.Combine(home, "Library", "Application Support", "Claude", "claude_desktop_config.json"));

            yield return (
                McpHost.ClaudeCode,
                Path.Combine(home, ".claude.json"));

            yield return (
                McpHost.Cursor,
                Path.Combine(home, ".cursor", "mcp.json"));

            yield return (
                McpHost.VsCodeContinue,
                Path.Combine(home, ".continue", "config.json"));
        }
        else if (OperatingSystem.IsLinux())
        {
            yield return (
                McpHost.ClaudeDesktop,
                Path.Combine(home, ".config", "Claude", "claude_desktop_config.json"));

            yield return (
                McpHost.ClaudeCode,
                Path.Combine(home, ".claude.json"));

            yield return (
                McpHost.Cursor,
                Path.Combine(home, ".cursor", "mcp.json"));

            yield return (
                McpHost.VsCodeContinue,
                Path.Combine(home, ".continue", "config.json"));
        }
        else if (OperatingSystem.IsWindows())
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            yield return (
                McpHost.ClaudeDesktop,
                Path.Combine(appData, "Claude", "claude_desktop_config.json"));

            yield return (
                McpHost.ClaudeCode,
                Path.Combine(home, ".claude.json"));

            yield return (
                McpHost.Cursor,
                Path.Combine(appData, "Cursor", "User", "globalStorage", "cursor.mcp", "mcp.json"));

            yield return (
                McpHost.VsCodeContinue,
                Path.Combine(home, ".continue", "config.json"));
        }
    }

    /// <summary>Path to the ses-mcp binary installed by the ses installer.</summary>
    public static string GetSesMcpBinaryPath()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".ses", "bin", "ses-mcp");
    }

    // ── Private helpers ────────────────────────────────────────────────────────

    private static JsonObject LoadOrCreateRoot(string path)
    {
        if (File.Exists(path))
        {
            try
            {
                var text = File.ReadAllText(path);
                if (!string.IsNullOrWhiteSpace(text) && JsonNode.Parse(text) is JsonObject obj)
                    return obj;
            }
            catch { /* fall through to empty */ }
        }

        return new JsonObject();
    }

    private static JsonObject EnsureMcpServersObject(JsonObject root)
    {
        if (root["mcpServers"] is JsonObject existing)
            return existing;

        var mcp = new JsonObject();
        root["mcpServers"] = mcp;
        return mcp;
    }

    private static JsonObject? GetMcpServersObject(JsonObject root) =>
        root["mcpServers"] as JsonObject;

    private static Dictionary<string, McpServerEntry> ReadMcpServers(string path)
    {
        if (!File.Exists(path))
            return new Dictionary<string, McpServerEntry>();

        try
        {
            var text = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(text))
                return new Dictionary<string, McpServerEntry>();

            var root = JsonNode.Parse(text) as JsonObject;
            if (root?["mcpServers"] is not JsonObject mcp)
                return new Dictionary<string, McpServerEntry>();

            var result = new Dictionary<string, McpServerEntry>();
            foreach (var kv in mcp)
            {
                if (kv.Value is not JsonObject entryNode) continue;

                var entry = new McpServerEntry
                {
                    Command = entryNode["command"]?.GetValue<string>() ?? string.Empty,
                };

                if (entryNode["args"] is JsonArray argsArr)
                {
                    foreach (var a in argsArr)
                    {
                        var s = a?.GetValue<string>();
                        if (s is not null) entry.Args.Add(s);
                    }
                }

                if (entryNode["env"] is JsonObject envObj)
                {
                    entry.Env = new Dictionary<string, string>();
                    foreach (var ev in envObj)
                    {
                        var v = ev.Value?.GetValue<string>();
                        if (v is not null) entry.Env[ev.Key] = v;
                    }
                }

                result[kv.Key] = entry;
            }

            return result;
        }
        catch
        {
            return new Dictionary<string, McpServerEntry>();
        }
    }

    private static void WriteWithBackup(string path, JsonObject root)
    {
        // Back up the original before touching it
        if (File.Exists(path))
        {
            var bakPath = path + ".bak";
            File.Copy(path, bakPath, overwrite: true);
        }

        var dir = Path.GetDirectoryName(path);
        if (dir is not null)
            Directory.CreateDirectory(dir);

        File.WriteAllText(path, root.ToJsonString(s_writeOpts));
    }
}
