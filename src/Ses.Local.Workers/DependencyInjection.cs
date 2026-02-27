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

        // Auth service
        services.AddSingleton<IAuthService, AuthService>();

        return services;
    }
}
