using System.Text.Json;
using Ses.Local.Hooks;

namespace Ses.Local.Hooks.Handlers;

/// <summary>
/// PreCompact: before context compression, extract and permanently store
/// key decisions/patterns. Prevents architectural decisions from being lost.
/// </summary>
internal static class PreCompactHandler
{
    internal static async Task RunAsync()
    {
        var json = await Console.In.ReadToEndAsync();
        if (string.IsNullOrWhiteSpace(json)) return;

        Dictionary<string, object>? input;
        try { input = JsonSerializer.Deserialize<Dictionary<string, object>>(json); }
        catch { return; }
        if (input is null) return;

        var sessionId      = input.GetValueOrDefault("session_id")?.ToString() ?? string.Empty;
        var contextSummary = input.GetValueOrDefault("context_summary")?.ToString() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(contextSummary)) return;

        // Extract key decisions: look for lines containing decision indicators
        var lines      = contextSummary.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var decisions  = lines
            .Where(l => ContainsDecisionKeyword(l))
            .Take(10)
            .ToList();

        if (decisions.Count == 0)
        {
            // Store the whole summary if no explicit decisions found
            decisions.Add(contextSummary.Length > 1000 ? contextSummary[..1000] : contextSummary);
        }

        using var ctx = await HookContext.CreateAsync();
        foreach (var decision in decisions)
        {
            await ctx.SaveObservationAsync(
                sessionId,
                decision.Trim(),
                "pre_compact_decision",
                importance: 0.9); // high importance — persisted before compaction
        }
    }

    private static bool ContainsDecisionKeyword(string line)
    {
        var lower = line.ToLowerInvariant();
        return lower.Contains("decided") || lower.Contains("decision") ||
               lower.Contains("pattern") || lower.Contains("architecture") ||
               lower.Contains("approach") || lower.Contains("use ") ||
               lower.Contains("don't ") || lower.Contains("always ") ||
               lower.Contains("never ") || lower.Contains("convention");
    }
}
