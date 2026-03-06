using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Ses.Local.Core.Interfaces;
using Ses.Local.Core.Models;
using Ses.Local.Core.Options;
using Ses.Local.Core.Services;
using Ses.Local.Workers;
using Ses.Local.Workers.Services;
using Ses.Local.Workers.Telemetry;
using Ses.Local.Workers.Workers;

namespace Ses.Local.Daemon;

internal static class Program
{
    public static async Task Main(string[] args)
    {
        // Single-instance enforcement via named mutex
        using var mutex = new Mutex(true, "com.supereasysoftware.ses-local-daemon", out var isNewInstance);
        if (!isNewInstance)
        {
            Console.Error.WriteLine("ses-local-daemon is already running.");
            return;
        }

        var socketPath = DaemonSocketPath.GetPath();
        DaemonSocketPath.CleanupStaleSocket();

        var builder = WebApplication.CreateBuilder(args);

        // Kestrel listens ONLY on Unix domain socket (or named pipe on Windows) — no TCP
        builder.WebHost.ConfigureKestrel(options =>
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                options.ListenNamedPipe(socketPath);
            else
                options.ListenUnixSocket(socketPath);
        });

        builder.Services.AddSesLocalWorkers(builder.Configuration);

        // OpenTelemetry — metrics and tracing (conditioned on EnableTelemetry option)
        var sesLocalSection = builder.Configuration.GetSection(SesLocalOptions.SectionName);
        var enableTelemetry = sesLocalSection.GetValue("EnableTelemetry", defaultValue: true);
        if (enableTelemetry)
        {
            builder.Services.AddOpenTelemetry()
                .WithMetrics(metrics => metrics
                    .AddMeter(SesLocalMetrics.MeterName)
                    .AddConsoleExporter())
                .WithTracing(tracing => tracing
                    .AddSource(SesLocalMetrics.ActivitySourceName)
                    .AddConsoleExporter());
        }

        builder.Services.AddSingleton<ImportProgressTracker>();
        builder.Services.AddHostedService<LevelDbWatcher>();
        builder.Services.AddHostedService<ClaudeCodeWatcher>();
        builder.Services.AddHostedService<CoworkWatcher>();
        builder.Services.AddHostedService<CloudSyncWorker>();
        builder.Services.AddHostedService<CloudPullWorker>();
        builder.Services.AddHostedService<BrowserExtensionListener>();
        builder.Services.AddHostedService<AutoUpdateWorker>();
        builder.Services.AddHostedService<SesMcpManagerWorker>();
        builder.Services.AddHostedService<ClaudeDesktopSyncWorker>();

        var app = builder.Build();

        var startTimestamp = Stopwatch.GetTimestamp();

        // IPC endpoints served over Unix domain socket
        app.MapGet("/api/status", async (IAuthService auth, ILicenseService license) =>
        {
            var state   = await auth.GetStateAsync();
            var licState = await license.GetStateAsync();
            var uptime  = Stopwatch.GetElapsedTime(startTimestamp);
            return Results.Ok(new
            {
                authenticated    = state.IsAuthenticated,
                needsReauth      = state.NeedsReauth,
                loginTimedOut    = state.LoginTimedOut,
                licenseValid     = licState.IsValid,
                licenseStatus    = licState.Status.ToString(),
                uptime           = uptime.ToString(@"d\.hh\:mm\:ss")
            });
        });

        app.MapGet("/api/license", async (ILicenseService license) =>
        {
            var state = await license.GetStateAsync();
            return Results.Ok(new
            {
                status    = state.Status.ToString(),
                isValid   = state.IsValid,
                email     = state.Email,
                expiresAt = state.ExpiresAt,
            });
        });

