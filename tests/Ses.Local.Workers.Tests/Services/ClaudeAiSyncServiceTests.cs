using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Ses.Local.Core.Interfaces;
using Ses.Local.Workers.Services;
using Xunit;

namespace Ses.Local.Workers.Tests.Services;

public sealed class ClaudeAiSyncServiceTests
{
    private static IHttpClientFactory BuildFactory(HttpMessageHandler? handler = null)
    {
        handler ??= new AlwaysFailHandler();
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>()))
               .Returns(() => new HttpClient(handler) { BaseAddress = new Uri("https://claude.ai/") });
        return factory.Object;
    }

    [Fact]
    public async Task SyncAsync_DoesNotThrow_OnNetworkFailure()
    {
        var extractor = new ClaudeSessionCookieExtractor(NullLogger<ClaudeSessionCookieExtractor>.Instance);
        var db = new Mock<ILocalDbService>();

        var service = new ClaudeAiSyncService(BuildFactory(), extractor, db.Object,
            NullLogger<ClaudeAiSyncService>.Instance);

        // Should never throw regardless of cookie state or network state
        var ex = await Record.ExceptionAsync(() => service.SyncAsync(null, CancellationToken.None));
        Assert.Null(ex);
    }

    [Fact]
    public async Task SyncAsync_MultipleCalls_DoNotThrow()
    {
        var extractor = new ClaudeSessionCookieExtractor(NullLogger<ClaudeSessionCookieExtractor>.Instance);
        var db = new Mock<ILocalDbService>();

        var service = new ClaudeAiSyncService(BuildFactory(), extractor, db.Object,
            NullLogger<ClaudeAiSyncService>.Instance);

        // Multiple rapid calls should all complete without throwing
        for (int i = 0; i < 5; i++)
        {
            var ex = await Record.ExceptionAsync(() => service.SyncAsync(null, CancellationToken.None));
            Assert.Null(ex);
        }
    }

    private sealed class AlwaysFailHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
            => Task.FromException<HttpResponseMessage>(new HttpRequestException("Simulated network failure"));
    }
}
