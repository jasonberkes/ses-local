using System.Text.Json;
using Ses.Local.Hooks;

namespace Ses.Local.Hooks.Handlers;

/// <summary>
/// Stop: session ended. Store a summary observation so future sessions
/// can find what was accomplished. Triggers cloud sync via ses-local API.
/// </summary>
internal static class StopHandler
{
    internal static async Task RunAsync()
    {
        var json = await Console.In.ReadToEndAsync();
        if (string.IsNullOrWhiteSpace(json)) return;

        Dictionary<string, object>? input;
        try { input = JsonSerializer.Deserialize(json, HooksJsonContext.Default.DictionaryStringObject); }
        catch { return; }
        if (input is null) return;

        var sessionId = input.GetValueOrDefault("session_id")?.ToString() ?? string.Empty;
        var numTurns  = input.GetValueOrDefault("num_turns")?.ToString() ?? "0";

        if (string.IsNullOrWhiteSpace(sessionId)) return;

        using var ctx = await HookContext.CreateAsync();

        // Store a session-end marker observation
        var summary = $"Session ended after {numTurns} turns (session: {sessionId})";
        await ctx.SaveObservationAsync(sessionId, summary, "session_stop", importance: 0.6);

        // Also call SaveSummary to trigger cloud sync
        await ctx.SaveSummaryAsync(sessionId, summary);
    }
}
