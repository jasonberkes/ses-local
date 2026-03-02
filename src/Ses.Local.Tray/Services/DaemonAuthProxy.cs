using System.Net.Http.Json;
using Ses.Local.Core.Interfaces;
using Ses.Local.Core.Models;

namespace Ses.Local.Tray.Services;

/// <summary>
/// Implements <see cref="IAuthService"/> by calling the daemon's HTTP API.
/// Used by the tray GUI to get auth state and trigger actions without
/// directly depending on the Workers project.
/// </summary>
public sealed class DaemonAuthProxy : IAuthService
{
    private readonly HttpClient _http;

    public DaemonAuthProxy(IHttpClientFactory factory)
    {
        _http = factory.CreateClient("daemon");
    }

    public async Task<SesAuthState> GetStateAsync(CancellationToken ct = default)
    {
        try
        {
            var resp = await _http.GetFromJsonAsync<DaemonStatusDto>("/api/status", ct);
            if (resp is null) return SesAuthState.Unauthenticated;

            return new SesAuthState
            {
                IsAuthenticated = resp.Authenticated,
                NeedsReauth     = resp.NeedsReauth
            };
        }
        catch
        {
            // Daemon not reachable
            return SesAuthState.Unauthenticated;
        }
    }

    public async Task SignOutAsync(CancellationToken ct = default)
    {
        try { await _http.PostAsync("/api/signout", null, ct); }
        catch { /* daemon unreachable */ }
    }

    public Task TriggerReauthAsync(CancellationToken ct = default)
    {
        var url = "https://identity.tm.supereasysoftware.com/api/v1/install/login?reauth=true";
        if (OperatingSystem.IsMacOS())
            System.Diagnostics.Process.Start("open", url);
        else if (OperatingSystem.IsWindows())
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        return Task.CompletedTask;
    }

    public Task HandleAuthCallbackAsync(string refreshToken, string accessToken, CancellationToken ct = default)
        => Task.CompletedTask; // Daemon handles auth callbacks

    public Task<string?> GetAccessTokenAsync(CancellationToken ct = default)
        => Task.FromResult<string?>(null); // Tray doesn't need access tokens

    public Task<string?> GetPatAsync(CancellationToken ct = default)
        => Task.FromResult<string?>(null); // Tray doesn't need PATs

    private sealed class DaemonStatusDto
    {
        public bool Authenticated { get; set; }
        public bool NeedsReauth { get; set; }
        public string Uptime { get; set; } = string.Empty;
    }
}
