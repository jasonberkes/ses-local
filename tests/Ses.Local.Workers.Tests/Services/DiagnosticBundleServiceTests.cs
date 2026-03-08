using Ses.Local.Tray.Services;
using Xunit;

namespace Ses.Local.Workers.Tests.Services;

/// <summary>Tests for DiagnosticBundleService.Scrub (OBS-3).</summary>
public sealed class DiagnosticBundleServiceTests
{
    [Fact]
    public void Scrub_RemovesBearedToken()
    {
        const string input = "Authorization: Bearer eyJhbGciOiJSUzI1NiJ9.some.payload";
        var result = DiagnosticBundleService.Scrub(input);
        Assert.DoesNotContain("eyJ", result);
        Assert.Contains("***REDACTED***", result);
    }

    [Fact]
    public void Scrub_RemovesTmPat()
    {
        const string input = "token=tm_pat_abc123xyz456789";
        var result = DiagnosticBundleService.Scrub(input);
        Assert.DoesNotContain("abc123xyz456789", result);
        Assert.Contains("***REDACTED***", result);
    }

    [Fact]
    public void Scrub_RemovesMcpHeaders()
    {
        const string input = """{"MCP_HEADERS": "Authorization: Bearer supersecrettoken"}""";
        var result = DiagnosticBundleService.Scrub(input);
        Assert.DoesNotContain("supersecrettoken", result);
        Assert.Contains("***REDACTED***", result);
    }

    [Fact]
    public void Scrub_LeavesNonSensitiveContentIntact()
    {
        const string input = """{"status": "Healthy", "checkedAt": "2026-03-07T00:00:00Z"}""";
        var result = DiagnosticBundleService.Scrub(input);
        Assert.Equal(input, result);
    }

    [Fact]
    public void Scrub_HandlesMultipleSensitiveTokensInOneString()
    {
        const string input = "Authorization: Bearer eyJabc123 and tm_pat_xyz123456789 and password=secret";
        var result = DiagnosticBundleService.Scrub(input);
        Assert.DoesNotContain("eyJabc123", result);
        Assert.DoesNotContain("xyz123456789", result);
        Assert.DoesNotContain("secret", result);
        Assert.Contains("***REDACTED***", result);
    }
}
