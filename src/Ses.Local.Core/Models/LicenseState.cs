namespace Ses.Local.Core.Models;

/// <summary>Status of the current ses-local license.</summary>
public enum LicenseStatus
{
    /// <summary>No license key has been activated on this machine.</summary>
    NoLicense,

    /// <summary>License is valid and has passed all checks.</summary>
    Valid,

    /// <summary>License JWT signature or claims are invalid (tampered key).</summary>
    InvalidSignature,

    /// <summary>License has expired.</summary>
    Expired,

    /// <summary>License has been revoked by an administrator.</summary>
    Revoked,
}

/// <summary>Current license state returned by <see cref="Interfaces.ILicenseService"/>.</summary>
public sealed class LicenseState
{
    public LicenseStatus Status { get; init; }
    public string? Email { get; init; }
    public Guid LicenseId { get; init; }
    public DateTime? ExpiresAt { get; init; }

    public bool IsValid => Status == LicenseStatus.Valid;

    public static LicenseState NoLicense => new() { Status = LicenseStatus.NoLicense };
}

/// <summary>Result of a license activation attempt.</summary>
public sealed class LicenseActivationResult
{
    public bool Succeeded { get; init; }
    public string? ErrorMessage { get; init; }
    public LicenseState? State { get; init; }

    public static LicenseActivationResult Success(LicenseState state) =>
        new() { Succeeded = true, State = state };

    public static LicenseActivationResult Failure(string error) =>
        new() { Succeeded = false, ErrorMessage = error };
}
