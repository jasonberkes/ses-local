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
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<CloudMemoryRetainer> _logger;
    private static readonly JsonSerializerOptions s_json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    // DNS health check state — avoids hammering an unresolvable host
    private bool _dnsAvailable = true;
    private DateTime _dnsRetryAfter = DateTime.MinValue;
    private bool _dnsWarningLogged;
    private static readonly TimeSpan DnsRetryInterval = TimeSpan.FromHours(1);

    public CloudMemoryRetainer(IHttpClientFactory httpClientFactory, ILogger<CloudMemoryRetainer> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger            = logger;
    }

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

        // Check DNS availability — skip if previously failed and retry window hasn't elapsed
        if (!_dnsAvailable)
        {
            if (DateTime.UtcNow < _dnsRetryAfter) return true; // silently skip

            // Retry window elapsed — attempt DNS resolution
            _dnsAvailable = await CheckDnsAsync(ct);
            if (!_dnsAvailable) return true;

            _logger.LogInformation("Cloud memory service DNS resolved — re-enabling cloud memory sync");
            _dnsWarningLogged = false;
        }

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
            var http = _httpClientFactory.CreateClient(DependencyInjection.CloudMemoryClientName);
            http.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", pat);

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
        catch (HttpRequestException ex) when (IsDnsOrNetworkError(ex))
        {
            HandleDnsFailure(ex);
            return true; // DNS/network failure is not a sync failure
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unexpected error retaining memory for session {Id}", session.Id);
            return false;
        }
    }

    private async Task<bool> CheckDnsAsync(CancellationToken ct)
    {
        try
        {
            var http = _httpClientFactory.CreateClient(DependencyInjection.CloudMemoryClientName);
            var host = http.BaseAddress?.Host;
            if (string.IsNullOrEmpty(host)) return false;

            await System.Net.Dns.GetHostEntryAsync(host, ct);
            return true;
        }
        catch
        {
            _dnsRetryAfter = DateTime.UtcNow + DnsRetryInterval;
            return false;
        }
    }

    private void HandleDnsFailure(HttpRequestException ex)
    {
        _dnsAvailable = false;
        _dnsRetryAfter = DateTime.UtcNow + DnsRetryInterval;

        if (!_dnsWarningLogged)
        {
            _logger.LogWarning("Cloud memory service unavailable ({Message}) — cloud memory sync disabled. Will retry in 1 hour",
                ex.Message);
            _dnsWarningLogged = true;
        }
    }

    private static bool IsDnsOrNetworkError(HttpRequestException ex) =>
        ex.StatusCode is null; // null StatusCode = network/DNS error, not HTTP error
}
