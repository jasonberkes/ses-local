using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Ses.Local.Core.Interfaces;
using Ses.Local.Core.Options;
using Ses.Local.Workers.Services;

namespace Ses.Local.Workers;

public static class DependencyInjection
{
    // Named client keys
    internal const string DocumentServiceClientName = "DocumentService";
    internal const string CloudMemoryClientName      = "CloudMemory";
    internal const string SesMcpInstallClientName    = "SesMcpInstall";

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

        // Identity HTTP client — 2 retries, 10s total timeout
        services.AddHttpClient<IdentityClient>(client =>
        {
            client.BaseAddress = new Uri("https://identity.tm.supereasysoftware.com/");
        })
        .AddStandardResilienceHandler(options =>
        {
            options.Retry.MaxRetryAttempts = 2;
            options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(10);
            options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(3);
        });

        // Auth service
        services.AddSingleton<IAuthService, AuthService>();

        // DocumentService HTTP client — 3 retries, 30s total timeout
        services.AddHttpClient(DocumentServiceClientName, client =>
        {
            client.BaseAddress = new Uri(
                "https://tm-documentservice-prod-eus2.redhill-040b1667.eastus2.azurecontainerapps.io");
        })
        .AddStandardResilienceHandler(options =>
        {
            options.Retry.MaxRetryAttempts = 3;
            options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(30);
            options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(7);
        });

        // CloudMemory HTTP client — 2 retries, 15s total timeout
        services.AddHttpClient(CloudMemoryClientName, client =>
        {
            client.BaseAddress = new Uri("https://memory.tm.supereasysoftware.com");
        })
        .AddStandardResilienceHandler(options =>
        {
            options.Retry.MaxRetryAttempts = 2;
            options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(15);
            options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(5);
        });

        // Cloud sync services
        services.AddSingleton<DocumentServiceUploader>();
        services.AddSingleton<CloudMemoryRetainer>();

        // Desktop activity event bus + LevelDB watcher (WI-940)
        services.AddSingleton<LevelDbUuidExtractor>();
        services.AddSingleton<IDesktopActivityNotifier, DesktopActivityNotifier>();

        // Claude.ai API client + sync service (WI-941)
        // No resilience — uses session cookies and has custom rate limiting
        services.AddHttpClient(ClaudeAiClient.HttpClientName, client =>
        {
            client.BaseAddress = new Uri("https://claude.ai");
            client.Timeout     = TimeSpan.FromSeconds(30);
        });
        services.AddSingleton<ClaudeSessionCookieExtractor>();
        services.AddSingleton<ClaudeAiSyncService>();

        // SesMcp binary install client — 3 retries, 60s per-attempt timeout
        services.AddHttpClient(SesMcpInstallClientName)
        .AddStandardResilienceHandler(options =>
        {
            options.Retry.MaxRetryAttempts = 3;
            options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(60);
            options.TotalRequestTimeout.Timeout = TimeSpan.FromMinutes(5);
        });

        // Auto-updaters — 3 retries, 60s per-attempt timeout (binary downloads)
        services.AddHttpClient<SesLocalUpdater>()
        .AddStandardResilienceHandler(options =>
        {
            options.Retry.MaxRetryAttempts = 3;
            options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(60);
            options.TotalRequestTimeout.Timeout = TimeSpan.FromMinutes(5);
        });

        services.AddHttpClient<SesMcpUpdater>()
        .AddStandardResilienceHandler(options =>
        {
            options.Retry.MaxRetryAttempts = 3;
            options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(60);
            options.TotalRequestTimeout.Timeout = TimeSpan.FromMinutes(5);
        });

        services.AddSingleton<SesMcpManager>();

        return services;
    }
}
