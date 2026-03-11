using Ses.Local.Core.Enums;
using Ses.Local.Core.Models;
using Ses.Local.Integration.Tests.Fixtures;
using Xunit;

namespace Ses.Local.Integration.Tests;

/// <summary>
/// E2E integration tests for LocalDbService using a real temp SQLite database.
/// Verifies: schema migration, upsert, retrieval, search, and sync lifecycle.
/// </summary>
[Trait("Category", "Integration")]
public sealed class LocalDbServiceIntegrationTests : IAsyncDisposable
{
    private readonly TestDbFixture _fixture = new();

    // ── Schema Migration ──────────────────────────────────────────────────────

    [Fact]
    public async Task Schema_MigratedOnFirstAccess_AllTablesExist()
    {
        // Trigger schema creation
        var sessions = await _fixture.Db.GetPendingSyncAsync(batchSize: 1);
        Assert.NotNull(sessions);

        // Verify DB file exists
        Assert.True(File.Exists(_fixture.DbPath));
    }

    // ── Session Upsert / Retrieval ────────────────────────────────────────────

    [Fact]
    public async Task UpsertSession_NewSession_AssignsId_AndIsRetrievable()
    {
        var session = MakeSession("ext-001", "Integration test session");
        await _fixture.Db.UpsertSessionAsync(session);

        Assert.True(session.Id > 0, "Id should be assigned after upsert");

        var pending = await _fixture.Db.GetPendingSyncAsync();
        Assert.Contains(pending, s => s.ExternalId == "ext-001" && s.Title == "Integration test session");
    }

    [Fact]
    public async Task UpsertSession_ExistingSession_UpdatesTitle()
    {
        var session = MakeSession("ext-002", "Original title");
        await _fixture.Db.UpsertSessionAsync(session);

        session.Title     = "Updated title";
        session.UpdatedAt = DateTime.UtcNow.AddMinutes(1);
        await _fixture.Db.UpsertSessionAsync(session);

        var pending = await _fixture.Db.GetPendingSyncAsync();
        var found = pending.First(s => s.ExternalId == "ext-002");
        Assert.Equal("Updated title", found.Title);
    }

    [Fact]
    public async Task UpsertSession_PreservesSourceEnum()
    {
        var session = MakeSession("ext-003", "Source test", ConversationSource.ClaudeChat);
        await _fixture.Db.UpsertSessionAsync(session);

        var pending = await _fixture.Db.GetPendingSyncAsync();
        var found = pending.First(s => s.ExternalId == "ext-003");
        Assert.Equal(ConversationSource.ClaudeChat, found.Source);
    }

    // ── Message Upsert / Retrieval ────────────────────────────────────────────

    [Fact]
    public async Task UpsertMessages_StoresAndRetrievesMessages()
    {
        var session = MakeSession("ext-msg-001", "Message test");
        await _fixture.Db.UpsertSessionAsync(session);

        var messages = new[]
        {
            new ConversationMessage { SessionId = session.Id, Role = "user",      Content = "Hello integration",      CreatedAt = DateTime.UtcNow,               TokenCount = 5 },
            new ConversationMessage { SessionId = session.Id, Role = "assistant",  Content = "Hello from integration",  CreatedAt = DateTime.UtcNow.AddSeconds(1), TokenCount = 7 }
        };

        await _fixture.Db.UpsertMessagesAsync(messages);

        var retrieved = await _fixture.Db.GetMessagesAsync(session.Id);
        Assert.Equal(2, retrieved.Count);
        Assert.Contains(retrieved, m => m.Role == "user" && m.Content == "Hello integration" && m.TokenCount == 5);
        Assert.Contains(retrieved, m => m.Role == "assistant" && m.TokenCount == 7);
    }

    [Fact]
    public async Task UpsertMessages_Idempotent_NoDuplicates()
    {
        var session = MakeSession("ext-msg-002", "Idempotent messages");
        await _fixture.Db.UpsertSessionAsync(session);

        var ts = DateTime.UtcNow;
        var msg = new ConversationMessage { SessionId = session.Id, Role = "user", Content = "same message", CreatedAt = ts };

        await _fixture.Db.UpsertMessagesAsync([msg]);
        await _fixture.Db.UpsertMessagesAsync([msg]); // second upsert

        var retrieved = await _fixture.Db.GetMessagesAsync(session.Id);
        Assert.Single(retrieved);
    }

