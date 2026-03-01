using System.Text;
using System.Text.Json;
using Ses.Local.Hooks;

namespace Ses.Local.Hooks.Handlers;

/// <summary>
/// UserPromptSubmit: fast keyword search on the incoming prompt.
/// Injects top-3 highly relevant memories if confidence > threshold.
/// Token budget: 2000 tokens max.
/// </summary>
internal static class UserPromptSubmitHandler
{
    private const int MaxTokenBudget   = 2000;
    private const int CharsPerToken    = 4; // rough estimate
    private const int MaxChars         = MaxTokenBudget * CharsPerToken;

    internal static async Task RunAsync()
    {
        var json  = await Console.In.ReadToEndAsync();
        if (string.IsNullOrWhiteSpace(json)) { Console.WriteLine("{}"); return; }

        Dictionary<string, object>? input;
        try { input = JsonSerializer.Deserialize(json, HooksJsonContext.Default.DictionaryStringObject); }
        catch { Console.WriteLine("{}"); return; }

        if (input is null) { Console.WriteLine("{}"); return; }

        var sessionId = input.GetValueOrDefault("session_id")?.ToString() ?? string.Empty;
        var prompt    = input.GetValueOrDefault("prompt")?.ToString() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(prompt)) { Console.WriteLine("{}"); return; }

        using var ctx = await HookContext.CreateAsync();
        var results   = await ctx.SearchMemoryAsync(prompt, limit: 5);

        // Filter to high-confidence results only (score > 0.6)
        var relevant = results.Where(r => r.Score > 0.6).Take(3).ToList();
        if (relevant.Count == 0) { Console.WriteLine("{}"); return; }

        var sb    = new StringBuilder();
        var chars = 0;
        sb.AppendLine("<relevant_memory>");
        foreach (var r in relevant)
        {
            var snippet = r.Content.Length > 500 ? r.Content[..500] + "..." : r.Content;
            if (chars + snippet.Length > MaxChars) break;
            sb.AppendLine(snippet.Trim());
            chars += snippet.Length;
        }
        sb.AppendLine("</relevant_memory>");

        var output = new Dictionary<string, string>
        {
            ["additionalContext"] = sb.ToString()
        };
        Console.WriteLine(JsonSerializer.Serialize(output, HooksJsonContext.Default.DictionaryStringString));
    }
}
