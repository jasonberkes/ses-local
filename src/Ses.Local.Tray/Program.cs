using Avalonia;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Ses.Local.Core.Interfaces;
using Ses.Local.Workers;
using Ses.Local.Workers.Workers;

namespace Ses.Local.Tray;

internal static class Program
{
    [STAThread]
    public static async Task Main(string[] args)
    {
        // Single-instance enforcement via named mutex
        using var mutex = new Mutex(true, "com.supereasysoftware.ses-local", out var isNewInstance);
        if (!isNewInstance)
        {
            Console.Error.WriteLine("ses-local is already running.");
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
            .CreateLogger("Ses.Local.Tray.Program");

        // Start background services first — these must survive regardless of GUI state
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

        // Attempt tray GUI — if Avalonia crashes, fall back to headless mode
        try
        {
            var app = BuildAvaloniaApp();
            if (app.Instance is TrayApp trayApp)
                trayApp.SetServiceProvider(host.Services);

            app.StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Tray GUI failed — running headless (background services still active)");

            // Keep process alive in headless mode until SIGTERM/SIGINT
            var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
            AppDomain.CurrentDomain.ProcessExit += (_, _) => cts.Cancel();

            logger.LogInformation("Running in headless mode. Services available on http://localhost:37780");
            try { await Task.Delay(Timeout.Infinite, cts.Token); }
            catch (OperationCanceledException) { /* shutdown requested */ }
        }

        await host.StopAsync();
    }

    private static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<TrayApp>()
            .UsePlatformDetect()
            .LogToTrace();
}
