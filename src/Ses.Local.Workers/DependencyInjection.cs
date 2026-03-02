using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using Ses.Local.Core.Interfaces;
using Ses.Local.Core.Options;
using Ses.Local.Workers.Services;
using Ses.Local.Workers.Workers;

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

        // Options — validate all URLs at startup
        if (configuration is not null)
            services.Configure<SesLocalOptions>(configuration.GetSection(SesLocalOptions.SectionName));
        else
            services.Configure<SesLocalOptions>(_ => { });
        services.AddSingleton<IValidateOptions<SesLocalOptions>, SesLocalOptionsValidator>();
        services.AddOptions<SesLocalOptions>().ValidateOnStart();

        // OS keychain — platform-specific
        if (OperatingSystem.IsMacOS())
            services.AddSingleton<ICredentialStore, MacCredentialStore>();
#if WINDOWS
        else if (OperatingSystem.IsWindows())
            services.AddSingleton<ICredentialStore, WindowsCredentialStore>();
#endif
        else
            services.AddSingleton<ICredentialStore, InMemoryCredentialStore>(); // Linux/CI

        // Identity HTTP client — 2 retries, 10s total timeout, base address from options
        services.AddHttpClient<IdentityClient>((sp, client) =>
        {
            var opts = sp.GetRequiredService<IOptions<SesLocalOptions>>().Value;
            client.BaseAddress = new Uri(opts.IdentityBaseUrl.TrimEnd('/') + "/");
        })
        .AddStandardResilienceHandler(options =>
        {
            options.Retry.MaxRetryAttempts = 2;
            options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(10);
            options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(3);
        });

        // License validation HTTP client — uses identity server base URL
        services.AddHttpClient<LicenseValidationClient>((sp, client) =>
        {
            var opts = sp.GetRequiredService<IOptions<SesLocalOptions>>().Value;
            client.BaseAddress = new Uri(opts.IdentityBaseUrl.TrimEnd('/') + "/");
        })
        .AddStandardResilienceHandler(options =>
        {
            options.Retry.MaxRetryAttempts = 2;
            options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(15);
            options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(5);
        });

        // Auth service
        services.AddSingleton<IAuthService, AuthService>();

        // License service
        services.AddSingleton<ILicenseService, LicenseService>();

        // DocumentService HTTP client — 3 retries, 30s total timeout, URL from options
        services.AddHttpClient(DocumentServiceClientName, (sp, client) =>
        {
            var opts = sp.GetRequiredService<IOptions<SesLocalOptions>>().Value;
            client.BaseAddress = new Uri(opts.DocumentServiceBaseUrl.TrimEnd('/') + "/");
        })
        .AddStandardResilienceHandler(options =>
        {
            options.Retry.MaxRetryAttempts = 3;
            options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(30);
            options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(7);
        });

        // CloudMemory HTTP client — 2 retries, 15s total timeout, URL from options
        services.AddHttpClient(CloudMemoryClientName, (sp, client) =>
        {
            var opts = sp.GetRequiredService<IOptions<SesLocalOptions>>().Value;
            client.BaseAddress = new Uri(opts.MemoryBaseUrl.TrimEnd('/') + "/");
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
        services.AddHttpClient(ClaudeAiClient.HttpClientName, (sp, client) =>
        {
            var opts = sp.GetRequiredService<IOptions<SesLocalOptions>>().Value;
            client.BaseAddress = new Uri(opts.ClaudeAiBaseUrl.TrimEnd('/') + "/");
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

        // Observation compression pipeline — Layer 1 (rule-based, always runs)
        services.AddSingleton<IObservationCompressor, RuleBasedCompressor>();
        services.AddHostedService<CompressionWorker>();

        // CLAUDE.md auto-generation (WI-982)
        services.AddSingleton<IClaudeMdGenerator, ClaudeMdGenerator>();

        // Claude.ai export importer (WI-985)
        services.AddSingleton<ClaudeExportParser>();

        // Vector search — ONNX embedding + brute-force cosine similarity (WI-989)
        // Model download client — 3 retries, 5 min total timeout (large binary download)
        services.AddHttpClient<ModelDownloadService>()
        .AddStandardResilienceHandler(options =>
        {
            options.Retry.MaxRetryAttempts = 3;
            options.AttemptTimeout.Timeout = TimeSpan.FromMinutes(3);
            options.TotalRequestTimeout.Timeout = TimeSpan.FromMinutes(10);
        });
        services.AddSingleton<IModelDownloadService>(sp =>
            sp.GetRequiredService<ModelDownloadService>());
        services.AddSingleton<ILocalEmbeddingService, LocalEmbeddingService>();
        services.AddSingleton<IVectorSearchService, VectorSearchService>();

        return services;
    }
}
