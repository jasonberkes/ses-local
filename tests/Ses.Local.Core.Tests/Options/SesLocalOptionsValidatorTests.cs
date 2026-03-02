using Microsoft.Extensions.Options;
using Ses.Local.Core.Options;
using Xunit;

namespace Ses.Local.Core.Tests.Options;

public sealed class SesLocalOptionsValidatorTests
{
    private static readonly SesLocalOptionsValidator Sut = new();

    [Fact]
    public void Validate_DefaultOptions_ReturnsSuccess()
    {
        var result = Sut.Validate(null, new SesLocalOptions());
        Assert.Equal(ValidateOptionsResult.Success, result);
    }

    [Theory]
    [InlineData("IdentityBaseUrl")]
    [InlineData("DocumentServiceBaseUrl")]
    [InlineData("MemoryBaseUrl")]
    [InlineData("ClaudeAiBaseUrl")]
    [InlineData("SesMcpManifestUrl")]
    [InlineData("SesLocalManifestUrl")]
    [InlineData("CloudMcpUrl")]
    [InlineData("DocsBaseUrl")]
    public void Validate_EmptyUrl_ReturnsFailure(string propertyName)
    {
        var options = BuildOptionsWithEmptyProperty(propertyName);
        var result  = Sut.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(propertyName, result.FailureMessage);
    }

    [Theory]
    [InlineData("not-a-url")]
    [InlineData("ftp://example.com")]
    [InlineData("http://example.com")]
    public void Validate_InvalidOrNonHttpsUrl_ReturnsFailure(string badUrl)
    {
        var options = new SesLocalOptions
        {
            IdentityBaseUrl = badUrl
        };

        var result = Sut.Validate(null, options);

        Assert.True(result.Failed);
    }

    [Fact]
    public void Validate_AllUrlsValid_ReturnsSuccess()
    {
        var options = new SesLocalOptions
        {
            IdentityBaseUrl       = "https://identity.example.com",
            DocumentServiceBaseUrl = "https://docs.example.com",
            MemoryBaseUrl         = "https://memory.example.com",
            ClaudeAiBaseUrl       = "https://claude.example.com",
            SesMcpManifestUrl     = "https://storage.example.com/ses-mcp/latest.json",
            SesLocalManifestUrl   = "https://storage.example.com/ses-local/latest.json",
            CloudMcpUrl           = "https://mcp.example.com/mcp",
            DocsBaseUrl           = "https://docs.example.com"
        };

        var result = Sut.Validate(null, options);

        Assert.Equal(ValidateOptionsResult.Success, result);
    }

    [Fact]
    public void Validate_MultipleInvalidUrls_ReportsAllFailures()
    {
        var options = new SesLocalOptions
        {
            IdentityBaseUrl = "not-valid",
            MemoryBaseUrl   = "http://insecure.com"
        };

        var result = Sut.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("IdentityBaseUrl", result.FailureMessage);
        Assert.Contains("MemoryBaseUrl", result.FailureMessage);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static SesLocalOptions BuildOptionsWithEmptyProperty(string propertyName)
    {
        return propertyName switch
        {
            "IdentityBaseUrl"       => new SesLocalOptions { IdentityBaseUrl       = "" },
            "DocumentServiceBaseUrl" => new SesLocalOptions { DocumentServiceBaseUrl = "" },
            "MemoryBaseUrl"         => new SesLocalOptions { MemoryBaseUrl         = "" },
            "ClaudeAiBaseUrl"       => new SesLocalOptions { ClaudeAiBaseUrl       = "" },
            "SesMcpManifestUrl"     => new SesLocalOptions { SesMcpManifestUrl     = "" },
            "SesLocalManifestUrl"   => new SesLocalOptions { SesLocalManifestUrl   = "" },
            "CloudMcpUrl"           => new SesLocalOptions { CloudMcpUrl           = "" },
            "DocsBaseUrl"           => new SesLocalOptions { DocsBaseUrl           = "" },
            _ => throw new ArgumentException($"Unknown property: {propertyName}")
        };
    }
}
