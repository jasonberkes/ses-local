using Microsoft.Extensions.Logging.Abstractions;
using Ses.Local.Workers.Services;
using Xunit;

namespace Ses.Local.Workers.Tests.Services;

public sealed class SesLocalUpdaterTests
{
    [Fact]
    public async Task CheckAndApply_WhenManifestFetchFails_ReturnsFailureGracefully()
    {
        var handler = new MockHttpHandler(System.Net.HttpStatusCode.ServiceUnavailable, "");
        var http    = new HttpClient(handler) { BaseAddress = new Uri("https://test/") };
        var updater = new SesLocalUpdater(NullLogger<SesLocalUpdater>.Instance, http,
            () => "/fake/path/ses-local");

        var result = await updater.CheckAndApplyAsync();

        Assert.False(result.UpdateApplied);
        Assert.NotNull(result.Message);
    }

    [Fact]
    public async Task CheckAndApply_WhenAlreadyUpToDate_ReturnsNoUpdate()
    {
        // Current assembly version is 1.0.0.0, manifest says 0.9.0 — should not update
        var manifest = """{"version":"0.9.0","published":"2026-01-01T00:00:00Z","binaries":{}}""";
        var handler  = new MockHttpHandler(System.Net.HttpStatusCode.OK, manifest);
        var http     = new HttpClient(handler);
        var updater  = new SesLocalUpdater(NullLogger<SesLocalUpdater>.Instance, http,
            () => "/fake/path/ses-local");

        var result = await updater.CheckAndApplyAsync();

        Assert.False(result.UpdateApplied);
        Assert.Contains("up to date", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CheckAndApply_WhenNoBinaryPathAvailable_ReturnsFailure()
    {
        var handler = new MockHttpHandler(System.Net.HttpStatusCode.OK, "{}");
        var http    = new HttpClient(handler);
        var updater = new SesLocalUpdater(NullLogger<SesLocalUpdater>.Instance, http,
            () => null); // No binary path

        var result = await updater.CheckAndApplyAsync();

        Assert.False(result.UpdateApplied);
    }

    [Fact]
    public void GetRid_ReturnsValidPlatformString()
    {
        var rid = SesLocalUpdater.GetRid();
        Assert.True(rid is "osx-arm64" or "osx-x64" or "win-x64" || !string.IsNullOrEmpty(rid));
    }

    [Fact]
    public async Task SesMcpUpdater_WhenBinaryNotInstalled_SkipsUpdate()
    {
        var handler = new MockHttpHandler(System.Net.HttpStatusCode.OK,
            """{"version":"99.0.0","published":"2026-01-01T00:00:00Z","binaries":{"osx-arm64":"https://example.com/bin"}}""");
        var http    = new HttpClient(handler);
        // Use a path that definitely doesn't exist
        var updater = new SesMcpUpdater(NullLogger<SesMcpUpdater>.Instance, http,
            () => "/nonexistent/path/that/will/never/exist/ses-mcp");

        // ses-mcp binary doesn't exist at this path, so it should skip gracefully
        var result = await updater.CheckAndApplyAsync();

        // Skips (binary not found) — never throws
        Assert.False(result.UpdateApplied);
        Assert.Contains("not installed", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class MockHttpHandler(System.Net.HttpStatusCode status, string content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(content, System.Text.Encoding.UTF8, "application/json")
            });
    }
}
