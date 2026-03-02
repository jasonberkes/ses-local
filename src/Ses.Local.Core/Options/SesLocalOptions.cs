namespace Ses.Local.Core.Options;

/// <summary>
/// Top-level configuration options for ses-local.
/// Populated from appsettings.json and/or environment variables.
/// Feature flags will be driven by PAT scopes in WI-936; for now use these bools.
/// </summary>
public sealed class SesLocalOptions
{
    public const string SectionName = "SesLocal";

    /// <summary>Identity server base URL.</summary>
    public string IdentityBaseUrl { get; init; } = "https://identity.tm.supereasysoftware.com";

    /// <summary>Enable Claude Code JSONL session watcher. Requires developer scope.</summary>
    public bool EnableClaudeCodeSync { get; set; } = true;

    /// <summary>Enable Claude Desktop LevelDB UUID watcher.</summary>
    public bool EnableClaudeDesktopSync { get; set; } = true;

    /// <summary>Polling interval in seconds for file watchers.</summary>
    public int PollingIntervalSeconds { get; set; } = 30;

    /// <summary>
    /// RSA public key PEM from the identity server, used for offline license key validation.
    /// Fetched from the JWKS endpoint on first activation and cached here for offline use.
    /// </summary>
    public string? LicensePublicKeyPem { get; set; }

    /// <summary>Days between online revocation checks. Default: 7.</summary>
    public int LicenseRevocationCheckDays { get; set; } = 7;
}
