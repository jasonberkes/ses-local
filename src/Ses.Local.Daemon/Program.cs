using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Ses.Local.Core.Interfaces;
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

        var host = Host.CreateDefaultBuilder(args)
            .ConfigureServices((ctx, services) =>
            {
                services.AddSesLocalWorkers(ctx.Configuration);
                services.AddSingleton<UriSchemeHandler>();
                services.AddHostedService<LevelDbWatcher>();
                services.AddHostedService<ClaudeCodeWatcher>();
                services.AddHostedService<CoworkWatcher>();
                services.AddHostedService<CloudSyncWorker>();
                services.AddHostedService<BrowserExtensionListener>();
                services.AddHostedService<AutoUpdateWorker>();
                services.AddHostedService<SesMcpManagerWorker>();
                services.AddHostedService<ClaudeDesktopSyncWorker>();
            })
            .Build();

        var logger = host.Services.GetRequiredService<ILoggerFactory>()
            .CreateLogger("Ses.Local.Daemon.Program");

        // Start background services
        await host.StartAsync();
        logger.LogInformation("Background services started");

        // Attempt auth silently — never let auth failures kill the process
        try
        {
            var auth = host.Services.GetRequiredService<IAuthService>();
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

        logger.LogInformation("Daemon running. Services available on http://localhost:37780");

        // Block until SIGTERM/SIGINT
        await host.WaitForShutdownAsync();
    }
}
