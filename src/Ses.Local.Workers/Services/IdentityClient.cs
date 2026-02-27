using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Ses.Local.Workers.Services;

/// <summary>Minimal HTTP client for identity server auth endpoints.</summary>
public sealed class IdentityClient
{
    private readonly HttpClient _http;
    private readonly ILogger<IdentityClient> _logger;

    private static readonly JsonSerializerOptions s_json = new() { PropertyNameCaseInsensitive = true };

    public IdentityClient(HttpClient http, ILogger<IdentityClient> logger)
    {
        _http   = http;
        _logger = logger;
    }

    public async Task<RefreshResponse?> RefreshTokenAsync(string refreshToken, CancellationToken ct)
    {
        try
        {
            var response = await _http.PostAsJsonAsync(
                "api/v1/auth/refresh",
                new { refreshToken },
                s_json, ct);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Token refresh failed: {Status}", response.StatusCode);
                return null;
            }

            return await response.Content.ReadFromJsonAsync<RefreshResponse>(s_json, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Token refresh request failed");
            return null;
        }
    }

    public async Task<CreatePatResponse?> CreatePatAsync(string accessToken, string name, string[] scopes, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "api/v1/tokens");
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
            request.Content = JsonContent.Create(new { name, scopes }, options: s_json);

            var response = await _http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("PAT creation failed: {Status}", response.StatusCode);
                return null;
            }

            return await response.Content.ReadFromJsonAsync<CreatePatResponse>(s_json, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PAT creation request failed");
            return null;
        }
    }
}

public sealed record RefreshResponse(string AccessToken, string RefreshToken, DateTime AccessTokenExpiresAt, DateTime RefreshTokenExpiresAt);
public sealed record CreatePatResponse(string Token, string Name, string[] Scopes, DateTime? ExpiresAt);
