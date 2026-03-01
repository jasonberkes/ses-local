using System.Text.Json;
using Ses.Local.Hooks;

namespace Ses.Local.Hooks.Handlers;

/// <summary>
/// PostToolUse: captures tool use observation to local SQLite / ses-local API.
/// Fire-and-forget: exits quickly, does not block Claude Code.
/// </summary>
internal static class PostToolUseHandler
{
    internal static async Task RunAsync()
    {
        var json = await Console.In.ReadToEndAsync();
        if (string.IsNullOrWhiteSpace(json)) return;

        Dictionary<string, object>? input;
        try { input = JsonSerializer.Deserialize(json, HooksJsonContext.Default.DictionaryStringObject); }
        catch { return; }
        if (input is null) return;

        var sessionId    = input.GetValueOrDefault("session_id")?.ToString() ?? string.Empty;
        var tool         = input.GetValueOrDefault("tool")?.ToString() ?? "unknown";
        var inputSummary = SummarizeInput(input.GetValueOrDefault("input"));
        var output       = Truncate(input.GetValueOrDefault("output")?.ToString(), 500);

        var content = $"Tool: {tool}\nInput: {inputSummary}\nOutput: {output}";

        using var ctx = await HookContext.CreateAsync();
        await ctx.SaveObservationAsync(sessionId, content, "tool_use", tool, importance: 0.4);
    }

    private static string SummarizeInput(object? input)
    {
        if (input is null) return string.Empty;
        var str = input.ToString() ?? string.Empty;
        return Truncate(str, 200);
    }

    private static string Truncate(string? s, int max) =>
        s is null ? string.Empty : s.Length > max ? s[..max] + "..." : s;
}
