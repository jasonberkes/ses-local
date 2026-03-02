using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Ses.Local.Core.Enums;
using Ses.Local.Core.Interfaces;
using Ses.Local.Core.Models;
using Ses.Local.Workers.Services;
using Xunit;

namespace Ses.Local.Workers.Tests.Services;

/// <summary>
/// Unit tests for ClaudeExportParser using in-memory sample JSON.
/// Tests cover array-rooted exports, object-rooted exports, deduplication,
/// content extraction, error handling, and edge cases.
/// </summary>
public sealed class ClaudeExportParserTests : IAsyncDisposable
{
    // ── Shared sample JSON ────────────────────────────────────────────────────

    private const string SampleArrayExport = """
        [
          {
            "uuid": "conv-001",
            "name": "Hello World Conversation",
            "created_at": "2024-03-01T10:00:00.000000+00:00",
            "updated_at": "2024-03-01T10:05:00.000000+00:00",
            "chat_messages": [
              {
                "uuid": "msg-001",
                "sender": "human",
                "text": "Hello Claude!",
                "created_at": "2024-03-01T10:00:00.000000+00:00"
              },
              {
                "uuid": "msg-002",
                "sender": "assistant",
                "text": "Hi there! How can I help you today?",
                "created_at": "2024-03-01T10:00:05.000000+00:00"
              }
            ]
          },
          {
            "uuid": "conv-002",
            "name": "Python Help",
            "created_at": "2024-03-02T09:00:00.000000+00:00",
            "updated_at": "2024-03-02T09:30:00.000000+00:00",
            "chat_messages": [
              {
                "uuid": "msg-003",
                "sender": "human",
                "text": "How do I reverse a list in Python?",
                "created_at": "2024-03-02T09:00:00.000000+00:00"
              },
              {
                "uuid": "msg-004",
                "sender": "assistant",
                "text": "",
                "created_at": "2024-03-02T09:00:03.000000+00:00",
                "content": [
                  { "type": "text", "text": "You can reverse a list using `list.reverse()` or `list[::-1]`." }
                ]
              }
            ]
          }
        ]
        """;

    private const string SampleObjectExport = """
        {
          "conversations": [
            {
              "uuid": "conv-obj-001",
              "name": "Object Root Conv",
              "created_at": "2024-04-01T08:00:00.000000+00:00",
              "updated_at": "2024-04-01T08:10:00.000000+00:00",
              "chat_messages": [
                {
                  "uuid": "msg-obj-001",
                  "sender": "human",
                  "text": "What is the capital of France?",
                  "created_at": "2024-04-01T08:00:00.000000+00:00"
                },
                {
                  "uuid": "msg-obj-002",
                  "sender": "assistant",
                  "text": "Paris is the capital of France.",
                  "created_at": "2024-04-01T08:00:02.000000+00:00"
                }
              ]
            }
          ]
        }
        """;

    private const string LargeArrayExport_Template = """
        [
          {
            "uuid": "bulk-conv-{0}",
            "name": "Bulk Conversation {0}",
            "created_at": "2024-01-01T00:00:00.000000+00:00",
            "updated_at": "2024-01-01T00:00:01.000000+00:00",
            "chat_messages": [
              { "uuid": "bulk-msg-{0}-1", "sender": "human", "text": "Message {0}", "created_at": "2024-01-01T00:00:00.000000+00:00" }
            ]
          }
        ]
        """;

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static async Task<string> WriteExportFileAsync(string json)
    {
        var path = Path.GetTempFileName() + ".json";
        await File.WriteAllTextAsync(path, json, Encoding.UTF8);
        return path;
    }

    private static async Task<string> WriteLargeBatchFileAsync(int count)
    {
        var sb = new StringBuilder("[");
        for (int i = 0; i < count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append($$"""
                {
                  "uuid": "bulk-conv-{{i}}",
                  "name": "Bulk Conversation {{i}}",
                  "created_at": "2024-01-01T00:00:00.000000+00:00",
                  "updated_at": "2024-01-01T00:00:01.000000+00:00",
                  "chat_messages": [
                    { "uuid": "bulk-msg-{{i}}", "sender": "human", "text": "Message {{i}}", "created_at": "2024-01-01T00:00:00.000000+00:00" }
                  ]
                }
                """);
        }
        sb.Append(']');
        var path = Path.GetTempFileName() + ".json";
        await File.WriteAllTextAsync(path, sb.ToString(), Encoding.UTF8);
        return path;
    }

