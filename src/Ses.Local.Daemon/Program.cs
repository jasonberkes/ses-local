using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Ses.Local.Core.Interfaces;
using Ses.Local.Core.Models;
using Ses.Local.Core.Services;
using Ses.Local.Workers;
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
        builder.Services.AddSingleton<UriSchemeHandler>();
        builder.Services.AddHostedService<LevelDbWatcher>();
        builder.Services.AddHostedService<ClaudeCodeWatcher>();
        builder.Services.AddHostedService<CoworkWatcher>();
        builder.Services.AddHostedService<CloudSyncWorker>();
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