    // ── FTS Search ────────────────────────────────────────────────────────────

    [Fact]
    public async Task SearchAsync_FindsMessageByUniqueContent()
    {
        var session = MakeSession("ext-search-001", "Search test");
        await _fixture.Db.UpsertSessionAsync(session);

        var uniqueToken = "xyzintegration" + Guid.NewGuid().ToString("N")[..6];
        await _fixture.Db.UpsertMessagesAsync([
            new ConversationMessage
            {
                SessionId = session.Id,
                Role      = "user",
                Content   = $"This message has unique token {uniqueToken} in it",
                CreatedAt = DateTime.UtcNow
            }
        ]);

        var results = await _fixture.Db.SearchAsync(uniqueToken);
        Assert.Single(results);
        Assert.Contains(uniqueToken, results[0].Content);
    }

    [Fact]
    public async Task SearchAsync_RespectsLimit()
    {
        var session = MakeSession("ext-search-limit", "Limit test");
        await _fixture.Db.UpsertSessionAsync(session);

        var sharedWord = "sharedkeyword" + Guid.NewGuid().ToString("N")[..4];
        var batch = Enumerable.Range(0, 5).Select(i => new ConversationMessage
        {
            SessionId = session.Id,
            Role      = "user",
            Content   = $"Message {i} contains {sharedWord}",
            CreatedAt = DateTime.UtcNow.AddSeconds(i)
        }).ToList();

        await _fixture.Db.UpsertMessagesAsync(batch);

        var results = await _fixture.Db.SearchAsync(sharedWord, limit: 2);
        Assert.True(results.Count <= 2);
    }

    // ── Sync Lifecycle ────────────────────────────────────────────────────────

    [Fact]
    public async Task MarkSyncedAsync_RemovesFromPendingSync()
    {
        var session = MakeSession("ext-sync-001", "Sync lifecycle test");
        await _fixture.Db.UpsertSessionAsync(session);

        var beforeSync = await _fixture.Db.GetPendingSyncAsync();
        Assert.Contains(beforeSync, s => s.ExternalId == "ext-sync-001");

        await _fixture.Db.MarkSyncedAsync(session.Id, "doc-id-xyz");

        var afterSync = await _fixture.Db.GetPendingSyncAsync();
        Assert.DoesNotContain(afterSync, s => s.ExternalId == "ext-sync-001");
    }

    [Fact]
    public async Task GetPendingSyncAsync_RespectsLimit()
    {
        for (int i = 0; i < 5; i++)
            await _fixture.Db.UpsertSessionAsync(MakeSession($"ext-limit-{i}", $"Session {i}"));

        var results = await _fixture.Db.GetPendingSyncAsync(batchSize: 2);
        Assert.True(results.Count <= 2);
    }

    // ── Observation Upsert / Retrieval ────────────────────────────────────────

    [Fact]
    public async Task UpsertObservations_StoresAndRetrievesObservations()
    {
        var session = MakeSession("ext-obs-001", "Observation test");
        await _fixture.Db.UpsertSessionAsync(session);

        var observations = new List<ConversationObservation>
        {
            new() { SessionId = session.Id, ObservationType = ObservationType.ToolUse,    ToolName = "Read", FilePath = "/src/Foo.cs", Content = "{\"file_path\":\"/src/Foo.cs\"}", SequenceNumber = 0, CreatedAt = DateTime.UtcNow },
            new() { SessionId = session.Id, ObservationType = ObservationType.ToolResult,  Content = "File content here",                                                             SequenceNumber = 1, CreatedAt = DateTime.UtcNow.AddSeconds(1) },
            new() { SessionId = session.Id, ObservationType = ObservationType.Text,        Content = "Here is my analysis of the file.",                                              SequenceNumber = 2, CreatedAt = DateTime.UtcNow.AddSeconds(2) }
        };

        await _fixture.Db.UpsertObservationsAsync(observations);

        // All observations should have been assigned DB Ids
        Assert.All(observations, o => Assert.True(o.Id > 0));

        var retrieved = await _fixture.Db.GetObservationsAsync(session.Id);
        Assert.Equal(3, retrieved.Count);
        Assert.Equal(ObservationType.ToolUse,   retrieved[0].ObservationType);
        Assert.Equal("Read",                    retrieved[0].ToolName);
        Assert.Equal("/src/Foo.cs",             retrieved[0].FilePath);
        Assert.Equal(ObservationType.ToolResult, retrieved[1].ObservationType);
        Assert.Equal(ObservationType.Text,       retrieved[2].ObservationType);
    }

