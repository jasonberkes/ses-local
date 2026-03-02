using System.IdentityModel.Tokens.Jwt;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Ses.Local.Core.Interfaces;
using Ses.Local.Core.Models;
using Ses.Local.Core.Options;

namespace Ses.Local.Workers.Services;

/// <summary>
/// Manages the ses-local license key lifecycle.
///
/// Storage keys (keychain):
///   ses-local-license         → raw license JWT
///   ses-local-license-checked → ISO-8601 UTC timestamp of last revocation check
/// </summary>
public sealed class LicenseService : ILicenseService
{
    private const string KeyLicense = "ses-local-license";
    private const string KeyLastChecked = "ses-local-license-checked";
    private const string ExpectedAudience = "ses-local-license";
    private const string ExpectedPurpose = "ses_license";

    private static readonly JwtSecurityTokenHandler s_tokenHandler = new();

    private readonly ICredentialStore _keychain;
    private readonly LicenseValidationClient _validator;
    private readonly SesLocalOptions _opts;
    private readonly ILogger<LicenseService> _logger;

    // Cached RSA public key PEM — loaded from options or fetched from identity server on first activation
    private string? _publicKeyPem;

    public LicenseService(
        ICredentialStore keychain,
        LicenseValidationClient validator,
        IOptions<SesLocalOptions> options,
        ILogger<LicenseService> logger)
    {
        _keychain = keychain;
        _validator = validator;
        _opts = options.Value;
        _publicKeyPem = options.Value.LicensePublicKeyPem;
        _logger = logger;
    }

    public async Task<LicenseState> GetStateAsync(CancellationToken ct = default)
    {
        var jwt = await _keychain.GetAsync(KeyLicense, ct);
        if (string.IsNullOrWhiteSpace(jwt))
            return LicenseState.NoLicense;

        return ValidateOffline(jwt);
    }

    public async Task<LicenseActivationResult> ActivateAsync(string licenseKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(licenseKey))
            return LicenseActivationResult.Failure("License key is required.");

        // 1. Validate online — this is the authoritative check on first activation
        var serverResult = await _validator.ValidateAsync(licenseKey, ct);

        if (serverResult is null)
        {
            // Server unreachable — try offline validation as fallback
            _logger.LogWarning("Identity server unreachable during activation — attempting offline validation");
            var offlineState = ValidateOffline(licenseKey);
            if (!offlineState.IsValid)
                return LicenseActivationResult.Failure("Could not reach the activation server. Please check your internet connection and try again.");

            // Offline-valid: store and accept (revocation check will happen in 7 days)
            await StoreAsync(licenseKey, ct);
            return LicenseActivationResult.Success(offlineState);
        }

        if (!serverResult.IsValid)
        {
            _logger.LogWarning("License key rejected by server: {Reason}", serverResult.InvalidReason);
            return LicenseActivationResult.Failure(serverResult.InvalidReason ?? "License key is not valid.");
        }

        // 2. Fetch and cache the RSA public key for future offline validation
        await EnsurePublicKeyAsync(ct);

        // 3. Offline-validate the JWT signature now that we have the public key
        var state = ValidateOffline(licenseKey);
        if (!state.IsValid)
        {
            _logger.LogError("Server accepted license but offline validation failed — key may be signed with unknown key");
            return LicenseActivationResult.Failure("License key signature validation failed.");
        }

        // 4. Store the key and record the check timestamp
        await StoreAsync(licenseKey, ct);
        await _keychain.SetAsync(KeyLastChecked, DateTime.UtcNow.ToString("O"), ct);

