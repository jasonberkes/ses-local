using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Ses.Local.Core.Interfaces;
using Ses.Local.Core.Models;
using Ses.Local.Core.Services;
using Ses.Local.Workers.Services;

namespace Ses.Local.Workers.Workers;

/// <summary>
/// Monitors all daemon components every 30 seconds and auto-repairs issues.
/// Checks: auth tokens, ses-mcp binary, Claude Desktop config, CC hooks, SQLite integrity, IPC socket.
/// Auto-repairs with exponential backoff: 30 s wait before attempt 2, 90 s before attempt 3,
/// then blocked until 1-hour window resets.
/// Exposes results via GET /api/health.
/// </summary>
public sealed class HealthMonitorWorker : BackgroundService
{
    private readonly IAuthService _auth;
    private readonly ILocalDbService _localDb;
    private readonly SesMcpManager _sesMcpManager;
    private readonly ILogger<HealthMonitorWorker> _logger;

    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(30);

    // Repair state: checkName → (attempts, lastAttempt)
    private readonly ConcurrentDictionary<string, (int Attempts, DateTime LastAttempt)> _repairState = new();

    private volatile HealthReport _latestReport = new()
    {
        CheckedAt = DateTime.UtcNow,
        Status = OverallStatus.Healthy,
        Checks = []
    };

    public HealthReport LatestReport => _latestReport;

    // Check name constants — used as both API response Name fields and repair state keys.
    private static class CheckNames
    {
        public const string Auth          = "Auth";
        public const string SesMcp        = "ses-mcp";
        public const string ClaudeDesktop = "ClaudeDesktop";
        public const string CcHooks       = "CCHooks";
        public const string Socket        = "Socket";
        public const string Sqlite        = "SQLite";
    }

    public HealthMonitorWorker(
        IAuthService auth,
        ILocalDbService localDb,
        SesMcpManager sesMcpManager,
        ILogger<HealthMonitorWorker> logger)
    {
        _auth = auth;
        _localDb = localDb;
        _sesMcpManager = sesMcpManager;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Run immediately on startup
        await RunChecksAsync(stoppingToken);

        using var timer = new PeriodicTimer(CheckInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
                await RunChecksAsync(stoppingToken);
        }
        catch (OperationCanceledException) { }
    }

    private async Task RunChecksAsync(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(25)); // each full check run must complete within 25s

