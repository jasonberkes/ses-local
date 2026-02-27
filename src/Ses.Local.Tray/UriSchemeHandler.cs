using Microsoft.Extensions.Logging;
using Ses.Local.Core.Interfaces;

namespace Ses.Local.Tray;

/// <summary>
/// Handles incoming ses-local://auth?refresh=...&amp;access=... URIs.
/// Registration:
///   Mac: CFBundleURLTypes in Info.plist (set up in installer, WI-948)
///   Windows: HKCU\Software\Classes\ses-local registry key (set up in installer, WI-948)
///
/// For now: exposes a method called from the installer or from a named pipe listener.
/// The actual OS URI scheme registration happens in WI-948 (installer).
/// </summary>
public sealed class UriSchemeHandler
{
    private readonly IAuthService _auth;
    private readonly ILogger<UriSchemeHandler> _logger;

    public UriSchemeHandler(IAuthService auth, ILogger<UriSchemeHandler> logger)
    {
        _auth   = auth;
        _logger = logger;
    }

    /// <summary>
    /// Parse and handle a ses-local:// URI.
    /// Called when OS activates the app with the URI after install-login redirect.
    /// </summary>
    public async Task HandleAsync(string uri, CancellationToken ct = default)
    {
        _logger.LogInformation("Handling URI scheme: {Uri}", MaskUri(uri));

        if (!uri.StartsWith("ses-local://auth", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("Unrecognised ses-local URI: {Prefix}", uri[..Math.Min(30, uri.Length)]);
            return;
        }

        try
        {
            var queryStart = uri.IndexOf('?');
            if (queryStart < 0)
            {
                _logger.LogWarning("ses-local://auth URI has no query string");
                return;
            }

            var query   = uri[(queryStart + 1)..];
            var params_ = ParseQueryString(query);

            if (!params_.TryGetValue("refresh", out var refresh) || string.IsNullOrEmpty(refresh))
            {
                _logger.LogWarning("ses-local://auth URI missing refresh token");
                return;
            }

            if (!params_.TryGetValue("access", out var access) || string.IsNullOrEmpty(access))
            {
                _logger.LogWarning("ses-local://auth URI missing access token");
                return;
            }

            await _auth.HandleAuthCallbackAsync(refresh, access, ct);
            _logger.LogInformation("Auth callback handled successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to handle ses-local URI");
        }
    }

    private static Dictionary<string, string> ParseQueryString(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            if (eq < 0) continue;
            var key = Uri.UnescapeDataString(part[..eq]);
            var val = Uri.UnescapeDataString(part[(eq + 1)..]);
            result[key] = val;
        }
        return result;
    }

    private static string MaskUri(string uri)
    {
        var q = uri.IndexOf('?');
        return q < 0 ? uri : uri[..q] + "?[redacted]";
    }
}
