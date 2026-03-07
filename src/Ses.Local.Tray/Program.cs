using Avalonia;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Ses.Local.Core.Interfaces;
using Ses.Local.Core.Options;
using Ses.Local.Core.Services;
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
            .ConfigureServices((ctx, services) =>
            {
                // SesLocal options — URLs are configurable via appsettings.json
                services.Configure<SesLocalOptions>(ctx.Configuration.GetSection(SesLocalOptions.SectionName));
                services.AddSingleton<IValidateOptions<SesLocalOptions>, SesLocalOptionsValidator>();
                services.AddOptions<SesLocalOptions>().ValidateOnStart();

                // DaemonAuthProxy connects to daemon via Unix domain socket
                services.AddSingleton<DaemonAuthProxy>();
                services.AddSingleton<IAuthService>(sp => sp.GetRequiredService<DaemonAuthProxy>());

                // MCP config manager — reads/writes host config files
                services.AddSingleton<IMcpConfigManager, McpConfigManager>();

                // DaemonSupervisor manages daemon lifecycle with crash recovery
                services.AddSingleton<DaemonSupervisor>();
            })
            .Build();

        // Store services so TrayApp can access them in OnFrameworkInitializationCompleted.
        // app.Instance is null until StartWithClassicDesktopLifetime creates the Application.
        TrayApp.PendingServices = host.Services;

        var app = BuildAvaloniaApp();
        app.StartWithClassicDesktopLifetime(args);
    }

    private static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<TrayApp>()
            .UsePlatformDetect()
            .With(new MacOSPlatformOptions { ShowInDock = false })
            .LogToTrace();
}
