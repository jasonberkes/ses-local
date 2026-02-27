using Microsoft.Extensions.DependencyInjection;
using Ses.Local.Core.Interfaces;
using Ses.Local.Workers.Services;

namespace Ses.Local.Workers;

/// <summary>Extension methods for registering ses-local worker services.</summary>
public static class DependencyInjection
{
    public static IServiceCollection AddSesLocalWorkers(this IServiceCollection services)
    {
        // Singleton — one shared SQLite connection (WAL mode handles concurrency)
        services.AddSingleton<ILocalDbService, LocalDbService>();
        return services;
    }
}
