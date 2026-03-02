using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Ses.Local.Workers.Services;

/// <summary>Calls the identity server to validate a license key online (revocation check).</summary>
public sealed class LicenseValidationClient
{
    private readonly HttpClient _http;
    private readonly ILogger<LicenseValidationClient> _logger;

    private static readonly JsonSerializerOptions s_json = new() { PropertyNameCaseInsensitive = true };

    public LicenseValidationClient(HttpClient http, ILogger<LicenseValidationClient> logger)
    {
        _http = http;
        _logger = logger;
    }

    /// <summary>
    /// Validate a license key against the identity server.
    /// Returns null if the server is unreachable (offline mode allowed).
    /// </summary>
    public async Task<LicenseValidationResponse?> ValidateAsync(string licenseKey, CancellationToken ct)
    {
        try
        {
            var response = await _http.PostAsJsonAsync(
                "api/v1/licenses/validate",
                new { licenseKey },
                s_json, ct);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("License validation returned {Status}", response.StatusCode);
                return null;
            }

            return await response.Content.ReadFromJsonAsync<LicenseValidationResponse>(s_json, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "License validation request failed — falling back to offline mode");
            return null;
        }
    }

    /// <summary>
    /// Fetch the RSA public key PEM from the identity server's JWKS endpoint.
    /// Used on first activation to embed the key for offline validation.
    /// </summary>
    public async Task<string?> FetchPublicKeyPemAsync(CancellationToken ct)
    {
        try
        {
            var jwks = await _http.GetFromJsonAsync<JwksResponse>(".well-known/jwks.json", s_json, ct);
            if (jwks?.Keys is null || jwks.Keys.Length == 0)
                return null;

            // Find the RS256 signing key
            var key = jwks.Keys.FirstOrDefault(k =>
                string.Equals(k.Use, "sig", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(k.Kty, "RSA", StringComparison.OrdinalIgnoreCase));

            if (key is null) return null;

            return ExportRsaPublicKeyPem(key);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch JWKS public key");
            return null;
        }
    }

    private static string? ExportRsaPublicKeyPem(JwkKey key)
    {
        try
        {
            var n = Base64UrlDecode(key.N);
            var e = Base64UrlDecode(key.E);

            using var rsa = System.Security.Cryptography.RSA.Create();
            rsa.ImportParameters(new System.Security.Cryptography.RSAParameters
            {
                Modulus = n,
                Exponent = e,
            });

            return rsa.ExportSubjectPublicKeyInfoPem();
        }
        catch
        {
            return null;
        }
    }

    private static byte[] Base64UrlDecode(string base64Url)
    {
        var padded = base64Url.Replace('-', '+').Replace('_', '/');
        padded = (padded.Length % 4) switch { 2 => padded + "==", 3 => padded + "=", _ => padded };
        return Convert.FromBase64String(padded);
    }
}

public sealed record LicenseValidationResponse
{
    public bool IsValid { get; init; }
    public Guid LicenseId { get; init; }
    public string Email { get; init; } = string.Empty;
    public DateTime ExpiresAt { get; init; }
    public string? InvalidReason { get; init; }
}

internal sealed class JwksResponse
{
    public JwkKey[] Keys { get; init; } = [];
}

internal sealed class JwkKey
{
    public string Kty { get; init; } = string.Empty;
    public string Use { get; init; } = string.Empty;
    public string Kid { get; init; } = string.Empty;
    public string Alg { get; init; } = string.Empty;
    public string N { get; init; } = string.Empty;
    public string E { get; init; } = string.Empty;
}