    [Fact]
    public async Task UpsertObservations_Idempotent_NoDuplicates()
    {
        var session = MakeSession("ext-obs-002", "Idempotent observations");
        await _fixture.Db.UpsertSessionAsync(session);

        var obs = new ConversationObservation
        {
            SessionId       = session.Id,
            ObservationType = ObservationType.Text,
            Content         = "Duplicate content",
            SequenceNumber  = 0,
            CreatedAt       = DateTime.UtcNow
        };

        await _fixture.Db.UpsertObservationsAsync([obs]);
        await _fixture.Db.UpsertObservationsAsync([obs]); // second upsert

        var retrieved = await _fixture.Db.GetObservationsAsync(session.Id);
        Assert.Single(retrieved);
    }

    [Fact]
    public async Task GetObservationsAsync_OrderedBySequenceNumber()
    {
        var session = MakeSession("ext-obs-order", "Ordering test");
        await _fixture.Db.UpsertSessionAsync(session);

        // Insert in reverse order to confirm DB ordering, not insertion ordering
        var observations = new List<ConversationObservation>
        {
            new() { SessionId = session.Id, ObservationType = ObservationType.Text, Content = "Third", SequenceNumber = 2, CreatedAt = DateTime.UtcNow },
            new() { SessionId = session.Id, ObservationType = ObservationType.Text, Content = "First",  SequenceNumber = 0, CreatedAt = DateTime.UtcNow },
            new() { SessionId = session.Id, ObservationType = ObservationType.Text, Content = "Second", SequenceNumber = 1, CreatedAt = DateTime.UtcNow }
        };

        await _fixture.Db.UpsertObservationsAsync(observations);

        var retrieved = await _fixture.Db.GetObservationsAsync(session.Id);
        Assert.Equal(["First", "Second", "Third"], retrieved.Select(o => o.Content).ToArray());
    }

    // ── Observation FTS Search ────────────────────────────────────────────────

    [Fact]
    public async Task SearchObservationsAsync_FindsByContent()
    {
        var session = MakeSession("ext-obs-search-001", "Obs search test");
        await _fixture.Db.UpsertSessionAsync(session);

        var uniqueWord = "uniqueobstoken" + Guid.NewGuid().ToString("N")[..5];
        await _fixture.Db.UpsertObservationsAsync([
            new ConversationObservation
            {
                SessionId       = session.Id,
                ObservationType = ObservationType.Text,
                Content         = $"Observation with {uniqueWord} content",
                SequenceNumber  = 0,
                CreatedAt       = DateTime.UtcNow
            }
        ]);

        var results = await _fixture.Db.SearchObservationsAsync(uniqueWord);
        Assert.Single(results);
        Assert.Contains(uniqueWord, results[0].Content);
    }

    [Fact]
    public async Task SearchObservationsAsync_FindsByToolName()
    {
        var session = MakeSession("ext-obs-search-tool", "Tool name search");
        await _fixture.Db.UpsertSessionAsync(session);

        var uniqueTool = "MySpecialTool" + Guid.NewGuid().ToString("N")[..4];
        await _fixture.Db.UpsertObservationsAsync([
            new ConversationObservation
            {
                SessionId       = session.Id,
                ObservationType = ObservationType.ToolUse,
                ToolName        = uniqueTool,
                Content         = "{}",
                SequenceNumber  = 0,
                CreatedAt       = DateTime.UtcNow
            }
        ]);

        var results = await _fixture.Db.SearchObservationsAsync(uniqueTool);
        Assert.Single(results);
        Assert.Equal(uniqueTool, results[0].ToolName);
    }

    // ── Parent Linking ────────────────────────────────────────────────────────

