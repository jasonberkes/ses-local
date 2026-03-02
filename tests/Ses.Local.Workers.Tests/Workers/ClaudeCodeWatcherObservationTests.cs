using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Ses.Local.Core.Enums;
using Ses.Local.Core.Interfaces;
using Ses.Local.Core.Models;
using Ses.Local.Core.Options;
using Ses.Local.Workers.Workers;
using Xunit;

namespace Ses.Local.Workers.Tests.Workers;

/// <summary>
/// Unit tests for structured observation extraction in ClaudeCodeWatcher (WI-979).
/// Uses reflection to invoke the private ProcessFileAsync method with temp JSONL files,
/// and verifies correct observation extraction, FilePath detection, parent linking,
/// and special type detection (GitCommit, TestResult, Error).
/// </summary>
public sealed class ClaudeCodeWatcherObservationTests : IDisposable
{
    private static readonly IOptions<SesLocalOptions> DefaultOptions =
        Options.Create(new SesLocalOptions { EnableClaudeCodeSync = true, PollingIntervalSeconds = 999 });

    private readonly string _tempDir;

    public ClaudeCodeWatcherObservationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static (Mock<ILocalDbService> db, ClaudeCodeWatcher watcher) CreateWatcher(
        Action<ConversationSession>? onSession = null,
        List<ConversationObservation>? captureObs = null,
        List<(long, long)>? captureParents = null)
    {
        var db = new Mock<ILocalDbService>();

        db.Setup(x => x.UpsertSessionAsync(It.IsAny<ConversationSession>(), It.IsAny<CancellationToken>()))
          .Callback<ConversationSession, CancellationToken>((s, _) => { s.Id = 1; onSession?.Invoke(s); })
          .Returns(Task.CompletedTask);

        db.Setup(x => x.UpsertMessagesAsync(It.IsAny<IEnumerable<ConversationMessage>>(), It.IsAny<CancellationToken>()))
          .Returns(Task.CompletedTask);

        db.Setup(x => x.UpsertObservationsAsync(It.IsAny<IEnumerable<ConversationObservation>>(), It.IsAny<CancellationToken>()))
          .Callback<IEnumerable<ConversationObservation>, CancellationToken>((obs, _) =>
          {
              long fakeId = 100;
              foreach (var o in obs)
              {
                  o.Id = fakeId++;
                  captureObs?.Add(o);
              }
          })
          .Returns(Task.CompletedTask);

        db.Setup(x => x.UpdateObservationParentsAsync(
              It.IsAny<IEnumerable<(long, long)>>(), It.IsAny<CancellationToken>()))
          .Callback<IEnumerable<(long, long)>, CancellationToken>((updates, _) =>
          {
              captureParents?.AddRange(updates);
          })
          .Returns(Task.CompletedTask);

        var watcher = new ClaudeCodeWatcher(db.Object, NullLogger<ClaudeCodeWatcher>.Instance, DefaultOptions);
        return (db, watcher);
    }

    private string WriteJsonl(string sessionId, string jsonl)
    {
        var sessionDir = Path.Combine(_tempDir, "encoded-project-dir");
        Directory.CreateDirectory(sessionDir);
        var filePath = Path.Combine(sessionDir, $"{sessionId}.jsonl");
        File.WriteAllText(filePath, jsonl);
        return filePath;
    }

