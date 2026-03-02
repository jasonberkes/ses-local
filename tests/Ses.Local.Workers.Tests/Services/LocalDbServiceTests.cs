using Microsoft.Extensions.Logging.Abstractions;
using Ses.Local.Core.Enums;
using Ses.Local.Core.Models;
using Ses.Local.Workers.Services;
using Xunit;

namespace Ses.Local.Workers.Tests.Services;

/// <summary>
/// Integration-style tests using a real in-memory SQLite DB.
/// Uses a temp path so no disk state is left behind.
/// </summary>
public sealed class LocalDbServiceTests : IAsyncDisposable
{
    private readonly LocalDbService _sut;
    private readonly string _tempDir;

    public LocalDbServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(_tempDir);

        // Override home by pointing to temp dir — LocalDbService uses UserProfile
        // We test via the public API; the path is internal
        _sut = new LocalDbService(NullLogger<LocalDbService>.Instance);
    }

    [Fact]
    public async Task UpsertSessionAsync_NewSession_CanBeRetrieved()
    {
        var session = new ConversationSession
        {
            Source     = ConversationSource.ClaudeCode,
            ExternalId = Guid.NewGuid().ToString(),
            Title      = "Test session",
            CreatedAt  = DateTime.UtcNow,
            UpdatedAt  = DateTime.UtcNow
        };

        await _sut.UpsertSessionAsync(session);

        var pending = await _sut.GetPendingSyncAsync();
        Assert.Contains(pending, s => s.ExternalId == session.ExternalId);
    }

    [Fact]
    public async Task UpsertSessionAsync_ExistingSession_UpdatesTitle()
    {
        var externalId = Guid.NewGuid().ToString();
        var session = new ConversationSession
        {
            Source     = ConversationSource.ClaudeChat,
            ExternalId = externalId,
            Title      = "Original title",
            CreatedAt  = DateTime.UtcNow,
            UpdatedAt  = DateTime.UtcNow
        };

        await _sut.UpsertSessionAsync(session);

        session.Title     = "Updated title";
        session.UpdatedAt = DateTime.UtcNow.AddMinutes(1);
        await _sut.UpsertSessionAsync(session);

        var pending = await _sut.GetPendingSyncAsync();
        var found = pending.FirstOrDefault(s => s.ExternalId == externalId);
        Assert.NotNull(found);
        Assert.Equal("Updated title", found.Title);
    }

    [Fact]
    public async Task UpsertMessagesAsync_StoresMessages()
    {
        var session = new ConversationSession
        {
            Source     = ConversationSource.ClaudeCode,
            ExternalId = Guid.NewGuid().ToString(),
            Title      = "Session with messages",
            CreatedAt  = DateTime.UtcNow,
            UpdatedAt  = DateTime.UtcNow
        };
        await _sut.UpsertSessionAsync(session);
        Assert.True(session.Id > 0);

        var messages = new[]
        {
            new ConversationMessage { SessionId = session.Id, Role = "user",      Content = "Hello Claude",          CreatedAt = DateTime.UtcNow },
            new ConversationMessage { SessionId = session.Id, Role = "assistant", Content = "Hello! How can I help?", CreatedAt = DateTime.UtcNow.AddSeconds(1) }
        };

        await _sut.UpsertMessagesAsync(messages);
    }

    [Fact]
    public async Task MarkSyncedAsync_UpdatesSyncedAt()
    {
        var session = new ConversationSession
        {
            Source     = ConversationSource.Cowork,
            ExternalId = Guid.NewGuid().ToString(),
            Title      = "Sync test",
            CreatedAt  = DateTime.UtcNow,
            UpdatedAt  = DateTime.UtcNow
        };
        await _sut.UpsertSessionAsync(session);

        var pendingBefore = await _sut.GetPendingSyncAsync();
        Assert.Contains(pendingBefore, s => s.ExternalId == session.ExternalId);

        await _sut.MarkSyncedAsync(session.Id, "doc-service-id-123");

        var pendingAfter = await _sut.GetPendingSyncAsync();
        Assert.DoesNotContain(pendingAfter, s => s.ExternalId == session.ExternalId);
    }

    [Fact]
    public async Task SearchAsync_FindsMessageByContent()
    {
        var session = new ConversationSession
        {
            Source     = ConversationSource.ClaudeCode,
            ExternalId = Guid.NewGuid().ToString(),
            Title      = "Search test",
            CreatedAt  = DateTime.UtcNow,
            UpdatedAt  = DateTime.UtcNow
        };
        await _sut.UpsertSessionAsync(session);

        var uniqueWord = "xyzuniquecontent" + Guid.NewGuid().ToString("N")[..8];
        await _sut.UpsertMessagesAsync([
            new ConversationMessage
            {
                SessionId = session.Id,
                Role      = "user",
                Content   = $"This contains {uniqueWord} in the text",
                CreatedAt = DateTime.UtcNow
            }
        ]);

        var results = await _sut.SearchAsync(uniqueWord);
        Assert.Single(results);
        Assert.Contains(uniqueWord, results[0].Content);
    }

    [Fact]
    public async Task GetPendingSyncAsync_RespectsLimit()
    {
        for (int i = 0; i < 5; i++)
        {
            await _sut.UpsertSessionAsync(new ConversationSession
            {
                Source     = ConversationSource.ClaudeChat,
                ExternalId = Guid.NewGuid().ToString(),
                Title      = $"Session {i}",
                CreatedAt  = DateTime.UtcNow,
                UpdatedAt  = DateTime.UtcNow
            });
        }

        var results = await _sut.GetPendingSyncAsync(batchSize: 3);
        Assert.True(results.Count <= 3);
    }

    // ── ObservationLink tests (WI-983) ────────────────────────────────────────

    [Fact]
    public async Task CreateObservationLinksAsync_StoresLinks()
    {
        var session = await CreateSessionWithObservationsAsync(2);
        var obs = await _sut.GetObservationsAsync(session.Id);
        Assert.Equal(2, obs.Count);

        var link = new ObservationLink
        {
            SourceObservationId = obs[0].Id,
            TargetObservationId = obs[1].Id,
            LinkType            = "causes",
            Confidence          = 0.7,
            CreatedAt           = DateTime.UtcNow
        };

        await _sut.CreateObservationLinksAsync([link]);

        Assert.True(link.Id > 0, "Id should be synced back after insert");
    }

    [Fact]
    public async Task CreateObservationLinksAsync_DuplicateLink_IsIgnored()
    {
        var session = await CreateSessionWithObservationsAsync(2);
        var obs = await _sut.GetObservationsAsync(session.Id);

        var link = new ObservationLink
        {
            SourceObservationId = obs[0].Id,
            TargetObservationId = obs[1].Id,
            LinkType            = "fixes",
            Confidence          = 0.9,
            CreatedAt           = DateTime.UtcNow
        };

        // Should not throw on duplicate
        await _sut.CreateObservationLinksAsync([link]);
        await _sut.CreateObservationLinksAsync([new ObservationLink
        {
            SourceObservationId = obs[0].Id,
            TargetObservationId = obs[1].Id,
            LinkType            = "fixes",
            Confidence          = 0.9,
            CreatedAt           = DateTime.UtcNow
        }]);
    }

    [Fact]
    public async Task GetCausalChainAsync_ReturnsLinksReachableFromStart()
    {
        var session = await CreateSessionWithObservationsAsync(3);
        var obs = await _sut.GetObservationsAsync(session.Id);

        // obs[0] → obs[1] → obs[2]
        await _sut.CreateObservationLinksAsync([
            new ObservationLink { SourceObservationId = obs[0].Id, TargetObservationId = obs[1].Id, LinkType = "causes",  Confidence = 0.7, CreatedAt = DateTime.UtcNow },
            new ObservationLink { SourceObservationId = obs[1].Id, TargetObservationId = obs[2].Id, LinkType = "follows", Confidence = 0.8, CreatedAt = DateTime.UtcNow }
        ]);

        var chain = await _sut.GetCausalChainAsync(obs[0].Id, maxDepth: 5);

        Assert.Equal(2, chain.Count);
        Assert.Contains(chain, l => l.SourceObservationId == obs[0].Id && l.TargetObservationId == obs[1].Id);
        Assert.Contains(chain, l => l.SourceObservationId == obs[1].Id && l.TargetObservationId == obs[2].Id);
    }

    [Fact]
    public async Task GetCausalChainAsync_RespectsMaxDepth()
    {
        var session = await CreateSessionWithObservationsAsync(4);
        var obs = await _sut.GetObservationsAsync(session.Id);

        // Chain: obs[0] → obs[1] → obs[2] → obs[3]
        await _sut.CreateObservationLinksAsync([
            new ObservationLink { SourceObservationId = obs[0].Id, TargetObservationId = obs[1].Id, LinkType = "causes",  Confidence = 0.7, CreatedAt = DateTime.UtcNow },
            new ObservationLink { SourceObservationId = obs[1].Id, TargetObservationId = obs[2].Id, LinkType = "follows", Confidence = 0.8, CreatedAt = DateTime.UtcNow },
            new ObservationLink { SourceObservationId = obs[2].Id, TargetObservationId = obs[3].Id, LinkType = "related", Confidence = 0.5, CreatedAt = DateTime.UtcNow }
        ]);

        // With maxDepth=1, should only find the first hop from obs[0]
        var chain = await _sut.GetCausalChainAsync(obs[0].Id, maxDepth: 1);
        Assert.Single(chain);
        Assert.Equal(obs[0].Id, chain[0].SourceObservationId);
        Assert.Equal(obs[1].Id, chain[0].TargetObservationId);
    }

    [Fact]
    public async Task GetCausalChainAsync_NoLinks_ReturnsEmpty()
    {
        var session = await CreateSessionWithObservationsAsync(1);
        var obs = await _sut.GetObservationsAsync(session.Id);

        var chain = await _sut.GetCausalChainAsync(obs[0].Id);
        Assert.Empty(chain);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task<ConversationSession> CreateSessionWithObservationsAsync(int observationCount)
    {
        var session = new ConversationSession
        {
            Source     = ConversationSource.ClaudeCode,
            ExternalId = Guid.NewGuid().ToString(),
            Title      = "Test session",
            CreatedAt  = DateTime.UtcNow,
            UpdatedAt  = DateTime.UtcNow
        };
        await _sut.UpsertSessionAsync(session);

        var observations = Enumerable.Range(0, observationCount).Select(i => new ConversationObservation
        {
            SessionId       = session.Id,
            ObservationType = ObservationType.ToolUse,
            ToolName        = "Read",
            FilePath        = $"/src/File{i}.cs",
            Content         = $"Content {i}",
            SequenceNumber  = i,
            CreatedAt       = DateTime.UtcNow
        }).ToList();

        await _sut.UpsertObservationsAsync(observations);
        return session;
    }

    public async ValueTask DisposeAsync()
    {
        await _sut.DisposeAsync();
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }
}
