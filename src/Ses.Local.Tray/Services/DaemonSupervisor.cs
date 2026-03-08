using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Ses.Local.Core.Services;

namespace Ses.Local.Tray.Services;

public enum DaemonStatus { Stopped, Starting, Running, Restarting, Crashed }

/// <summary>
/// Supervises the ses-local daemon process. Starts on tray launch, restarts on crash
/// with exponential backoff (5s → 15s → 45s, max 3 retries), then shows manual
/// restart option in tray. Resets retry count after 60 seconds of stable running.
/// </summary>
public sealed class DaemonSupervisor : IDisposable
{
    private static readonly TimeSpan[] BackoffDelays =
    [
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(15),
        TimeSpan.FromSeconds(45),
    ];

    internal const int MaxRetries = 3;

    private static readonly TimeSpan StabilityWindow    = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan HealthPollInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan SocketWaitTimeout  = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan SocketWaitInterval = TimeSpan.FromMilliseconds(500);

    private readonly ILogger<DaemonSupervisor> _logger;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly TimeProvider _timeProvider;
    private readonly Func<bool> _isSocketAvailable;
    private readonly Func<string, ILaunchedProcess?> _launchProcess;
    private readonly Func<CancellationToken, Task> _sendShutdown;
    private readonly TimeSpan _shutdownGracePeriod;

    private ILaunchedProcess? _process;
    private bool _quarantineRemoved;
    private int _retryCount;
    private DateTimeOffset? _stableStart;
    private DaemonStatus _status = DaemonStatus.Stopped;
    private CancellationTokenSource? _cts;
    private Task? _supervisionTask;

    public DaemonStatus Status      => _status;
    public int          RetryAttempt => _retryCount;
    public event Action<DaemonStatus>? StatusChanged;

    /// <summary>Production constructor — uses real process launch and socket health check.</summary>
    public DaemonSupervisor(ILogger<DaemonSupervisor> logger, DaemonAuthProxy authProxy)
    {
        _logger              = logger;
        _delay               = Task.Delay;
        _timeProvider        = TimeProvider.System;
        _isSocketAvailable   = DaemonSocketPath.IsAvailable;
        _launchProcess       = LaunchRealProcess;
        _sendShutdown        = ct => authProxy.ShutdownAsync(ct);
        _shutdownGracePeriod = TimeSpan.FromSeconds(5);
    }

    /// <summary>Internal constructor for tests — all side-effects are injectable.</summary>
    internal DaemonSupervisor(
        ILogger<DaemonSupervisor> logger,
        Func<TimeSpan, CancellationToken, Task> delay,
        TimeProvider timeProvider,
        Func<bool> isSocketAvailable,
        Func<string, ILaunchedProcess?> launchProcess,
        Func<CancellationToken, Task> sendShutdown,
        TimeSpan shutdownGracePeriod = default)
    {
        _logger              = logger;
        _delay               = delay;
        _timeProvider        = timeProvider;
        _isSocketAvailable   = isSocketAvailable;
        _launchProcess       = launchProcess;
        _sendShutdown        = sendShutdown;
        _shutdownGracePeriod = shutdownGracePeriod == default
            ? TimeSpan.FromSeconds(5)
            : shutdownGracePeriod;
    }

    /// <summary>Begin supervision. Safe to call if already running (no-op).</summary>
    public void Start()
    {
        if (_supervisionTask is { IsCompleted: false })
            return;

        // Remove stale socket left by a previous daemon crash so the supervisor
        // always launches a fresh daemon rather than falsely detecting one running.
        DaemonSocketPath.CleanupStaleSocket();

        _cts = new CancellationTokenSource();
        // Task.Run ensures the supervision loop runs on a thread pool thread,
        // so Start() returns immediately even when delay functions complete synchronously.
        _supervisionTask = Task.Run(() => RunSupervisionLoopAsync(_cts.Token));
    }

    /// <summary>
    /// Called when the tray detects the daemon is running (via IPC) even though the
    /// supervisor didn't start it (e.g., started manually or by a previous tray instance).
    /// Updates internal state accordingly.
    /// </summary>
    public void AcknowledgeDaemonRunning()
    {
        if (Status != DaemonStatus.Running)
        {
            SetStatus(DaemonStatus.Running);
            _retryCount  = 0;
            _stableStart ??= _timeProvider.GetUtcNow();
        }
    }

