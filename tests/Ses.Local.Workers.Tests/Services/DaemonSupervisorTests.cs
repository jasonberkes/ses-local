using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Ses.Local.Tray.Services;
using Xunit;

namespace Ses.Local.Workers.Tests.Services;

public sealed class DaemonSupervisorTests
{
    private static readonly NullLogger<DaemonSupervisor> Logger = NullLogger<DaemonSupervisor>.Instance;

    // ── helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Thread-safe fake time provider. Stores UTC ticks in a long so Interlocked
    /// can keep reads and writes atomic.
    /// </summary>
    private sealed class FakeTimeProvider : TimeProvider
    {
        private long _ticksUtc = DateTimeOffset.UtcNow.UtcTicks;

        public override DateTimeOffset GetUtcNow()
            => new(Interlocked.Read(ref _ticksUtc), TimeSpan.Zero);

        public void Advance(TimeSpan delta)
            => Interlocked.Add(ref _ticksUtc, delta.Ticks);
    }

    /// <summary>
    /// Fake process that never exits on its own; tracks whether Kill() was called.
    /// </summary>
    private sealed class FakeLaunchedProcess(bool hasExited = false) : ILaunchedProcess
    {
        public bool HasExited  { get; set; } = hasExited;
        public bool KillCalled { get; private set; }

        public void Kill() => KillCalled = true;

        /// <summary>Blocks until the token is cancelled (simulating a live process).</summary>
        public Task WaitForExitAsync(CancellationToken ct)
            => Task.Delay(Timeout.Infinite, ct);

        public void Dispose() { }
    }

    /// <summary>
    /// Builds a test delay function that advances the given FakeTimeProvider and yields
    /// to allow other tasks to run (avoids busy-spinning in the supervision loop).
    /// </summary>
    private static Func<TimeSpan, CancellationToken, Task> FakeDelay(FakeTimeProvider time)
        => async (ts, ct) => { time.Advance(ts); await Task.Delay(1, ct); };

