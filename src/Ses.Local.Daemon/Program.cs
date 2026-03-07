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

        builder.Services.AddHostedService<LevelDbWatcher>();
        builder.Services.AddHostedService<ClaudeCodeWatcher>();
        builder.Services.AddHostedService<CoworkWatcher>();
        builder.Services.AddHostedService<ChatGptDesktopWatcher>();
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

        app.MapPost("/api/conversations/import", async (HttpContext ctx, ConversationImportDispatcher dispatcher) =>
        {
            var body = await System.Text.Json.JsonSerializer.DeserializeAsync<ImportConversationsRequest>(
                ctx.Request.Body,
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (body is null || string.IsNullOrWhiteSpace(body.FilePath))
                return Results.BadRequest(new { error = "filePath is required." });

            if (!File.Exists(body.FilePath))
                return Results.BadRequest(new { error = "File not found." });

            var detectedFormat = ConversationImportDispatcher.DetectFormat(body.FilePath);
            var result = await dispatcher.ImportAsync(body.FilePath, ct: ctx.RequestAborted);

            return Results.Ok(new
            {
                sessionsImported = result.SessionsImported,
                messagesImported = result.MessagesImported,
                duplicates       = result.Duplicates,
                errors           = result.Errors,
                format           = ConversationImportDispatcher.FormatLabel(detectedFormat)
            });
        });

        app.MapPost("/api/signout", async (IAuthService auth) =>
        {
            await auth.SignOutAsync();
            return Results.Ok(new { message = "Signed out" });
        });

        app.MapGet("/api/components", (SesMcpManager mcpManager) =>
        {
            var health = mcpManager.GetStatus();
            return Results.Ok(new
            {
                sesMcp = new
                {
                    installed  = health.IsInstalled,
                    configured = health.IsConfigured,
                    version    = health.InstalledVersion
                },
                daemon = new
                {
                    installed = true,
                    version   = System.Reflection.Assembly.GetExecutingAssembly()
                                    .GetName().Version?.ToString(3)
                },
                sesHooks = new
                {
                    installed = health.SesHooksInstalled
                }
            });
        });

        app.MapGet("/api/sync-stats", async (ILocalDbService db, HttpContext ctx) =>
        {
            var stats = await db.GetSyncStatsAsync(ctx.RequestAborted);
            return Results.Ok(stats);
        });

        app.MapPost("/api/shutdown", (IHostApplicationLifetime lifetime) =>
        {
            lifetime.StopApplication();
            return Results.Ok(new { message = "Shutting down" });
        });

        app.MapGet("/api/hooks/status", async (SesMcpManager mcpManager, ILocalDbService db) =>
        {
            var health       = mcpManager.GetStatus();
            var settingsPath = SesMcpManager.GetClaudeCodeSettingsPath();
            var hooksPath    = SesMcpManager.GetSesHooksBinaryPath();

            var registered = ClaudeCodeSettings.LoadOrCreate(settingsPath).HasCorrectHooks(hooksPath);

            var lastActivity = await db.GetLastHookObservationTimeAsync();

            return Results.Ok(new
            {
                registered,
                binaryExists = health.SesHooksInstalled,
                lastActivity = lastActivity?.ToString("O")
            });
        });

        app.MapGet("/api/hooks/logs", async (ILocalDbService db) =>
        {
            var obs = await db.GetRecentHookObservationsAsync(20);
            return Results.Ok(obs.Select(o => new
            {
                timestamp = o.CreatedAt.ToString("O"),
                toolName  = o.ToolName,
                filePath  = o.FilePath
            }));
        });

        app.MapPost("/api/hooks/enable", async (SesMcpManager mcpManager) =>
        {
            await mcpManager.CheckAndRepairClaudeCodeHooksAsync();
            return Results.Ok(new { message = "Hooks registered" });
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
