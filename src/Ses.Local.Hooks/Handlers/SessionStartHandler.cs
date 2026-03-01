using System.Text;
using System.Text.Json;
using Ses.Local.Hooks;

namespace Ses.Local.Hooks.Handlers;

/// <summary>
/// SessionStart: queries local SQLite FTS5 for relevant memories based on CWD.
/// Outputs additionalContext XML for Claude to inject into the session.
/// </summary>
internal static class SessionStartHandler
{
    internal static async Task RunAsync()
    {
        var input = await ReadInputAsync();
        if (input is null) return;

        var sessionId = input.GetValueOrDefault("session_id")?.ToString() ?? string.Empty;
        var cwd       = input.GetValueOrDefault("cwd")?.ToString() ?? string.Empty;

        using var ctx = await HookContext.CreateAsync();

        // Search for memories relevant to the current working directory
        var query   = BuildQuery(cwd);
        var results = await ctx.SearchMemoryAsync(query, limit: 10);

        if (results.Count == 0)
        {
            Console.WriteLine("{}");
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine("<memory>");
        foreach (var r in results.Take(10))
        {
            var snippet = r.Content.Length > 300 ? r.Content[..300] + "..." : r.Content;
            sb.AppendLine(snippet.Trim());
        }
        sb.AppendLine("</memory>");

        var output = new Dictionary<string, string>
        {
            ["additionalContext"] = sb.ToString()
        };

        Console.WriteLine(JsonSerializer.Serialize(output, HooksJsonContext.Default.DictionaryStringString));
    }

    private static string BuildQuery(string cwd)
    {
        if (string.IsNullOrEmpty(cwd)) return "decision architecture pattern";
        // Use the last 2 path components as search terms — most specific to project
        var parts = cwd.TrimEnd('/', '\\').Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        var terms = parts.TakeLast(2).ToArray();
        return terms.Length > 0 ? string.Join(" ", terms) : "decision architecture";
    }

    private static async Task<Dictionary<string, object>?> ReadInputAsync()
    {
        try
        {
            var json = await Console.In.ReadToEndAsync();
            if (string.IsNullOrWhiteSpace(json)) return new Dictionary<string, object>();
            return JsonSerializer.Deserialize(json, HooksJsonContext.Default.DictionaryStringObject);
        }
        catch { return null; }
    }
}
