using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Ses.Local.Core.Enums;
using Ses.Local.Core.Interfaces;
using Ses.Local.Core.Options;
using Ses.Local.Integration.Tests.Fixtures;
using Ses.Local.Workers.Services;
using Ses.Local.Workers.Workers;
using Xunit;

namespace Ses.Local.Integration.Tests;

/// <summary>
/// E2E integration tests for the ClaudeCodeWatcher → LocalDbService pipeline.
/// Writes real JSONL to disk, processes it with a real watcher and real SQLite DB,
/// then verifies the resulting sessions, messages, and observations.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ClaudeCodeWatcherIntegrationTests : IAsyncDisposable
{
    private readonly TestDbFixture _dbFixture = new();
    private readonly string _tempDir;

    private static readonly IOptions<SesLocalOptions> DefaultOptions =
        Options.Create(new SesLocalOptions { EnableClaudeCodeSync = true, PollingIntervalSeconds = 999 });

    public ClaudeCodeWatcherIntegrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ses-watcher-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private ClaudeCodeWatcher BuildWatcher()
    {
        var claudeMdGenerator = new Mock<IClaudeMdGenerator>();
        claudeMdGenerator.Setup(x => x.GenerateAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var wiLinkerDb = new Mock<ILocalDbService>();
        wiLinkerDb.Setup(d => d.GetObservationsAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync([]);
        var workItemLinker = new WorkItemLinker(wiLinkerDb.Object, NullLogger<WorkItemLinker>.Instance);
        return new ClaudeCodeWatcher(_dbFixture.Db, claudeMdGenerator.Object,
            workItemLinker, NullLogger<ClaudeCodeWatcher>.Instance, DefaultOptions);
    }

    /// <summary>Writes JSONL to a temp file and invokes ProcessFileAsync via reflection.</summary>
    private async Task ProcessJsonlAsync(ClaudeCodeWatcher watcher, string filePath)
    {
        var method = typeof(ClaudeCodeWatcher).GetMethod(
            "ProcessFileAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;

        await (Task)method.Invoke(watcher, [filePath, CancellationToken.None])!;
    }

    private string WriteJsonl(string sessionId, string content)
    {
        var dir = Path.Combine(_tempDir, "project-dir");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"{sessionId}.jsonl");
        File.WriteAllText(path, content);
        return path;
    }

    // ── Pipeline: basic session ───────────────────────────────────────────────

    [Fact]
    public async Task BasicSession_ProcessedJsonl_SessionAndMessagesStored()
    {
        var sessionId = "session-basic-" + Guid.NewGuid().ToString("N")[..6];
        var jsonl     = SampleJsonlGenerator.BuildBasicSession();
        var filePath  = WriteJsonl(sessionId, jsonl);

        var watcher = BuildWatcher();
        await ProcessJsonlAsync(watcher, filePath);

        // Session should be stored
        var sessions = await _dbFixture.Db.GetPendingSyncAsync(batchSize: 100);
        var session  = sessions.FirstOrDefault(s => s.Source == ConversationSource.ClaudeCode);
        Assert.NotNull(session);
        Assert.Contains("myproject", session.Title);

        // Messages should be stored
        var messages = await _dbFixture.Db.GetMessagesAsync(session.Id);
        Assert.NotEmpty(messages);
        Assert.Contains(messages, m => m.Role == "user");
        Assert.Contains(messages, m => m.Role == "assistant");
    }

    [Fact]
    public async Task BasicSession_ProcessedJsonl_ObservationsStored()
    {
        var sessionId = "session-obs-" + Guid.NewGuid().ToString("N")[..6];
        var jsonl     = SampleJsonlGenerator.BuildBasicSession();
        var filePath  = WriteJsonl(sessionId, jsonl);

        var watcher = BuildWatcher();
        await ProcessJsonlAsync(watcher, filePath);

        var sessions = await _dbFixture.Db.GetPendingSyncAsync(batchSize: 100);
        var session  = sessions.FirstOrDefault(s => s.Source == ConversationSource.ClaudeCode);
        Assert.NotNull(session);

        var observations = await _dbFixture.Db.GetObservationsAsync(session.Id);
        Assert.NotEmpty(observations);

        // Should have tool_use (Read), tool_result, and text observations
        Assert.Contains(observations, o => o.ObservationType == ObservationType.ToolUse && o.ToolName == "Read");
        Assert.Contains(observations, o => o.ObservationType == ObservationType.ToolResult);
        Assert.Contains(observations, o => o.ObservationType == ObservationType.Text);
    }

    [Fact]
    public async Task BasicSession_FilePath_ExtractedFromToolUse()
    {
        var sessionId = "session-filepath-" + Guid.NewGuid().ToString("N")[..6];
        var jsonl     = SampleJsonlGenerator.BuildBasicSession();
        var filePath  = WriteJsonl(sessionId, jsonl);

        var watcher = BuildWatcher();
        await ProcessJsonlAsync(watcher, filePath);

        var sessions = await _dbFixture.Db.GetPendingSyncAsync(batchSize: 100);
        var session  = sessions.First(s => s.Source == ConversationSource.ClaudeCode);

        var observations = await _dbFixture.Db.GetObservationsAsync(session.Id);
        var readObs      = observations.FirstOrDefault(o => o.ToolName == "Read");
        Assert.NotNull(readObs);
        Assert.Equal("/src/Program.cs", readObs.FilePath);
    }

    // ── Pipeline: special tool classifications ────────────────────────────────

    [Fact]
    public async Task SpecialTools_TestResult_AndGitCommit_ClassifiedCorrectly()
    {
        var sessionId = "session-special-" + Guid.NewGuid().ToString("N")[..6];
        var jsonl     = SampleJsonlGenerator.BuildSessionWithSpecialTools();
        var filePath  = WriteJsonl(sessionId, jsonl);

        var watcher = BuildWatcher();
        await ProcessJsonlAsync(watcher, filePath);

        var sessions     = await _dbFixture.Db.GetPendingSyncAsync(batchSize: 100);
        var session      = sessions.First(s => s.Source == ConversationSource.ClaudeCode);
        var observations = await _dbFixture.Db.GetObservationsAsync(session.Id);

        Assert.Contains(observations, o => o.ObservationType == ObservationType.TestResult);
        Assert.Contains(observations, o => o.ObservationType == ObservationType.GitCommit);
    }

    // ── Pipeline: error classification ───────────────────────────────────────

    [Fact]
    public async Task ErrorResult_ClassifiedAsError()
    {
        var sessionId = "session-error-" + Guid.NewGuid().ToString("N")[..6];
        var jsonl     = SampleJsonlGenerator.BuildSessionWithError();
        var filePath  = WriteJsonl(sessionId, jsonl);

        var watcher = BuildWatcher();
        await ProcessJsonlAsync(watcher, filePath);

        var sessions     = await _dbFixture.Db.GetPendingSyncAsync(batchSize: 100);
        var session      = sessions.First(s => s.Source == ConversationSource.ClaudeCode);
        var observations = await _dbFixture.Db.GetObservationsAsync(session.Id);

        Assert.Contains(observations, o => o.ObservationType == ObservationType.Error);
    }

    // ── Pipeline: thinking blocks ─────────────────────────────────────────────

    [Fact]
    public async Task ThinkingBlock_StoredAsThinkingObservation()
    {
        var sessionId = "session-thinking-" + Guid.NewGuid().ToString("N")[..6];
        var jsonl     = SampleJsonlGenerator.BuildSessionWithThinking();
        var filePath  = WriteJsonl(sessionId, jsonl);

        var watcher = BuildWatcher();
        await ProcessJsonlAsync(watcher, filePath);

        var sessions     = await _dbFixture.Db.GetPendingSyncAsync(batchSize: 100);
        var session      = sessions.First(s => s.Source == ConversationSource.ClaudeCode);
        var observations = await _dbFixture.Db.GetObservationsAsync(session.Id);

        Assert.Contains(observations, o => o.ObservationType == ObservationType.Thinking);
        Assert.Contains(observations, o => o.ObservationType == ObservationType.Text);
    }

    // ── Pipeline: parent linking ──────────────────────────────────────────────

    [Fact]
    public async Task ParentLinking_ToolResult_LinkedToToolUse()
    {
        var sessionId = "session-parent-" + Guid.NewGuid().ToString("N")[..6];
        var jsonl     = SampleJsonlGenerator.BuildBasicSession();
        var filePath  = WriteJsonl(sessionId, jsonl);

        var watcher = BuildWatcher();
        await ProcessJsonlAsync(watcher, filePath);

        var sessions     = await _dbFixture.Db.GetPendingSyncAsync(batchSize: 100);
        var session      = sessions.First(s => s.Source == ConversationSource.ClaudeCode);
        var observations = await _dbFixture.Db.GetObservationsAsync(session.Id);

        var toolUse    = observations.FirstOrDefault(o => o.ObservationType == ObservationType.ToolUse);
        var toolResult = observations.FirstOrDefault(o => o.ObservationType == ObservationType.ToolResult);

        Assert.NotNull(toolUse);
        Assert.NotNull(toolResult);
        Assert.Equal(toolUse.Id, toolResult.ParentObservationId);
    }

    // ── Pipeline: observation sequence ordering ───────────────────────────────

    [Fact]
    public async Task Observations_StoredInSequenceOrder()
    {
        var sessionId = "session-seq-" + Guid.NewGuid().ToString("N")[..6];
        var jsonl     = SampleJsonlGenerator.BuildBasicSession();
        var filePath  = WriteJsonl(sessionId, jsonl);

        var watcher = BuildWatcher();
        await ProcessJsonlAsync(watcher, filePath);

        var sessions     = await _dbFixture.Db.GetPendingSyncAsync(batchSize: 100);
        var session      = sessions.First(s => s.Source == ConversationSource.ClaudeCode);
        var observations = await _dbFixture.Db.GetObservationsAsync(session.Id);

        // Sequence numbers must be monotonically increasing
        for (int i = 1; i < observations.Count; i++)
            Assert.True(observations[i].SequenceNumber > observations[i - 1].SequenceNumber);
    }

    // ── Pipeline: incremental processing ─────────────────────────────────────

    [Fact]
    public async Task IncrementalProcessing_OnlyNewLinesProcessed()
    {
        var sessionId = "session-incr-" + Guid.NewGuid().ToString("N")[..6];
        var dir       = Path.Combine(_tempDir, "incr-project");
        Directory.CreateDirectory(dir);
        var filePath  = Path.Combine(dir, $"{sessionId}.jsonl");

        // Write first user turn
        var line1 = """{"type":"user","message":{"role":"user","content":"First message"},"sessionId":"test","uuid":"u1","timestamp":"2026-01-01T10:00:00Z","cwd":"/Users/test/incr"}""";
        await File.WriteAllTextAsync(filePath, line1 + "\n");

        var watcher = BuildWatcher();
        await ProcessJsonlAsync(watcher, filePath);

        var sessionsBefore = await _dbFixture.Db.GetPendingSyncAsync(batchSize: 100);
        var sessionBefore  = sessionsBefore.First(s => s.Source == ConversationSource.ClaudeCode);
        var msgsBefore     = await _dbFixture.Db.GetMessagesAsync(sessionBefore.Id);
        var countBefore    = msgsBefore.Count;

        // Append a second user turn — ProcessFileAsync only initialises the session
        // from a "user" type event, so incremental processing requires a new user line.
        var line2 = """{"type":"user","message":{"role":"user","content":"Follow-up question"},"sessionId":"test","uuid":"u2","timestamp":"2026-01-01T10:00:02Z","cwd":"/Users/test/incr"}""";
        await File.AppendAllTextAsync(filePath, line2 + "\n");

        await ProcessJsonlAsync(watcher, filePath);

        var msgsAfter = await _dbFixture.Db.GetMessagesAsync(sessionBefore.Id);
        Assert.True(msgsAfter.Count > countBefore, "Follow-up user message should be added on second pass");
    }

    // ── Pipeline: FTS search on observations ──────────────────────────────────

    [Fact]
    public async Task SearchObservationsAsync_FindsStoredObservation()
    {
        var sessionId = "session-obs-fts-" + Guid.NewGuid().ToString("N")[..6];
        var jsonl     = SampleJsonlGenerator.BuildBasicSession();
        var filePath  = WriteJsonl(sessionId, jsonl);

        var watcher = BuildWatcher();
        await ProcessJsonlAsync(watcher, filePath);

        // "Program.cs" appears in the file_path and content of the Read tool_use
        var results = await _dbFixture.Db.SearchObservationsAsync("Program");
        Assert.NotEmpty(results);
    }

    // ── Cleanup ───────────────────────────────────────────────────────────────

    public async ValueTask DisposeAsync()
    {
        await _dbFixture.DisposeAsync();
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }
}
