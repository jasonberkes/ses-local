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

    /// <summary>Uptime string from the last successful <see cref="GetStateAsync"/> call.</summary>
    public string LastKnownUptime { get; private set; } = string.Empty;

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

            LastKnownUptime = resp.Uptime;
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
            LastKnownUptime = string.Empty;
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
        OsOpen.Launch(_loginUrl);
        return Task.CompletedTask;
    }

    public Task HandleAuthCallbackAsync(string refreshToken, string accessToken, CancellationToken ct = default)
        => Task.CompletedTask; // Daemon handles auth callbacks directly via BrowserExtensionListener

    /// <summary>
    /// Forwards OAuth tokens received via the ses-local:// URL scheme to the daemon for storage.
    /// Called by the tray's macOS Apple Event handler after URL scheme activation.
    /// </summary>
    public async Task ForwardAuthCallbackAsync(string refreshToken, string accessToken, CancellationToken ct = default)
    {
        try
        {
            using var response = await _http.PostAsJsonAsync(
                "/api/auth/callback",
                new { refreshToken, accessToken },
                ct);
            response.EnsureSuccessStatusCode();
        }
        catch { /* daemon unreachable — user will need to re-authenticate */ }
    }

    public Task<string?> GetAccessTokenAsync(CancellationToken ct = default)
        => Task.FromResult<string?>(null); // Tray doesn't need access tokens

    public Task<string?> GetPatAsync(CancellationToken ct = default)
        => Task.FromResult<string?>(null); // Tray doesn't need PATs

    public bool ValidateOAuthState(string? state)
        => throw new NotSupportedException("OAuth callbacks are handled by the daemon, not the tray.");

    /// <summary>
    /// Triggers a background import on the daemon and returns immediately (202 Accepted).
    /// Poll <see cref="GetImportStatusAsync"/> for progress updates.
    /// Returns false if the daemon is unreachable or an import is already running.
    /// </summary>
    public async Task<bool> StartImportAsync(string filePath, CancellationToken ct = default)
    {
        try
        {
            var response = await _http.PostAsJsonAsync("/api/conversations/import", new { filePath }, ct);
            return response.StatusCode == System.Net.HttpStatusCode.Accepted;
        }
        catch { return false; }
    }

    /// <summary>Returns the current import progress, or null if the daemon is unreachable.</summary>
    public async Task<ImportStatusResponse?> GetImportStatusAsync(CancellationToken ct = default)
    {
        try
        {
            return await _http.GetFromJsonAsync<ImportStatusResponse>(
                "/api/conversations/import/status", s_jsonOptions, ct);
        }
        catch { return null; }
    }

    /// <summary>Requests the daemon to cancel any in-progress import.</summary>
    public async Task CancelImportAsync(CancellationToken ct = default)
    {
        try { await _http.PostAsync("/api/conversations/import/cancel", null, ct); }
        catch { /* daemon unreachable */ }
    }

    /// <summary>Returns the last 20 import history records, or null if the daemon is unreachable.</summary>
    public async Task<IReadOnlyList<ImportHistoryRecord>?> GetImportHistoryAsync(CancellationToken ct = default)
    {
        try
        {
            return await _http.GetFromJsonAsync<List<ImportHistoryRecord>>(
                "/api/conversations/import/history", s_jsonOptions, ct);
        }
        catch { return null; }
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

    /// <summary>Returns sync statistics from the daemon's /api/sync-stats endpoint, or null if daemon unreachable.</summary>
    public async Task<SyncStats?> GetSyncStatsAsync(CancellationToken ct = default)
    {
        try
        {
            return await _http.GetFromJsonAsync<SyncStats>("/api/sync-stats", s_jsonOptions, ct);
        }
        catch { return null; }
    }

    /// <summary>Returns known project directories from the daemon's /api/projects endpoint, or null if unreachable.</summary>
    public async Task<List<string>?> GetKnownProjectsAsync(CancellationToken ct = default)
    {
        try
        {
            return await _http.GetFromJsonAsync<List<string>>("/api/projects", s_jsonOptions, ct);
        }
        catch { return null; }
    }

    /// <summary>Returns per-component update availability from /api/updates/check, or null if daemon unreachable.</summary>
    public async Task<IReadOnlyList<ComponentUpdateInfo>?> CheckUpdatesAsync(CancellationToken ct = default)
    {
        try
        {
            return await _http.GetFromJsonAsync<List<ComponentUpdateInfo>>("/api/updates/check", s_jsonOptions, ct);
        }
        catch { return null; }
    }

    /// <summary>Asks the daemon to apply an update for the given component. Returns a message or null on failure.</summary>
    public async Task<string?> ApplyUpdateAsync(string component, CancellationToken ct = default)
    {
        try
        {
            var response = await _longRunHttp.PostAsync($"/api/updates/apply/{component}", null, ct);
            if (!response.IsSuccessStatusCode) return null;
            var dto = await response.Content.ReadFromJsonAsync<ApplyUpdateResponse>(s_jsonOptions, ct);
            return dto?.Message;
        }
        catch { return null; }
    }

    /// <summary>Returns the latest health report from /api/health, or null if daemon unreachable.</summary>
    public async Task<HealthReport?> GetHealthAsync(CancellationToken ct = default)
    {
        try
        {
            return await _http.GetFromJsonAsync<HealthReport>("/api/health", s_jsonOptions, ct);
        }
        catch { return null; }
    }

    /// <summary>Runs comprehensive one-shot repair via POST /api/repair, or null if daemon unreachable.</summary>
    public async Task<RepairResponse?> RepairAsync(CancellationToken ct = default)
    {
        try
        {
            var response = await _http.PostAsync("/api/repair", null, ct);
            if (!response.IsSuccessStatusCode) return null;
            return await response.Content.ReadFromJsonAsync<RepairResponse>(s_jsonOptions, ct);
        }
        catch { return null; }
    }

    /// <summary>Returns recently active Claude Code sessions from /api/sessions/active, or null if daemon unreachable.</summary>
    public async Task<IReadOnlyList<ActiveSessionInfo>?> GetActiveSessionsAsync(CancellationToken ct = default)
    {
        try
        {
            return await _http.GetFromJsonAsync<List<ActiveSessionInfo>>("/api/sessions/active", s_jsonOptions, ct);
        }
        catch { return null; }
    }

    /// <summary>Returns daemon log entries from /api/logs, or null if daemon unreachable.</summary>
    public async Task<LogEntriesResponse?> GetLogsAsync(int lines = 50, string? level = null, CancellationToken ct = default)
    {
        try
        {
            var url = level is not null
                ? $"/api/logs?lines={lines}&level={Uri.EscapeDataString(level)}"
                : $"/api/logs?lines={lines}";
            return await _http.GetFromJsonAsync<LogEntriesResponse>(url, s_jsonOptions, ct);
        }
        catch { return null; }
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

/// <summary>Progress/result DTO returned by GET /api/conversations/import/status.</summary>
public sealed class ImportStatusResponse
{
    public bool    IsRunning       { get; set; }
    public int     SessionsImported { get; set; }
    public int     MessagesImported { get; set; }
    public int     Duplicates       { get; set; }
    public int     Errors           { get; set; }
    public string  Format           { get; set; } = string.Empty;
    public bool    WasCancelled     { get; set; }
    public string? FailureMessage   { get; set; }
}

/// <summary>Per-component update info returned by /api/updates/check.</summary>
public sealed class ComponentUpdateInfo
{
    public string  Name             { get; set; } = string.Empty;
    public string? InstalledVersion { get; set; }
    public string? LatestVersion    { get; set; }
    public bool    UpdateAvailable  { get; set; }
}

/// <summary>Response from /api/updates/apply/{component}.</summary>
internal sealed class ApplyUpdateResponse
{
    public bool    Applied    { get; set; }
    public string? NewVersion { get; set; }
    public string? Message    { get; set; }
}

/// <summary>Active Claude Code session returned by /api/sessions/active.</summary>
public sealed class ActiveSessionInfo
{
    public string   ProjectName  { get; set; } = string.Empty;
    public string?  FullPath     { get; set; }
    public DateTime LastActivity { get; set; }
}

/// <summary>Log entries returned by the daemon's /api/logs endpoint.</summary>
public sealed class LogEntriesResponse
{
    public string?   File    { get; set; }
    public string[]  Entries { get; set; } = [];
}

/// <summary>Response from POST /api/repair.</summary>
public sealed class RepairResponse
{
    public List<string> Steps { get; set; } = [];
}
