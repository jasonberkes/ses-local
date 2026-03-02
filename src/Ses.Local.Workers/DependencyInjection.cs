using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Ses.Local.Core.Interfaces;
using Ses.Local.Core.Options;
using Ses.Local.Workers.Services;

namespace Ses.Local.Workers;

public static class DependencyInjection
{
    public static IServiceCollection AddSesLocalWorkers(
        this IServiceCollection services,
        IConfiguration? configuration = null)
    {
        // SQLite data layer
        services.AddSingleton<ILocalDbService, LocalDbService>();

        // Options
        if (configuration is not null)
            services.Configure<SesLocalOptions>(configuration.GetSection(SesLocalOptions.SectionName));
        else
            services.Configure<SesLocalOptions>(_ => { });

        // OS keychain — platform-specific
        if (OperatingSystem.IsMacOS())
            services.AddSingleton<ICredentialStore, MacCredentialStore>();
#if WINDOWS
        else if (OperatingSystem.IsWindows())
            services.AddSingleton<ICredentialStore, WindowsCredentialStore>();
#endif
        else
            services.AddSingleton<ICredentialStore, InMemoryCredentialStore>(); // Linux/CI

        // Identity HTTP client
        services.AddHttpClient<IdentityClient>(client =>
        {
            client.BaseAddress = new Uri("https://identity.tm.supereasysoftware.com/");
            client.Timeout = TimeSpan.FromSeconds(30);
        });

        // License validation HTTP client
        services.AddHttpClient<LicenseValidationClient>(client =>
        {
            client.BaseAddress = new Uri("https://identity.tm.supereasysoftware.com/");
            client.Timeout = TimeSpan.FromSeconds(15);
        });

        // Auth service
        services.AddSingleton<IAuthService, AuthService>();

        // License service
        services.AddSingleton<ILicenseService, LicenseService>();

        // Cloud sync services
        services.AddSingleton<DocumentServiceUploader>();
        services.AddSingleton<CloudMemoryRetainer>();

        // Desktop activity event bus + LevelDB watcher (WI-940)
        services.AddSingleton<LevelDbUuidExtractor>();
        services.AddSingleton<IDesktopActivityNotifier, DesktopActivityNotifier>();

        // Claude.ai API client + sync service (WI-941)
        services.AddSingleton<ClaudeSessionCookieExtractor>();
        services.AddSingleton<ClaudeAiSyncService>();

        // Auto-updaters
        services.AddHttpClient<SesLocalUpdater>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
        });
        services.AddHttpClient<SesMcpUpdater>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
        });
        services.AddSingleton<SesLocalUpdater>();
        services.AddSingleton<SesMcpUpdater>();
        services.AddSingleton<SesMcpManager>();

        return services;
    }
}
