using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ses.Local.Core.Models;

public sealed class ClaudeDesktopConfig
{
    [JsonPropertyName("mcpServers")]
    public Dictionary<string, McpServerEntry> McpServers { get; set; } = new();

    public static ClaudeDesktopConfig Load(string path)
    {
        if (!File.Exists(path)) return new ClaudeDesktopConfig();
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<ClaudeDesktopConfig>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? new ClaudeDesktopConfig();
        }
        catch { return new ClaudeDesktopConfig(); }
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var json = JsonSerializer.Serialize(this,
            new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        File.WriteAllText(path, json);
    }
}

public sealed class McpServerEntry
{
    [JsonPropertyName("command")]
    public string Command { get; set; } = string.Empty;

    [JsonPropertyName("args")]
    public List<string> Args { get; set; } = [];

    [JsonPropertyName("env")]
    public Dictionary<string, string>? Env { get; set; }
}