        try
        {
            // Run async checks concurrently; sync checks wrap in Task.FromResult
            var results = await Task.WhenAll(
                CheckAuthAsync(timeout.Token),
                Task.FromResult(CheckSesMcpBinary()),
                Task.FromResult(CheckClaudeDesktopConfig()),
                Task.FromResult(CheckCcHooks()),
                Task.FromResult(CheckSocket()),
                CheckSqliteAsync(timeout.Token));

            var checks = results.ToList();

            // Auto-repair degraded/unhealthy config checks
            await MaybeRepairConfigAsync(checks, ct);

            var overall = DeriveOverall(checks);
            _latestReport = new HealthReport
            {
                CheckedAt = DateTime.UtcNow,
                Status = overall,
                Checks = checks
            };

            if (overall != OverallStatus.Healthy)
                _logger.LogWarning("Health check: {Status} — {Issues}",
                    overall,
                    string.Join(", ", checks.Where(c => c.Status != ComponentHealth.Healthy).Select(c => $"{c.Name}:{c.Status}")));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Health check run failed");
        }
    }

    // ── Individual checks ──────────────────────────────────────────────────

    private async Task<HealthCheckResult> CheckAuthAsync(CancellationToken ct)
    {
        try
        {
            var state = await _auth.GetStateAsync(ct);

            if (state.IsAuthenticated)
            {
                ResetRepairCounter(CheckNames.Auth);
                return Healthy(CheckNames.Auth, "Auth", "Authenticated");
            }

            if (state.NeedsReauth)
            {
                if (ShouldAttemptRepair(CheckNames.Auth))
                {
                    await _auth.TriggerReauthAsync(ct);
                    RecordRepairAttempt(CheckNames.Auth);
                }
                return Degraded(CheckNames.Auth, "Auth", "Token expired — reauth triggered");
            }

            return Unhealthy(CheckNames.Auth, "Auth", "Not authenticated");
        }
        catch (Exception ex)
        {
            return Degraded(CheckNames.Auth, "Auth", $"Auth check failed: {ex.GetType().Name}");
        }
    }

    private HealthCheckResult CheckSesMcpBinary()
    {
        var path = SesMcpUpdater.GetSesMcpBinaryPath();
        if (File.Exists(path))
        {
            ResetRepairCounter(CheckNames.SesMcp);
            return Healthy(CheckNames.SesMcp, "Config", $"Binary at {path}");
        }

        return Unhealthy(CheckNames.SesMcp, "Config", "Binary not found — will auto-install");
    }

    private HealthCheckResult CheckClaudeDesktopConfig()
    {
        try
        {
            var status = _sesMcpManager.GetStatus();

            if (status.IsConfigured && !status.HasConfigDrift)
            {
                ResetRepairCounter(CheckNames.ClaudeDesktop);
                return Healthy(CheckNames.ClaudeDesktop, "Config", "Config correct");
            }

            if (status.HasConfigDrift)
                return Degraded(CheckNames.ClaudeDesktop, "Config", "Config drift detected — will auto-repair");

            return Unhealthy(CheckNames.ClaudeDesktop, "Config", "Not configured");
        }
        catch (Exception ex)
        {
            return Degraded(CheckNames.ClaudeDesktop, "Config", $"Config check failed: {ex.GetType().Name}");
        }
    }

    private HealthCheckResult CheckCcHooks()
    {
        var settingsPath = SesMcpManager.GetClaudeCodeSettingsPath();

        if (!File.Exists(settingsPath))
            return Degraded(CheckNames.CcHooks, "Config", "CC settings.json not found");

        try
        {
            var hooksPath  = SesMcpManager.GetSesHooksBinaryPath();
            var registered = ClaudeCodeSettings.LoadOrCreate(settingsPath).HasCorrectHooks(hooksPath);

            if (registered)
            {
                ResetRepairCounter(CheckNames.CcHooks);
                return Healthy(CheckNames.CcHooks, "Config", "Hooks registered");
            }

            return Degraded(CheckNames.CcHooks, "Config", "Hooks not registered — will auto-register");
        }
        catch (Exception ex)
        {
            return Degraded(CheckNames.CcHooks, "Config", $"Could not read settings: {ex.GetType().Name}");
        }
    }

    private HealthCheckResult CheckSocket()
    {
        // Use a connection test, not just File.Exists — verifies Kestrel is actively bound,
        // not just that the filesystem entry exists.
        if (DaemonSocketPath.IsConnectable())
        {
            ResetRepairCounter(CheckNames.Socket);
            return Healthy(CheckNames.Socket, "Infrastructure", "IPC socket accessible");
        }

        return Unhealthy(CheckNames.Socket, "Infrastructure", "Socket missing or not connectable");
    }

    private async Task<HealthCheckResult> CheckSqliteAsync(CancellationToken ct)
    {
        try
        {
            var stats = await _localDb.GetSyncStatsAsync(ct);
            ResetRepairCounter(CheckNames.Sqlite);
            return Healthy(CheckNames.Sqlite, "Storage", $"{stats.TotalConversations} conversations");
        }
        catch (Exception ex)
        {
            return Unhealthy(CheckNames.Sqlite, "Storage", $"Database error: {ex.GetType().Name}");
        }
    }

    // ── Auto-repair ────────────────────────────────────────────────────────

    private async Task MaybeRepairConfigAsync(List<HealthCheckResult> checks, CancellationToken ct)
    {
        var desktopNeedsRepair = checks.Any(c => c.Name == CheckNames.ClaudeDesktop && c.Status != ComponentHealth.Healthy);
        var mcpNeedsRepair     = checks.Any(c => c.Name == CheckNames.SesMcp        && c.Status != ComponentHealth.Healthy);

        if (!desktopNeedsRepair && !mcpNeedsRepair) return;

        var shouldRepairDesktop = desktopNeedsRepair && ShouldAttemptRepair(CheckNames.ClaudeDesktop);
        var shouldRepairMcp     = mcpNeedsRepair     && ShouldAttemptRepair(CheckNames.SesMcp);

        if (!shouldRepairDesktop && !shouldRepairMcp) return;

        try
        {
            await _sesMcpManager.CheckAndRepairAsync(ct);
            if (shouldRepairDesktop) RecordRepairAttempt(CheckNames.ClaudeDesktop);
            if (shouldRepairMcp)     RecordRepairAttempt(CheckNames.SesMcp);
            _logger.LogInformation("Health auto-repair: ran SesMcpManager.CheckAndRepairAsync");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Health auto-repair: CheckAndRepairAsync failed");
            if (shouldRepairDesktop) RecordRepairAttempt(CheckNames.ClaudeDesktop);
            if (shouldRepairMcp)     RecordRepairAttempt(CheckNames.SesMcp);
        }
    }

    // ── Repair backoff ─────────────────────────────────────────────────────

    internal bool ShouldAttemptRepair(string checkName)
    {
        if (!_repairState.TryGetValue(checkName, out var state))
            return true;

        // Reset after 1 hour
        if ((DateTime.UtcNow - state.LastAttempt) >= TimeSpan.FromHours(1))
            return true;

        // After 3 attempts, block for the rest of the hour
        if (state.Attempts >= 3)
            return false;

        // Backoff before attempt 2: 30 s; before attempt 3: 90 s
        var backoff = TimeSpan.FromSeconds(30 * Math.Pow(3, state.Attempts - 1));
        return (DateTime.UtcNow - state.LastAttempt) >= backoff;
    }

    internal void RecordRepairAttempt(string checkName)
    {
        _repairState.AddOrUpdate(checkName,
            _ => (1, DateTime.UtcNow),
            (_, old) =>
            {
                // Reset count if over 1 hour since last attempt
                var count = (DateTime.UtcNow - old.LastAttempt) >= TimeSpan.FromHours(1)
                    ? 1
                    : old.Attempts + 1;
                return (count, DateTime.UtcNow);
            });
    }

    internal void ResetRepairCounter(string checkName) => _repairState.TryRemove(checkName, out _);

    // ── Helpers ────────────────────────────────────────────────────────────

    private static OverallStatus DeriveOverall(IEnumerable<HealthCheckResult> checks)
    {
        var worst = ComponentHealth.Healthy;
        foreach (var c in checks)
        {
            if (c.Status > worst) worst = c.Status;
        }
        return worst switch
        {
            ComponentHealth.Unhealthy => OverallStatus.Unhealthy,
            ComponentHealth.Degraded  => OverallStatus.Degraded,
            _                         => OverallStatus.Healthy,
        };
    }

    private HealthCheckResult Healthy(string name, string category, string message) =>
        Result(name, category, ComponentHealth.Healthy, message);

    private HealthCheckResult Degraded(string name, string category, string message) =>
        Result(name, category, ComponentHealth.Degraded, message);

    private HealthCheckResult Unhealthy(string name, string category, string message) =>
        Result(name, category, ComponentHealth.Unhealthy, message);

    private HealthCheckResult Result(string name, string category, ComponentHealth status, string message)
    {
        _repairState.TryGetValue(name, out var state);
        return new HealthCheckResult
        {
            Name              = name,
            Category          = category,
            Status            = status,
            Message           = message,
            LastRepairAttempt = state.Attempts > 0 ? state.LastAttempt : null,
            RepairAttempts    = state.Attempts,
        };
    }
}