    /// <summary>
    /// Gracefully shuts down the daemon (sends /api/shutdown, waits, kills if needed), then
    /// cancels the supervision loop. Called when the user clicks "Quit Tray".
    /// </summary>
    public async Task StopAsync()
    {
        // Single CTS bounds the entire shutdown sequence (send + wait) to one grace period.
        using var gracefulCts = new CancellationTokenSource(_shutdownGracePeriod);
        try   { await _sendShutdown(gracefulCts.Token); }
        catch { /* daemon may already be gone */ }

        if (_process is not null)
        {
            try   { await _process.WaitForExitAsync(gracefulCts.Token); }
            catch (OperationCanceledException) { _process.Kill(); }
        }

        _cts?.Cancel();
        if (_supervisionTask is not null)
            try { await _supervisionTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }

        SetStatus(DaemonStatus.Stopped);
    }

    /// <summary>
    /// Resets the retry counter and restarts supervision from scratch.
    /// Called when the user manually clicks "Restart Daemon" after a crash.
    /// </summary>
    public async Task RestartAsync()
    {
        _cts?.Cancel();
        if (_supervisionTask is not null)
            try { await _supervisionTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }

        if (_process is not null && !_process.HasExited)
            _process.Kill();

        _process     = null;
        _retryCount  = 0;
        _stableStart = null;

        Start();
    }

    // ── supervision loop ─────────────────────────────────────────────────────

