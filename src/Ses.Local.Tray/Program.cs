using Avalonia;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
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

        await host.StartAsync();

        var auth = host.Services.GetRequiredService<IAuthService>();
        var state = await auth.GetStateAsync();
        if (!state.IsAuthenticated)
            await auth.TriggerReauthAsync();

        var app = BuildAvaloniaApp();
        if (app.Instance is TrayApp trayApp)
            trayApp.SetServiceProvider(host.Services);

        app.StartWithClassicDesktopLifetime(args);
        await host.StopAsync();
    }

    private static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<TrayApp>()
            .UsePlatformDetect()
            .LogToTrace();
}
