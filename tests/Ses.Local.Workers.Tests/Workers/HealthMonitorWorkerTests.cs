using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Ses.Local.Core.Interfaces;
using Ses.Local.Core.Models;
using Ses.Local.Core.Options;
using Ses.Local.Workers.Services;
using Ses.Local.Workers.Workers;
using Xunit;

namespace Ses.Local.Workers.Tests.Workers;

public sealed class HealthMonitorWorkerTests
{
    // ── Helpers ────────────────────────────────────────────────────────────

    private static HealthMonitorWorker BuildWorker(
        IAuthService? auth = null,
        ILocalDbService? db = null,
        SesMcpManager? mcpManager = null)
    {
        var authMock = new Mock<IAuthService>();
        authMock.Setup(x => x.GetStateAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new SesAuthState { IsAuthenticated = true });

        var dbMock = new Mock<ILocalDbService>();
        dbMock.Setup(x => x.GetSyncStatsAsync(It.IsAny<CancellationToken>()))
              .ReturnsAsync(new SyncStats { TotalConversations = 5 });

        return new HealthMonitorWorker(
            auth ?? authMock.Object,
            db ?? dbMock.Object,
            mcpManager ?? BuildSesMcpManager(),
            NullLogger<HealthMonitorWorker>.Instance);
    }

    private static SesMcpManager BuildSesMcpManager()
    {
        var httpFactory = new Mock<IHttpClientFactory>();
        httpFactory.Setup(x => x.CreateClient(It.IsAny<string>()))
                   .Returns(new System.Net.Http.HttpClient());

        var keychain = new Mock<ICredentialStore>();
        var auth     = new Mock<IAuthService>();
        var options  = Options.Create(new SesLocalOptions());
        var updater  = new SesMcpUpdater(
            NullLogger<SesMcpUpdater>.Instance,
            new System.Net.Http.HttpClient(), options);

        return new SesMcpManager(
            httpFactory.Object, keychain.Object, updater, auth.Object,
            NullLogger<SesMcpManager>.Instance, options);
    }

    /// <summary>
    /// Runs the worker until the first check completes (or 3 s timeout).
    /// BackgroundService.StartAsync fires ExecuteAsync in background so we must await the checks.
    /// </summary>
    private static async Task<HealthReport> RunAndGetReportAsync(HealthMonitorWorker worker)
    {
        await worker.StartAsync(CancellationToken.None);

        // Poll until the report is populated (first check runs immediately in ExecuteAsync)
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!timeout.IsCancellationRequested)
        {
            var report = worker.LatestReport;
            if (report.Checks.Count > 0) break;
            await Task.Delay(20, timeout.Token).ConfigureAwait(false);
        }

        await worker.StopAsync(CancellationToken.None);
        return worker.LatestReport;
    }

    // ── Tests ──────────────────────────────────────────────────────────────

    [Fact]
    public void LatestReport_InitiallyHealthy()
    {
        var worker = BuildWorker();
        Assert.Equal(OverallStatus.Healthy, worker.LatestReport.Status);
        Assert.Empty(worker.LatestReport.Checks);
    }

    [Fact]
    public async Task AllChecks_Healthy_WhenAuthenticatedAndDbResponds()
    {
        var worker = BuildWorker();
        var report = await RunAndGetReportAsync(worker);

        Assert.NotEmpty(report.Checks);
        Assert.Equal(ComponentHealth.Healthy, report.Checks.Single(c => c.Name == "Auth").Status);
        Assert.Equal(ComponentHealth.Healthy, report.Checks.Single(c => c.Name == "SQLite").Status);
    }

    [Fact]
    public async Task SesMcpBinaryCheck_ReturnsResult_WhetherOrNotBinaryExists()
    {
        var worker = BuildWorker();
        var report = await RunAndGetReportAsync(worker);

        var binaryCheck = report.Checks.Single(c => c.Name == "ses-mcp");
        Assert.Contains(binaryCheck.Status, new[] { ComponentHealth.Healthy, ComponentHealth.Unhealthy });
    }

    [Fact]
    public async Task OverallStatus_Unhealthy_WhenSqliteFails()
    {
        var dbMock = new Mock<ILocalDbService>();
        dbMock.Setup(x => x.GetSyncStatsAsync(It.IsAny<CancellationToken>()))
              .ThrowsAsync(new InvalidOperationException("DB gone"));

        var worker = BuildWorker(db: dbMock.Object);
        var report = await RunAndGetReportAsync(worker);

        Assert.Equal(OverallStatus.Unhealthy, report.Status);
    }

    [Fact]
    public async Task OverallStatus_AtLeastDegraded_WhenAuthNeedsReauth()
    {
        var authMock = new Mock<IAuthService>();
        authMock.Setup(x => x.GetStateAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new SesAuthState { IsAuthenticated = false, NeedsReauth = true });
        authMock.Setup(x => x.TriggerReauthAsync(It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

        var dbMock = new Mock<ILocalDbService>();
        dbMock.Setup(x => x.GetSyncStatsAsync(It.IsAny<CancellationToken>()))
              .ReturnsAsync(new SyncStats { TotalConversations = 1 });

        var worker = BuildWorker(auth: authMock.Object, db: dbMock.Object);
        var report = await RunAndGetReportAsync(worker);

        // Auth returns Degraded → overall must be at least Degraded
        Assert.NotEqual(OverallStatus.Healthy, report.Status);
    }

    // ── Repair backoff ─────────────────────────────────────────────────────

    [Fact]
    public void ShouldAttemptRepair_TrueOnFirstAttempt()
    {
        var worker = BuildWorker();
        Assert.True(worker.ShouldAttemptRepair("TestCheck"));
    }

    [Fact]
    public void ShouldAttemptRepair_FalseAfterThreeAttempts()
    {
        var worker = BuildWorker();

        worker.RecordRepairAttempt("TestCheck");
        worker.RecordRepairAttempt("TestCheck");
        worker.RecordRepairAttempt("TestCheck");

        Assert.False(worker.ShouldAttemptRepair("TestCheck"));
    }

    [Fact]
    public void ResetRepairCounter_AllowsRepairAgain()
    {
        var worker = BuildWorker();

        worker.RecordRepairAttempt("TestCheck");
        worker.RecordRepairAttempt("TestCheck");
        worker.RecordRepairAttempt("TestCheck");
        Assert.False(worker.ShouldAttemptRepair("TestCheck"));

        worker.ResetRepairCounter("TestCheck");
        Assert.True(worker.ShouldAttemptRepair("TestCheck"));
    }

}