    private static async Task InvokeProcessFileAsync(ClaudeCodeWatcher watcher, string filePath)
    {
        var method = typeof(ClaudeCodeWatcher).GetMethod(
            "ProcessFileAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        await (Task)method.Invoke(watcher, [filePath, CancellationToken.None])!;
    }

    // ── Tests: basic block extraction ─────────────────────────────────────────

    [Fact]
    public async Task ProcessFile_TextBlock_ProducesTextObservation()
    {
        var observations = new List<ConversationObservation>();
        var (_, watcher) = CreateWatcher(captureObs: observations);

        var jsonl = """
            {"type":"user","message":{"role":"user","content":"Hello"},"timestamp":"2026-01-01T00:00:00Z","cwd":"/proj"}
            {"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"Sure, I can help!"}]},"timestamp":"2026-01-01T00:00:01Z"}
            """;

        var filePath = WriteJsonl("sess-text", jsonl);
        await InvokeProcessFileAsync(watcher, filePath);

        var textObs = observations.Single(o => o.ObservationType == ObservationType.Text);
        Assert.Equal("Sure, I can help!", textObs.Content);
        Assert.Null(textObs.ToolName);
        Assert.Null(textObs.FilePath);
    }

    [Fact]
    public async Task ProcessFile_ThinkingBlock_ProducesThinkingObservation()
    {
        var observations = new List<ConversationObservation>();
        var (_, watcher) = CreateWatcher(captureObs: observations);

        var jsonl = """
            {"type":"user","message":{"role":"user","content":"Think about it"},"timestamp":"2026-01-01T00:00:00Z","cwd":"/proj"}
            {"type":"assistant","message":{"role":"assistant","content":[{"type":"thinking","thinking":"I am reasoning through this..."}]},"timestamp":"2026-01-01T00:00:01Z"}
            """;

        var filePath = WriteJsonl("sess-think", jsonl);
        await InvokeProcessFileAsync(watcher, filePath);

        var obs = observations.Single(o => o.ObservationType == ObservationType.Thinking);
        Assert.Equal("I am reasoning through this...", obs.Content);
    }

    [Fact]
    public async Task ProcessFile_ToolUseBlock_ProducesToolUseObservation()
    {
        var observations = new List<ConversationObservation>();
        var (_, watcher) = CreateWatcher(captureObs: observations);

        var jsonl = """
            {"type":"user","message":{"role":"user","content":"Read this file"},"timestamp":"2026-01-01T00:00:00Z","cwd":"/proj"}
            {"type":"assistant","message":{"role":"assistant","content":[{"type":"tool_use","id":"toolu_01","name":"Read","input":{"path":"/src/foo.cs"}}]},"timestamp":"2026-01-01T00:00:01Z"}
            """;

        var filePath = WriteJsonl("sess-tooluse", jsonl);
        await InvokeProcessFileAsync(watcher, filePath);

        var obs = observations.Single(o => o.ObservationType == ObservationType.ToolUse);
        Assert.Equal("Read", obs.ToolName);
        Assert.Equal("/src/foo.cs", obs.FilePath);
        Assert.Contains("foo.cs", obs.Content);
    }

    [Fact]
    public async Task ProcessFile_ToolResultBlock_ProducesToolResultObservation()
    {
        var observations = new List<ConversationObservation>();
        var (_, watcher) = CreateWatcher(captureObs: observations);

        var jsonl = """
            {"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"toolu_01","content":"file contents here"}]},"timestamp":"2026-01-01T00:00:00Z","cwd":"/proj"}
            {"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"done"}]},"timestamp":"2026-01-01T00:00:01Z"}
            """;

        var filePath = WriteJsonl("sess-toolresult", jsonl);
        await InvokeProcessFileAsync(watcher, filePath);

        var obs = observations.Single(o => o.ObservationType == ObservationType.ToolResult);
        Assert.Equal("file contents here", obs.Content);
    }

    [Fact]
    public async Task ProcessFile_SequenceNumbers_AreMonotonicallyIncreasing()
    {
        var observations = new List<ConversationObservation>();
        var (_, watcher) = CreateWatcher(captureObs: observations);

        var jsonl = """
            {"type":"user","message":{"role":"user","content":"go"},"timestamp":"2026-01-01T00:00:00Z","cwd":"/proj"}
            {"type":"assistant","message":{"role":"assistant","content":[{"type":"thinking","thinking":"hmm"},{"type":"text","text":"ok"},{"type":"tool_use","id":"toolu_01","name":"Write","input":{"path":"/out.cs","content":"x"}}]},"timestamp":"2026-01-01T00:00:01Z"}
            {"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"toolu_01","content":"ok"}]},"timestamp":"2026-01-01T00:00:02Z"}
            """;

        var filePath = WriteJsonl("sess-seq", jsonl);
        await InvokeProcessFileAsync(watcher, filePath);

        Assert.True(observations.Count >= 4);
        var seqs = observations.Select(o => o.SequenceNumber).ToList();
        for (int i = 1; i < seqs.Count; i++)
            Assert.True(seqs[i] > seqs[i - 1], $"sequence {seqs[i]} not > {seqs[i - 1]}");
    }

    // ── Tests: FilePath extraction ─────────────────────────────────────────────

    [Theory]
    [InlineData("path",      "/src/Foo.cs")]
    [InlineData("file_path", "/src/Bar.cs")]
    [InlineData("filename",  "baz.txt")]
    public void ExtractFilePath_RecognisesAllKeyVariants(string key, string value)
    {
        var input = System.Text.Json.Nodes.JsonNode.Parse($"{{\"{key}\":\"{value}\"}}");
        var result = ClaudeCodeWatcher.ExtractFilePath(input);
        Assert.Equal(value, result);
    }

    [Fact]
    public void ExtractFilePath_NoMatchingKey_ReturnsNull()
    {
        var input = System.Text.Json.Nodes.JsonNode.Parse("{\"command\":\"ls\"}");
        var result = ClaudeCodeWatcher.ExtractFilePath(input);
        Assert.Null(result);
    }

    [Fact]
    public void ExtractFilePath_NullInput_ReturnsNull()
    {
        var result = ClaudeCodeWatcher.ExtractFilePath(null);
        Assert.Null(result);
    }

    // ── Tests: special type detection ─────────────────────────────────────────

    [Fact]
    public async Task ProcessFile_BashWithGitCommit_ProducesGitCommitObservation()
    {
        var observations = new List<ConversationObservation>();
        var (_, watcher) = CreateWatcher(captureObs: observations);

        var jsonl = """
            {"type":"user","message":{"role":"user","content":"commit it"},"timestamp":"2026-01-01T00:00:00Z","cwd":"/proj"}
            {"type":"assistant","message":{"role":"assistant","content":[{"type":"tool_use","id":"toolu_01","name":"Bash","input":{"command":"git commit -m \"Add feature\""}}]},"timestamp":"2026-01-01T00:00:01Z"}
            """;

        var filePath = WriteJsonl("sess-gitcommit", jsonl);
        await InvokeProcessFileAsync(watcher, filePath);

        var obs = observations.Single(o => o.ObservationType == ObservationType.GitCommit);
        Assert.Equal("Bash", obs.ToolName);
    }

    [Theory]
    [InlineData("dotnet test --project MyTests")]
    [InlineData("npm test")]
    [InlineData("pytest tests/")]
    [InlineData("yarn test --coverage")]
    public async Task ProcessFile_BashWithTestRunner_ProducesTestResultObservation(string command)
    {
        var observations = new List<ConversationObservation>();
        var (_, watcher) = CreateWatcher(captureObs: observations);

        var escapedCmd = command.Replace("\"", "\\\"");
        // Build via Replace to avoid raw-string interpolation clashing with JSON braces
        var assistantLine = """{"type":"assistant","message":{"role":"assistant","content":[{"type":"tool_use","id":"toolu_01","name":"Bash","input":{"command":"__CMD__"}}]},"timestamp":"2026-01-01T00:00:01Z"}"""
            .Replace("__CMD__", escapedCmd);
        var jsonl =
            """{"type":"user","message":{"role":"user","content":"run tests"},"timestamp":"2026-01-01T00:00:00Z","cwd":"/proj"}""" + "\n" +
            assistantLine + "\n";

        var filePath = WriteJsonl($"sess-test-{Guid.NewGuid():N}", jsonl);
        await InvokeProcessFileAsync(watcher, filePath);

        Assert.Contains(observations, o => o.ObservationType == ObservationType.TestResult);
    }

    [Theory]
    [InlineData("Error: file not found")]
    [InlineData("NullReferenceException thrown at line 42")]
    [InlineData("Build failed with 3 errors")]
    public async Task ProcessFile_ToolResultWithErrorKeyword_ProducesErrorObservation(string errorContent)
    {
        var observations = new List<ConversationObservation>();
        var (_, watcher) = CreateWatcher(captureObs: observations);

        var escaped = errorContent.Replace("\"", "\\\"");
        // Build via Replace to avoid raw-string interpolation clashing with JSON braces
        var userLine = """{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"toolu_01","content":"__ERR__"}]},"timestamp":"2026-01-01T00:00:00Z","cwd":"/proj"}"""
            .Replace("__ERR__", escaped);
        var jsonl =
            userLine + "\n" +
            """{"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"I see the error"}]},"timestamp":"2026-01-01T00:00:01Z"}""" + "\n";

        var filePath = WriteJsonl($"sess-err-{Guid.NewGuid():N}", jsonl);
        await InvokeProcessFileAsync(watcher, filePath);

        Assert.Contains(observations, o => o.ObservationType == ObservationType.Error);
    }

    // ── Tests: ParentObservationId linking ────────────────────────────────────

    [Fact]
    public async Task ProcessFile_ToolResultLinkedToToolUse_ParentLinksResolved()
    {
        var observations  = new List<ConversationObservation>();
        var parentUpdates = new List<(long observationId, long parentId)>();
        var (_, watcher)  = CreateWatcher(captureObs: observations, captureParents: parentUpdates);

        var jsonl = """
            {"type":"user","message":{"role":"user","content":"do it"},"timestamp":"2026-01-01T00:00:00Z","cwd":"/proj"}
            {"type":"assistant","message":{"role":"assistant","content":[{"type":"tool_use","id":"toolu_42","name":"Read","input":{"path":"/src/x.cs"}}]},"timestamp":"2026-01-01T00:00:01Z"}
            {"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"toolu_42","content":"the file content"}]},"timestamp":"2026-01-01T00:00:02Z"}
            """;

        var filePath = WriteJsonl("sess-parent", jsonl);
        await InvokeProcessFileAsync(watcher, filePath);

        // Exactly one parent link should have been resolved
        Assert.Single(parentUpdates);

        var toolUseObs    = observations.Single(o => o.ObservationType == ObservationType.ToolUse);
        var toolResultObs = observations.Single(o => o.ObservationType == ObservationType.ToolResult);

        var (resultId, parentId) = parentUpdates[0];
        Assert.Equal(toolResultObs.Id, resultId);
        Assert.Equal(toolUseObs.Id, parentId);
    }

    [Fact]
    public async Task ProcessFile_MultipleToolUses_EachToolResultLinkedToCorrectParent()
    {
        var observations  = new List<ConversationObservation>();
        var parentUpdates = new List<(long observationId, long parentId)>();
        var (_, watcher)  = CreateWatcher(captureObs: observations, captureParents: parentUpdates);

        // Each JSONL line must be a single complete JSON object
        var jsonl = """
            {"type":"user","message":{"role":"user","content":"go"},"timestamp":"2026-01-01T00:00:00Z","cwd":"/proj"}
            {"type":"assistant","message":{"role":"assistant","content":[{"type":"tool_use","id":"toolu_A","name":"Read","input":{"path":"/a.cs"}},{"type":"tool_use","id":"toolu_B","name":"Read","input":{"path":"/b.cs"}}]},"timestamp":"2026-01-01T00:00:01Z"}
            {"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"toolu_A","content":"contents of a"},{"type":"tool_result","tool_use_id":"toolu_B","content":"contents of b"}]},"timestamp":"2026-01-01T00:00:02Z"}
            """;

        var filePath = WriteJsonl("sess-multi-parent", jsonl);
        await InvokeProcessFileAsync(watcher, filePath);

        Assert.Equal(2, parentUpdates.Count);

        var toolUseA    = observations.Single(o => o.ObservationType == ObservationType.ToolUse && o.FilePath == "/a.cs");
        var toolUseB    = observations.Single(o => o.ObservationType == ObservationType.ToolUse && o.FilePath == "/b.cs");
        var toolResults = observations.Where(o => o.ObservationType == ObservationType.ToolResult).ToList();
        Assert.Equal(2, toolResults.Count);

        var parentIdSet = parentUpdates.Select(p => p.parentId).ToHashSet();
        Assert.Contains(toolUseA.Id, parentIdSet);
        Assert.Contains(toolUseB.Id, parentIdSet);
    }

    // ── Tests: backward compatibility ─────────────────────────────────────────

    [Fact]
    public async Task ProcessFile_ObservationExtraction_DoesNotBreakExistingMessageFlow()
    {
        var capturedMessages = new List<ConversationMessage>();
        var observations     = new List<ConversationObservation>();
        var db               = new Mock<ILocalDbService>();

        db.Setup(x => x.UpsertSessionAsync(It.IsAny<ConversationSession>(), It.IsAny<CancellationToken>()))
          .Callback<ConversationSession, CancellationToken>((s, _) => s.Id = 1)
          .Returns(Task.CompletedTask);

        db.Setup(x => x.UpsertMessagesAsync(It.IsAny<IEnumerable<ConversationMessage>>(), It.IsAny<CancellationToken>()))
          .Callback<IEnumerable<ConversationMessage>, CancellationToken>((msgs, _) => capturedMessages.AddRange(msgs))
          .Returns(Task.CompletedTask);

        db.Setup(x => x.UpsertObservationsAsync(It.IsAny<IEnumerable<ConversationObservation>>(), It.IsAny<CancellationToken>()))
          .Callback<IEnumerable<ConversationObservation>, CancellationToken>((obs, _) =>
          {
              long id = 100;
              foreach (var o in obs) { o.Id = id++; observations.Add(o); }
          })
          .Returns(Task.CompletedTask);

        db.Setup(x => x.UpdateObservationParentsAsync(
              It.IsAny<IEnumerable<(long, long)>>(), It.IsAny<CancellationToken>()))
          .Returns(Task.CompletedTask);

        var watcher = new ClaudeCodeWatcher(db.Object, NullLogger<ClaudeCodeWatcher>.Instance, DefaultOptions);

        var jsonl = """
            {"type":"user","message":{"role":"user","content":"Write a file"},"timestamp":"2026-01-01T00:00:00Z","cwd":"/myproject"}
            {"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"Sure!"},{"type":"tool_use","id":"toolu_01","name":"Write","input":{"path":"/out.txt","content":"hello"}}],"usage":{"input_tokens":5,"output_tokens":10}},"timestamp":"2026-01-01T00:00:01Z"}
            """;

        var filePath = WriteJsonl("sess-compat", jsonl);
        await InvokeProcessFileAsync(watcher, filePath);

        // Original message flow still works
        Assert.Equal(2, capturedMessages.Count);
        Assert.Contains(capturedMessages, m => m.Role == "user");
        Assert.Contains(capturedMessages, m => m.Role == "assistant" && m.TokenCount == 15);

        // Observations are also produced
        Assert.True(observations.Count >= 2);
        Assert.Contains(observations, o => o.ObservationType == ObservationType.Text);
        Assert.Contains(observations, o => o.ObservationType == ObservationType.ToolUse && o.ToolName == "Write");
    }
}
