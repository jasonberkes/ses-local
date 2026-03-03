using System.IO.Compression;
using System.Text;
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
/// Unit tests for ChatGptExportParser.
/// Tests cover: ZIP extraction, message tree flattening, role mapping,
/// content extraction, deduplication hash, error handling, and edge cases.
/// </summary>
public sealed class ChatGptExportParserTests : IAsyncDisposable
{
    private static IOptions<SesLocalOptions> DefaultOptions =>
        Options.Create(new SesLocalOptions());

    // ── Sample JSON ───────────────────────────────────────────────────────────

    /// <summary>
    /// Two-turn conversation: user → assistant.
    /// mapping: root(system) → user_node → assistant_node.
    /// current_node points to the assistant_node.
    /// </summary>
    private const string SampleConversationsJson = """
        [
          {
            "id": "chatgpt-conv-001",
            "title": "Python Help",
            "create_time": 1700000000.0,
            "update_time": 1700001000.0,
            "current_node": "node-c",
            "mapping": {
              "node-a": {
                "id": "node-a",
                "parent": null,
                "children": ["node-b"],
                "message": {
                  "id": "node-a",
                  "author": {"role": "system"},
                  "create_time": 1700000000.0,
                  "content": {"content_type": "text", "parts": [""]}
                }
              },
              "node-b": {
                "id": "node-b",
                "parent": "node-a",
                "children": ["node-c"],
                "message": {
                  "id": "node-b",
                  "author": {"role": "user"},
                  "create_time": 1700000001.0,
                  "content": {"content_type": "text", "parts": ["How do I sort a list in Python?"]}
                }
              },
              "node-c": {
                "id": "node-c",
                "parent": "node-b",
                "children": [],
                "message": {
                  "id": "node-c",
                  "author": {"role": "assistant"},
                  "create_time": 1700000002.0,
                  "content": {"content_type": "text", "parts": ["Use list.sort() or sorted(list)."]}
                }
              }
            }
          },
          {
            "id": "chatgpt-conv-002",
            "title": "Math Question",
            "create_time": 1700005000.0,
            "update_time": 1700006000.0,
            "current_node": "node-y",
            "mapping": {
              "node-x": {
                "id": "node-x",
                "parent": null,
                "children": ["node-y"],
                "message": {
                  "id": "node-x",
                  "author": {"role": "user"},
                  "create_time": 1700005000.0,
                  "content": {"content_type": "text", "parts": ["What is 2+2?"]}
                }
              },
              "node-y": {
                "id": "node-y",
                "parent": "node-x",
                "children": [],
                "message": {
                  "id": "node-y",
                  "author": {"role": "assistant"},
                  "create_time": 1700005001.0,
                  "content": {"content_type": "text", "parts": ["2+2 = 4"]}
                }
              }
            }
          }
        ]
        """;

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static async Task<string> WriteJsonFileAsync(string json)
    {
        var path = Path.GetTempFileName() + ".json";
        await File.WriteAllTextAsync(path, json, Encoding.UTF8);
        return path;
    }

    private static async Task<string> WriteZipFileAsync(string json, string entryName = "conversations.json")
    {
        var zipPath = Path.GetTempFileName() + ".zip";
        await using var zipStream = new FileStream(zipPath, FileMode.Create);
        using var archive = new ZipArchive(zipStream, ZipArchiveMode.Create);
        var entry = archive.CreateEntry(entryName);
        await using var entryStream = entry.Open();
        await using var writer = new StreamWriter(entryStream);
        await writer.WriteAsync(json);
        return zipPath;
    }

    private static (Mock<ILocalDbService> db, List<ConversationSession> sessions, List<ConversationMessage> messages)
        CreateCapturingMock()
    {
        var sessions = new List<ConversationSession>();
        var messages = new List<ConversationMessage>();
        var db = new Mock<ILocalDbService>();

        db.Setup(x => x.UpsertSessionAsync(It.IsAny<ConversationSession>(), It.IsAny<CancellationToken>()))
          .Callback<ConversationSession, CancellationToken>((s, _) =>
          {
              s.Id = sessions.Count + 1;
              sessions.Add(s);
          })
          .Returns(Task.CompletedTask);

        db.Setup(x => x.UpsertMessagesAsync(It.IsAny<IEnumerable<ConversationMessage>>(), It.IsAny<CancellationToken>()))
          .Callback<IEnumerable<ConversationMessage>, CancellationToken>((msgs, _) => messages.AddRange(msgs))
          .Returns(Task.CompletedTask);

        return (db, sessions, messages);
    }

