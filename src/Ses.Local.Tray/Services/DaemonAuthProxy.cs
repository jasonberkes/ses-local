using System.Net.Http.Json;
using System.Net.Sockets;
using Microsoft.Extensions.Options;
using Ses.Local.Core.Interfaces;
using Ses.Local.Core.Models;
using Ses.Local.Core.Options;
using Ses.Local.Core.Services;

namespace Ses.Local.Tray.Services;

/// <summary>
/// Implements <see cref="IAuthService"/> by calling the daemon's IPC endpoints
/// over a Unix domain socket (macOS/Linux) or named pipe (Windows).
/// </summary>
public sealed class DaemonAuthProxy : IAuthService, IDisposable
{
    private static readonly System.Text.Json.JsonSerializerOptions s_jsonOptions =
        new(System.Text.Json.JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly HttpClient _longRunHttp; // separate client for long-running operations (e.g. bulk import)
    private readonly string _loginUrl;

    public DaemonAuthProxy(IOptions<SesLocalOptions> options)
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

        // Long-running operations (import of thousands of conversations) need more headroom
        var longRunHandler = new SocketsHttpHandler
        {
            ConnectCallback = async (context, ct) =>
            {
                var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                var endpoint = new UnixDomainSocketEndPoint(sockPath);
                await socket.ConnectAsync(endpoint, ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
        };
        _longRunHttp = new HttpClient(longRunHandler)
        {
            BaseAddress = new Uri("http://ses-local-daemon"),
            Timeout     = TimeSpan.FromMinutes(15)
        };

        _loginUrl = options.Value.IdentityBaseUrl.TrimEnd('/') + "/api/v1/install/login?reauth=true";
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
                NeedsReauth     = resp.NeedsReauth,
                LoginTimedOut   = resp.LoginTimedOut,
                LicenseValid    = resp.LicenseValid,
                LicenseStatus   = resp.LicenseStatus,
            };
        }
        catch
        {
            // Daemon not reachable (socket missing or refused)
            return SesAuthState.Unauthenticated;
        }
    }

    /// <summary>Activate a license key via the daemon IPC.</summary>
    public async Task<(bool Succeeded, string? Error)> ActivateLicenseAsync(string licenseKey, CancellationToken ct = default)
    {
        try
        {
            var response = await _http.PostAsJsonAsync(
                "/api/license/activate",
                new { licenseKey },
                ct);

            if (response.IsSuccessStatusCode)
                return (true, null);

            var body = await response.Content.ReadAsStringAsync(ct);
            return (false, body);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
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
        if (OperatingSystem.IsMacOS())
            System.Diagnostics.Process.Start("open", _loginUrl);
        else if (OperatingSystem.IsWindows())
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_loginUrl) { UseShellExecute = true });
        return Task.CompletedTask;
    }

    public Task HandleAuthCallbackAsync(string refreshToken, string accessToken, CancellationToken ct = default)
        => Task.CompletedTask; // Daemon handles auth callbacks

    public Task<string?> GetAccessTokenAsync(CancellationToken ct = default)
        => Task.FromResult<string?>(null); // Tray doesn't need access tokens

    public Task<string?> GetPatAsync(CancellationToken ct = default)
        => Task.FromResult<string?>(null); // Tray doesn't need PATs

    /// <summary>
    /// Sends a Claude.ai export file to the daemon for import into local.db.
    /// Returns null if the daemon is unreachable.
    /// </summary>
    public async Task<ImportConversationsResult?> ImportConversationsAsync(
        string filePath, CancellationToken ct = default)
    {
        try
        {
            var response = await _longRunHttp.PostAsJsonAsync(
                "/api/conversations/import",
                new { filePath },
                ct);

            if (!response.IsSuccessStatusCode) return null;

            return await response.Content.ReadFromJsonAsync<ImportConversationsResult>(s_jsonOptions, ct);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Returns component health from the daemon's /api/components endpoint, or null if daemon unreachable.</summary>
    public async Task<ComponentsResponse?> GetComponentsAsync(CancellationToken ct = default)
    {
        try
        {
            return await _http.GetFromJsonAsync<ComponentsResponse>("/api/components", s_jsonOptions, ct);
        }
        catch { return null; }
    }

    /// <summary>Returns hooks health + last activity from the daemon's /api/hooks/status endpoint, or null if unreachable.</summary>
    public async Task<HooksStatusResponse?> GetHooksStatusAsync(CancellationToken ct = default)
    {
        try
        {
            return await _http.GetFromJsonAsync<HooksStatusResponse>("/api/hooks/status", s_jsonOptions, ct);
        }
        catch { return null; }
    }

    /// <summary>Returns the last 20 hook observations from the daemon's /api/hooks/logs endpoint, or null if unreachable.</summary>
    public async Task<IReadOnlyList<HookLogEntry>?> GetHookLogsAsync(CancellationToken ct = default)
    {
        try
        {
            return await _http.GetFromJsonAsync<List<HookLogEntry>>("/api/hooks/logs", s_jsonOptions, ct);
        }
        catch { return null; }
    }

    /// <summary>Requests the daemon to register ses-hooks fresh (when _hooksDisabled was absent).</summary>
    public async Task EnableHooksAsync(CancellationToken ct = default)
    {
        try { await _http.PostAsync("/api/hooks/enable", null, ct); }
        catch { /* daemon unreachable */ }
    }

    public void Dispose()
    {
        _http.Dispose();
        _longRunHttp.Dispose();
    }

    private sealed class DaemonStatusDto
    {
        public bool Authenticated { get; set; }
        public bool NeedsReauth { get; set; }
        public bool LoginTimedOut { get; set; }
        public bool LicenseValid { get; set; }
        public string LicenseStatus { get; set; } = string.Empty;
        public string Uptime { get; set; } = string.Empty;
    }
}

/// <summary>Component health DTO returned by the daemon's /api/components endpoint.</summary>
public sealed class ComponentsResponse
{
    public ComponentInfo SesMcp    { get; set; } = new();
    public ComponentInfo Daemon    { get; set; } = new();
    public ComponentInfo SesHooks  { get; set; } = new();

    public sealed class ComponentInfo
    {
        public bool    Installed  { get; set; }
        public bool    Configured { get; set; }
        public string? Version    { get; set; }
    }
}

/// <summary>Result DTO returned by the daemon's /api/conversations/import endpoint.</summary>
public sealed class ImportConversationsResult
{
    public int SessionsImported { get; set; }
    public int MessagesImported { get; set; }
    public int Duplicates       { get; set; }
    public int Errors           { get; set; }
}

/// <summary>DTO returned by the daemon's /api/hooks/status endpoint.</summary>
public sealed class HooksStatusResponse
{
    public bool      Registered   { get; set; }
    public bool      BinaryExists { get; set; }
    public DateTime? LastActivity { get; set; }
}

/// <summary>Single hook log entry returned by the daemon's /api/hooks/logs endpoint.</summary>
public sealed class HookLogEntry
{
    public DateTime Timestamp { get; set; }
    public string?  ToolName  { get; set; }
    public string?  FilePath  { get; set; }
}
