using Ses.Local.Core.Interfaces;

namespace Ses.Local.Workers.Services;

/// <summary>
/// In-memory credential store for testing and Linux (dev only).
/// NOT for production use — credentials lost on restart.
/// </summary>
public sealed class InMemoryCredentialStore : ICredentialStore
{
    private readonly Dictionary<string, string> _store = new();

    public Task<string?> GetAsync(string key, CancellationToken ct = default) =>
        Task.FromResult(_store.TryGetValue(key, out var v) ? v : null);

    public Task SetAsync(string key, string value, CancellationToken ct = default)
    {
        _store[key] = value;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string key, CancellationToken ct = default)
    {
        _store.Remove(key);
        return Task.CompletedTask;
    }
}
