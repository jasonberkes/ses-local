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

public sealed class ClaudeCodeWatcherTests
{
    private static IOptions<SesLocalOptions> DefaultOptions =>
        Options.Create(new SesLocalOptions { EnableClaudeCodeSync = true, PollingIntervalSeconds = 999 });

    [Fact]
    public async Task ExecuteAsync_WhenDisabled_DoesNotStart()
    {
        var db      = new Mock<ILocalDbService>();
        var options = Options.Create(new SesLocalOptions { EnableClaudeCodeSync = false });
        var watcher = new ClaudeCodeWatcher(db.Object, NullLogger<ClaudeCodeWatcher>.Instance, options);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await watcher.StartAsync(cts.Token);

        db.Verify(x => x.UpsertSessionAsync(It.IsAny<ConversationSession>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_WhenProjectsDirMissing_DoesNotThrow()
    {
        var db      = new Mock<ILocalDbService>();
        var watcher = new ClaudeCodeWatcher(db.Object, NullLogger<ClaudeCodeWatcher>.Instance, DefaultOptions);

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

        var watcher = new ClaudeCodeWatcher(db.Object, NullLogger<ClaudeCodeWatcher>.Instance, DefaultOptions);

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
}
