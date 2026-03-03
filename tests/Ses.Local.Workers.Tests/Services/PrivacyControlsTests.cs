using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Ses.Local.Core.Enums;
using Ses.Local.Core.Interfaces;
using Ses.Local.Core.Models;
using Ses.Local.Core.Options;
using Ses.Local.Workers.Services;
using Xunit;

namespace Ses.Local.Workers.Tests.Services;

/// <summary>
/// Tests for WI-992 privacy controls:
/// - Excluded sessions filtered from search, sync, and CLAUDE.md
/// - Private tag stripping in ClaudeExportParser
/// - Import filtering by title pattern and date range
/// - Project path exclusion
/// </summary>
public sealed class PrivacyControlsTests : IAsyncDisposable
{
    private readonly LocalDbService _db;
    private readonly string _tempDir;

    public PrivacyControlsTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ses-privacy-" + Guid.NewGuid());
        Directory.CreateDirectory(_tempDir);
        _db = new LocalDbService(
            Path.Combine(_tempDir, "local.db"),
            NullLogger<LocalDbService>.Instance);
    }

    // ── ExcludeSessionAsync / IsSessionExcludedAsync ─────────────────────────

    [Fact]
    public async Task ExcludeSession_MarksSessionAsExcluded()
    {
        var session = await CreateTestSessionAsync();

        await _db.ExcludeSessionAsync(session.Id, true);

        Assert.True(await _db.IsSessionExcludedAsync(session.Id));
    }

    [Fact]
    public async Task ExcludeSession_CanBeUnexcluded()
    {
        var session = await CreateTestSessionAsync();

        await _db.ExcludeSessionAsync(session.Id, true);
        Assert.True(await _db.IsSessionExcludedAsync(session.Id));

        await _db.ExcludeSessionAsync(session.Id, false);
        Assert.False(await _db.IsSessionExcludedAsync(session.Id));
    }

    // ── GetPendingSyncAsync filters excluded sessions ────────────────────────

    [Fact]
    public async Task GetPendingSyncAsync_ExcludesExcludedSessions()
    {
        var s1 = await CreateTestSessionAsync("included-session");
        var s2 = await CreateTestSessionAsync("excluded-session");

        await _db.ExcludeSessionAsync(s2.Id, true);

        var pending = await _db.GetPendingSyncAsync();

        Assert.Contains(pending, s => s.ExternalId == "included-session");
        Assert.DoesNotContain(pending, s => s.ExternalId == "excluded-session");
    }

    // ── SearchAsync filters excluded sessions ────────────────────────────────

    [Fact]
    public async Task SearchAsync_ExcludesExcludedSessions()
    {
        var session = await CreateTestSessionAsync();
        var uniqueWord = "xyzprivacytest" + Guid.NewGuid().ToString("N")[..8];

        await _db.UpsertMessagesAsync([
            new ConversationMessage
            {
                SessionId = session.Id,
                Role      = "user",
                Content   = $"This contains {uniqueWord}",
                CreatedAt = DateTime.UtcNow
            }
        ]);

        // Search should find it before exclusion
        var resultsBefore = await _db.SearchAsync(uniqueWord);
        Assert.Single(resultsBefore);

        // Exclude the session
        await _db.ExcludeSessionAsync(session.Id, true);

        // Search should no longer find it
        var resultsAfter = await _db.SearchAsync(uniqueWord);
        Assert.Empty(resultsAfter);
    }

    // ── GetRecentSessionsByProjectNameAsync filters excluded ─────────────────

    [Fact]
    public async Task GetRecentSessionsByProjectName_ExcludesExcludedSessions()
    {
        var s1 = new ConversationSession
        {
            Source     = ConversationSource.ClaudeCode,
            ExternalId = "proj-" + Guid.NewGuid().ToString("N")[..8],
            Title      = "myproj/abc12345",
            CreatedAt  = DateTime.UtcNow,
            UpdatedAt  = DateTime.UtcNow
        };
        await _db.UpsertSessionAsync(s1);

        var s2 = new ConversationSession
        {
            Source     = ConversationSource.ClaudeCode,
            ExternalId = "proj-" + Guid.NewGuid().ToString("N")[..8],
            Title      = "myproj/def67890",
            CreatedAt  = DateTime.UtcNow,
            UpdatedAt  = DateTime.UtcNow
        };
        await _db.UpsertSessionAsync(s2);
        await _db.ExcludeSessionAsync(s2.Id, true);

        var results = await _db.GetRecentSessionsByProjectNameAsync("myproj", DateTime.UtcNow.AddDays(-1));

        Assert.Single(results);
        Assert.Equal(s1.ExternalId, results[0].ExternalId);
    }

    // ── Import filtering tests ───────────────────────────────────────────────

    [Fact]
    public void ShouldExclude_TitlePattern_ExcludesMatchingConversations()
    {
        var conv = new ExportConversation
        {
            Uuid        = "conv-1",
            Name        = "personal diary entry",
            CreatedAt   = "2024-06-01T00:00:00Z",
            UpdatedAt   = "2024-06-01T00:00:00Z",
            ChatMessages = []
        };
        var filter = new ImportFilterOptions { ExcludeTitlePatterns = ["personal*"] };

        Assert.True(ClaudeExportParser.ShouldExclude(conv, filter));
    }

    [Fact]
    public void ShouldExclude_TitlePattern_DoesNotExcludeNonMatching()
    {
        var conv = new ExportConversation
        {
            Uuid        = "conv-2",
            Name        = "work project discussion",
            CreatedAt   = "2024-06-01T00:00:00Z",
            UpdatedAt   = "2024-06-01T00:00:00Z",
            ChatMessages = []
        };
        var filter = new ImportFilterOptions { ExcludeTitlePatterns = ["personal*"] };

        Assert.False(ClaudeExportParser.ShouldExclude(conv, filter));
    }

    [Fact]
    public void ShouldExclude_DateBefore_ExcludesOlderConversations()
    {
        var conv = new ExportConversation
        {
            Uuid        = "conv-3",
            Name        = "Old conversation",
            CreatedAt   = "2023-01-01T00:00:00Z",
            UpdatedAt   = "2023-01-01T00:00:00Z",
            ChatMessages = []
        };
        var filter = new ImportFilterOptions { ExcludeBefore = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc) };

        Assert.True(ClaudeExportParser.ShouldExclude(conv, filter));
    }

    [Fact]
    public void ShouldExclude_DateBefore_DoesNotExcludeNewerConversations()
    {
        var conv = new ExportConversation
        {
            Uuid        = "conv-4",
            Name        = "New conversation",
            CreatedAt   = "2024-06-01T00:00:00Z",
            UpdatedAt   = "2024-06-01T00:00:00Z",
            ChatMessages = []
        };
        var filter = new ImportFilterOptions { ExcludeBefore = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc) };

        Assert.False(ClaudeExportParser.ShouldExclude(conv, filter));
    }

    [Fact]
    public void ShouldExclude_NullFilter_NeverExcludes()
    {
        var conv = new ExportConversation
        {
            Uuid        = "conv-5",
            Name        = "personal stuff",
            CreatedAt   = "2024-06-01T00:00:00Z",
            UpdatedAt   = "2024-06-01T00:00:00Z",
            ChatMessages = []
        };

        Assert.False(ClaudeExportParser.ShouldExclude(conv, null));
    }

    [Fact]
    public void MatchesGlobPattern_Wildcard_MatchesPrefix()
    {
        Assert.True(ClaudeExportParser.MatchesGlobPattern("personal diary", "personal*"));
        Assert.False(ClaudeExportParser.MatchesGlobPattern("work project", "personal*"));
    }

    [Fact]
    public void MatchesGlobPattern_CaseInsensitive()
    {
        Assert.True(ClaudeExportParser.MatchesGlobPattern("Personal Diary", "personal*"));
    }

    [Fact]
    public void MatchesGlobPattern_MiddleWildcard()
    {
        Assert.True(ClaudeExportParser.MatchesGlobPattern("my-diary-2024", "my*2024"));
    }

    // ── Private tag stripping in import ──────────────────────────────────────

    [Fact]
    public async Task ImportAsync_PrivateTagStripping_RedactsContent()
    {
        var json = """
            [
              {
                "uuid": "conv-private-1",
                "name": "Private Test",
                "created_at": "2024-06-01T00:00:00Z",
                "updated_at": "2024-06-01T00:00:00Z",
                "chat_messages": [
                  {
                    "uuid": "msg-1",
                    "sender": "human",
                    "text": "My API key is <private>sk-1234567890</private> please remember it",
                    "created_at": "2024-06-01T00:00:00Z"
                  }
                ]
              }
            ]
            """;

        var capturedMessages = new List<ConversationMessage>();
        var db = new Mock<ILocalDbService>();
        db.Setup(x => x.UpsertSessionAsync(It.IsAny<ConversationSession>(), It.IsAny<CancellationToken>()))
          .Callback<ConversationSession, CancellationToken>((s, _) => s.Id = 1)
          .Returns(Task.CompletedTask);
        db.Setup(x => x.UpsertMessagesAsync(It.IsAny<IEnumerable<ConversationMessage>>(), It.IsAny<CancellationToken>()))
          .Callback<IEnumerable<ConversationMessage>, CancellationToken>((m, _) => capturedMessages.AddRange(m))
          .Returns(Task.CompletedTask);

        var options = Options.Create(new SesLocalOptions { EnablePrivateTagStripping = true });
        var parser = new ClaudeExportParser(db.Object, NullLogger<ClaudeExportParser>.Instance, options);

        var path = Path.GetTempFileName() + ".json";
        await File.WriteAllTextAsync(path, json);
        try
        {
            await parser.ImportAsync(path);

            Assert.Single(capturedMessages);
            Assert.DoesNotContain("sk-1234567890", capturedMessages[0].Content);
            Assert.Contains("[PRIVATE — redacted]", capturedMessages[0].Content);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task ImportAsync_PrivateTagStrippingDisabled_PreservesContent()
    {
        var json = """
            [
              {
                "uuid": "conv-private-2",
                "name": "Non-stripped Test",
                "created_at": "2024-06-01T00:00:00Z",
                "updated_at": "2024-06-01T00:00:00Z",
                "chat_messages": [
                  {
                    "uuid": "msg-1",
                    "sender": "human",
                    "text": "My key is <private>sk-0000000000</private>",
                    "created_at": "2024-06-01T00:00:00Z"
                  }
                ]
              }
            ]
            """;

        var capturedMessages = new List<ConversationMessage>();
        var db = new Mock<ILocalDbService>();
        db.Setup(x => x.UpsertSessionAsync(It.IsAny<ConversationSession>(), It.IsAny<CancellationToken>()))
          .Callback<ConversationSession, CancellationToken>((s, _) => s.Id = 1)
          .Returns(Task.CompletedTask);
        db.Setup(x => x.UpsertMessagesAsync(It.IsAny<IEnumerable<ConversationMessage>>(), It.IsAny<CancellationToken>()))
          .Callback<IEnumerable<ConversationMessage>, CancellationToken>((m, _) => capturedMessages.AddRange(m))
          .Returns(Task.CompletedTask);

        var options = Options.Create(new SesLocalOptions { EnablePrivateTagStripping = false });
        var parser = new ClaudeExportParser(db.Object, NullLogger<ClaudeExportParser>.Instance, options);

        var path = Path.GetTempFileName() + ".json";
        await File.WriteAllTextAsync(path, json);
        try
        {
            await parser.ImportAsync(path);

            Assert.Single(capturedMessages);
            Assert.Contains("sk-0000000000", capturedMessages[0].Content);
        }
        finally { File.Delete(path); }
    }

    // ── Import filtering integration test ────────────────────────────────────

    [Fact]
    public async Task ImportAsync_WithTitleFilter_ExcludesMatchingConversations()
    {
        var json = """
            [
              {
                "uuid": "conv-work-1",
                "name": "Work discussion",
                "created_at": "2024-06-01T00:00:00Z",
                "updated_at": "2024-06-01T00:00:00Z",
                "chat_messages": [
                  { "uuid": "m1", "sender": "human", "text": "Work stuff", "created_at": "2024-06-01T00:00:00Z" }
                ]
              },
              {
                "uuid": "conv-personal-1",
                "name": "personal diary entry",
                "created_at": "2024-06-01T00:00:00Z",
                "updated_at": "2024-06-01T00:00:00Z",
                "chat_messages": [
                  { "uuid": "m2", "sender": "human", "text": "Private stuff", "created_at": "2024-06-01T00:00:00Z" }
                ]
              }
            ]
            """;

        var sessions = new List<ConversationSession>();
        var db = new Mock<ILocalDbService>();
        db.Setup(x => x.UpsertSessionAsync(It.IsAny<ConversationSession>(), It.IsAny<CancellationToken>()))
          .Callback<ConversationSession, CancellationToken>((s, _) => { s.Id = sessions.Count + 1; sessions.Add(s); })
          .Returns(Task.CompletedTask);
        db.Setup(x => x.UpsertMessagesAsync(It.IsAny<IEnumerable<ConversationMessage>>(), It.IsAny<CancellationToken>()))
          .Returns(Task.CompletedTask);

        var options = Options.Create(new SesLocalOptions());
        var parser = new ClaudeExportParser(db.Object, NullLogger<ClaudeExportParser>.Instance, options);

        var path = Path.GetTempFileName() + ".json";
        await File.WriteAllTextAsync(path, json);
        try
        {
            var filter = new ImportFilterOptions { ExcludeTitlePatterns = ["personal*"] };
            var result = await parser.ImportAsync(path, importOptions: filter);

            Assert.Equal(1, result.SessionsImported);
            Assert.Equal(1, result.Filtered);
            Assert.Single(sessions);
            Assert.Equal("conv-work-1", sessions[0].ExternalId);
        }
        finally { File.Delete(path); }
    }

    // ── Excluded session in MapSession ────────────────────────────────────────

    [Fact]
    public async Task GetSessionByIdAsync_ReturnsExcludedFlag()
    {
        var session = await CreateTestSessionAsync();
        await _db.ExcludeSessionAsync(session.Id, true);

        var fetched = await _db.GetSessionByIdAsync(session.Id);

        Assert.NotNull(fetched);
        Assert.True(fetched!.Excluded);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task<ConversationSession> CreateTestSessionAsync(string? externalId = null)
    {
        var session = new ConversationSession
        {
            Source     = ConversationSource.ClaudeCode,
            ExternalId = externalId ?? Guid.NewGuid().ToString(),
            Title      = "Test session",
            CreatedAt  = DateTime.UtcNow,
            UpdatedAt  = DateTime.UtcNow
        };
        await _db.UpsertSessionAsync(session);
        return session;
    }

    public async ValueTask DisposeAsync()
    {
        await _db.DisposeAsync();
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }
}
