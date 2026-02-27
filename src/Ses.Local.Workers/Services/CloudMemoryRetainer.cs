using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Ses.Local.Core.Models;

namespace Ses.Local.Workers.Services;

/// <summary>
/// Retains key observations from conversations to cloud memory service.
/// Uses a simple heuristic to extract observations rather than an AI call,
/// keeping this dependency-free and resilient.
/// </summary>
public sealed class CloudMemoryRetainer
{
    private readonly ILogger<CloudMemoryRetainer> _logger;
    private const string MemoryBaseUrl = "https://memory.tm.supereasysoftware.com";
    private static readonly JsonSerializerOptions s_json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public CloudMemoryRetainer(ILogger<CloudMemoryRetainer> logger) => _logger = logger;

    /// <summary>
    /// Retains a summary of the conversation as a memory.
    /// Simple heuristic: use the first assistant message as the "key observation".
    /// Returns true on success.
    /// </summary>
    public async Task<bool> RetainAsync(
        ConversationSession session,
        IReadOnlyList<ConversationMessage> messages,
        string pat,
        CancellationToken ct = default)
    {
        if (messages.Count == 0) return true; // nothing to retain

        // Extract the observation: first assistant response summary
        var firstAssistant = messages
            .Where(m => m.Role == "assistant")
            .OrderBy(m => m.CreatedAt)
            .FirstOrDefault();

        if (firstAssistant is null) return true; // no assistant messages

        // Truncate to 500 chars max for the memory content
        var content = firstAssistant.Content.Length > 500
            ? firstAssistant.Content[..500] + "..."
            : firstAssistant.Content;

        try
        {
            using var http = BuildHttpClient(pat);
            var body = new
            {
                content,
                importance = 3, // medium importance for auto-retained conv observations
                tags       = $"{session.Source.ToString().ToLower()},conversation,auto-sync"
            };

            var response = await http.PostAsJsonAsync("api/v1/memory/retain", body, s_json, ct);

            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized
             || response.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                _logger.LogDebug("Memory retain skipped — PAT lacks memory:cloud scope");
                return true; // Not a failure — just not enabled
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug("Memory retain failed: {Status}", response.StatusCode);
                return false;
            }

            return true;
        }
        catch (HttpRequestException ex) when (ex.StatusCode is null)
        {
            _logger.LogDebug("Memory service unreachable — skipping retain");
            return true; // Network down is not a sync failure
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unexpected error retaining memory for session {Id}", session.Id);
            return false;
        }
    }

    private static System.Net.Http.HttpClient BuildHttpClient(string pat)
    {
        var http = new System.Net.Http.HttpClient
        {
            BaseAddress = new Uri(MemoryBaseUrl),
            Timeout     = TimeSpan.FromSeconds(15)
        };
        http.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", pat);
        return http;
    }
}
