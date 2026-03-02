namespace Ses.Local.Core.Models;

/// <summary>Current authentication state of ses-local.</summary>
public sealed class SesAuthState
{
    public bool IsAuthenticated { get; init; }
    public string? AccessToken { get; init; }
    public DateTime? AccessTokenExpiresAt { get; init; }
    public bool NeedsReauth { get; init; }

    /// <summary>True if the user has a valid license key (Tier 1 mode).</summary>
    public bool LicenseValid { get; init; }

    /// <summary>License status string (e.g. "Valid", "NoLicense", "Expired").</summary>
    public string? LicenseStatus { get; init; }

    /// <summary>True if the user can use ses-local (either authenticated or has valid license).</summary>
    public bool CanUse => IsAuthenticated || LicenseValid;

    public static SesAuthState Unauthenticated => new() { IsAuthenticated = false };
    public static SesAuthState ReauthRequired => new() { IsAuthenticated = false, NeedsReauth = true };
}
