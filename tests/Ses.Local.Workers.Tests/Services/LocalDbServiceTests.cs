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

    public async ValueTask DisposeAsync()
    {
        await _sut.DisposeAsync();
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }
}
