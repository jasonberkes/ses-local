using System.Net.Http.Json;
using System.Net.Sockets;
using Ses.Local.Core.Interfaces;
using Ses.Local.Core.Models;
using Ses.Local.Core.Services;

namespace Ses.Local.Tray.Services;

/// <summary>
/// Implements <see cref="IAuthService"/> by calling the daemon's IPC endpoints
/// over a Unix domain socket (macOS/Linux) or named pipe (Windows).
/// </summary>
public sealed class DaemonAuthProxy : IAuthService, IDisposable
{
    private readonly HttpClient _http;

    public DaemonAuthProxy()
    {
        var sockPath = DaemonSocketPath.GetPath();
        var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (context, ct) =>
            {
                var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                var endpoint = new UnixDomainSocketEndPoint(sockPath);
                await socket.ConnectAsync(endpoint, ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
        };

        // Base address is required by HttpClient but ignored for UDS routing
        _http = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://ses-local-daemon"),
            Timeout     = TimeSpan.FromSeconds(5)
        };
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
            // Daemon not reachable (socket missing or refused)
            return SesAuthState.Unauthenticated;
        }
    }

    public async Task SignOutAsync(CancellationToken ct = default)
    {
        try { await _http.PostAsync("/api/signout", null, ct); }
        catch { /* daemon unreachable */ }
    }

    /// <summary>
    /// Request graceful daemon shutdown over the IPC socket.
    /// </summary>
    public async Task ShutdownAsync(CancellationToken ct = default)
    {
        try { await _http.PostAsync("/api/shutdown", null, ct); }
        catch { /* daemon already stopped */ }
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

    public void Dispose() => _http.Dispose();

    private sealed class DaemonStatusDto
    {
        public bool Authenticated { get; set; }
        public bool NeedsReauth { get; set; }
        public string Uptime { get; set; } = string.Empty;
    }
}
