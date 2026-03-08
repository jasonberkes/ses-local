namespace Ses.Local.Core.Interfaces;

/// <summary>
/// OS keychain abstraction.
/// Mac: Security.framework | Windows: Windows.Security.Credentials.PasswordVault
/// Keys: ses-local-refresh (refresh token), ses-local-pat (MCP PAT for ses-mcp)
/// </summary>
public interface ICredentialStore
{
    /// <summary>Retrieves a credential value by key, or null if not found.</summary>
    Task<string?> GetAsync(string key, CancellationToken ct = default);

    /// <summary>Stores or updates a credential value for the given key.</summary>
    Task SetAsync(string key, string value, CancellationToken ct = default);

    /// <summary>Removes the credential for the given key (no-op if not found).</summary>
    Task DeleteAsync(string key, CancellationToken ct = default);
}