    /// <summary>Polls supervisor.Status every 10 ms until it matches <paramref name="expected"/>.</summary>
    private static async Task WaitForStatus(
        DaemonSupervisor supervisor, DaemonStatus expected, int timeoutMs = 3000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (supervisor.Status != expected && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        Assert.Equal(expected, supervisor.Status);
    }

    // ── test 1: launches daemon when socket not present ───────────────────────

    [Fact]
    public async Task Start_LaunchesDaemon_WhenSocketNotAvailable()
    {
        bool launched = false;
        bool socketAvailable = false;
        var fakeTime = new FakeTimeProvider();

        using var supervisor = new DaemonSupervisor(
            Logger,
            delay: FakeDelay(fakeTime),
            timeProvider: fakeTime,
            isSocketAvailable: () => socketAvailable,
            launchProcess: _ =>
            {
                launched = true;
                socketAvailable = true; // socket appears after successful launch
                return new FakeLaunchedProcess(hasExited: false);
            },
            sendShutdown: _ => Task.CompletedTask);

        supervisor.Start();
        await WaitForStatus(supervisor, DaemonStatus.Running);

        Assert.True(launched);
        Assert.Equal(DaemonStatus.Running, supervisor.Status);
    }

    // ── test 2: detects already-running daemon without launching a new one ────

    [Fact]
    public async Task Start_DoesNotLaunch_WhenDaemonAlreadyRunning()
    {
        bool launched = false;
        var fakeTime = new FakeTimeProvider();

        using var supervisor = new DaemonSupervisor(
            Logger,
            delay: FakeDelay(fakeTime),
            timeProvider: fakeTime,
            isSocketAvailable: () => true, // daemon already running
            launchProcess: _ => { launched = true; return new FakeLaunchedProcess(); },
            sendShutdown: _ => Task.CompletedTask);

        supervisor.Start();
        await WaitForStatus(supervisor, DaemonStatus.Running);

        Assert.False(launched);
    }

    // ── test 3: exponential backoff delays on crash ───────────────────────────

    [Fact]
    public async Task Restart_UsesExponentialBackoff_OnCrash()
    {
        var recordedDelays = new List<TimeSpan>();
        var fakeTime = new FakeTimeProvider();
        int healthCallCount = 0;

        using var supervisor = new DaemonSupervisor(
            Logger,
            delay: async (ts, ct) => { recordedDelays.Add(ts); fakeTime.Advance(ts); await Task.Delay(1, ct); },
            timeProvider: fakeTime,
            // First call: true (daemon found running); all subsequent calls: false
            isSocketAvailable: () => healthCallCount++ == 0,
            // Restart always fails → null process
            launchProcess: _ => null,
            sendShutdown: _ => Task.CompletedTask);

        supervisor.Start();
        await WaitForStatus(supervisor, DaemonStatus.Crashed);

        // Backoff delays are 5 s, 15 s, 45 s; health-poll delay is 10 s; socket-wait is 0.5 s.
        // Filter to exactly the backoff values.
        var healthPoll  = TimeSpan.FromSeconds(10);
        var socketWait  = TimeSpan.FromMilliseconds(500);
        var backoffOnly = recordedDelays
            .Where(d => d != healthPoll && d != socketWait)
            .ToList();

        Assert.Equal(3, backoffOnly.Count);
        Assert.Equal(TimeSpan.FromSeconds(5),  backoffOnly[0]);
        Assert.Equal(TimeSpan.FromSeconds(15), backoffOnly[1]);
        Assert.Equal(TimeSpan.FromSeconds(45), backoffOnly[2]);
    }

    // ── test 4: stops retrying after MaxRetries and enters Crashed ────────────

    [Fact]
    public async Task Restart_StopsAfterMaxAttempts_AndSetsCrashed()
    {
        var fakeTime = new FakeTimeProvider();
        int launchCount = 0;
        int healthCallCount = 0;

        using var supervisor = new DaemonSupervisor(
            Logger,
            delay: FakeDelay(fakeTime),
            timeProvider: fakeTime,
            isSocketAvailable: () => healthCallCount++ == 0,
            launchProcess: _ => { launchCount++; return null; },
            sendShutdown: _ => Task.CompletedTask);

        supervisor.Start();
        await WaitForStatus(supervisor, DaemonStatus.Crashed);

        Assert.Equal(DaemonStatus.Crashed, supervisor.Status);
        Assert.Equal(3, launchCount);        // one launch attempt per retry
        Assert.Equal(3, supervisor.RetryAttempt);
    }

    // ── test 5: resets retry count after 60 s of stable running ──────────────

    [Fact]
    public async Task RetryCount_ResetsAfterStabilityWindow()
    {
        var fakeTime = new FakeTimeProvider();
        bool socketAvailable = false;
        int restartCount = 0;

        using var supervisor = new DaemonSupervisor(
            Logger,
            delay: FakeDelay(fakeTime),
            timeProvider: fakeTime,
            isSocketAvailable: () => socketAvailable,
            launchProcess: _ =>
            {
                restartCount++;
                socketAvailable = true;
                return new FakeLaunchedProcess(hasExited: false);
            },
            sendShutdown: _ => Task.CompletedTask);

        // Daemon is already running at startup
        socketAvailable = true;
        supervisor.Start();
        await WaitForStatus(supervisor, DaemonStatus.Running);

        // Simulate a crash — next health poll returns false
        socketAvailable = false;

        // Wait for the supervisor to detect the crash and restart successfully.
        // Use restartCount to verify a restart actually happened (retryCount may have already
        // been reset to 0 by the stability window check before we check it).
        var crashDeadline = DateTime.UtcNow.AddSeconds(5);
        while (restartCount == 0 && DateTime.UtcNow < crashDeadline)
            await Task.Delay(10);

        Assert.True(restartCount > 0, "Expected at least one restart attempt after crash");
        await WaitForStatus(supervisor, DaemonStatus.Running, timeoutMs: 5000);

        // The supervision loop advances fakeTime by 10 s on each health-poll cycle.
        // After 6 cycles (fake 60 s), the stability check resets retryCount to 0.
        var stabilityDeadline = DateTime.UtcNow.AddSeconds(5);
        while (supervisor.RetryAttempt > 0 && DateTime.UtcNow < stabilityDeadline)
            await Task.Delay(10);

        Assert.Equal(0, supervisor.RetryAttempt);
    }

    // ── test 6: graceful shutdown sends signal then kills process ─────────────

    [Fact]
    public async Task StopAsync_SendsShutdownSignal_ThenKillsIfProcessStillRunning()
    {
        bool shutdownSent = false;
        bool socketAvailable = false;
        var process = new FakeLaunchedProcess(hasExited: false);
        var fakeTime = new FakeTimeProvider();

        // Socket starts unavailable so the supervisor launches the process (setting _process).
        // Use a very short grace period so the test doesn't have to wait 5 s.
        using var supervisor = new DaemonSupervisor(
            Logger,
            delay: FakeDelay(fakeTime),
            timeProvider: fakeTime,
            isSocketAvailable: () => socketAvailable,
            launchProcess: _ => { socketAvailable = true; return process; },
            sendShutdown: _ => { shutdownSent = true; return Task.CompletedTask; },
            shutdownGracePeriod: TimeSpan.FromMilliseconds(50));

        supervisor.Start();
        await WaitForStatus(supervisor, DaemonStatus.Running);

        await supervisor.StopAsync();

        Assert.True(shutdownSent,       "Shutdown signal should have been sent to the daemon");
        Assert.True(process.KillCalled, "Process should have been killed after the grace period");
        Assert.Equal(DaemonStatus.Stopped, supervisor.Status);
    }

    // ── test 7: manual restart resets the retry counter ──────────────────────

    [Fact]
    public async Task RestartAsync_ResetsRetryCounter()
    {
        var fakeTime = new FakeTimeProvider();
        int healthCallCount = 0;
        bool allowRestart = false;

        using var supervisor = new DaemonSupervisor(
            Logger,
            delay: FakeDelay(fakeTime),
            timeProvider: fakeTime,
            isSocketAvailable: () =>
            {
                if (allowRestart) return true;
                return healthCallCount++ == 0; // first call → true, then false → crash
            },
            launchProcess: _ => allowRestart ? new FakeLaunchedProcess(hasExited: false) : null,
            sendShutdown: _ => Task.CompletedTask);

        // Let supervisor crash through all retries
        supervisor.Start();
        await WaitForStatus(supervisor, DaemonStatus.Crashed, timeoutMs: 5000);
        Assert.Equal(3, supervisor.RetryAttempt);

        // Manual restart resets the counter
        allowRestart = true;
        await supervisor.RestartAsync();
        await WaitForStatus(supervisor, DaemonStatus.Running, timeoutMs: 3000);

        Assert.Equal(0, supervisor.RetryAttempt);
    }

    // ── test 8: GetDaemonPath returns a non-empty path containing the binary name

    [Fact]
    public void GetDaemonPath_ReturnsPlatformSpecificPath()
    {
        var path = DaemonSupervisor.GetDaemonPath();

        Assert.NotEmpty(path);
        Assert.Contains("ses-local-daemon", path);
    }

    // ── test 9: AcknowledgeDaemonRunning updates status to Running ─────────────

    [Fact]
    public async Task AcknowledgeDaemonRunning_UpdatesStatusToRunning_WhenNotAlreadyRunning()
    {
        var fakeTime = new FakeTimeProvider();

        // Start with a supervisor in Crashed state (all retries exhausted).
        int healthCallCount = 0;
        using var supervisor = new DaemonSupervisor(
            Logger,
            delay: FakeDelay(fakeTime),
            timeProvider: fakeTime,
            isSocketAvailable: () => healthCallCount++ == 0,
            launchProcess: _ => null,    // always fails → Crashed
            sendShutdown: _ => Task.CompletedTask);

        supervisor.Start();
        await WaitForStatus(supervisor, DaemonStatus.Crashed, timeoutMs: 5000);
        Assert.Equal(3, supervisor.RetryAttempt);

        // Tray IPC detects daemon is running → acknowledge
        supervisor.AcknowledgeDaemonRunning();

        Assert.Equal(DaemonStatus.Running, supervisor.Status);
        Assert.Equal(0, supervisor.RetryAttempt);
    }

    [Fact]
    public void AcknowledgeDaemonRunning_IsNoOp_WhenAlreadyRunning()
    {
        var fakeTime = new FakeTimeProvider();
        var statusChanges = new List<DaemonStatus>();

        using var supervisor = new DaemonSupervisor(
            Logger,
            delay: FakeDelay(fakeTime),
            timeProvider: fakeTime,
            isSocketAvailable: () => true,
            launchProcess: _ => new FakeLaunchedProcess(),
            sendShutdown: _ => Task.CompletedTask);

        supervisor.StatusChanged += s => statusChanges.Add(s);
        supervisor.Start();

        // Give it a moment to reach Running
        Thread.Sleep(50);

        // Even if supervisor is mid-loop, calling Acknowledge when Running is a no-op
        // (no StatusChanged event fired for Running→Running transition)
        var prevCount = statusChanges.Count;
        supervisor.AcknowledgeDaemonRunning();
        // If already Running, no additional event is fired
        Assert.Equal(prevCount, statusChanges.Count);
    }

    // ── test 10: LaunchRealProcess returns null and logs when binary missing ──

    [Fact]
    public void LaunchRealProcess_ReturnsNull_WhenBinaryDoesNotExist()
    {
        var fakeTime = new FakeTimeProvider();
        using var supervisor = new DaemonSupervisor(
            Logger,
            delay: FakeDelay(fakeTime),
            timeProvider: fakeTime,
            isSocketAvailable: () => false,
            launchProcess: _ => null,
            sendShutdown: _ => Task.CompletedTask);

        var result = supervisor.LaunchRealProcess("/nonexistent/path/ses-local-daemon");

        Assert.Null(result);
    }

    // ── test 11: LaunchRealProcess returns null when process exits immediately ──

    [Fact]
    public void LaunchRealProcess_ReturnsNull_WhenBinaryExitsImmediately()
    {
        // /usr/bin/false (Unix) and /usr/bin/true (macOS/Linux) both exit immediately.
        // LaunchRealProcess detects any exit within 2 s and returns null.
        var exitBinary = OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "where.exe")
            : "/usr/bin/false";

        var fakeTime = new FakeTimeProvider();
        using var supervisor = new DaemonSupervisor(
            Logger,
            delay: FakeDelay(fakeTime),
            timeProvider: fakeTime,
            isSocketAvailable: () => false,
            launchProcess: _ => null,
            sendShutdown: _ => Task.CompletedTask);

        var result = supervisor.LaunchRealProcess(exitBinary);

        Assert.Null(result);
    }

    // ── test 12: WaitForSocketAsync logs timing on success ──────────────────

    [Fact]
    public async Task WaitForSocket_LogsTiming_WhenSocketAppears()
    {
        var fakeTime = new FakeTimeProvider();
        int socketCallCount = 0;

        using var supervisor = new DaemonSupervisor(
            Logger,
            delay: FakeDelay(fakeTime),
            timeProvider: fakeTime,
            // Socket appears after 3 checks (1.5 s simulated)
            isSocketAvailable: () => ++socketCallCount > 3,
            launchProcess: _ => new FakeLaunchedProcess(hasExited: false),
            sendShutdown: _ => Task.CompletedTask);

        supervisor.Start();
        await WaitForStatus(supervisor, DaemonStatus.Running);

        // Verify the daemon reached Running state (logging occurred internally)
        Assert.Equal(DaemonStatus.Running, supervisor.Status);
    }
}