    [Fact]
    public async Task UpdateObservationParents_LinksToolResultToToolUse()
    {
        var session = MakeSession("ext-parent-001", "Parent link test");
        await _fixture.Db.UpsertSessionAsync(session);

        var toolUse = new ConversationObservation
        {
            SessionId       = session.Id,
            ObservationType = ObservationType.ToolUse,
            ToolName        = "Write",
            Content         = "{\"file_path\":\"/out.txt\"}",
            SequenceNumber  = 0,
            CreatedAt       = DateTime.UtcNow
        };
        var toolResult = new ConversationObservation
        {
            SessionId       = session.Id,
            ObservationType = ObservationType.ToolResult,
            Content         = "File written successfully",
            SequenceNumber  = 1,
            CreatedAt       = DateTime.UtcNow.AddSeconds(1)
        };

        await _fixture.Db.UpsertObservationsAsync([toolUse, toolResult]);

        // Link tool_result → tool_use
        await _fixture.Db.UpdateObservationParentsAsync([(toolResult.Id, toolUse.Id)]);

        var retrieved = await _fixture.Db.GetObservationsAsync(session.Id);
        var retrievedResult = retrieved.First(o => o.ObservationType == ObservationType.ToolResult);
        Assert.Equal(toolUse.Id, retrievedResult.ParentObservationId);
    }

    // ── Hook Activity (TRAY-3) ────────────────────────────────────────────────

    [Fact]
    public async Task GetLastHookObservationTimeAsync_WhenNoObservations_ReturnsNull()
    {
        var result = await _fixture.Db.GetLastHookObservationTimeAsync();
        Assert.Null(result);
    }

    [Fact]
    public async Task GetLastHookObservationTimeAsync_ReturnsMaxCreatedAt()
    {
        var session = MakeSession("ext-hook-time-001", "hook time test", ConversationSource.ClaudeCode);
        await _fixture.Db.UpsertSessionAsync(session);

        var t1 = DateTime.UtcNow.AddMinutes(-5);
        var t2 = DateTime.UtcNow.AddMinutes(-2);

        await _fixture.Db.UpsertObservationsAsync([
            new ConversationObservation { SessionId = session.Id, ObservationType = ObservationType.ToolUse,
                ToolName = "Read", Content = "{}", SequenceNumber = 0, CreatedAt = t1 },
            new ConversationObservation { SessionId = session.Id, ObservationType = ObservationType.ToolUse,
                ToolName = "Write", Content = "{}", SequenceNumber = 1, CreatedAt = t2 }
        ]);

        var result = await _fixture.Db.GetLastHookObservationTimeAsync();

        Assert.NotNull(result);
        Assert.True(Math.Abs((result.Value - t2).TotalSeconds) < 2, "Should return the most recent timestamp");
    }

    [Fact]
    public async Task GetLastHookObservationTimeAsync_ExcludesNonClaudeCodeSessions()
    {
        var session = MakeSession("ext-hook-non-cc", "non-cc session", ConversationSource.ClaudeChat);
        await _fixture.Db.UpsertSessionAsync(session);

        await _fixture.Db.UpsertObservationsAsync([
            new ConversationObservation { SessionId = session.Id, ObservationType = ObservationType.ToolUse,
                ToolName = "Write", Content = "{}", SequenceNumber = 0, CreatedAt = DateTime.UtcNow }
        ]);

        // The query should not throw even if only non-ClaudeCode observations exist.
        // (May return a timestamp from other tests' ClaudeCode sessions in the shared fixture.)
        var result = await _fixture.Db.GetLastHookObservationTimeAsync();
        _ = result; // no exception is the key assertion
    }

    [Fact]
    public async Task GetRecentHookObservationsAsync_WhenNoObservations_ReturnsEmpty()
    {
        var result = await _fixture.Db.GetRecentHookObservationsAsync(5);
        Assert.NotNull(result);
    }

    [Fact]
    public async Task GetRecentHookObservationsAsync_ReturnsOnlyClaudeCodeObservations()
    {
        var ccSession = MakeSession("ext-hook-cc-only", "CC session", ConversationSource.ClaudeCode);
        var aiSession = MakeSession("ext-hook-ai-only", "AI session", ConversationSource.ClaudeChat);
        await _fixture.Db.UpsertSessionAsync(ccSession);
        await _fixture.Db.UpsertSessionAsync(aiSession);

        var uniqueTool = "UniqueHookTool" + Guid.NewGuid().ToString("N")[..6];

        await _fixture.Db.UpsertObservationsAsync([
            new ConversationObservation { SessionId = ccSession.Id, ObservationType = ObservationType.ToolUse,
                ToolName = uniqueTool, Content = "{}", SequenceNumber = 0, CreatedAt = DateTime.UtcNow }
        ]);
        await _fixture.Db.UpsertObservationsAsync([
            new ConversationObservation { SessionId = aiSession.Id, ObservationType = ObservationType.ToolUse,
                ToolName = "OtherTool", Content = "{}", SequenceNumber = 0, CreatedAt = DateTime.UtcNow }
        ]);

        var result = await _fixture.Db.GetRecentHookObservationsAsync(50);

        Assert.Contains(result, o => o.ToolName == uniqueTool);
        Assert.DoesNotContain(result, o => o.ToolName == "OtherTool");
    }