        _logger.LogInformation("License key activated for {Email}, expires {ExpiresAt}", state.Email, state.ExpiresAt);
        return LicenseActivationResult.Success(state);
    }

    public async Task<bool> CheckRevocationAsync(CancellationToken ct = default)
    {
        var jwt = await _keychain.GetAsync(KeyLicense, ct);
        if (string.IsNullOrWhiteSpace(jwt))
            return false;

        var result = await _validator.ValidateAsync(jwt, ct);
        if (result is null)
        {
            // Offline — cannot check revocation; allow to proceed (fail-open on network errors)
            _logger.LogWarning("Could not reach identity server for revocation check — license allowed (offline)");
            return true;
        }

        if (!result.IsValid)
        {
            _logger.LogWarning("Revocation check failed: {Reason}", result.InvalidReason);
            // Clear the stored license key so user gets prompted
            await _keychain.DeleteAsync(KeyLicense, ct);
            await _keychain.DeleteAsync(KeyLastChecked, ct);
            return false;
        }

        // Update last-checked timestamp
        await _keychain.SetAsync(KeyLastChecked, DateTime.UtcNow.ToString("O"), ct);
        _logger.LogInformation("License revocation check passed");
        return true;
    }

    public async Task<bool> NeedsRevocationCheckAsync(CancellationToken ct = default)
    {
        var jwt = await _keychain.GetAsync(KeyLicense, ct);
        if (string.IsNullOrWhiteSpace(jwt))
            return false;

        var lastCheckedStr = await _keychain.GetAsync(KeyLastChecked, ct);
        if (string.IsNullOrWhiteSpace(lastCheckedStr))
            return true; // Never checked — needs check

        if (!DateTime.TryParse(lastCheckedStr, null, System.Globalization.DateTimeStyles.RoundtripKind, out var lastChecked))
            return true;

        var daysSinceCheck = (DateTime.UtcNow - lastChecked).TotalDays;
        return daysSinceCheck >= _opts.LicenseRevocationCheckDays;
    }

    // ─── Private Helpers ───────────────────────────────────────────────────────

    private LicenseState ValidateOffline(string jwt)
    {
        var publicKeyPem = _publicKeyPem;
        if (string.IsNullOrWhiteSpace(publicKeyPem))
        {
            // No public key yet — can only do basic JWT structure check
            return ValidateJwtStructureOnly(jwt);
        }

        try
        {
            using var rsa = RSA.Create();
            rsa.ImportFromPem(publicKeyPem);
            var securityKey = new RsaSecurityKey(rsa.ExportParameters(false));

            var validationParams = new TokenValidationParameters
            {
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = securityKey,
                ValidateIssuer = true,
                ValidIssuers = ["taskmaster-identity"],
                ValidateAudience = true,
                ValidAudience = ExpectedAudience,
                ValidateLifetime = true,
                ClockSkew = TimeSpan.Zero,
            };

            s_tokenHandler.ValidateToken(jwt, validationParams, out var validatedToken);

            if (validatedToken is not JwtSecurityToken jwtToken)
                return new LicenseState { Status = LicenseStatus.InvalidSignature };

            // Verify purpose claim
            var purpose = jwtToken.Claims.FirstOrDefault(c => c.Type == "purpose")?.Value;
            if (!string.Equals(purpose, ExpectedPurpose, StringComparison.Ordinal))
                return new LicenseState { Status = LicenseStatus.InvalidSignature };

            return BuildState(jwtToken);
        }
        catch (SecurityTokenExpiredException)
        {
            return new LicenseState { Status = LicenseStatus.Expired };
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Offline license validation failed");
            return new LicenseState { Status = LicenseStatus.InvalidSignature };
        }
    }

    private static LicenseState ValidateJwtStructureOnly(string jwt)
    {
        try
        {
            // No public key — just decode and check expiry without signature validation
            var token = s_tokenHandler.ReadJwtToken(jwt);

            if (token.ValidTo < DateTime.UtcNow)
                return new LicenseState { Status = LicenseStatus.Expired };

            var purpose = token.Claims.FirstOrDefault(c => c.Type == "purpose")?.Value;
            if (!string.Equals(purpose, ExpectedPurpose, StringComparison.Ordinal))
                return new LicenseState { Status = LicenseStatus.InvalidSignature };

            return BuildState(token);
        }
        catch
        {
            return new LicenseState { Status = LicenseStatus.InvalidSignature };
        }
    }

    private static LicenseState BuildState(JwtSecurityToken token)
    {
        var email = token.Claims.FirstOrDefault(c => c.Type == "email")?.Value ?? string.Empty;
        _ = Guid.TryParse(token.Subject, out var licenseId);

        return new LicenseState
        {
            Status = LicenseStatus.Valid,
            Email = email,
            LicenseId = licenseId,
            ExpiresAt = token.ValidTo,
        };
    }

    private async Task StoreAsync(string jwt, CancellationToken ct)
    {
        await _keychain.SetAsync(KeyLicense, jwt, ct);
    }

    private async Task EnsurePublicKeyAsync(CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(_publicKeyPem))
            return;

        _logger.LogInformation("Fetching RSA public key from identity server for offline license validation");
        var pem = await _validator.FetchPublicKeyPemAsync(ct);

        if (!string.IsNullOrWhiteSpace(pem))
        {
            _publicKeyPem = pem;
            _logger.LogInformation("RSA public key cached for offline validation");
        }
        else
        {
            _logger.LogWarning("Could not fetch RSA public key — offline validation will use structure-only check");
        }
    }
}
