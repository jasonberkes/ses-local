using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ses.Local.Tray.Services;

public sealed record McpServerInfo(string Name, string ConnectionType, string Target, bool IsAvailable);

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
        File.WriteAllText(_settingsPath,
            root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
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
        if (settings["mcpServers"] is not JsonObject servers) return [];

        var result = new List<McpServerInfo>();
        foreach (var kvp in servers)
        {
            if (kvp.Value is not JsonObject obj) continue;
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
            var available = type == "http"
                ? !string.IsNullOrEmpty(target)
                : !Path.IsPathRooted(target) || File.Exists(target);

            result.Add(new McpServerInfo(kvp.Key, type, target, available));
        }
        return result;
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

    public void Dispose() => _watcher?.Dispose();
}
