using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Ses.Local.Core.Enums;
using Ses.Local.Core.Interfaces;
using Ses.Local.Core.Models;
using Ses.Local.Core.Options;
using Ses.Local.Workers.Services;  // DocumentServiceDownloader, LocalDbService (for integration tests)
using Ses.Local.Workers.Workers;
using Xunit;

namespace Ses.Local.Workers.Tests.Workers;

public sealed class CloudPullWorkerTests
{
    // ── CloudPullWorker — gating behaviour ───────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_WhenDisabled_DoesNotRun()
    {
        var (db, auth, downloader) = BuildMocks();
        var worker = BuildWorker(db, auth, downloader, enableCloudPull: false);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await worker.StartAsync(cts.Token);
        await Task.Delay(50); // give it time to run (it shouldn't)
        await worker.StopAsync(CancellationToken.None);

        auth.Verify(x => x.GetAccessTokenAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task PullPass_WhenNoAccessToken_SkipsGracefully()
    {
        var (db, auth, downloader) = BuildMocks();
        auth.Setup(x => x.GetAccessTokenAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);

        var worker = BuildWorker(db, auth, downloader, enableCloudPull: true);
        int imported = await worker.RunPullPassAsync(CancellationToken.None);

        Assert.Equal(0, imported);
        downloader.Verify(x => x.GetDocumentsAsync(
            It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task PullPass_WhenNoDocuments_ReturnsZero()
    {
        var (db, auth, downloader) = BuildMocks();
        auth.Setup(x => x.GetAccessTokenAsync(It.IsAny<CancellationToken>())).ReturnsAsync("pat");
        db.Setup(x => x.GetSyncMetadataAsync("device_id", It.IsAny<CancellationToken>()))
          .ReturnsAsync("device-abc");
        db.Setup(x => x.GetSyncMetadataAsync("last_pull_at", It.IsAny<CancellationToken>()))
          .ReturnsAsync((string?)null);
        downloader.Setup(x => x.GetDocumentsAsync(
                It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PulledDocument>());

        var worker = BuildWorker(db, auth, downloader, enableCloudPull: true);
        int imported = await worker.RunPullPassAsync(CancellationToken.None);

        Assert.Equal(0, imported);
        db.Verify(x => x.SetSyncMetadataAsync("last_pull_at", It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task PullPass_GeneratesDeviceIdOnFirstRun()
    {
        var (db, auth, downloader) = BuildMocks();
        auth.Setup(x => x.GetAccessTokenAsync(It.IsAny<CancellationToken>())).ReturnsAsync("pat");
        db.Setup(x => x.GetSyncMetadataAsync("device_id", It.IsAny<CancellationToken>()))
          .ReturnsAsync((string?)null);  // No device_id yet
        db.Setup(x => x.GetSyncMetadataAsync("last_pull_at", It.IsAny<CancellationToken>()))
          .ReturnsAsync((string?)null);
        downloader.Setup(x => x.GetDocumentsAsync(
                It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PulledDocument>());

        var worker = BuildWorker(db, auth, downloader, enableCloudPull: true);
        await worker.RunPullPassAsync(CancellationToken.None);

        // device_id should have been generated and stored
        db.Verify(x => x.SetSyncMetadataAsync(
            "device_id",
            It.Is<string>(v => IsValidGuid(v)),
            It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task PullPass_ImportNewSession_CallsUpsertAndReturnsOne()
    {
        var (db, auth, downloader) = BuildMocks();
        auth.Setup(x => x.GetAccessTokenAsync(It.IsAny<CancellationToken>())).ReturnsAsync("pat");
        db.Setup(x => x.GetSyncMetadataAsync("device_id", It.IsAny<CancellationToken>()))
          .ReturnsAsync("device-abc");
        db.Setup(x => x.GetSyncMetadataAsync("last_pull_at", It.IsAny<CancellationToken>()))
          .ReturnsAsync((string?)null);

        var pulledSession = new ConversationSession
        {
            Source      = ConversationSource.ClaudeCode,
            ExternalId  = "ext-123",
            Title       = "Remote session",
            CreatedAt   = DateTime.UtcNow.AddHours(-1),
            UpdatedAt   = DateTime.UtcNow,
            ContentHash = "newhash"
        };
        var pulledMessages = new List<ConversationMessage>
        {
            new() { Role = "user", Content = "Hello from other device", CreatedAt = DateTime.UtcNow.AddHours(-1) }
        };
        var pulledDoc = new PulledDocument(pulledSession, pulledMessages, "doc-id-1");

        downloader.Setup(x => x.GetDocumentsAsync(
                It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([pulledDoc]);

        // Session doesn't exist locally
        db.Setup(x => x.GetSessionBySourceAndExternalIdAsync("ClaudeCode", "ext-123", It.IsAny<CancellationToken>()))
          .ReturnsAsync((ConversationSession?)null);

        var worker = BuildWorker(db, auth, downloader, enableCloudPull: true);
        int imported = await worker.RunPullPassAsync(CancellationToken.None);

        Assert.Equal(1, imported);
        db.Verify(x => x.UpsertSessionAsync(It.Is<ConversationSession>(s => s.ExternalId == "ext-123"),
            It.IsAny<CancellationToken>()), Times.Once);
        db.Verify(x => x.UpsertMessagesAsync(It.IsAny<IEnumerable<ConversationMessage>>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task PullPass_SkipsSessionWithMatchingContentHash()
    {
        var (db, auth, downloader) = BuildMocks();
        auth.Setup(x => x.GetAccessTokenAsync(It.IsAny<CancellationToken>())).ReturnsAsync("pat");
        db.Setup(x => x.GetSyncMetadataAsync("device_id", It.IsAny<CancellationToken>()))
          .ReturnsAsync("device-abc");
        db.Setup(x => x.GetSyncMetadataAsync("last_pull_at", It.IsAny<CancellationToken>()))
          .ReturnsAsync((string?)null);

        var existingLocal = new ConversationSession
        {
            Id          = 5,
            Source      = ConversationSource.ClaudeCode,
            ExternalId  = "ext-same",
            ContentHash = "samehash"
        };
        var pulledSession = new ConversationSession
        {
            Source      = ConversationSource.ClaudeCode,
            ExternalId  = "ext-same",
            Title       = "Same session",
            ContentHash = "samehash"  // identical hash
        };
        var doc = new PulledDocument(pulledSession, [], "doc-id-2");

        downloader.Setup(x => x.GetDocumentsAsync(
                It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([doc]);
        db.Setup(x => x.GetSessionBySourceAndExternalIdAsync("ClaudeCode", "ext-same", It.IsAny<CancellationToken>()))
          .ReturnsAsync(existingLocal);

        var worker = BuildWorker(db, auth, downloader, enableCloudPull: true);
        int imported = await worker.RunPullPassAsync(CancellationToken.None);

        Assert.Equal(0, imported);
        db.Verify(x => x.UpsertSessionAsync(It.IsAny<ConversationSession>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task PullPass_UpdatesExistingSessionWhenContentHashDiffers()
    {
        var (db, auth, downloader) = BuildMocks();
        auth.Setup(x => x.GetAccessTokenAsync(It.IsAny<CancellationToken>())).ReturnsAsync("pat");
        db.Setup(x => x.GetSyncMetadataAsync("device_id", It.IsAny<CancellationToken>()))
          .ReturnsAsync("device-abc");
        db.Setup(x => x.GetSyncMetadataAsync("last_pull_at", It.IsAny<CancellationToken>()))
          .ReturnsAsync((string?)null);

        var existingLocal = new ConversationSession
        {
            Id          = 7,
            Source      = ConversationSource.ClaudeCode,
            ExternalId  = "ext-changed",
            ContentHash = "oldhash"
        };
        var pulledSession = new ConversationSession
        {
            Source      = ConversationSource.ClaudeCode,
            ExternalId  = "ext-changed",
            Title       = "Updated session",
            ContentHash = "newhash"
        };
        var doc = new PulledDocument(pulledSession, [], "doc-id-3");

        downloader.Setup(x => x.GetDocumentsAsync(
                It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([doc]);
        db.Setup(x => x.GetSessionBySourceAndExternalIdAsync("ClaudeCode", "ext-changed", It.IsAny<CancellationToken>()))
          .ReturnsAsync(existingLocal);

        var worker = BuildWorker(db, auth, downloader, enableCloudPull: true);
        int imported = await worker.RunPullPassAsync(CancellationToken.None);

        Assert.Equal(1, imported);
        db.Verify(x => x.UpsertSessionAsync(It.Is<ConversationSession>(s => s.ContentHash == "newhash"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task PullPass_UpdatesLastPullAt_AfterEachPass()
    {
        var (db, auth, downloader) = BuildMocks();
        auth.Setup(x => x.GetAccessTokenAsync(It.IsAny<CancellationToken>())).ReturnsAsync("pat");
        db.Setup(x => x.GetSyncMetadataAsync("device_id", It.IsAny<CancellationToken>()))
          .ReturnsAsync("device-abc");
        db.Setup(x => x.GetSyncMetadataAsync("last_pull_at", It.IsAny<CancellationToken>()))
          .ReturnsAsync("2024-01-01T00:00:00.0000000Z");
        downloader.Setup(x => x.GetDocumentsAsync(
                It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PulledDocument>());

        var worker = BuildWorker(db, auth, downloader, enableCloudPull: true);
        await worker.RunPullPassAsync(CancellationToken.None);

        db.Verify(x => x.SetSyncMetadataAsync(
            "last_pull_at",
            It.Is<string>(v => IsValidDateTime(v)),
            It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task PullPass_UsesStoredLastPullAt_ForQuery()
    {
        var (db, auth, downloader) = BuildMocks();
        auth.Setup(x => x.GetAccessTokenAsync(It.IsAny<CancellationToken>())).ReturnsAsync("pat");
        db.Setup(x => x.GetSyncMetadataAsync("device_id", It.IsAny<CancellationToken>()))
          .ReturnsAsync("device-abc");

        var storedPullAt = new DateTime(2025, 6, 15, 12, 0, 0, DateTimeKind.Utc);
        db.Setup(x => x.GetSyncMetadataAsync("last_pull_at", It.IsAny<CancellationToken>()))
          .ReturnsAsync(storedPullAt.ToString("O"));

        DateTime capturedUpdatedAfter = default;
        downloader.Setup(x => x.GetDocumentsAsync(
                It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, DateTime, string, CancellationToken>((_, dt, _, _) => capturedUpdatedAfter = dt)
            .ReturnsAsync(Array.Empty<PulledDocument>());

        var worker = BuildWorker(db, auth, downloader, enableCloudPull: true);
        await worker.RunPullPassAsync(CancellationToken.None);

        Assert.Equal(storedPullAt, capturedUpdatedAfter);
    }

    // ── DocumentServiceDownloader — ParseTranscriptMessages ──────────────────

    [Fact]
    public void ParseTranscriptMessages_ExtractsUserAndAssistantMessages()
    {
        var transcript = """
            # My Session
            Source: ClaudeCode | Created: 2025-01-01 10:00

            **Human** (10:00):
            Hello world

            **Assistant** (10:01):
            Hi there!

            """;

        var messages = DocumentServiceDownloader.ParseTranscriptMessages(transcript);

        Assert.Equal(2, messages.Count);
        Assert.Equal("user",      messages[0].Role);
        Assert.Equal("Hello world", messages[0].Content);
        Assert.Equal("assistant", messages[1].Role);
        Assert.Equal("Hi there!", messages[1].Content);
    }

    [Fact]
    public void ParseTranscriptMessages_HandlesEmptyTranscript()
    {
        var messages = DocumentServiceDownloader.ParseTranscriptMessages(string.Empty);
        Assert.Empty(messages);
    }

    [Fact]
    public void ParseTranscriptMessages_HandlesMultilineMessageContent()
    {
        var transcript = """
            # Session
            Source: ClaudeCode | Created: 2025-01-01 10:00

            **Human** (10:00):
            Line one
            Line two
            Line three

            **Assistant** (10:01):
            Response

            """;

        var messages = DocumentServiceDownloader.ParseTranscriptMessages(transcript);

        Assert.Equal(2, messages.Count);
        Assert.Contains("Line one", messages[0].Content);
        Assert.Contains("Line two", messages[0].Content);
        Assert.Contains("Line three", messages[0].Content);
    }

    [Fact]
    public void ParseTranscriptMessages_HeaderOnlyTranscript_ReturnsEmpty()
    {
        var transcript = """
            # Empty Session
            Source: ClaudeCode | Created: 2025-01-01 10:00

            """;

        var messages = DocumentServiceDownloader.ParseTranscriptMessages(transcript);
        Assert.Empty(messages);
    }

    // ── LocalDbService — sync_metadata CRUD ──────────────────────────────────
    // These run against a real in-memory SQLite to verify the schema and SQL.

    [Fact]
    public async Task SyncMetadata_RoundTrip_GetAfterSet()
    {
        var dbPath  = Path.Combine(Path.GetTempPath(), $"test-{Guid.NewGuid()}.db");
        await using var db = new Ses.Local.Workers.Services.LocalDbService(
            dbPath, NullLogger<Ses.Local.Workers.Services.LocalDbService>.Instance);

        await db.SetSyncMetadataAsync("last_pull_at", "2025-01-01T00:00:00Z");
        var value = await db.GetSyncMetadataAsync("last_pull_at");

        Assert.Equal("2025-01-01T00:00:00Z", value);
    }

    [Fact]
    public async Task SyncMetadata_GetMissingKey_ReturnsNull()
    {
        var dbPath  = Path.Combine(Path.GetTempPath(), $"test-{Guid.NewGuid()}.db");
        await using var db = new Ses.Local.Workers.Services.LocalDbService(
            dbPath, NullLogger<Ses.Local.Workers.Services.LocalDbService>.Instance);

        var value = await db.GetSyncMetadataAsync("nonexistent_key");
        Assert.Null(value);
    }

    [Fact]
    public async Task SyncMetadata_SetTwice_UpdatesValue()
    {
        var dbPath  = Path.Combine(Path.GetTempPath(), $"test-{Guid.NewGuid()}.db");
        await using var db = new Ses.Local.Workers.Services.LocalDbService(
            dbPath, NullLogger<Ses.Local.Workers.Services.LocalDbService>.Instance);

        await db.SetSyncMetadataAsync("key", "value1");
        await db.SetSyncMetadataAsync("key", "value2");
        var result = await db.GetSyncMetadataAsync("key");

        Assert.Equal("value2", result);
    }

    [Fact]
    public async Task GetSessionBySourceAndExternalId_ReturnsSession_WhenExists()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"test-{Guid.NewGuid()}.db");
        await using var db = new Ses.Local.Workers.Services.LocalDbService(
            dbPath, NullLogger<Ses.Local.Workers.Services.LocalDbService>.Instance);

        var session = new ConversationSession
        {
            Source      = ConversationSource.ClaudeCode,
            ExternalId  = "ext-lookup",
            Title       = "Lookup Test",
            CreatedAt   = DateTime.UtcNow,
            UpdatedAt   = DateTime.UtcNow,
            ContentHash = "hash123"
        };
        await db.UpsertSessionAsync(session);

        var found = await db.GetSessionBySourceAndExternalIdAsync("ClaudeCode", "ext-lookup");

        Assert.NotNull(found);
        Assert.Equal("ext-lookup", found.ExternalId);
        Assert.Equal("hash123", found.ContentHash);
    }

    [Fact]
    public async Task GetSessionBySourceAndExternalId_ReturnsNull_WhenNotFound()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"test-{Guid.NewGuid()}.db");
        await using var db = new Ses.Local.Workers.Services.LocalDbService(
            dbPath, NullLogger<Ses.Local.Workers.Services.LocalDbService>.Instance);

        var result = await db.GetSessionBySourceAndExternalIdAsync("ClaudeCode", "no-such-id");
        Assert.Null(result);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static (Mock<ILocalDbService> db, Mock<IAuthService> auth, Mock<IDocumentServiceDownloader> downloader) BuildMocks()
    {
        var db         = new Mock<ILocalDbService>();
        var auth       = new Mock<IAuthService>();
        var downloader = new Mock<IDocumentServiceDownloader>();

        // Sensible defaults
        db.Setup(x => x.SetSyncMetadataAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
          .Returns(Task.CompletedTask);
        db.Setup(x => x.UpsertSessionAsync(It.IsAny<ConversationSession>(), It.IsAny<CancellationToken>()))
          .Returns(Task.CompletedTask);
        db.Setup(x => x.UpsertMessagesAsync(It.IsAny<IEnumerable<ConversationMessage>>(), It.IsAny<CancellationToken>()))
          .Returns(Task.CompletedTask);

        return (db, auth, downloader);
    }

    private static CloudPullWorker BuildWorker(
        Mock<ILocalDbService> db,
        Mock<IAuthService> auth,
        Mock<IDocumentServiceDownloader> downloader,
        bool enableCloudPull)
    {
        var options = Options.Create(new SesLocalOptions
        {
            EnableCloudPull         = enableCloudPull,
            CloudPullIntervalMinutes = 10
        });

        return new CloudPullWorker(
            db.Object,
            auth.Object,
            downloader.Object,
            options,
            NullLogger<CloudPullWorker>.Instance);
    }

    private static IHttpClientFactory BuildNullHttpClientFactory()
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>()))
               .Returns(() => new HttpClient(new ServiceUnavailableHandler())
               {
                   BaseAddress = new Uri("https://test.example.com")
               });
        return factory.Object;
    }

    private static bool IsValidGuid(string v) => Guid.TryParse(v, out _);
    private static bool IsValidDateTime(string v) => DateTime.TryParse(v, out _);

    private sealed class ServiceUnavailableHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable));
    }
}