    // Captures what was upserted via mocked ILocalDbService
    private static (Mock<ILocalDbService> db, List<ConversationSession> sessions, List<ConversationMessage> messages)
        CreateCapturingMock()
    {
        var sessions = new List<ConversationSession>();
        var messages = new List<ConversationMessage>();
        var db = new Mock<ILocalDbService>();

        db.Setup(x => x.UpsertSessionAsync(It.IsAny<ConversationSession>(), It.IsAny<CancellationToken>()))
          .Callback<ConversationSession, CancellationToken>((s, _) =>
          {
              s.Id = sessions.Count + 1; // simulate DB-assigned Id
              sessions.Add(s);
          })
          .Returns(Task.CompletedTask);

        db.Setup(x => x.UpsertMessagesAsync(It.IsAny<IEnumerable<ConversationMessage>>(), It.IsAny<CancellationToken>()))
          .Callback<IEnumerable<ConversationMessage>, CancellationToken>((msgs, _) => messages.AddRange(msgs))
          .Returns(Task.CompletedTask);

        return (db, sessions, messages);
    }

    // ── Tests: Array-rooted export ────────────────────────────────────────────

    [Fact]
    public async Task ImportAsync_ArrayRoot_ImportsAllConversations()
    {
        var (db, sessions, messages) = CreateCapturingMock();
        var parser = new ClaudeExportParser(db.Object, NullLogger<ClaudeExportParser>.Instance);

        var path = await WriteExportFileAsync(SampleArrayExport);
        try
        {
            var result = await parser.ImportAsync(path);

            Assert.Equal(2, result.SessionsImported);
            Assert.Equal(0, result.Errors);

            Assert.Equal(2, sessions.Count);
            Assert.Contains(sessions, s => s.ExternalId == "conv-001");
            Assert.Contains(sessions, s => s.ExternalId == "conv-002");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task ImportAsync_ArrayRoot_SetsCorrectSource()
    {
        var (db, sessions, _) = CreateCapturingMock();
        var parser = new ClaudeExportParser(db.Object, NullLogger<ClaudeExportParser>.Instance);

        var path = await WriteExportFileAsync(SampleArrayExport);
        try
        {
            await parser.ImportAsync(path);

            Assert.All(sessions, s => Assert.Equal(ConversationSource.ClaudeChat, s.Source));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task ImportAsync_ArrayRoot_SetsCorrectTitle()
    {
        var (db, sessions, _) = CreateCapturingMock();
        var parser = new ClaudeExportParser(db.Object, NullLogger<ClaudeExportParser>.Instance);

        var path = await WriteExportFileAsync(SampleArrayExport);
        try
        {
            await parser.ImportAsync(path);

            var conv1 = sessions.First(s => s.ExternalId == "conv-001");
            Assert.Equal("Hello World Conversation", conv1.Title);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task ImportAsync_ArrayRoot_StoresMessages()
    {
        var (db, _, messages) = CreateCapturingMock();
        var parser = new ClaudeExportParser(db.Object, NullLogger<ClaudeExportParser>.Instance);

        var path = await WriteExportFileAsync(SampleArrayExport);
        try
        {
            var result = await parser.ImportAsync(path);

            Assert.True(result.MessagesImported > 0);

            // conv-001 has 2 messages: user + assistant
            var userMsg = messages.FirstOrDefault(m => m.Role == "user" && m.Content == "Hello Claude!");
            Assert.NotNull(userMsg);

            var assistantMsg = messages.FirstOrDefault(m => m.Role == "assistant" && m.Content.Contains("How can I help"));
            Assert.NotNull(assistantMsg);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task ImportAsync_ArrayRoot_MapsRolesCorrectly()
    {
        var (db, _, messages) = CreateCapturingMock();
        var parser = new ClaudeExportParser(db.Object, NullLogger<ClaudeExportParser>.Instance);

        var path = await WriteExportFileAsync(SampleArrayExport);
        try
        {
            await parser.ImportAsync(path);

            // "human" → "user", "assistant" → "assistant"
            Assert.All(messages.Where(m => m.Content == "Hello Claude!"), m => Assert.Equal("user", m.Role));
            Assert.All(messages.Where(m => m.Content.Contains("How can I help")), m => Assert.Equal("assistant", m.Role));
        }
        finally { File.Delete(path); }
    }

    // ── Tests: Object-rooted export ───────────────────────────────────────────

    [Fact]
    public async Task ImportAsync_ObjectRoot_ImportsConversations()
    {
        var (db, sessions, messages) = CreateCapturingMock();
        var parser = new ClaudeExportParser(db.Object, NullLogger<ClaudeExportParser>.Instance);

        var path = await WriteExportFileAsync(SampleObjectExport);
        try
        {
            var result = await parser.ImportAsync(path);

            Assert.Equal(1, result.SessionsImported);
            Assert.Equal(0, result.Errors);

            Assert.Single(sessions);
            Assert.Equal("conv-obj-001", sessions[0].ExternalId);
            Assert.Equal("Object Root Conv", sessions[0].Title);

            Assert.Equal(2, messages.Count);
        }
        finally { File.Delete(path); }
    }

    // ── Tests: Content extraction ─────────────────────────────────────────────

    [Fact]
    public async Task ImportAsync_ContentArrayFallback_ExtractsTextBlocks()
    {
        // conv-002 assistant has text="" but content=[{type:"text", text:"..."}]
        var (db, _, messages) = CreateCapturingMock();
        var parser = new ClaudeExportParser(db.Object, NullLogger<ClaudeExportParser>.Instance);

        var path = await WriteExportFileAsync(SampleArrayExport);
        try
        {
            await parser.ImportAsync(path);

            var assistantMsg = messages.FirstOrDefault(m =>
                m.Role == "assistant" && m.Content.Contains("reverse a list"));
            Assert.NotNull(assistantMsg);
            Assert.Contains("list.reverse()", assistantMsg!.Content);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ExtractContent_PrefersTextField_WhenNonEmpty()
    {
        var msg = new ExportMessage
        {
            Sender = "assistant",
            Text   = "Direct text",
            Content = [new ExportContentBlock { Type = "text", Text = "Block text" }]
        };

        var result = ClaudeExportParser.ExtractContent(msg);

        Assert.Equal("Direct text", result);
    }

    [Fact]
    public void ExtractContent_FallsBackToContentArray_WhenTextEmpty()
    {
        var msg = new ExportMessage
        {
            Sender = "assistant",
            Text   = "",
            Content = [new ExportContentBlock { Type = "text", Text = "Block text" }]
        };

        var result = ClaudeExportParser.ExtractContent(msg);

        Assert.Equal("Block text", result);
    }

    [Fact]
    public void ExtractContent_JoinsMultipleTextBlocks()
    {
        var msg = new ExportMessage
        {
            Sender = "assistant",
            Text   = "",
            Content =
            [
                new ExportContentBlock { Type = "text", Text = "Part one." },
                new ExportContentBlock { Type = "text", Text = "Part two." }
            ]
        };

        var result = ClaudeExportParser.ExtractContent(msg);

        Assert.Equal("Part one.\nPart two.", result);
    }

    [Fact]
    public void ExtractContent_IgnoresNonTextBlocks()
    {
        var msg = new ExportMessage
        {
            Sender = "assistant",
            Text   = "",
            Content =
            [
                new ExportContentBlock { Type = "image", Text = null },
                new ExportContentBlock { Type = "text", Text  = "Only this" }
            ]
        };

        var result = ClaudeExportParser.ExtractContent(msg);

        Assert.Equal("Only this", result);
    }

    [Fact]
    public void ExtractContent_ReturnsEmpty_WhenBothTextAndContentAreEmpty()
    {
        var msg = new ExportMessage { Sender = "human", Text = "", Content = null };

        var result = ClaudeExportParser.ExtractContent(msg);

        Assert.Equal(string.Empty, result);
    }

    // ── Tests: Format detection ───────────────────────────────────────────────

    [Fact]
    public async Task DetectFormat_ReturnsArray_ForArrayRootedFile()
    {
        var path = await WriteExportFileAsync(SampleArrayExport);
        try
        {
            var format = ClaudeExportParser.DetectFormat(path);
            Assert.Equal(ClaudeExportParser.ExportFormat.Array, format);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task DetectFormat_ReturnsObject_ForObjectRootedFile()
    {
        var path = await WriteExportFileAsync(SampleObjectExport);
        try
        {
            var format = ClaudeExportParser.DetectFormat(path);
            Assert.Equal(ClaudeExportParser.ExportFormat.Object, format);
        }
        finally { File.Delete(path); }
    }

    // ── Tests: ContentHash ───────────────────────────────────────────────────

    [Fact]
    public void ComputeHash_IsConsistent_ForSameInput()
    {
        var conv = new ExportConversation
        {
            Uuid        = "conv-hash-test",
            UpdatedAt   = "2024-01-01T00:00:00Z",
            ChatMessages = [new ExportMessage { Text = "hi" }]
        };

        var hash1 = ClaudeExportParser.ComputeHash(conv);
        var hash2 = ClaudeExportParser.ComputeHash(conv);

        Assert.Equal(hash1, hash2);
    }

    [Fact]
    public void ComputeHash_IsDifferent_WhenMessageCountChanges()
    {
        var conv1 = new ExportConversation
        {
            Uuid      = "same-uuid",
            UpdatedAt = "2024-01-01T00:00:00Z",
            ChatMessages = [new ExportMessage { Text = "msg1" }]
        };
        var conv2 = new ExportConversation
        {
            Uuid      = "same-uuid",
            UpdatedAt = "2024-01-01T00:00:00Z",
            ChatMessages = [new ExportMessage { Text = "msg1" }, new ExportMessage { Text = "msg2" }]
        };

        Assert.NotEqual(ClaudeExportParser.ComputeHash(conv1), ClaudeExportParser.ComputeHash(conv2));
    }

    [Fact]
    public void ComputeHash_Returns16CharHexString()
    {
        var conv = new ExportConversation
        {
            Uuid      = "conv-hash-len",
            UpdatedAt = "2024-01-01T00:00:00Z",
            ChatMessages = []
        };

        var hash = ClaudeExportParser.ComputeHash(conv);

        Assert.Equal(16, hash.Length);
        Assert.True(hash.All(c => char.IsAsciiHexDigit(c)));
    }

    // ── Tests: Error handling ─────────────────────────────────────────────────

    [Fact]
    public async Task ImportAsync_FileNotFound_ThrowsFileNotFoundException()
    {
        var db = new Mock<ILocalDbService>();
        var parser = new ClaudeExportParser(db.Object, NullLogger<ClaudeExportParser>.Instance);

        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            parser.ImportAsync("/nonexistent/path/export.json"));
    }

    [Fact]
    public async Task ImportAsync_InvalidJson_ReturnsErrorCount()
    {
        var db = new Mock<ILocalDbService>();
        var parser = new ClaudeExportParser(db.Object, NullLogger<ClaudeExportParser>.Instance);

        var path = await WriteExportFileAsync("{ this is not valid json }}}}");
        try
        {
            var result = await parser.ImportAsync(path);

            Assert.True(result.Errors > 0);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task ImportAsync_EmptyArray_ReturnsZeroCounts()
    {
        var db = new Mock<ILocalDbService>();
        db.Setup(x => x.UpsertSessionAsync(It.IsAny<ConversationSession>(), It.IsAny<CancellationToken>()))
          .Returns(Task.CompletedTask);

        var parser = new ClaudeExportParser(db.Object, NullLogger<ClaudeExportParser>.Instance);

        var path = await WriteExportFileAsync("[]");
        try
        {
            var result = await parser.ImportAsync(path);

            Assert.Equal(0, result.SessionsImported);
            Assert.Equal(0, result.MessagesImported);
            Assert.Equal(0, result.Errors);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task ImportAsync_ConversationWithEmptyUuid_CountsAsError()
    {
        var json = """
            [
              {
                "uuid": "",
                "name": "Bad conversation",
                "created_at": "2024-01-01T00:00:00Z",
                "updated_at": "2024-01-01T00:00:00Z",
                "chat_messages": []
              }
            ]
            """;

        var db = new Mock<ILocalDbService>();
        var parser = new ClaudeExportParser(db.Object, NullLogger<ClaudeExportParser>.Instance);

        var path = await WriteExportFileAsync(json);
        try
        {
            var result = await parser.ImportAsync(path);

            Assert.Equal(1, result.Errors);
            Assert.Equal(0, result.SessionsImported);
        }
        finally { File.Delete(path); }
    }

    // ── Tests: Progress reporting ─────────────────────────────────────────────

    [Fact]
    public async Task ImportAsync_ReportsProgress_ForEachConversation()
    {
        var (db, _, _) = CreateCapturingMock();
        var parser = new ClaudeExportParser(db.Object, NullLogger<ClaudeExportParser>.Instance);

        var reports = new List<ImportProgress>();
        var progress = new Progress<ImportProgress>(p => reports.Add(p));

        var path = await WriteExportFileAsync(SampleArrayExport);
        try
        {
            await parser.ImportAsync(path, progress);

            // Allow progress callbacks to fire (Progress<T> dispatches async)
            await Task.Delay(50);

            Assert.Equal(2, reports.Count);
            Assert.Equal(1, reports[0].Processed);
            Assert.Equal(2, reports[1].Processed);
        }
        finally { File.Delete(path); }
    }

    // ── Tests: Batch / large file processing ─────────────────────────────────

    [Fact]
    public async Task ImportAsync_LargeBatch_ProcessesAllConversations()
    {
        const int count = 100;

        var (db, sessions, _) = CreateCapturingMock();
        var parser = new ClaudeExportParser(db.Object, NullLogger<ClaudeExportParser>.Instance);

        var path = await WriteLargeBatchFileAsync(count);
        try
        {
            var result = await parser.ImportAsync(path);

            Assert.Equal(count, result.SessionsImported);
            Assert.Equal(count, sessions.Count);
            Assert.Equal(0, result.Errors);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task ImportAsync_Cancellation_StopsProcessing()
    {
        using var cts = new CancellationTokenSource();

        // Cancel after the first session is processed
        int callCount = 0;
        var db = new Mock<ILocalDbService>();
        db.Setup(x => x.UpsertSessionAsync(It.IsAny<ConversationSession>(), It.IsAny<CancellationToken>()))
          .Callback<ConversationSession, CancellationToken>((s, _) =>
          {
              s.Id = ++callCount;
              if (callCount >= 1) cts.Cancel();
          })
          .Returns(Task.CompletedTask);
        db.Setup(x => x.UpsertMessagesAsync(It.IsAny<IEnumerable<ConversationMessage>>(), It.IsAny<CancellationToken>()))
          .Returns(Task.CompletedTask);

        var parser = new ClaudeExportParser(db.Object, NullLogger<ClaudeExportParser>.Instance);

        var path = await WriteLargeBatchFileAsync(50);
        try
        {
            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                parser.ImportAsync(path, ct: cts.Token));
        }
        finally { File.Delete(path); }
    }

    // ── Tests: Timestamps ────────────────────────────────────────────────────

    [Fact]
    public async Task ImportAsync_ParsesDates_AsUtc()
    {
        var (db, sessions, _) = CreateCapturingMock();
        var parser = new ClaudeExportParser(db.Object, NullLogger<ClaudeExportParser>.Instance);

        var path = await WriteExportFileAsync(SampleArrayExport);
        try
        {
            await parser.ImportAsync(path);

            var conv = sessions.First(s => s.ExternalId == "conv-001");
            Assert.Equal(DateTimeKind.Utc, conv.CreatedAt.Kind);
            Assert.Equal(new DateTime(2024, 3, 1, 10, 0, 0, DateTimeKind.Utc), conv.CreatedAt);
        }
        finally { File.Delete(path); }
    }

    // ── Tests: Missing optional name ─────────────────────────────────────────

    [Fact]
    public async Task ImportAsync_EmptyTitle_FallsBackToUuidPrefix()
    {
        var json = """
            [
              {
                "uuid": "conv-no-title-abc",
                "name": "",
                "created_at": "2024-01-01T00:00:00Z",
                "updated_at": "2024-01-01T00:00:01Z",
                "chat_messages": [
                  { "uuid": "m1", "sender": "human", "text": "Hello", "created_at": "2024-01-01T00:00:00Z" }
                ]
              }
            ]
            """;

        var (db, sessions, _) = CreateCapturingMock();
        var parser = new ClaudeExportParser(db.Object, NullLogger<ClaudeExportParser>.Instance);

        var path = await WriteExportFileAsync(json);
        try
        {
            await parser.ImportAsync(path);

            var session = sessions.First();
            // Falls back to first 8 chars of UUID
            Assert.Equal("conv-no-", session.Title);
        }
        finally { File.Delete(path); }
    }

    // ── IAsyncDisposable ─────────────────────────────────────────────────────

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
