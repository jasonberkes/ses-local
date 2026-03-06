using Ses.Local.Core.Models;

namespace Ses.Local.Core.Interfaces;

/// <summary>
/// Manages ses-local authentication lifecycle.
/// - Receives tokens from http://localhost:37780/auth/callback (RFC 8252 §7.3 loopback redirect)
/// - Stores refresh token and PAT in OS keychain
/// - Silently renews access tokens
/// - Triggers re-auth flow when refresh token expires
/// </summary>
public interface IAuthService
{
    /// <summary>Handle the OAuth loopback callback from the identity server (http://localhost:37780/auth/callback).</summary>
    Task HandleAuthCallbackAsync(string refreshToken, string accessToken, CancellationToken ct = default);

    /// <summary>Get a valid access token, renewing silently if expired.</summary>
    Task<string?> GetAccessTokenAsync(CancellationToken ct = default);

    /// <summary>Current auth state.</summary>
    Task<SesAuthState> GetStateAsync(CancellationToken ct = default);

    /// <summary>Sign out — clear all stored tokens.</summary>
    Task SignOutAsync(CancellationToken ct = default);

    /// <summary>Open browser to re-auth login page (refresh token expired).</summary>
    Task TriggerReauthAsync(CancellationToken ct = default);

    /// <summary>Gets the current MCP PAT for use by the browser extension listener.</summary>
    Task<string?> GetPatAsync(CancellationToken ct = default);
}
