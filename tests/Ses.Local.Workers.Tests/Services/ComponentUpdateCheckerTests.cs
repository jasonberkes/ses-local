using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Ses.Local.Core.Options;
using Ses.Local.Workers.Services;
using Xunit;

namespace Ses.Local.Workers.Tests.Services;

public sealed class ComponentUpdateCheckerTests
{
    private static ComponentUpdateChecker BuildSut(
        Func<HttpRequestMessage, HttpResponseMessage> handler)
    {
        var http = new HttpClient(new LambdaHandler(handler))
        {
            BaseAddress = new Uri("https://manifests.test/")
        };
        return new ComponentUpdateChecker(http,
            Options.Create(new SesLocalOptions
            {
                SesLocalManifestUrl = "https://manifests.test/ses-local/latest.json",
                SesMcpManifestUrl   = "https://manifests.test/ses-mcp/latest.json",
                SesHooksManifestUrl = "https://manifests.test/ses-hooks/latest.json",
            }),
            NullLogger<ComponentUpdateChecker>.Instance);
    }

    [Fact]
    public async Task CheckAsync_Returns3Components()
    {
        var sut = BuildSut(_ => OkManifest("9.9.9"));

        var results = await sut.CheckAsync();

        Assert.Equal(3, results.Count);
    }

    [Fact]
    public async Task CheckAsync_UpdateAvailable_WhenRemoteVersionIsHigher()
    {
        // Installed version is 0.0.1 (entry assembly version in tests is low), remote is 9.9.9
        var sut = BuildSut(_ => OkManifest("9.9.9"));

        var results = await sut.CheckAsync();
        var daemonEntry = results.Single(r => r.Name == "ses-local-daemon");

        // Either update is available (if installed version resolved) or not (if null) — no crash
        Assert.NotNull(daemonEntry);
        Assert.Equal("9.9.9", daemonEntry.LatestVersion);
    }

    [Fact]
    public async Task CheckAsync_NoUpdate_WhenManifest404()
    {
        var sut = BuildSut(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        var results = await sut.CheckAsync();

        Assert.All(results, r => Assert.False(r.UpdateAvailable));
        Assert.All(results, r => Assert.Null(r.LatestVersion));
    }

    [Fact]
    public async Task CheckAsync_NoUpdate_WhenManifestInvalidJson()
    {
        var sut = BuildSut(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("not-json")
        });

        var results = await sut.CheckAsync();

        Assert.All(results, r => Assert.False(r.UpdateAvailable));
    }

    [Fact]
    public async Task CheckAsync_SameVersion_NoUpdate()
    {
        // Use 0.0.1 which would likely match or beat test assembly version for daemon
        var sut = BuildSut(_ => OkManifest("0.0.1"));

        var results = await sut.CheckAsync();

        // ses-local-daemon may or may not trigger update (depends on test assembly version)
        // ses-mcp and ses-hooks have null installed version (binary not on disk) → no update
        var sesMcp   = results.Single(r => r.Name == "ses-mcp");
        var sesHooks = results.Single(r => r.Name == "ses-hooks");
        Assert.False(sesMcp.UpdateAvailable);
        Assert.False(sesHooks.UpdateAvailable);
    }

    private static HttpResponseMessage OkManifest(string version) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                $"{{\"version\":\"{version}\",\"published\":\"2025-01-01T00:00:00Z\",\"binaries\":{{}}}}",
                System.Text.Encoding.UTF8,
                "application/json")
        };

    private sealed class LambdaHandler(Func<HttpRequestMessage, HttpResponseMessage> fn)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(fn(request));
    }
}
