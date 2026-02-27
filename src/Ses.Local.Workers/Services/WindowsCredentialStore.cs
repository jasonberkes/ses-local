#if WINDOWS
using Microsoft.Extensions.Logging;
using Ses.Local.Core.Interfaces;
using System.Runtime.Versioning;

namespace Ses.Local.Workers.Services;

/// <summary>
/// Windows credential store using Windows.Security.Credentials.PasswordVault.
/// Service: SuperEasySoftware.TaskMaster
/// Falls back gracefully on non-Windows platforms.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsCredentialStore : ICredentialStore
{
    private const string ResourcePrefix = "SuperEasySoftware.TaskMaster/";
    private readonly ILogger<WindowsCredentialStore> _logger;

    public WindowsCredentialStore(ILogger<WindowsCredentialStore> logger) => _logger = logger;

    public Task<string?> GetAsync(string key, CancellationToken ct = default)
    {
        try
        {
            var vault      = new Windows.Security.Credentials.PasswordVault();
            var credential = vault.Retrieve(ResourcePrefix + key, key);
            credential.RetrievePassword();
            return Task.FromResult<string?>(credential.Password);
        }
        catch (Exception ex) when (ex.HResult == unchecked((int)0x80070490)) // Element not found
        {
            return Task.FromResult<string?>(null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read credential: {Key}", key);
            return Task.FromResult<string?>(null);
        }
    }

    public Task SetAsync(string key, string value, CancellationToken ct = default)
    {
        try
        {
            var vault      = new Windows.Security.Credentials.PasswordVault();
            var credential = new Windows.Security.Credentials.PasswordCredential(ResourcePrefix + key, key, value);
            vault.Add(credential);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to store credential: {Key}", key);
        }
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string key, CancellationToken ct = default)
    {
        try
        {
            var vault      = new Windows.Security.Credentials.PasswordVault();
            var credential = vault.Retrieve(ResourcePrefix + key, key);
            vault.Remove(credential);
        }
        catch (Exception ex) when (ex.HResult == unchecked((int)0x80070490))
        {
            // Already gone — fine
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete credential: {Key}", key);
        }
        return Task.CompletedTask;
    }
}
#endif
