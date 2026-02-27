using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ses.Local.Core.Models;

/// <summary>
/// Represents ~/.claude/settings.json — Claude Code user settings.
/// Uses JsonNode for safe partial manipulation (preserves unknown fields).
/// </summary>
public sealed class ClaudeCodeSettings
{
    private JsonObject _root;

    private ClaudeCodeSettings(JsonObject root) => _root = root;

    public static ClaudeCodeSettings LoadOrCreate(string path)
    {
        if (File.Exists(path))
        {
            try
            {
                var text = File.ReadAllText(path);
                if (JsonNode.Parse(text) is JsonObject obj)
                    return new ClaudeCodeSettings(obj);
            }
            catch { }
        }
        return new ClaudeCodeSettings(new JsonObject());
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, _root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>
    /// Returns true if all 6 ses-hooks entries are present and point to the correct binary.
    /// </summary>
    public bool HasCorrectHooks(string hooksPath)
    {
        foreach (var eventName in SesHookEvents)
        {
            if (!HasHookEntry(eventName, hooksPath))
                return false;
        }
        return true;
    }

    /// <summary>
    /// Adds or repairs all 6 ses-hooks entries. Preserves existing unrelated hooks.
    /// </summary>
    public void UpsertSesHooks(string hooksPath)
    {
        if (_root["hooks"] is not JsonObject hooksObj)
        {
            hooksObj = new JsonObject();
            _root["hooks"] = hooksObj;
        }

        foreach (var eventName in SesHookEvents)
        {
            UpsertHookEntry(hooksObj, eventName, hooksPath);
        }
    }

    /// <summary>
    /// Removes all ses-hooks entries (used when developer scope is revoked).
    /// </summary>
    public void RemoveSesHooks(string hooksPath)
    {
        if (_root["hooks"] is not JsonObject hooksObj) return;

        foreach (var eventName in SesHookEvents)
        {
            RemoveHookEntry(hooksObj, eventName, hooksPath);
        }
    }

    // ── Internals ─────────────────────────────────────────────────────────────

    public static readonly string[] SesHookEvents =
        ["SessionStart", "UserPromptSubmit", "PostToolUse", "PreCompact", "Stop", "SubagentStop"];

    private bool HasHookEntry(string eventName, string hooksPath)
    {
        if (_root["hooks"] is not JsonObject hooksObj) return false;
        if (hooksObj[eventName] is not JsonArray arr) return false;

        var command = BuildCommand(eventName, hooksPath);
        foreach (var item in arr)
        {
            // Check both formats Claude Code supports
            var cmd = item?["command"]?.GetValue<string>()
                   ?? item?["hooks"]?[0]?["command"]?.GetValue<string>();
            if (cmd == command) return true;
        }
        return false;
    }

    private static void UpsertHookEntry(JsonObject hooksObj, string eventName, string hooksPath)
    {
        var command = BuildCommand(eventName, hooksPath);

        if (hooksObj[eventName] is not JsonArray arr)
        {
            arr = new JsonArray();
            hooksObj[eventName] = arr;
        }

        // Remove stale ses-hooks entries for this event
        for (int i = arr.Count - 1; i >= 0; i--)
        {
            var item = arr[i];
            var cmd = item?["command"]?.GetValue<string>()
                   ?? item?["hooks"]?[0]?["command"]?.GetValue<string>();
            if (cmd is not null && cmd.Contains("ses-hooks"))
                arr.RemoveAt(i);
        }

        // Add the correct entry
        var entry = new JsonObject
        {
            ["hooks"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"]    = "command",
                    ["command"] = command
                }
            }
        };

        // PostToolUse needs an empty matcher
        if (eventName == "PostToolUse")
            entry["matcher"] = "";

        arr.Add(entry);
    }

    private static void RemoveHookEntry(JsonObject hooksObj, string eventName, string hooksPath)
    {
        if (hooksObj[eventName] is not JsonArray arr) return;
        for (int i = arr.Count - 1; i >= 0; i--)
        {
            var item = arr[i];
            var cmd = item?["command"]?.GetValue<string>()
                   ?? item?["hooks"]?[0]?["command"]?.GetValue<string>();
            if (cmd is not null && cmd.Contains("ses-hooks"))
                arr.RemoveAt(i);
        }
        if (arr.Count == 0)
            hooksObj.Remove(eventName);
    }

    private static string BuildCommand(string eventName, string hooksPath) =>
        $"{hooksPath} {eventName}";
}
