using System.Text.Json;

namespace Ses.Local.Core.Models;

/// <summary>
/// Local config cached at ~/.ses/config.json.
/// Synced from identity server feature-profiles on startup.
/// </summary>
public sealed class SesConfig
{
    public string? UserDisplayName { get; set; }
    public Dictionary<string, bool> FeatureFlags { get; set; } = new();
    public bool IsFirstRun { get; set; } = true;

    private static readonly JsonSerializerOptions s_json = new() { WriteIndented = true };

    public static SesConfig Load()
    {
        var path = GetPath();
        if (!File.Exists(path)) return new SesConfig();
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<SesConfig>(json) ?? new SesConfig();
        }
        catch { return new SesConfig(); }
    }

    public void Save()
    {
        var path = GetPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, s_json));
    }

    private static string GetPath()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".ses", "config.json");
    }
}
