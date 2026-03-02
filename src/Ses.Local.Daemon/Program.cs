using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Ses.Local.Core.Interfaces;
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
        app.MapGet("/api/status", async (IAuthService auth) =>
        {
            var state  = await auth.GetStateAsync();
            var uptime = Stopwatch.GetElapsedTime(startTimestamp);
            return Results.Ok(new
            {
                authenticated = state.IsAuthenticated,
                needsReauth   = state.NeedsReauth,
                uptime        = uptime.ToString(@"d\.hh\:mm\:ss")
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

        // Attempt auth silently — never let auth failures kill the process
        try
        {
            var auth = app.Services.GetRequiredService<IAuthService>();
            var state = await auth.GetStateAsync();
            if (!state.IsAuthenticated)
            {
                logger.LogInformation("Not authenticated — triggering browser login");
                await auth.TriggerReauthAsync();
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Auth check failed on startup — continuing headless");
        }

        // Block until SIGTERM/SIGINT
        await app.WaitForShutdownAsync();
    }
}
