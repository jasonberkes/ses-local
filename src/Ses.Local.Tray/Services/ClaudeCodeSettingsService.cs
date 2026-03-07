using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ses.Local.Tray.Services;

public sealed record McpServerInfo(string Name, string ConnectionType, string Target, bool IsAvailable, bool IsDisabled = false);

public sealed record ClaudeCodeSettingsInfo(
    string ModelName,
    IReadOnlyList<string> PermissionsAllow,
    IReadOnlyList<string> PermissionsDeny,
    IReadOnlyList<McpServerInfo> McpServers,
    IReadOnlyList<string> RegisteredHooks,
    string SettingsFilePath,
    string LocalSettingsFilePath,
    DateTime? SettingsLastModified,
    DateTime? LocalSettingsLastModified);

public sealed class ClaudeCodeSettingsService : IDisposable
{
    public static readonly string[] CommonModels =
    [
        "claude-sonnet-4-6",
        "claude-opus-4-6",
        "claude-haiku-4-5"
    ];

    public string SettingsFilePath => _settingsPath;

    private readonly string _settingsPath;
    private readonly string _localSettingsPath;
    private FileSystemWatcher? _watcher;
    private DateTime _lastChangedUtc = DateTime.MinValue;

    public event EventHandler? SettingsChanged;

    public ClaudeCodeSettingsService()
    {
        var claudeDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
        _settingsPath = Path.Combine(claudeDir, "settings.json");
        _localSettingsPath = Path.Combine(claudeDir, "settings.local.json");
        SetupWatcher(claudeDir);
    }

    internal ClaudeCodeSettingsService(string settingsPath, string localSettingsPath)
    {
        _settingsPath = settingsPath;
        _localSettingsPath = localSettingsPath;
    }

