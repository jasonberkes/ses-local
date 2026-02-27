using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Ses.Local.Workers.Services;

public sealed class ClaudeAiClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly ILogger<ClaudeAiClient> _logger;
    private string? _orgId;

    // Rate limiting: 5 req/s token bucket
    private readonly SemaphoreSlim _rateLimiter = new(5, 5);
    private static readonly TimeSpan RateWindow = TimeSpan.FromSeconds(1);

    public ClaudeAiClient(string sessionCookie, ILogger<ClaudeAiClient> logger)
    {
        _logger = logger;
        _http   = new HttpClient
        {
            BaseAddress = new Uri("https://claude.ai"),
            Timeout     = TimeSpan.FromSeconds(30)
        };
        // Set the cookie in both possible formats
        _http.DefaultRequestHeaders.Add("Cookie",
            $"sessionKey={sessionCookie}; __Host-next-auth.session-token={sessionCookie}");
        _http.DefaultRequestHeaders.Add("User-Agent",
            "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
        _http.DefaultRequestHeaders.Add("Referer", "https://claude.ai/");
    }

    public async Task<string?> GetOrgIdAsync(CancellationToken ct = default)
    {
        if (_orgId is not null) return _orgId;
        await RateLimitAsync(ct);
        try
        {
            var res = await _http.GetAsync("api/organizations", ct);
            if (res.StatusCode == HttpStatusCode.Unauthorized) return null;
            res.EnsureSuccessStatusCode();
            var orgs = await res.Content.ReadFromJsonAsync<List<ClaudeOrg>>(
                ClaudeApiJsonContext.Default.ListClaudeOrg, ct);
            _orgId = orgs?.FirstOrDefault()?.Uuid;
            return _orgId;
        }
        catch (Exception ex) { _logger.LogDebug(ex, "GetOrgId failed"); return null; }
    }

    public async IAsyncEnumerable<ClaudeConversationMeta> ListConversationsAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var orgId = await GetOrgIdAsync(ct);
        if (orgId is null) yield break;

        int offset = 0;
        const int limit = 50;
        while (true)
        {
            await RateLimitAsync(ct);
            List<ClaudeConversationMeta>? page;
            try
            {
                var res = await _http.GetAsync(
                    $"api/organizations/{orgId}/chat_conversations?limit={limit}&offset={offset}", ct);
                if (!res.IsSuccessStatusCode) yield break;
                page = await res.Content.ReadFromJsonAsync<List<ClaudeConversationMeta>>(
                    ClaudeApiJsonContext.Default.ListClaudeConversationMeta, ct);
            }
            catch { yield break; }

            if (page is null || page.Count == 0) yield break;
            foreach (var item in page) yield return item;
            if (page.Count < limit) yield break;
            offset += limit;
        }
    }

    public async Task<ClaudeConversation?> GetConversationAsync(string uuid, CancellationToken ct = default)
    {
        var orgId = await GetOrgIdAsync(ct);
        if (orgId is null) return null;
        await RateLimitAsync(ct);
        try
        {
            var res = await _http.GetAsync(
                $"api/organizations/{orgId}/chat_conversations/{uuid}", ct);
            if (!res.IsSuccessStatusCode) return null;
            return await res.Content.ReadFromJsonAsync<ClaudeConversation>(
                ClaudeApiJsonContext.Default.ClaudeConversation, ct);
        }
        catch (Exception ex) { _logger.LogDebug(ex, "GetConversation {Uuid} failed", uuid); return null; }
    }

    private async Task RateLimitAsync(CancellationToken ct)
    {
        await _rateLimiter.WaitAsync(ct);
        _ = Task.Delay(RateWindow, ct).ContinueWith(_ =>
        {
            try { _rateLimiter.Release(); } catch { }
        }, TaskContinuationOptions.None);
    }

    public void Dispose() => _http.Dispose();
}

// ── DTOs ──────────────────────────────────────────────────────────────────────

public sealed class ClaudeOrg
{
    [JsonPropertyName("uuid")] public string Uuid { get; set; } = string.Empty;
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
}

public sealed class ClaudeConversationMeta
{
    [JsonPropertyName("uuid")]       public string Uuid       { get; set; } = string.Empty;
    [JsonPropertyName("name")]       public string Name       { get; set; } = string.Empty;
    [JsonPropertyName("updated_at")] public DateTime UpdatedAt { get; set; }
    [JsonPropertyName("created_at")] public DateTime CreatedAt { get; set; }
}

public sealed class ClaudeConversation
{
    [JsonPropertyName("uuid")]          public string Uuid        { get; set; } = string.Empty;
    [JsonPropertyName("name")]          public string Name        { get; set; } = string.Empty;
    [JsonPropertyName("created_at")]    public DateTime CreatedAt  { get; set; }
    [JsonPropertyName("updated_at")]    public DateTime UpdatedAt  { get; set; }
    [JsonPropertyName("chat_messages")] public List<ClaudeMessage> Messages { get; set; } = [];
}

public sealed class ClaudeMessage
{
    [JsonPropertyName("uuid")]       public string Uuid       { get; set; } = string.Empty;
    [JsonPropertyName("sender")]     public string Sender     { get; set; } = string.Empty;
    [JsonPropertyName("text")]       public string Text       { get; set; } = string.Empty;
    [JsonPropertyName("created_at")] public DateTime CreatedAt { get; set; }
}

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(List<ClaudeOrg>))]
[JsonSerializable(typeof(List<ClaudeConversationMeta>))]
[JsonSerializable(typeof(ClaudeConversation))]
internal partial class ClaudeApiJsonContext : JsonSerializerContext { }