    // ── Tests: Basic import ───────────────────────────────────────────────────

    [Fact]
    public async Task ImportAsync_JsonFile_ImportsAllConversations()
    {
        var (db, sessions, _) = CreateCapturingMock();
        var parser = new ChatGptExportParser(db.Object, NullLogger<ChatGptExportParser>.Instance, DefaultOptions);

        var path = await WriteJsonFileAsync(SampleConversationsJson);
        try
        {
            var result = await parser.ImportAsync(path);

            Assert.Equal(2, result.SessionsImported);
            Assert.Equal(0, result.Errors);
            Assert.Contains(sessions, s => s.ExternalId == "chatgpt-conv-001");
            Assert.Contains(sessions, s => s.ExternalId == "chatgpt-conv-002");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task ImportAsync_SetsSourceToChatGpt()
    {
        var (db, sessions, _) = CreateCapturingMock();
        var parser = new ChatGptExportParser(db.Object, NullLogger<ChatGptExportParser>.Instance, DefaultOptions);

        var path = await WriteJsonFileAsync(SampleConversationsJson);
        try
        {
            await parser.ImportAsync(path);
            Assert.All(sessions, s => Assert.Equal(ConversationSource.ChatGpt, s.Source));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task ImportAsync_SetsCorrectTitle()
    {
        var (db, sessions, _) = CreateCapturingMock();
        var parser = new ChatGptExportParser(db.Object, NullLogger<ChatGptExportParser>.Instance, DefaultOptions);

        var path = await WriteJsonFileAsync(SampleConversationsJson);
        try
        {
            await parser.ImportAsync(path);

            var conv1 = sessions.First(s => s.ExternalId == "chatgpt-conv-001");
            Assert.Equal("Python Help", conv1.Title);
        }
        finally { File.Delete(path); }
    }

    // ── Tests: ZIP import ─────────────────────────────────────────────────────

    [Fact]
    public async Task ImportAsync_ZipFile_ExtractsAndImports()
    {
        var (db, sessions, _) = CreateCapturingMock();
        var parser = new ChatGptExportParser(db.Object, NullLogger<ChatGptExportParser>.Instance, DefaultOptions);

        var zipPath = await WriteZipFileAsync(SampleConversationsJson);
        try
        {
            var result = await parser.ImportAsync(zipPath);

            Assert.Equal(2, result.SessionsImported);
            Assert.Equal(0, result.Errors);
        }
        finally { File.Delete(zipPath); }
    }

    [Fact]
    public async Task ImportAsync_ZipMissingConversationsJson_ReturnsError()
    {
        var db = new Mock<ILocalDbService>();
        var parser = new ChatGptExportParser(db.Object, NullLogger<ChatGptExportParser>.Instance, DefaultOptions);

        var zipPath = await WriteZipFileAsync("{}", entryName: "other_file.json");
        try
        {
            var result = await parser.ImportAsync(zipPath);
            Assert.True(result.Errors > 0);
        }
        finally { File.Delete(zipPath); }
    }

    // ── Tests: Message tree flattening ────────────────────────────────────────

    [Fact]
    public async Task ImportAsync_FlattensTreeInChronologicalOrder()
    {
        var (db, _, messages) = CreateCapturingMock();
        var parser = new ChatGptExportParser(db.Object, NullLogger<ChatGptExportParser>.Instance, DefaultOptions);

        var path = await WriteJsonFileAsync(SampleConversationsJson);
        try
        {
            await parser.ImportAsync(path);

            // conv-001: user then assistant
            var conv1Messages = messages.Take(2).ToList();
            Assert.Equal("user", conv1Messages[0].Role);
            Assert.Equal("assistant", conv1Messages[1].Role);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task ImportAsync_SkipsSystemMessages()
    {
        var (db, _, messages) = CreateCapturingMock();
        var parser = new ChatGptExportParser(db.Object, NullLogger<ChatGptExportParser>.Instance, DefaultOptions);

        var path = await WriteJsonFileAsync(SampleConversationsJson);
        try
        {
            await parser.ImportAsync(path);

            // conv-001 has system + user + assistant — system should be excluded
            Assert.DoesNotContain(messages, m => m.Role == "system");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task ImportAsync_ExtractsTextFromParts()
    {
        var (db, _, messages) = CreateCapturingMock();
        var parser = new ChatGptExportParser(db.Object, NullLogger<ChatGptExportParser>.Instance, DefaultOptions);

        var path = await WriteJsonFileAsync(SampleConversationsJson);
        try
        {
            await parser.ImportAsync(path);

            var userMsg = messages.FirstOrDefault(m =>
                m.Role == "user" && m.Content.Contains("sort a list"));
            Assert.NotNull(userMsg);

            var assistantMsg = messages.FirstOrDefault(m =>
                m.Role == "assistant" && m.Content.Contains("sorted"));
            Assert.NotNull(assistantMsg);
        }
        finally { File.Delete(path); }
    }

    // ── Tests: Content extraction ─────────────────────────────────────────────

    [Fact]
    public void ExtractContent_ReturnsStringParts()
    {
        var content = new GptContent
        {
            ContentType = "text",
            Parts = [System.Text.Json.JsonDocument.Parse("\"Hello world\"").RootElement]
        };
        var msg = new GptMessage { Content = content };
        var result = ChatGptExportParser.ExtractContent(msg);
        Assert.Equal("Hello world", result);
    }

    [Fact]
    public void ExtractContent_JoinsMultipleParts()
    {
        var content = new GptContent
        {
            ContentType = "text",
            Parts =
            [
                System.Text.Json.JsonDocument.Parse("\"Part one\"").RootElement,
                System.Text.Json.JsonDocument.Parse("\"Part two\"").RootElement
            ]
        };
        var msg = new GptMessage { Content = content };
        var result = ChatGptExportParser.ExtractContent(msg);
        Assert.Equal("Part one\nPart two", result);
    }

    [Fact]
    public void ExtractContent_ReturnsEmpty_WhenNoParts()
    {
        var msg = new GptMessage { Content = new GptContent { Parts = null } };
        var result = ChatGptExportParser.ExtractContent(msg);
        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void ExtractContent_ReturnsEmpty_WhenContentNull()
    {
        var msg = new GptMessage { Content = null };
        var result = ChatGptExportParser.ExtractContent(msg);
        Assert.Equal(string.Empty, result);
    }

    // ── Tests: Unix timestamp conversion ─────────────────────────────────────

    [Fact]
    public void UnixToUtc_ConvertsCorrectly()
    {
        // 1700000000 seconds = 2023-11-14T22:13:20Z
        var dt = ChatGptExportParser.UnixToUtc(1_700_000_000.0);
        Assert.Equal(DateTimeKind.Utc, dt.Kind);
        Assert.Equal(2023, dt.Year);
        Assert.Equal(11, dt.Month);
        Assert.Equal(14, dt.Day);
    }

    [Fact]
    public void UnixToUtc_ReturnsNow_ForZero()
    {
        var before = DateTime.UtcNow.AddSeconds(-1);
        var dt = ChatGptExportParser.UnixToUtc(0);
        Assert.True(dt >= before);
        Assert.Equal(DateTimeKind.Utc, dt.Kind);
    }

    // ── Tests: Hash computation ───────────────────────────────────────────────

    [Fact]
    public void ComputeHash_IsConsistent()
    {
        var h1 = ChatGptExportParser.ComputeHash("id-1", 1234.0, 5);
        var h2 = ChatGptExportParser.ComputeHash("id-1", 1234.0, 5);
        Assert.Equal(h1, h2);
    }

    [Fact]
    public void ComputeHash_DiffersWhenMessageCountChanges()
    {
        var h1 = ChatGptExportParser.ComputeHash("id-1", 1234.0, 5);
        var h2 = ChatGptExportParser.ComputeHash("id-1", 1234.0, 6);
        Assert.NotEqual(h1, h2);
    }

    [Fact]
    public void ComputeHash_Returns16CharHex()
    {
        var hash = ChatGptExportParser.ComputeHash("some-id", 1000.0, 2);
        Assert.Equal(16, hash.Length);
        Assert.True(hash.All(c => char.IsAsciiHexDigit(c)));
    }

    // ── Tests: Error handling ─────────────────────────────────────────────────

    [Fact]
    public async Task ImportAsync_FileNotFound_ThrowsFileNotFoundException()
    {
        var db = new Mock<ILocalDbService>();
        var parser = new ChatGptExportParser(db.Object, NullLogger<ChatGptExportParser>.Instance, DefaultOptions);

        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            parser.ImportAsync("/nonexistent/path/export.json"));
    }

    [Fact]
    public async Task ImportAsync_InvalidJson_ReturnsErrorCount()
    {
        var db = new Mock<ILocalDbService>();
        var parser = new ChatGptExportParser(db.Object, NullLogger<ChatGptExportParser>.Instance, DefaultOptions);

        var path = await WriteJsonFileAsync("not valid json {{}}");
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

        var parser = new ChatGptExportParser(db.Object, NullLogger<ChatGptExportParser>.Instance, DefaultOptions);

        var path = await WriteJsonFileAsync("[]");
        try
        {
            var result = await parser.ImportAsync(path);
            Assert.Equal(0, result.SessionsImported);
            Assert.Equal(0, result.Errors);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task ImportAsync_EmptyId_CountsAsError()
    {
        var json = """
            [
              {
                "id": "",
                "title": "No ID",
                "create_time": 1700000000.0,
                "update_time": 1700000001.0,
                "current_node": null,
                "mapping": {}
              }
            ]
            """;

        var db = new Mock<ILocalDbService>();
        var parser = new ChatGptExportParser(db.Object, NullLogger<ChatGptExportParser>.Instance, DefaultOptions);

        var path = await WriteJsonFileAsync(json);
        try
        {
            var result = await parser.ImportAsync(path);
            Assert.Equal(1, result.Errors);
        }
        finally { File.Delete(path); }
    }

    // ── Tests: Empty title fallback ───────────────────────────────────────────

    [Fact]
    public async Task ImportAsync_EmptyTitle_FallsBackToIdPrefix()
    {
        var json = """
            [
              {
                "id": "chatgpt-no-title",
                "title": "",
                "create_time": 1700000000.0,
                "update_time": 1700000001.0,
                "current_node": null,
                "mapping": {}
              }
            ]
            """;

        var (db, sessions, _) = CreateCapturingMock();
        var parser = new ChatGptExportParser(db.Object, NullLogger<ChatGptExportParser>.Instance, DefaultOptions);

        var path = await WriteJsonFileAsync(json);
        try
        {
            await parser.ImportAsync(path);

            var session = sessions.First();
            Assert.StartsWith("chatgpt-", session.Title);
        }
        finally { File.Delete(path); }
    }

    // ── Tests: Progress reporting ─────────────────────────────────────────────

    [Fact]
    public async Task ImportAsync_ReportsProgress_ForEachConversation()
    {
        var (db, _, _) = CreateCapturingMock();
        var parser = new ChatGptExportParser(db.Object, NullLogger<ChatGptExportParser>.Instance, DefaultOptions);

        var reports = new List<ImportProgress>();
        var progress = new Progress<ImportProgress>(p => reports.Add(p));

        var path = await WriteJsonFileAsync(SampleConversationsJson);
        try
        {
            await parser.ImportAsync(path, progress);
            await Task.Delay(50);  // allow async progress callbacks to fire

            Assert.Equal(2, reports.Count);
            Assert.Equal(1, reports[0].Processed);
            Assert.Equal(2, reports[1].Processed);
        }
        finally { File.Delete(path); }
    }

    // ── Tests: ZIP ResolveJsonPathAsync ──────────────────────────────────────

    [Fact]
    public async Task ResolveJsonPath_JsonFile_ReturnsSamePath()
    {
        var path = await WriteJsonFileAsync("[]");
        try
        {
            var (resolved, isTemp) = await ChatGptExportParser.ResolveJsonPathAsync(path, default);
            Assert.Equal(path, resolved);
            Assert.False(isTemp);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task ResolveJsonPath_ZipFile_ExtractsTempFile()
    {
        var zipPath = await WriteZipFileAsync(SampleConversationsJson);
        string? tempPath = null;
        try
        {
            var (resolved, isTemp) = await ChatGptExportParser.ResolveJsonPathAsync(zipPath, default);
            tempPath = resolved;

            Assert.True(isTemp);
            Assert.True(File.Exists(resolved));
            Assert.NotEqual(zipPath, resolved);
        }
        finally
        {
            File.Delete(zipPath);
            if (tempPath is not null) File.Delete(tempPath);
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
