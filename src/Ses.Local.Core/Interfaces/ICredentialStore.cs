namespace Ses.Local.Core.Interfaces;

/// <summary>
/// OS keychain abstraction.
/// Mac: Security.framework | Windows: Windows.Security.Credentials.PasswordVault
/// Keys: ses-local-refresh (refresh token), ses-local-pat (MCP PAT for ses-mcp)
/// </summary>
public interface ICredentialStore
{
    Task<string?> GetAsync(string key, CancellationToken ct = default);
    Task SetAsync(string key, string value, CancellationToken ct = default);
    Task DeleteAsync(string key, CancellationToken ct = default);
}
