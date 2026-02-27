using System.Text.Json;
using Ses.Local.Hooks;

namespace Ses.Local.Hooks.Handlers;

/// <summary>
/// SubagentStop: merge subagent observations into parent session memory.
/// Stores the subagent summary as a high-importance observation on the parent.
/// </summary>
internal static class SubagentStopHandler
{
    internal static async Task RunAsync()
    {
        var json = await Console.In.ReadToEndAsync();
        if (string.IsNullOrWhiteSpace(json)) return;

        Dictionary<string, object>? input;
        try { input = JsonSerializer.Deserialize<Dictionary<string, object>>(json); }
        catch { return; }
        if (input is null) return;

        var sessionId       = input.GetValueOrDefault("session_id")?.ToString() ?? string.Empty;
        var parentSessionId = input.GetValueOrDefault("parent_session_id")?.ToString() ?? string.Empty;
        var subSummary      = input.GetValueOrDefault("summary")?.ToString() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(subSummary)) return;

        // Store on parent session if available, otherwise on the subagent session
        var targetSession = !string.IsNullOrEmpty(parentSessionId) ? parentSessionId : sessionId;

        using var ctx = await HookContext.CreateAsync();
        var content = $"Subagent completed: {subSummary}";
        await ctx.SaveObservationAsync(targetSession, content, "subagent_stop", importance: 0.7);
    }
}
