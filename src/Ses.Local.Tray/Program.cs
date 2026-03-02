using Avalonia;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Ses.Local.Core.Interfaces;
using Ses.Local.Tray.Services;

namespace Ses.Local.Tray;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Single-instance enforcement via named mutex
        using var mutex = new Mutex(true, "com.supereasysoftware.ses-local-tray", out var isNewInstance);
        if (!isNewInstance)
        {
            Console.Error.WriteLine("ses-local tray is already running.");
            return;
        }

        var host = Host.CreateDefaultBuilder(args)
            .ConfigureServices((_, services) =>
            {
                services.AddHttpClient("daemon", client =>
                {
                    client.BaseAddress = new Uri("http://localhost:37780/");
                    client.Timeout = TimeSpan.FromSeconds(5);
                });
                services.AddSingleton<IAuthService, DaemonAuthProxy>();
            })
            .Build();

        var app = BuildAvaloniaApp();
        if (app.Instance is TrayApp trayApp)
            trayApp.SetServiceProvider(host.Services);

        app.StartWithClassicDesktopLifetime(args);
    }

    private static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<TrayApp>()
            .UsePlatformDetect()
            .LogToTrace();
}
