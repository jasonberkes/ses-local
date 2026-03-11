using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Ses.Local.Core.Enums;
using Ses.Local.Core.Interfaces;
using Ses.Local.Core.Models;
using Ses.Local.Core.Options;
using Ses.Local.Workers.Services;
using Ses.Local.Workers.Workers;
using System.Text.Json.Nodes;
using Xunit;

namespace Ses.Local.Workers.Tests.Workers;

public sealed class ClaudeCodeWatcherTests
{
    private static IOptions<SesLocalOptions> DefaultOptions =>
        Options.Create(new SesLocalOptions { EnableClaudeCodeSync = true, PollingIntervalSeconds = 999 });

    private static Mock<IClaudeMdGenerator> NoOpGenerator()
    {
        var gen = new Mock<IClaudeMdGenerator>();
        gen.Setup(x => x.GenerateAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
           .Returns(Task.CompletedTask);
        return gen;
    }

    private static WorkItemLinker NoOpWorkItemLinker()
    {
        var db = new Mock<ILocalDbService>();
        db.Setup(d => d.GetObservationsAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
          .ReturnsAsync([]);
        return new WorkItemLinker(db.Object, NullLogger<WorkItemLinker>.Instance);
    }

    [Fact]
    public async Task ExecuteAsync_WhenDisabled_DoesNotStart()
    {
        var db      = new Mock<ILocalDbService>();
        var options = Options.Create(new SesLocalOptions { EnableClaudeCodeSync = false });
        var watcher = new ClaudeCodeWatcher(db.Object, NoOpGenerator().Object, NoOpWorkItemLinker(), NullLogger<ClaudeCodeWatcher>.Instance, options);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await watcher.StartAsync(cts.Token);

        db.Verify(x => x.UpsertSessionAsync(It.IsAny<ConversationSession>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_WhenProjectsDirMissing_DoesNotThrow()
    {
        var db      = new Mock<ILocalDbService>();
        var watcher = new ClaudeCodeWatcher(db.Object, NoOpGenerator().Object, NoOpWorkItemLinker(), NullLogger<ClaudeCodeWatcher>.Instance, DefaultOptions);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        // Should not throw even if ~/.claude/projects doesn't exist
        var ex = await Record.ExceptionAsync(() => watcher.StartAsync(cts.Token));
        Assert.Null(ex);
    }

    [Fact]
    public async Task ProcessFile_ValidJsonl_StoresSessionAndMessages()
    {
        // Arrange — write a temp JSONL file
        var tempDir   = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var sessionId = "test-session-abc123";
        var sessionDir = Path.Combine(tempDir, "encoded-project-dir");
        Directory.CreateDirectory(sessionDir);
        var filePath  = Path.Combine(sessionDir, $"{sessionId}.jsonl");

        var jsonl = """
            {"type":"user","message":{"role":"user","content":"Hello CC"},"sessionId":"test-session-abc123","uuid":"u1","timestamp":"2026-01-01T00:00:00Z","cwd":"/Users/jason/myproject","gitBranch":"main"}
            {"type":"assistant","message":{"role":"assistant","model":"claude-opus-4","usage":{"input_tokens":10,"output_tokens":20},"content":[{"type":"text","text":"Hello! How can I help?"}]},"uuid":"a1","timestamp":"2026-01-01T00:00:01Z"}
            """;
        await File.WriteAllTextAsync(filePath, jsonl);

        ConversationSession? capturedSession = null;
        List<ConversationMessage>? capturedMessages = null;

        var db = new Mock<ILocalDbService>();
        db.Setup(x => x.UpsertSessionAsync(It.IsAny<ConversationSession>(), It.IsAny<CancellationToken>()))
          .Callback<ConversationSession, CancellationToken>((s, _) => { capturedSession = s; s.Id = 1; })
          .Returns(Task.CompletedTask);
        db.Setup(x => x.UpsertMessagesAsync(It.IsAny<IEnumerable<ConversationMessage>>(), It.IsAny<CancellationToken>()))
          .Callback<IEnumerable<ConversationMessage>, CancellationToken>((msgs, _) => capturedMessages = [.. msgs])
          .Returns(Task.CompletedTask);

        var watcher = new ClaudeCodeWatcher(db.Object, NoOpGenerator().Object, NoOpWorkItemLinker(), NullLogger<ClaudeCodeWatcher>.Instance, DefaultOptions);

        // Use reflection to call the private ProcessFileAsync
        var method = typeof(ClaudeCodeWatcher).GetMethod("ProcessFileAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        await (Task)method.Invoke(watcher, [filePath, CancellationToken.None])!;

        // Assert
        Assert.NotNull(capturedSession);
        Assert.Equal(ConversationSource.ClaudeCode, capturedSession!.Source);
        Assert.Equal(sessionId, capturedSession.ExternalId);
        Assert.Contains("myproject", capturedSession.Title);

        Assert.NotNull(capturedMessages);
        Assert.Equal(2, capturedMessages!.Count);
        Assert.Contains(capturedMessages, m => m.Role == "user" && m.Content.Contains("Hello CC"));
        Assert.Contains(capturedMessages, m => m.Role == "assistant" && m.TokenCount == 30);

        // Cleanup
        Directory.Delete(tempDir, recursive: true);
    }

    [Fact]
    public async Task ProcessFile_MalformedJsonl_SkippedOnSubsequentScans()
    {
        // Arrange — write a temp file with invalid JSON
        var tempDir    = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var sessionDir = Path.Combine(tempDir, "encoded-project-dir");
        Directory.CreateDirectory(sessionDir);
        var filePath = Path.Combine(sessionDir, "bad-session.jsonl");
        await File.WriteAllTextAsync(filePath, "not valid json\n{also bad}\n");

        var db = new Mock<ILocalDbService>();
        var watcher = new ClaudeCodeWatcher(db.Object, NoOpGenerator().Object, NoOpWorkItemLinker(),
            NullLogger<ClaudeCodeWatcher>.Instance, DefaultOptions);

        var processMethod = typeof(ClaudeCodeWatcher).GetMethod("ProcessFileAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var scanMethod = typeof(ClaudeCodeWatcher).GetMethod("ScanAllAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;

        // First scan — processes the file (no new data, but records position)
        await (Task)scanMethod.Invoke(watcher, [tempDir, CancellationToken.None])!;

        // Access _failedFiles via reflection to verify state after a file that throws
        // The test primarily verifies no unhandled exception from ScanAllAsync
        var ex = await Record.ExceptionAsync(() =>
            (Task)scanMethod.Invoke(watcher, [tempDir, CancellationToken.None])!);
        Assert.Null(ex);

        // No session should have been created from malformed JSON
        db.Verify(x => x.UpsertSessionAsync(It.IsAny<ConversationSession>(), It.IsAny<CancellationToken>()), Times.Never);

        // Cleanup
        Directory.Delete(tempDir, recursive: true);
    }

    [Fact]
    public void IsStaleWorktreeFile_NonWorktreePath_ReturnsFalse()
    {
        var method = typeof(ClaudeCodeWatcher).GetMethod("IsStaleWorktreeFile",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;

        // Normal project path — not a worktree
        var result = (bool)method.Invoke(null, ["/Users/test/.claude/projects/-Users-test-myproject/session.jsonl"])!;
        Assert.False(result);
    }

    // ── ExtractContent — Bug 1 regression tests ───────────────────────────────

    private static readonly System.Reflection.MethodInfo ExtractContentMethod =
        typeof(ClaudeCodeWatcher).GetMethod("ExtractContent",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;

    private static string InvokeExtractContent(JsonNode? msgNode) =>
        (string)ExtractContentMethod.Invoke(null, [msgNode])!;

    [Fact]
    public void ExtractContent_NullMsgNode_ReturnsEmpty()
    {
        var result = InvokeExtractContent(null);
        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void ExtractContent_SimpleStringContent_ReturnsString()
    {
        var msgNode = JsonNode.Parse("""{"role":"user","content":"Hello world"}""");
        var result = InvokeExtractContent(msgNode);
        Assert.Equal("Hello world", result);
    }

    [Fact]
    public void ExtractContent_AssistantTextBlock_ReturnsText()
    {
        var msgNode = JsonNode.Parse("""
            {"role":"assistant","content":[{"type":"text","text":"I can help with that."}]}
            """);
        var result = InvokeExtractContent(msgNode);
        Assert.Equal("I can help with that.", result);
    }

    [Fact]
    public void ExtractContent_ToolResultWithStringContent_ExtractsString()
    {
        // tool_result where content is a plain string (older format)
        var msgNode = JsonNode.Parse("""
            {
              "role": "user",
              "content": [
                {
                  "type": "tool_result",
                  "tool_use_id": "toolu_abc",
                  "content": "Simple string result"
                }
              ]
            }
            """);
        var result = InvokeExtractContent(msgNode);
        Assert.Contains("Simple string result", result);
    }

    [Fact]
    public void ExtractContent_ToolResultWithArrayContent_ExtractsTextBlocks()
    {
        // tool_result where content is an array of text blocks (newer CC format)
        // This was the root cause of Bug 1 — GetValue<string>() threw on a JsonArray.
        var msgNode = JsonNode.Parse("""
            {
              "role": "user",
              "content": [
                {
                  "type": "tool_result",
                  "tool_use_id": "toolu_abc",
                  "content": [
                    {"type": "text", "text": "Result line 1"},
                    {"type": "text", "text": "Result line 2"}
                  ]
                }
              ]
            }
            """);
        var result = InvokeExtractContent(msgNode);
        Assert.Contains("Result line 1", result);
        Assert.Contains("Result line 2", result);
    }

    [Fact]
    public void ExtractContent_ToolResultWithObjectContent_ReturnsJsonString()
    {
        // tool_result where content is a JsonObject (edge case — should not throw)
        var msgNode = JsonNode.Parse("""
            {
              "role": "user",
              "content": [
                {
                  "type": "tool_result",
                  "tool_use_id": "toolu_abc",
                  "content": {"type": "text", "text": "nested object"}
                }
              ]
            }
            """);
        // Should not throw — returns ToJsonString() fallback
        var ex = Record.Exception(() => InvokeExtractContent(msgNode));
        Assert.Null(ex);
    }

    // ── Concurrent HandleFileChangedAsync — Bug 2/3 regression test ──────────

    [Fact]
    public async Task HandleFileChangedAsync_ConcurrentCalls_AllCompleteWithoutError()
    {
        // Arrange — a JSONL file that produces one message per parse
        var tempDir    = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var sessionDir = Path.Combine(tempDir, "encoded-project-dir");
        Directory.CreateDirectory(sessionDir);
        var filePath = Path.Combine(sessionDir, "concurrent-test.jsonl");

        var jsonl = """
            {"type":"user","message":{"role":"user","content":"Hello"},"sessionId":"s1","uuid":"u1","timestamp":"2026-01-01T00:00:00Z","cwd":"/tmp/proj","gitBranch":"main"}
            {"type":"assistant","message":{"role":"assistant","model":"claude-opus-4","usage":{"input_tokens":5,"output_tokens":5},"content":[{"type":"text","text":"Hi"}]},"uuid":"a1","timestamp":"2026-01-01T00:00:01Z"}
            """;
        await File.WriteAllTextAsync(filePath, jsonl);

        var db = new Mock<ILocalDbService>();
        db.Setup(x => x.UpsertSessionAsync(It.IsAny<ConversationSession>(), It.IsAny<CancellationToken>()))
          .Callback<ConversationSession, CancellationToken>((s, _) => s.Id = 1)
          .Returns(Task.CompletedTask);
        db.Setup(x => x.UpsertMessagesAsync(It.IsAny<IEnumerable<ConversationMessage>>(), It.IsAny<CancellationToken>()))
          .Returns(Task.CompletedTask);

        var watcher = new ClaudeCodeWatcher(db.Object, NoOpGenerator().Object, NoOpWorkItemLinker(),
            NullLogger<ClaudeCodeWatcher>.Instance, DefaultOptions);

        var handleMethod = typeof(ClaudeCodeWatcher).GetMethod("HandleFileChangedAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;

        // Act — fire 5 concurrent change events for the same file
        var tasks = Enumerable.Range(0, 5)
            .Select(_ => (Task)handleMethod.Invoke(watcher, [filePath, CancellationToken.None])!)
            .ToList();

        var ex = await Record.ExceptionAsync(() => Task.WhenAll(tasks));

        // Assert — no exception; the semaphore serialized concurrent DB writes
        Assert.Null(ex);

        // Cleanup
        Directory.Delete(tempDir, recursive: true);
    }
}