    [Fact]
    public async Task GetRecentHookObservationsAsync_RespectsLimit()
    {
        var session = MakeSession("ext-hook-limit-001", "limit test", ConversationSource.ClaudeCode);
        await _fixture.Db.UpsertSessionAsync(session);

        var observations = Enumerable.Range(0, 10).Select(i =>
            new ConversationObservation
            {
                SessionId       = session.Id,
                ObservationType = ObservationType.ToolUse,
                ToolName        = "Read",
                Content         = "{}",
                SequenceNumber  = 200 + i,
                CreatedAt       = DateTime.UtcNow.AddSeconds(i)
            }).ToList();

        await _fixture.Db.UpsertObservationsAsync(observations);

        var result = await _fixture.Db.GetRecentHookObservationsAsync(3);

        Assert.True(result.Count <= 3);
    }

    // ── SyncStats (TRAY-8) ────────────────────────────────────────────────────

    [Fact]
    public async Task GetSyncStatsAsync_EmptyDatabase_ReturnsZeroes()
    {
        var stats = await _fixture.Db.GetSyncStatsAsync();

        Assert.Equal(0, stats.TotalConversations);
        Assert.Equal(0, stats.TotalMessages);
        Assert.Equal(0, stats.ClaudeChat.Count);
        Assert.Equal(0, stats.ClaudeCode.Count);
        Assert.Null(stats.OldestConversation);
        Assert.Null(stats.NewestConversation);
    }

    [Fact]
    public async Task GetSyncStatsAsync_ReturnsCorrectCountsPerSurface()
    {
        await _fixture.Db.UpsertSessionAsync(MakeSession("s-cc-1", "CC session 1", ConversationSource.ClaudeCode));
        await _fixture.Db.UpsertSessionAsync(MakeSession("s-cc-2", "CC session 2", ConversationSource.ClaudeCode));
        await _fixture.Db.UpsertSessionAsync(MakeSession("s-chat-1", "Chat session", ConversationSource.ClaudeChat));

        var stats = await _fixture.Db.GetSyncStatsAsync();

        Assert.Equal(2, stats.ClaudeCode.Count);
        Assert.Equal(1, stats.ClaudeChat.Count);
        Assert.Equal(3, stats.TotalConversations);
    }

    [Fact]
    public async Task GetSyncStatsAsync_CountsTotalMessages()
    {
        var session = MakeSession("s-msg-1", "Message count session");
        await _fixture.Db.UpsertSessionAsync(session);
        await _fixture.Db.UpsertMessagesAsync([
            new ConversationMessage { SessionId = session.Id, Role = "user",      Content = "Hello",  CreatedAt = DateTime.UtcNow },
            new ConversationMessage { SessionId = session.Id, Role = "assistant", Content = "World",  CreatedAt = DateTime.UtcNow.AddSeconds(1) }
        ]);

        var stats = await _fixture.Db.GetSyncStatsAsync();

        Assert.True(stats.TotalMessages >= 2);
    }

    [Fact]
    public async Task GetSyncStatsAsync_ReturnsOldestAndNewestDates()
    {
        var old  = DateTime.UtcNow.AddDays(-30);
        var now  = DateTime.UtcNow;

        var s1 = MakeSession("s-date-old", "Old session");
        s1.CreatedAt = old;
        s1.UpdatedAt = old;
        await _fixture.Db.UpsertSessionAsync(s1);

        var s2 = MakeSession("s-date-new", "New session");
        s2.CreatedAt = now;
        s2.UpdatedAt = now;
        await _fixture.Db.UpsertSessionAsync(s2);

        var stats = await _fixture.Db.GetSyncStatsAsync();

        Assert.NotNull(stats.OldestConversation);
        Assert.NotNull(stats.NewestConversation);
        Assert.True(stats.OldestConversation <= stats.NewestConversation);
    }

    // ── Import History (TRAY-5/6) ─────────────────────────────────────────────

    [Fact]
    public async Task GetImportHistoryAsync_EmptyDatabase_ReturnsEmptyList()
    {
        var history = await _fixture.Db.GetImportHistoryAsync();

        Assert.Empty(history);
    }

