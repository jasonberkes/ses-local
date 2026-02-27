using Avalonia;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Ses.Local.Workers.Workers;

namespace Ses.Local.Tray;

internal static class Program
{
    [STAThread]
    public static async Task Main(string[] args)
    {
        var host = Host.CreateDefaultBuilder(args)
            .ConfigureServices(services =>
            {
                services.AddHostedService<LevelDbWatcher>();
                services.AddHostedService<ClaudeCodeWatcher>();
                services.AddHostedService<CoworkWatcher>();
                services.AddHostedService<CloudSyncWorker>();
                services.AddHostedService<BrowserExtensionListener>();
            })
            .Build();

        await host.StartAsync();
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        await host.StopAsync();
    }

    private static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<TrayApp>()
            .UsePlatformDetect()
            .LogToTrace();
}
