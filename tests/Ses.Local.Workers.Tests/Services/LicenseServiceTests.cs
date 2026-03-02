using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Ses.Local.Core.Models;
using Ses.Local.Core.Options;
using Ses.Local.Workers.Services;
using Xunit;

namespace Ses.Local.Workers.Tests.Services;

public sealed class LicenseServiceTests
{
    // ─── RSA test key pair (generated once for all tests) ────────────────────
    private static readonly RSA s_rsa = RSA.Create(2048);
    private static readonly string s_publicKeyPem = s_rsa.ExportSubjectPublicKeyInfoPem();

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private static LicenseService BuildSut(
        InMemoryCredentialStore? keychain = null,
        HttpMessageHandler? httpHandler = null,
        SesLocalOptions? opts = null)
    {
        keychain ??= new InMemoryCredentialStore();
        opts ??= new SesLocalOptions { LicensePublicKeyPem = s_publicKeyPem };
        var options = Options.Create(opts);

        var handler = httpHandler ?? new MockValidationHandler(isValid: true);
        var http = new System.Net.Http.HttpClient(handler)
        {
            BaseAddress = new Uri("https://identity.test/")
        };
        var client = new LicenseValidationClient(http, NullLogger<LicenseValidationClient>.Instance);

        return new LicenseService(keychain, client, options, NullLogger<LicenseService>.Instance);
    }

