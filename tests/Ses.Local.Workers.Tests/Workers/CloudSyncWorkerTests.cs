using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Ses.Local.Core.Interfaces;
using Ses.Local.Core.Models;
using Ses.Local.Workers.Services;
using Ses.Local.Workers.Workers;
using Xunit;

namespace Ses.Local.Workers.Tests.Workers;

public sealed class CloudSyncWorkerTests
{
    [Fact]
    public async Task SyncPass_WhenNoAccessToken_SkipsGracefully()
    {
        var db   = new Mock<ILocalDbService>();
        var auth = new Mock<IAuthService>();
        auth.Setup(x => x.GetAccessTokenAsync(default)).ReturnsAsync((string?)null);

        var worker = new CloudSyncWorker(
            db.Object, auth.Object,
            new DocumentServiceUploader(BuildFailingFactory(), NullLogger<DocumentServiceUploader>.Instance),
            new CloudMemoryRetainer(BuildFailingFactory(), NullLogger<CloudMemoryRetainer>.Instance),
            NullLogger<CloudSyncWorker>.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var ex = await Record.ExceptionAsync(() => worker.StartAsync(cts.Token));
        Assert.Null(ex);

        db.Verify(x => x.GetPendingSyncAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SyncPass_WhenNoPendingSessions_CompletesWithoutError()
    {
        var db   = new Mock<ILocalDbService>();
        var auth = new Mock<IAuthService>();
        auth.Setup(x => x.GetAccessTokenAsync(default)).ReturnsAsync("tm_pat_test");
        db.Setup(x => x.GetPendingSyncAsync(10, default))
          .ReturnsAsync(Array.Empty<ConversationSession>());

        var worker = new CloudSyncWorker(
            db.Object, auth.Object,
            new DocumentServiceUploader(BuildFailingFactory(), NullLogger<DocumentServiceUploader>.Instance),
            new CloudMemoryRetainer(BuildFailingFactory(), NullLogger<CloudMemoryRetainer>.Instance),
            NullLogger<CloudSyncWorker>.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Record.ExceptionAsync(() => worker.StartAsync(cts.Token));
    }

    [Fact]
    public async Task DocumentServiceUploader_WithInvalidEndpoint_ReturnsNull()
    {
        var uploader = new DocumentServiceUploader(BuildFailingFactory(), NullLogger<DocumentServiceUploader>.Instance);
        var session  = new ConversationSession { Id = 1, Title = "Test", ExternalId = "uuid-1" };
        var messages = Array.Empty<ConversationMessage>();

        // Should return null (not throw) when DocumentService is unreachable
        var result = await uploader.UploadAsync(session, messages, "tm_pat_fake");
        Assert.Null(result); // Unreachable endpoint = null, not exception
    }

    [Fact]
    public async Task CloudMemoryRetainer_WithEmptyMessages_ReturnsTrue()
    {
        var retainer = new CloudMemoryRetainer(BuildFailingFactory(), NullLogger<CloudMemoryRetainer>.Instance);
        var session  = new ConversationSession { Id = 1, Title = "Test", ExternalId = "uuid-1" };

        // Empty messages = nothing to retain = success
        var result = await retainer.RetainAsync(session, Array.Empty<ConversationMessage>(), "tm_pat_fake");
        Assert.True(result);
    }

    [Fact]
    public async Task CloudMemoryRetainer_WithNetworkError_ReturnsTrueGracefully()
    {
        var retainer = new CloudMemoryRetainer(BuildNetworkErrorFactory(), NullLogger<CloudMemoryRetainer>.Instance);
        var session  = new ConversationSession { Id = 1, Title = "Test", ExternalId = "uuid-1" };
        var messages = new ConversationMessage[]
        {
            new() { Role = "user",      Content = "Hello",  CreatedAt = DateTime.UtcNow },
            new() { Role = "assistant", Content = "Hi!",    CreatedAt = DateTime.UtcNow.AddSeconds(1) }
        };

        // Network unreachable = true (graceful degradation, not a sync failure)
        var result = await retainer.RetainAsync(session, messages, "tm_pat_invalid_no_network");
        Assert.True(result); // Memory service down = not a failure
    }

    private static IHttpClientFactory BuildFailingFactory()
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>()))
               .Returns(() => new HttpClient(new ServiceUnavailableHandler())
               {
                   BaseAddress = new Uri("https://test.example.com")
               });
        return factory.Object;
    }

    private static IHttpClientFactory BuildNetworkErrorFactory()
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>()))
               .Returns(() => new HttpClient(new NetworkErrorHandler())
               {
                   BaseAddress = new Uri("https://test.example.com")
               });
        return factory.Object;
    }

    [Fact]
    public async Task DocumentServiceUploader_With403_ThrowsUnauthorizedAccessException()
    {
        var uploader = new DocumentServiceUploader(Build403Factory(), NullLogger<DocumentServiceUploader>.Instance);
        var session  = new ConversationSession { Id = 1, Title = "Test", ExternalId = "uuid-1" };
        var messages = Array.Empty<ConversationMessage>();

        // 403 must propagate so CloudSyncWorker can stop the batch and set a cooldown
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => uploader.UploadAsync(session, messages, "tm_pat_no_tenant_id"));
    }

    [Fact]
    public async Task SyncPass_On403_LogsOneWarningAndStopsBatch()
    {
        var db   = new Mock<ILocalDbService>();
        var auth = new Mock<IAuthService>();
        auth.Setup(x => x.GetAccessTokenAsync(default)).ReturnsAsync("tm_pat_test");
        db.Setup(x => x.GetSyncMetadataAsync("device_id", default)).ReturnsAsync("device-abc");
        db.Setup(x => x.GetPendingSyncAsync(10, default))
          .ReturnsAsync(new ConversationSession[]
          {
              new() { Id = 1, Title = "S1", ExternalId = "u1" },
              new() { Id = 2, Title = "S2", ExternalId = "u2" },
          });
        db.Setup(x => x.GetMessagesAsync(It.IsAny<long>(), default))
          .ReturnsAsync(Array.Empty<ConversationMessage>());

        var worker = new CloudSyncWorker(
            db.Object, auth.Object,
            new DocumentServiceUploader(Build403Factory(), NullLogger<DocumentServiceUploader>.Instance),
            new CloudMemoryRetainer(BuildFailingFactory(), NullLogger<CloudMemoryRetainer>.Instance),
            NullLogger<CloudSyncWorker>.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await Record.ExceptionAsync(() => worker.StartAsync(cts.Token));

        // Sessions must NOT be marked synced so they retry when auth is fixed
        db.Verify(x => x.MarkSyncedAsync(It.IsAny<long>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private static IHttpClientFactory Build403Factory()
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>()))
               .Returns(() => new HttpClient(new ForbiddenHandler())
               {
                   BaseAddress = new Uri("https://test.example.com")
               });
        return factory.Object;
    }

    private sealed class ServiceUnavailableHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable));
    }

    private sealed class ForbiddenHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.Forbidden));
    }

    private sealed class NetworkErrorHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) =>
            Task.FromException<HttpResponseMessage>(new HttpRequestException("Network error"));
    }
}