        app.MapPost("/api/license/activate", async (HttpContext ctx, ILicenseService license) =>
        {
            var body = await System.Text.Json.JsonSerializer.DeserializeAsync<LicenseActivateRequest>(
                ctx.Request.Body,
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (body is null || string.IsNullOrWhiteSpace(body.LicenseKey))
                return Results.BadRequest(new { error = "License key is required." });

            var result = await license.ActivateAsync(body.LicenseKey, ctx.RequestAborted);

            if (!result.Succeeded)
                return Results.BadRequest(new { error = result.ErrorMessage });

            return Results.Ok(new
            {
                status    = result.State!.Status.ToString(),
                email     = result.State.Email,
                expiresAt = result.State.ExpiresAt,
            });
        });

        // Start an import in the background. Returns 202 immediately; poll /status for progress.
        app.MapPost("/api/conversations/import", async (HttpContext ctx, ConversationImportDispatcher dispatcher, ImportProgressTracker tracker, ILocalDbService db) =>
        {
            var body = await System.Text.Json.JsonSerializer.DeserializeAsync<ImportConversationsRequest>(
                ctx.Request.Body,
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (body is null || string.IsNullOrWhiteSpace(body.FilePath))
                return Results.BadRequest(new { error = "filePath is required." });

            if (!File.Exists(body.FilePath))
                return Results.BadRequest(new { error = "File not found." });

            if (tracker.IsRunning)
                return Results.Conflict(new { error = "An import is already in progress." });

            var format = ConversationImportDispatcher.DetectFormat(body.FilePath);
            var formatLabel = ConversationImportDispatcher.FormatLabel(format);
            tracker.Start(formatLabel);

            var filePath = body.FilePath;
            _ = Task.Run(async () =>
            {
                try
                {
                    var progress = new Progress<ImportProgress>(p =>
                        tracker.UpdateProgress(p.Processed));

                    var result = await dispatcher.ImportAsync(filePath, progress, tracker.CancellationToken);
                    tracker.Complete(result);

                    await db.RecordImportHistoryAsync(new ImportHistoryRecord
                    {
                        Source            = formatLabel.ToLowerInvariant(),
                        FilePath          = filePath,
                        ImportedAt        = DateTime.UtcNow,
                        SessionsImported  = result.SessionsImported,
                        MessagesImported  = result.MessagesImported,
                        DuplicatesSkipped = result.Duplicates,
                        Errors            = result.Errors,
                    });
                }
                catch (OperationCanceledException)
                {
                    tracker.Cancel();
                }
                catch (Exception ex)
                {
                    tracker.Fail(ex.Message);
                }
            });

            return Results.Accepted("/api/conversations/import/status", new { format = formatLabel });
        });

        app.MapGet("/api/conversations/import/status", (ImportProgressTracker tracker) =>
            Results.Ok(tracker.GetStatus()));

        app.MapPost("/api/conversations/import/cancel", (ImportProgressTracker tracker) =>
        {
            tracker.RequestCancel();
            return Results.Ok(new { message = "Cancel requested." });
        });

        app.MapPost("/api/signout", async (IAuthService auth) =>
        {
            await auth.SignOutAsync();
            return Results.Ok(new { message = "Signed out" });
        });

        app.MapPost("/api/shutdown", (IHostApplicationLifetime lifetime) =>
        {
            lifetime.StopApplication();
            return Results.Ok(new { message = "Shutting down" });
        });

        var logger = app.Services.GetRequiredService<ILoggerFactory>()
            .CreateLogger("Ses.Local.Daemon.Program");

        await app.StartAsync();
        logger.LogInformation("Background services started");

        // Set socket permissions to owner-only (0600) on Unix platforms
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            File.SetUnixFileMode(socketPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        logger.LogInformation("Daemon IPC socket: {SocketPath}", socketPath);
        logger.LogInformation("OAuth/extension listener: http://localhost:37780");

        // Startup: require valid license OR OAuth tokens — never let failures kill the daemon
        try
        {
            var auth    = app.Services.GetRequiredService<IAuthService>();
            var license = app.Services.GetRequiredService<ILicenseService>();

            var authState    = await auth.GetStateAsync();
            var licenseState = await license.GetStateAsync();

            if (authState.IsAuthenticated)
            {
                logger.LogInformation("OAuth authentication active");

                // Periodic revocation check for license keys (if also have a license)
                if (licenseState.IsValid && await license.NeedsRevocationCheckAsync())
                {
                    logger.LogInformation("Performing license revocation check");
                    await license.CheckRevocationAsync();
                }
            }
            else if (licenseState.IsValid)
            {
                logger.LogInformation("License-only mode — Tier 1 user (license expires {ExpiresAt})", licenseState.ExpiresAt);

                // Periodic revocation check
                if (await license.NeedsRevocationCheckAsync())
                {
                    logger.LogInformation("Performing license revocation check");
                    var revocationOk = await license.CheckRevocationAsync();
                    if (!revocationOk)
                        logger.LogWarning("License revocation check failed — license may have been revoked");
                }
            }
            else if (licenseState.Status == LicenseStatus.NoLicense)
            {
                logger.LogInformation("No license and not authenticated — tray app will prompt for license key");
                await auth.TriggerReauthAsync();
            }
            else
            {
                logger.LogWarning("License invalid (status: {Status}) and not authenticated — prompting reauth", licenseState.Status);
                await auth.TriggerReauthAsync();
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Startup auth/license check failed — continuing headless");
        }

        // Block until SIGTERM/SIGINT
        await app.WaitForShutdownAsync();
    }
}

internal sealed record LicenseActivateRequest
{
    public string LicenseKey { get; init; } = string.Empty;
}

internal sealed record ImportConversationsRequest
{
    public string FilePath { get; init; } = string.Empty;
}

/// <summary>
/// Thread-safe singleton that tracks the state of an in-progress or recently completed import.
/// The tray polls GET /api/conversations/import/status every 500 ms during import.
/// </summary>
internal sealed class ImportProgressTracker
{
    private readonly object              _lock = new();
    private CancellationTokenSource      _cts  = new();

    public bool   IsRunning          { get; private set; }
    public int    SessionsImported   { get; private set; }
    public int    MessagesImported   { get; private set; }
    public int    Duplicates         { get; private set; }
    public int    Errors             { get; private set; }
    public string Format             { get; private set; } = string.Empty;
    public string? FailureMessage    { get; private set; }
    public bool   WasCancelled       { get; private set; }

    public CancellationToken CancellationToken { get { lock (_lock) return _cts.Token; } }

    public void Start(string format)
    {
        CancellationTokenSource old;
        lock (_lock)
        {
            old              = _cts;
            _cts             = new CancellationTokenSource();
            IsRunning        = true;
            SessionsImported = 0;
            MessagesImported = 0;
            Duplicates       = 0;
            Errors           = 0;
            Format           = format;
            FailureMessage   = null;
            WasCancelled     = false;
        }
        old.Dispose();
    }

    public void UpdateProgress(int sessionsProcessed)
    {
        lock (_lock) SessionsImported = sessionsProcessed;
    }

    public void Complete(Ses.Local.Workers.Services.ImportResult result)
    {
        lock (_lock)
        {
            IsRunning        = false;
            SessionsImported = result.SessionsImported;
            MessagesImported = result.MessagesImported;
            Duplicates       = result.Duplicates;
            Errors           = result.Errors;
        }
    }

    public void Cancel()
    {
        lock (_lock)
        {
            IsRunning    = false;
            WasCancelled = true;
        }
    }

    public void Fail(string message)
    {
        lock (_lock)
        {
            IsRunning      = false;
            FailureMessage = message;
        }
    }

    public void RequestCancel() { lock (_lock) _cts.Cancel(); }

    public object GetStatus()
    {
        lock (_lock)
            return new
            {
                isRunning        = IsRunning,
                sessionsImported = SessionsImported,
                messagesImported = MessagesImported,
                duplicates       = Duplicates,
                errors           = Errors,
                format           = Format,
                wasCancelled     = WasCancelled,
                failureMessage   = FailureMessage,
            };
    }
}
