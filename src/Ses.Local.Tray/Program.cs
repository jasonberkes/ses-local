using Avalonia;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Ses.Local.Workers;
using Ses.Local.Workers.Workers;

namespace Ses.Local.Tray;

internal static class Program
{
    [STAThread]
    public static async Task Main(string[] args)
    {
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
            })
            .Build();

        await host.StartAsync();

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
