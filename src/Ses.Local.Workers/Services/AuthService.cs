using Microsoft.Extensions.Logging;
using Ses.Local.Core.Interfaces;
using Ses.Local.Core.Models;

namespace Ses.Local.Workers.Services;

/// <summary>
/// Manages the ses-local authentication lifecycle.
/// Keys stored in OS keychain:
///   ses-local-refresh → refresh token (90d sliding)
///   ses-local-pat     → PAT for ses-mcp
/// </summary>
public sealed class AuthService : IAuthService
{
    private const string KeyRefresh = "ses-local-refresh";
    private const string KeyPat     = "ses-local-pat";
    private const string LoginUrl   = "https://identity.tm.supereasysoftware.com/api/v1/install/login";

    private readonly ICredentialStore _keychain;
    private readonly IdentityClient _identity;
    private readonly ILogger<AuthService> _logger;

    // In-memory cache of current access token to avoid keychain reads on every call
    private string? _cachedAccessToken;
    private DateTime _cachedAccessTokenExpiry = DateTime.MinValue;
    private readonly SemaphoreSlim _renewLock = new(1, 1);

    public AuthService(
        ICredentialStore keychain,
        IdentityClient identity,
        ILogger<AuthService> logger)
    {
        _keychain = keychain;
        _identity = identity;
        _logger   = logger;
    }

    public async Task HandleAuthCallbackAsync(string refreshToken, string accessToken, CancellationToken ct = default)
    {
        _logger.LogInformation("Handling auth callback — storing tokens in keychain");

        // Store refresh token
        await _keychain.SetAsync(KeyRefresh, refreshToken, ct);

        // Cache access token in memory
        var expiry = ParseExpiry(accessToken);
        _cachedAccessToken       = accessToken;
        _cachedAccessTokenExpiry = expiry;

        // Derive and store PAT for ses-mcp
        await DerivePat(accessToken, ct);

        _logger.LogInformation("Authentication complete. Access token expires: {Expiry}", expiry);
    }

    public async Task<string?> GetAccessTokenAsync(CancellationToken ct = default)
    {
        // Return cached token if still valid (with 60s buffer)
        if (_cachedAccessToken is not null && DateTime.UtcNow < _cachedAccessTokenExpiry.AddSeconds(-60))
            return _cachedAccessToken;

        await _renewLock.WaitAsync(ct);
        try
        {
            // Double-check after acquiring lock
            if (_cachedAccessToken is not null && DateTime.UtcNow < _cachedAccessTokenExpiry.AddSeconds(-60))
                return _cachedAccessToken;

            return await RenewAccessTokenAsync(ct);
        }
        finally
        {
            _renewLock.Release();
        }
    }

    public async Task<SesAuthState> GetStateAsync(CancellationToken ct = default)
    {
        var token = await GetAccessTokenAsync(ct);
        if (token is null)
        {
            var refresh = await _keychain.GetAsync(KeyRefresh, ct);
            return string.IsNullOrEmpty(refresh)
                ? SesAuthState.Unauthenticated
                : SesAuthState.ReauthRequired;
        }
        return new SesAuthState
        {
            IsAuthenticated      = true,
            AccessToken          = token,
            AccessTokenExpiresAt = _cachedAccessTokenExpiry
        };
    }

    public async Task SignOutAsync(CancellationToken ct = default)
    {
        _cachedAccessToken       = null;
        _cachedAccessTokenExpiry = DateTime.MinValue;
        await _keychain.DeleteAsync(KeyRefresh, ct);
        await _keychain.DeleteAsync(KeyPat, ct);
        _logger.LogInformation("Signed out — keychain cleared");
    }

    public Task TriggerReauthAsync(CancellationToken ct = default)
    {
        // Open browser to re-auth login (no install token required for re-auth)
        var url = $"{LoginUrl}?reauth=true";
        _logger.LogInformation("Triggering re-auth: {Url}", url);
        OpenBrowser(url);
        return Task.CompletedTask;
    }

    public async Task<string?> GetPatAsync(CancellationToken ct = default) =>
        await _keychain.GetAsync(KeyPat, ct);

    // ── Private ───────────────────────────────────────────────────────────────

    private async Task<string?> RenewAccessTokenAsync(CancellationToken ct)
    {
        var refreshToken = await _keychain.GetAsync(KeyRefresh, ct);
        if (string.IsNullOrEmpty(refreshToken))
        {
            _logger.LogInformation("No refresh token in keychain — unauthenticated");
            return null;
        }

        var result = await _identity.RefreshTokenAsync(refreshToken, ct);
        if (result is null)
        {
            _logger.LogWarning("Token refresh failed — triggering re-auth");
            await TriggerReauthAsync(ct);
            return null;
        }

        // Rotate refresh token in keychain
        await _keychain.SetAsync(KeyRefresh, result.RefreshToken, ct);

        // Cache new access token
        _cachedAccessToken       = result.AccessToken;
        _cachedAccessTokenExpiry = result.AccessTokenExpiresAt;

        _logger.LogDebug("Access token renewed. Expires: {Expiry}", result.AccessTokenExpiresAt);
        return result.AccessToken;
    }

    private async Task DerivePat(string accessToken, CancellationToken ct)
    {
        try
        {
            var pat = await _identity.CreatePatAsync(
                accessToken,
                name: "ses-mcp",
                scopes: ["memory:local", "memory:cloud", "conv:read"],
                ct);

            if (pat?.Token is not null)
            {
                await _keychain.SetAsync(KeyPat, pat.Token, ct);
                _logger.LogInformation("ses-mcp PAT stored in keychain");
            }
            else
            {
                _logger.LogWarning("PAT derivation failed — ses-mcp will be unauthenticated");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PAT derivation failed");
        }
    }

    private static DateTime ParseExpiry(string jwt)
    {
        try
        {
            var parts = jwt.Split('.');
            if (parts.Length < 2) return DateTime.UtcNow.AddMinutes(15);

            var padded = parts[1].Replace('-', '+').Replace('_', '/');
            var rem = padded.Length % 4;
            if (rem == 2) padded += "==";
            else if (rem == 3) padded += "=";

            var json = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(padded));
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("exp", out var expProp) && expProp.TryGetInt64(out var exp))
                return DateTimeOffset.FromUnixTimeSeconds(exp).UtcDateTime;

            return DateTime.UtcNow.AddMinutes(15);
        }
        catch
        {
            return DateTime.UtcNow.AddMinutes(15);
        }
    }

    private static void OpenBrowser(string url)
    {
        try
        {
            if (OperatingSystem.IsMacOS())
                System.Diagnostics.Process.Start("open", url);
            else if (OperatingSystem.IsWindows())
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception)
        {
            // Best effort
        }
    }
}