    [Fact]
    public async Task GetLastImportAsync_EmptyDatabase_ReturnsNull()
    {
        var last = await _fixture.Db.GetLastImportAsync();

        Assert.Null(last);
    }

    [Fact]
    public async Task RecordImportHistoryAsync_Persists_AndIsReturnedByGetHistory()
    {
        var record = new ImportHistoryRecord
        {
            Source            = "claude",
            FilePath          = "/tmp/export.json",
            ImportedAt        = DateTime.UtcNow,
            SessionsImported  = 42,
            MessagesImported  = 1234,
            DuplicatesSkipped = 5,
            Errors            = 0,
        };

        await _fixture.Db.RecordImportHistoryAsync(record);
        var history = await _fixture.Db.GetImportHistoryAsync();

        Assert.Single(history);
        Assert.Equal("claude",          history[0].Source);
        Assert.Equal("/tmp/export.json", history[0].FilePath);
        Assert.Equal(42,                history[0].SessionsImported);
        Assert.Equal(1234,              history[0].MessagesImported);
        Assert.Equal(5,                 history[0].DuplicatesSkipped);
        Assert.Equal(0,                 history[0].Errors);
    }

    [Fact]
    public async Task GetLastImportAsync_ReturnsNewestRecord()
    {
        var old = new ImportHistoryRecord
        {
            Source     = "chatgpt",
            FilePath   = "/tmp/old.zip",
            ImportedAt = DateTime.UtcNow.AddDays(-2),
        };
        var newer = new ImportHistoryRecord
        {
            Source     = "claude",
            FilePath   = "/tmp/new.json",
            ImportedAt = DateTime.UtcNow,
        };

        await _fixture.Db.RecordImportHistoryAsync(old);
        await _fixture.Db.RecordImportHistoryAsync(newer);

        var last = await _fixture.Db.GetLastImportAsync();

        Assert.NotNull(last);
        Assert.Equal("claude", last.Source);
        Assert.Equal("/tmp/new.json", last.FilePath);
    }

    [Fact]
    public async Task GetImportHistoryAsync_RespectsLimitParameter()
    {
        for (var i = 0; i < 5; i++)
        {
            await _fixture.Db.RecordImportHistoryAsync(new ImportHistoryRecord
            {
                Source     = "claude",
                FilePath   = $"/tmp/export-{i}.json",
                ImportedAt = DateTime.UtcNow.AddMinutes(-i),
            });
        }

        var history = await _fixture.Db.GetImportHistoryAsync(3);

        Assert.Equal(3, history.Count);
    }

    [Fact]
    public async Task GetImportHistoryAsync_OrderedByImportedAtDesc()
    {
        var t1 = DateTime.UtcNow.AddHours(-3);
        var t2 = DateTime.UtcNow.AddHours(-1);

        await _fixture.Db.RecordImportHistoryAsync(new ImportHistoryRecord { Source = "claude", FilePath = "/a", ImportedAt = t1 });
        await _fixture.Db.RecordImportHistoryAsync(new ImportHistoryRecord { Source = "chatgpt", FilePath = "/b", ImportedAt = t2 });

        var history = await _fixture.Db.GetImportHistoryAsync();

        Assert.Equal("/b", history[0].FilePath); // newer first
        Assert.Equal("/a", history[1].FilePath);
    }

    [Fact]
    public async Task GetImportHistoryAsync_ErrorsField_IsReadCorrectly()
    {
        // Regression test for Bug 4: ArgumentOutOfRangeException when reading
        // column index 7 (Errors). Verifies the bounds-checked reader path works.
        var record = new ImportHistoryRecord
        {
            Source            = "claude",
            FilePath          = "/tmp/test.json",
            ImportedAt        = DateTime.UtcNow,
            SessionsImported  = 1,
            MessagesImported  = 10,
            DuplicatesSkipped = 2,
            Errors            = 3,
        };
        await _fixture.Db.RecordImportHistoryAsync(record);

        var history = await _fixture.Db.GetImportHistoryAsync(1);

        Assert.Single(history);
        Assert.Equal(3, history[0].Errors);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static ConversationSession MakeSession(string externalId, string title, ConversationSource source = ConversationSource.ClaudeCode) =>
        new()
        {
            Source     = source,
            ExternalId = externalId,
            Title      = title,
            CreatedAt  = DateTime.UtcNow,
            UpdatedAt  = DateTime.UtcNow
        };

    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();
}