    private async Task RunSupervisionLoopAsync(CancellationToken ct)
    {
        try
        {
            if (_isSocketAvailable())
            {
                _logger.LogInformation("Daemon already running (socket found)");
                SetStatus(DaemonStatus.Running);
                _stableStart = _timeProvider.GetUtcNow();
            }
            else
            {
                SetStatus(DaemonStatus.Starting);
                _process = _launchProcess(GetDaemonPath());

                if (_process is null || !await WaitForSocketAsync(ct))
                {
                    _logger.LogError("Failed to start daemon process");
                    SetStatus(DaemonStatus.Crashed);
                    return;
                }

                _logger.LogInformation("Daemon started successfully");
                SetStatus(DaemonStatus.Running);
                _stableStart = _timeProvider.GetUtcNow();
            }

            while (!ct.IsCancellationRequested)
            {
                await _delay(HealthPollInterval, ct);

                // Reset retry counter after stability window
                if (_stableStart.HasValue &&
                    _timeProvider.GetUtcNow() - _stableStart.Value >= StabilityWindow &&
                    _retryCount > 0)
                {
                    _logger.LogInformation(
                        "Daemon stable for {Window}s — resetting retry counter",
                        StabilityWindow.TotalSeconds);
                    _retryCount = 0;
                }

                bool healthy = _process is not null ? !_process.HasExited : _isSocketAvailable();
                if (healthy)
                {
                    _stableStart ??= _timeProvider.GetUtcNow();
                    continue;
                }

                _logger.LogWarning("Daemon is unhealthy — beginning restart sequence");
                _stableStart = null;

                if (!await AttemptRestartAsync(ct))
                    return; // Crashed state set inside AttemptRestartAsync
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Normal cancellation on tray quit — not an error
        }
    }

    private async Task<bool> AttemptRestartAsync(CancellationToken ct)
    {
        while (_retryCount < MaxRetries && !ct.IsCancellationRequested)
        {
            var backoff = BackoffDelays[_retryCount];
            _retryCount++;
            SetStatus(DaemonStatus.Restarting);

            _logger.LogWarning(
                "Restarting daemon in {Delay}s (attempt {Attempt}/{Max})",
                backoff.TotalSeconds, _retryCount, MaxRetries);

            await _delay(backoff, ct);

            _process?.Dispose();
            _process = _launchProcess(GetDaemonPath());

            if (_process is not null && await WaitForSocketAsync(ct))
            {
                _logger.LogInformation("Daemon restarted on attempt {Attempt}", _retryCount);
                SetStatus(DaemonStatus.Running);
                _stableStart = _timeProvider.GetUtcNow();
                return true;
            }

            _logger.LogError("Daemon restart attempt {Attempt} failed", _retryCount);
        }

        _logger.LogError(
            "Daemon crashed after {Max} restart attempts — manual intervention required", MaxRetries);
        SetStatus(DaemonStatus.Crashed);
        return false;
    }

    private async Task<bool> WaitForSocketAsync(CancellationToken ct)
    {
        var start = _timeProvider.GetUtcNow();
        var deadline = start + SocketWaitTimeout;
        while (_timeProvider.GetUtcNow() < deadline && !ct.IsCancellationRequested)
        {
            if (_isSocketAvailable())
            {
                var elapsed = _timeProvider.GetUtcNow() - start;
                _logger.LogInformation("Daemon socket available after {Seconds:F1}s", elapsed.TotalSeconds);
                return true;
            }
            await _delay(SocketWaitInterval, ct);
        }
        _logger.LogWarning("Daemon socket not available after {Timeout}s", SocketWaitTimeout.TotalSeconds);
        return false;
    }

    private void SetStatus(DaemonStatus status)
    {
        _status = status;
        StatusChanged?.Invoke(status);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    internal static string GetDaemonPath()
    {
        if (OperatingSystem.IsWindows())
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SuperEasySoftware", "ses-local-daemon.exe");

        if (OperatingSystem.IsMacOS() || OperatingSystem.IsLinux())
        {
            // Derive the ~/.ses root from DaemonSocketPath to keep the path in one place.
            var sesDir = Path.GetDirectoryName(DaemonSocketPath.GetPath())!;
            return Path.Combine(sesDir, "bin", "ses-local-daemon");
        }

        return "ses-local-daemon"; // fallback: assume on PATH
    }

    internal ILaunchedProcess? LaunchRealProcess(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                _logger.LogError("Daemon binary not found at {Path}", path);
                return null;
            }

            // macOS Gatekeeper blocks unsigned downloaded binaries via quarantine flag
            if (OperatingSystem.IsMacOS())
                RemoveQuarantineFlag(path);

            var process = new Process
            {
                StartInfo = new ProcessStartInfo(path)
                {
                    UseShellExecute     = false,
                    CreateNoWindow      = true,
                    WorkingDirectory    = Path.GetDirectoryName(path)!,
                    RedirectStandardError = true,
                },
                EnableRaisingEvents = true,
            };

            if (!process.Start())
            {
                _logger.LogError("Process.Start returned false for {Path}", path);
                return null;
            }

            // Check if process exited immediately (crash on startup)
            if (process.WaitForExit(2000))
            {
                var stderr = process.StandardError.ReadToEnd();
                _logger.LogError(
                    "Daemon exited immediately with code {ExitCode}. stderr: {StdErr}",
                    process.ExitCode, stderr);
                return null;
            }

            _logger.LogInformation("Daemon started at {DaemonPath}, PID {ProcessId}", path, process.Id);
            return new RealLaunchedProcess(process);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception launching daemon from {Path}", path);
            return null;
        }
    }

    private void RemoveQuarantineFlag(string path)
    {
        if (_quarantineRemoved) return;
        try
        {
            var xattr = new Process
            {
                StartInfo = new ProcessStartInfo("xattr", $"-d com.apple.quarantine \"{path}\"")
                {
                    UseShellExecute = false,
                    CreateNoWindow  = true,
                }
            };
            xattr.Start();
            xattr.WaitForExit(3000);
            _quarantineRemoved = true;
        }
        catch
        {
            // Quarantine flag may not exist — that's fine
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _process?.Dispose();
    }
}

// ── process abstraction ───────────────────────────────────────────────────────

internal interface ILaunchedProcess : IDisposable
{
    bool HasExited { get; }
    void Kill();
    Task WaitForExitAsync(CancellationToken ct);
}

internal sealed class RealLaunchedProcess : ILaunchedProcess
{
    private readonly Process _process;

    internal RealLaunchedProcess(Process process) => _process = process;

    public bool HasExited => _process.HasExited;
    public void Kill()    => _process.Kill(entireProcessTree: true);
    public Task WaitForExitAsync(CancellationToken ct) => _process.WaitForExitAsync(ct);
    public void Dispose() => _process.Dispose();
}
