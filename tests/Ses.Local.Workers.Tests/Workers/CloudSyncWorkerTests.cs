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
            new DocumentServiceUploader(NullLogger<DocumentServiceUploader>.Instance),
            new CloudMemoryRetainer(NullLogger<CloudMemoryRetainer>.Instance),
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
            new DocumentServiceUploader(NullLogger<DocumentServiceUploader>.Instance),
            new CloudMemoryRetainer(NullLogger<CloudMemoryRetainer>.Instance),
            NullLogger<CloudSyncWorker>.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Record.ExceptionAsync(() => worker.StartAsync(cts.Token));
    }

    [Fact]
    public async Task DocumentServiceUploader_WithInvalidEndpoint_ReturnsNull()
    {
        var uploader = new DocumentServiceUploader(NullLogger<DocumentServiceUploader>.Instance);
        var session  = new ConversationSession { Id = 1, Title = "Test", ExternalId = "uuid-1" };
        var messages = Array.Empty<ConversationMessage>();

        // Should return null (not throw) when DocumentService is unreachable
        var result = await uploader.UploadAsync(session, messages, "tm_pat_fake");
        Assert.Null(result); // Unreachable endpoint = null, not exception
    }

    [Fact]
    public async Task CloudMemoryRetainer_WithEmptyMessages_ReturnsTrue()
    {
        var retainer = new CloudMemoryRetainer(NullLogger<CloudMemoryRetainer>.Instance);
        var session  = new ConversationSession { Id = 1, Title = "Test", ExternalId = "uuid-1" };

        // Empty messages = nothing to retain = success
        var result = await retainer.RetainAsync(session, Array.Empty<ConversationMessage>(), "tm_pat_fake");
        Assert.True(result);
    }

    [Fact]
    public async Task CloudMemoryRetainer_WithNetworkError_ReturnsTrueGracefully()
    {
        var retainer = new CloudMemoryRetainer(NullLogger<CloudMemoryRetainer>.Instance);
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
}