    private static string CreateLicenseJwt(
        Guid? licenseId = null,
        string email = "test@example.com",
        string purpose = "ses_license",
        string audience = "ses-local-license",
        DateTime? expires = null)
    {
        var id = licenseId ?? Guid.NewGuid();
        var exp = expires ?? DateTime.UtcNow.AddDays(365);

        var signingCredentials = new SigningCredentials(
            new RsaSecurityKey(s_rsa) { KeyId = "key-1" },
            SecurityAlgorithms.RsaSha256);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, id.ToString()),
            new(JwtRegisteredClaimNames.Jti, id.ToString()),
            new(JwtRegisteredClaimNames.Email, email),
            new("purpose", purpose),
        };

        var header = new JwtHeader(signingCredentials);
        var payload = new JwtPayload(
            issuer: "taskmaster-identity",
            audience: audience,
            claims: claims,
            notBefore: null,
            expires: exp);

        var token = new JwtSecurityToken(header, payload);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    // ─── GetStateAsync ────────────────────────────────────────────────────────

    [Fact]
    public async Task GetStateAsync_NoStoredLicense_ReturnsNoLicense()
    {
        var sut = BuildSut();
        var state = await sut.GetStateAsync();
        Assert.Equal(LicenseStatus.NoLicense, state.Status);
    }

    [Fact]
    public async Task GetStateAsync_ValidLicenseStored_ReturnsValid()
    {
        var keychain = new InMemoryCredentialStore();
        var jwt = CreateLicenseJwt(email: "user@test.com");
        await keychain.SetAsync("ses-local-license", jwt);

        var sut = BuildSut(keychain);
        var state = await sut.GetStateAsync();

        Assert.Equal(LicenseStatus.Valid, state.Status);
        Assert.Equal("user@test.com", state.Email);
        Assert.True(state.IsValid);
    }

    [Fact]
    public async Task GetStateAsync_ExpiredLicense_ReturnsExpired()
    {
        var keychain = new InMemoryCredentialStore();
        // Use structure-only path (no public key) to test expiry detection
        var opts = new SesLocalOptions(); // No public key — structure-only check
        var jwt = CreateLicenseJwt(expires: DateTime.UtcNow.AddMinutes(-1));
        await keychain.SetAsync("ses-local-license", jwt);

        var sut = BuildSut(keychain, opts: opts);
        var state = await sut.GetStateAsync();

        Assert.Equal(LicenseStatus.Expired, state.Status);
    }

    [Fact]
    public async Task GetStateAsync_InvalidSignature_ReturnsInvalidSignature()
    {
        var keychain = new InMemoryCredentialStore();
        // Create JWT with a DIFFERENT RSA key
        using var wrongKey = RSA.Create(2048);
        var signingCreds = new SigningCredentials(
            new RsaSecurityKey(wrongKey), SecurityAlgorithms.RsaSha256);
        var claims = new[] { new Claim("purpose", "ses_license"), new Claim(JwtRegisteredClaimNames.Email, "x@x.com") };
        var token = new JwtSecurityToken(
            issuer: "taskmaster-identity",
            audience: "ses-local-license",
            claims: claims,
            expires: DateTime.UtcNow.AddDays(365),
            signingCredentials: signingCreds);
        var jwt = new JwtSecurityTokenHandler().WriteToken(token);

        await keychain.SetAsync("ses-local-license", jwt);

        var sut = BuildSut(keychain); // has correct public key
        var state = await sut.GetStateAsync();

        Assert.Equal(LicenseStatus.InvalidSignature, state.Status);
    }

    // ─── ActivateAsync ────────────────────────────────────────────────────────

    [Fact]
    public async Task ActivateAsync_ValidKey_StoresKeyAndReturnsSuccess()
    {
        var keychain = new InMemoryCredentialStore();
        var jwt = CreateLicenseJwt(email: "activate@test.com");
        var sut = BuildSut(keychain, new MockValidationHandler(isValid: true, email: "activate@test.com"));

        var result = await sut.ActivateAsync(jwt);

        Assert.True(result.Succeeded);
        Assert.NotNull(result.State);
        Assert.Equal(LicenseStatus.Valid, result.State!.Status);

        var stored = await keychain.GetAsync("ses-local-license");
        Assert.Equal(jwt, stored);
    }

    [Fact]
    public async Task ActivateAsync_ServerRejectsKey_ReturnsFailure()
    {
        var jwt = CreateLicenseJwt();
        var sut = BuildSut(httpHandler: new MockValidationHandler(isValid: false, invalidReason: "License revoked."));

        var result = await sut.ActivateAsync(jwt);

        Assert.False(result.Succeeded);
        Assert.Equal("License revoked.", result.ErrorMessage);
    }

    [Fact]
    public async Task ActivateAsync_EmptyKey_ReturnsFailure()
    {
        var sut = BuildSut();
        var result = await sut.ActivateAsync("  ");
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task ActivateAsync_ServerUnreachable_OfflineValidationSucceeds()
    {
        var keychain = new InMemoryCredentialStore();
        var jwt = CreateLicenseJwt();
        var sut = BuildSut(keychain, new UnreachableHandler());

        var result = await sut.ActivateAsync(jwt);

        Assert.True(result.Succeeded);
        var stored = await keychain.GetAsync("ses-local-license");
        Assert.Equal(jwt, stored);
    }

    // ─── NeedsRevocationCheckAsync ────────────────────────────────────────────

    [Fact]
    public async Task NeedsRevocationCheckAsync_NoLicense_ReturnsFalse()
    {
        var sut = BuildSut();
        Assert.False(await sut.NeedsRevocationCheckAsync());
    }

    [Fact]
    public async Task NeedsRevocationCheckAsync_NeverChecked_ReturnsTrue()
    {
        var keychain = new InMemoryCredentialStore();
        await keychain.SetAsync("ses-local-license", CreateLicenseJwt());
        // No "ses-local-license-checked" key set

        var sut = BuildSut(keychain);
        Assert.True(await sut.NeedsRevocationCheckAsync());
    }

    [Fact]
    public async Task NeedsRevocationCheckAsync_RecentlyChecked_ReturnsFalse()
    {
        var keychain = new InMemoryCredentialStore();
        await keychain.SetAsync("ses-local-license", CreateLicenseJwt());
        await keychain.SetAsync("ses-local-license-checked", DateTime.UtcNow.ToString("O"));

        var opts = new SesLocalOptions { LicenseRevocationCheckDays = 7, LicensePublicKeyPem = s_publicKeyPem };
        var sut = BuildSut(keychain, opts: opts);

        Assert.False(await sut.NeedsRevocationCheckAsync());
    }

    [Fact]
    public async Task NeedsRevocationCheckAsync_CheckExpired_ReturnsTrue()
    {
        var keychain = new InMemoryCredentialStore();
        await keychain.SetAsync("ses-local-license", CreateLicenseJwt());
        await keychain.SetAsync("ses-local-license-checked", DateTime.UtcNow.AddDays(-8).ToString("O"));

        var opts = new SesLocalOptions { LicenseRevocationCheckDays = 7, LicensePublicKeyPem = s_publicKeyPem };
        var sut = BuildSut(keychain, opts: opts);

        Assert.True(await sut.NeedsRevocationCheckAsync());
    }

    // ─── CheckRevocationAsync ─────────────────────────────────────────────────

    [Fact]
    public async Task CheckRevocationAsync_ServerSaysValid_ReturnsTrue()
    {
        var keychain = new InMemoryCredentialStore();
        await keychain.SetAsync("ses-local-license", CreateLicenseJwt());

        var sut = BuildSut(keychain, new MockValidationHandler(isValid: true));
        var result = await sut.CheckRevocationAsync();

        Assert.True(result);
        // Timestamp should be updated
        var lastChecked = await keychain.GetAsync("ses-local-license-checked");
        Assert.NotNull(lastChecked);
    }

    [Fact]
    public async Task CheckRevocationAsync_ServerSaysRevoked_ReturnsFalseAndClearsKey()
    {
        var keychain = new InMemoryCredentialStore();
        await keychain.SetAsync("ses-local-license", CreateLicenseJwt());

        var sut = BuildSut(keychain, new MockValidationHandler(isValid: false, invalidReason: "Revoked."));
        var result = await sut.CheckRevocationAsync();

        Assert.False(result);
        Assert.Null(await keychain.GetAsync("ses-local-license"));
    }

    [Fact]
    public async Task CheckRevocationAsync_ServerUnreachable_ReturnsTrueFailOpen()
    {
        var keychain = new InMemoryCredentialStore();
        await keychain.SetAsync("ses-local-license", CreateLicenseJwt());

        var sut = BuildSut(keychain, new UnreachableHandler());
        var result = await sut.CheckRevocationAsync();

        Assert.True(result); // Fail-open when offline
    }

    // ─── Mock helpers ─────────────────────────────────────────────────────────

    private sealed class MockValidationHandler(
        bool isValid,
        string email = "test@example.com",
        string? invalidReason = null) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var response = new
            {
                isValid,
                licenseId = Guid.NewGuid(),
                email,
                expiresAt = DateTime.UtcNow.AddDays(365),
                invalidReason,
            };

            var json = JsonSerializer.Serialize(response, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class UnreachableHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromException<HttpResponseMessage>(new HttpRequestException("Connection refused"));
    }
}