    private void SetupWatcher(string directory)
    {
        if (!Directory.Exists(directory)) return;
        try
        {
            _watcher = new FileSystemWatcher(directory, "settings*.json")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
                EnableRaisingEvents = true
            };
            // Debounce: FSW can fire 2–4 times per save; suppress events within 300 ms of the last one.
            _watcher.Changed += (_, _) =>
            {
                var now = DateTime.UtcNow;
                if ((now - _lastChangedUtc).TotalMilliseconds < 300) return;
                _lastChangedUtc = now;
                SettingsChanged?.Invoke(this, EventArgs.Empty);
            };
        }
        catch { /* non-fatal — tray continues without file watching */ }
    }

    public ClaudeCodeSettingsInfo ReadSettings()
    {
        var main  = TryReadJson(_settingsPath);
        var local = TryReadJson(_localSettingsPath);
        var merged = MergeSettings(main, local);

        return new ClaudeCodeSettingsInfo(
            ModelName:                 GetModelName(merged),
            PermissionsAllow:          GetPermissionsAllow(merged),
            PermissionsDeny:           GetPermissionsDeny(merged),
            McpServers:                GetMcpServers(merged),
            RegisteredHooks:           GetHooks(merged),
            SettingsFilePath:          _settingsPath,
            LocalSettingsFilePath:     _localSettingsPath,
            SettingsLastModified:      TryGetModified(_settingsPath),
            LocalSettingsLastModified: TryGetModified(_localSettingsPath));
    }

    public void WriteModelName(string model)
    {
        var root = TryReadJson(_settingsPath) ?? new JsonObject();
        var env  = root["env"] as JsonObject ?? new JsonObject();
        env["ANTHROPIC_MODEL"] = model;
        root["env"] = env;
        WriteJson(root);
    }

    /// <summary>Toggles an MCP server on/off by moving its config between mcpServers and _mcpDisabled.</summary>
    public void ToggleMcpServer(string name, bool enable)
    {
        var root     = TryReadJson(_settingsPath) ?? new JsonObject();
        var servers  = root["mcpServers"]  as JsonObject ?? new JsonObject();
        var disabled = root["_mcpDisabled"] as JsonObject ?? new JsonObject();

        if (enable)
        {
            if (disabled[name] is JsonNode entry)
            {
                servers[name] = entry.DeepClone();
                disabled.Remove(name);
            }
        }
        else
        {
            if (servers[name] is JsonNode entry)
            {
                disabled[name] = entry.DeepClone();
                servers.Remove(name);
            }
        }

        root["mcpServers"] = servers;
        if (disabled.Count > 0)
            root["_mcpDisabled"] = disabled;
        else
            root.Remove("_mcpDisabled");

        WriteJson(root);
    }

    /// <summary>Adds a new stdio MCP server to mcpServers.</summary>
    public void AddStdioMcpServer(string name, string command, string[] args)
    {
        var root    = TryReadJson(_settingsPath) ?? new JsonObject();
        var servers = root["mcpServers"] as JsonObject ?? new JsonObject();

        var argsArr = new JsonArray();
        foreach (var arg in args) argsArr.Add(arg);

        servers[name] = new JsonObject { ["command"] = command, ["args"] = argsArr };
        root["mcpServers"] = servers;
        WriteJson(root);
    }

    /// <summary>Adds a new HTTP MCP server to mcpServers.</summary>
    public void AddHttpMcpServer(string name, string url)
    {
        var root    = TryReadJson(_settingsPath) ?? new JsonObject();
        var servers = root["mcpServers"] as JsonObject ?? new JsonObject();
        servers[name] = new JsonObject { ["url"] = url };
        root["mcpServers"] = servers;
        WriteJson(root);
    }

    /// <summary>Removes an MCP server from mcpServers (and _mcpDisabled if present).</summary>
    public void RemoveMcpServer(string name)
    {
        var root = TryReadJson(_settingsPath) ?? new JsonObject();
        (root["mcpServers"]   as JsonObject)?.Remove(name);
        (root["_mcpDisabled"] as JsonObject)?.Remove(name);
        WriteJson(root);
    }

    /// <summary>Returns true if a server name already exists in mcpServers or _mcpDisabled.</summary>
    public bool McpServerExists(string name)
    {
        var root = TryReadJson(_settingsPath) ?? new JsonObject();
        return (root["mcpServers"]   as JsonObject)?.ContainsKey(name) == true
            || (root["_mcpDisabled"] as JsonObject)?.ContainsKey(name) == true;
    }

    /// <summary>
    /// Moves the active hooks section to _hooksDisabled (toggle off).
    /// Idempotent: no-op if hooks are already empty/absent.
    /// </summary>
    public void DisableHooks()
    {
        var root = TryReadJson(_settingsPath) ?? new JsonObject();
        if (root["hooks"] is JsonObject hooksObj && hooksObj.Count > 0)
        {
            root["_hooksDisabled"] = hooksObj.DeepClone();
            root["hooks"] = new JsonObject();
            File.WriteAllText(_settingsPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    /// <summary>
    /// Restores hooks from _hooksDisabled (toggle on).
    /// Returns true if restored successfully; false if _hooksDisabled was absent
    /// (caller should trigger a fresh registration via the daemon).
    /// </summary>
    public bool EnableHooks()
    {
        var root = TryReadJson(_settingsPath) ?? new JsonObject();
        if (root["_hooksDisabled"] is JsonObject disabled && disabled.Count > 0)
        {
            root["hooks"] = disabled.DeepClone();
            root.Remove("_hooksDisabled");
            File.WriteAllText(_settingsPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            return true;
        }
        return false;
    }

    /// <summary>Returns true if hooks were explicitly disabled (the _hooksDisabled key exists with entries).</summary>
    public bool AreHooksDisabled()
    {
        var root = TryReadJson(_settingsPath);
        return root is not null && root["_hooksDisabled"] is JsonObject disabled && disabled.Count > 0;
    }

    // ── private helpers ───────────────────────────────────────────────────────

    internal static JsonObject? TryReadJson(string path)
    {
        if (!File.Exists(path)) return null;
        try { return JsonNode.Parse(File.ReadAllText(path)) as JsonObject; }
        catch { return null; }
    }

    internal static JsonObject MergeSettings(JsonObject? main, JsonObject? local)
    {
        if (main is null && local is null) return new JsonObject();
        if (main is null) return (local!.DeepClone() as JsonObject)!;
        if (local is null) return (main.DeepClone() as JsonObject)!;

        var result = (main.DeepClone() as JsonObject)!;
        foreach (var kvp in local)
        {
            if (kvp.Key == "env" && result["env"] is JsonObject mainEnv && kvp.Value is JsonObject localEnv)
            {
                foreach (var ep in localEnv)
                    mainEnv[ep.Key] = ep.Value?.DeepClone();
            }
            else
            {
                result[kvp.Key] = kvp.Value?.DeepClone();
            }
        }
        return result;
    }

    private static string GetModelName(JsonObject settings)
    {
        var model = (settings["env"] as JsonObject)?["ANTHROPIC_MODEL"]?.GetValue<string>();
        return string.IsNullOrWhiteSpace(model) ? "default" : model;
    }

    private static IReadOnlyList<string> GetPermissionsAllow(JsonObject settings) =>
        GetPermissionList(settings, "allow");

    private static IReadOnlyList<string> GetPermissionsDeny(JsonObject settings) =>
        GetPermissionList(settings, "deny");

    private static IReadOnlyList<string> GetPermissionList(JsonObject settings, string key)
    {
        var arr = (settings["permissions"] as JsonObject)?[key] as JsonArray;
        if (arr is null) return [];
        return arr.Select(x => x?.GetValue<string>() ?? "")
                  .Where(s => s.Length > 0)
                  .ToList();
    }

    private static IReadOnlyList<McpServerInfo> GetMcpServers(JsonObject settings)
    {
        var result = new List<McpServerInfo>();

        if (settings["mcpServers"] is JsonObject servers)
            foreach (var kvp in servers)
                if (ParseMcpEntry(kvp.Key, kvp.Value as JsonObject, isDisabled: false) is { } info)
                    result.Add(info);

        if (settings["_mcpDisabled"] is JsonObject disabled)
            foreach (var kvp in disabled)
                if (ParseMcpEntry(kvp.Key, kvp.Value as JsonObject, isDisabled: true) is { } info)
                    result.Add(info);

        return result;
    }

    internal static McpServerInfo? ParseMcpEntry(string name, JsonObject? obj, bool isDisabled)
    {
        if (obj is null) return null;
        string type, target;
        if (obj["url"]?.GetValue<string>() is { Length: > 0 } url)
        {
            type = "http"; target = url;
        }
        else
        {
            type = "stdio"; target = obj["command"]?.GetValue<string>() ?? "";
        }

        // Stdio servers with absolute paths are checked for existence;
        // relative/npx-style commands are assumed available.
        var available = isDisabled ? false
            : type == "http" ? !string.IsNullOrEmpty(target)
            : !Path.IsPathRooted(target) || File.Exists(target);

        return new McpServerInfo(name, type, target, available, isDisabled);
    }

    private static IReadOnlyList<string> GetHooks(JsonObject settings)
    {
        if (settings["hooks"] is not JsonObject hooks) return [];
        return hooks.Select(kvp => kvp.Key).ToList();
    }

    private static DateTime? TryGetModified(string path)
    {
        try { return File.Exists(path) ? File.GetLastWriteTime(path) : null; }
        catch { return null; }
    }

    private void WriteJson(JsonObject root) =>
        File.WriteAllText(_settingsPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

    public void Dispose() => _watcher?.Dispose();
}
