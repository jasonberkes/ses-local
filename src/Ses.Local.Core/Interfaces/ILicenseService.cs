using Ses.Local.Core.Models;

namespace Ses.Local.Core.Interfaces;

/// <summary>
/// Manages the ses-local license key lifecycle.
/// - First activation: validate online against identity server
/// - Subsequent startups: validate offline via embedded RSA public key
/// - Revocation check: every 7 days via online validation
/// </summary>
public interface ILicenseService
{
    /// <summary>
    /// Get the current license state. Validates offline if a key is stored;
    /// returns <see cref="LicenseState.NoLicense"/> if no key has been activated.
    /// </summary>
    Task<LicenseState> GetStateAsync(CancellationToken ct = default);

    /// <summary>
    /// Activate a license key for the first time. Validates the key online,
    /// stores it in the OS keychain, and records the activation timestamp.
    /// </summary>
    Task<LicenseActivationResult> ActivateAsync(string licenseKey, CancellationToken ct = default);

    /// <summary>
    /// Perform an online revocation check. Called automatically every 7 days.
    /// Returns false if the key has been revoked or is no longer valid.
    /// </summary>
    Task<bool> CheckRevocationAsync(CancellationToken ct = default);

    /// <summary>
    /// True if the stored license requires an online revocation check
    /// (i.e., more than 7 days since last check or never checked).
    /// </summary>
    Task<bool> NeedsRevocationCheckAsync(CancellationToken ct = default);
}
