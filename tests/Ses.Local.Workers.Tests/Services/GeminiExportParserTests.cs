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
/// Unit tests for GeminiExportParser.
/// Tests cover: ZIP extraction (Google Takeout), activity parsing, message creation,
/// deduplication ID generation, error handling, and filter options.
/// </summary>
public sealed class GeminiExportParserTests : IAsyncDisposable
{
    private static IOptions<SesLocalOptions> DefaultOptions =>
        Options.Create(new SesLocalOptions());

    // ── Sample JSON ───────────────────────────────────────────────────────────

    private const string SampleActivityJson = """
        [
          {
            "header": "Gemini Apps",
            "title": "Used Gemini Apps",
            "time": "2024-03-01T10:00:00.000Z",
            "products": ["Gemini Apps"],
            "subtitles": [
              {"name": "Prompt", "value": "What is the capital of France?"},
              {"name": "Response", "value": "The capital of France is Paris."}
            ]
          },
          {
            "header": "Gemini Apps",
            "title": "Used Gemini Apps",
            "time": "2024-03-02T09:00:00.000Z",
            "products": ["Gemini Apps"],
            "subtitles": [
              {"name": "Prompt", "value": "Write a haiku about spring."},
              {"name": "Response", "value": "Cherry blossoms fall,\nPetals dance in gentle breeze,\nSpring has come again."}
            ]
          }
        ]
        """;

    private const string SampleActivityNoResponse = """
        [
          {
            "header": "Gemini Apps",
            "title": "Used Gemini Apps",
            "time": "2024-03-03T08:00:00.000Z",
            "subtitles": [
              {"name": "Prompt", "value": "Hello Gemini"}
            ]
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

    private static async Task<string> WriteTakeoutZipAsync(
        string json,
        string entryPath = "Takeout/Gemini Apps Activity/My Activity.json")
    {
        var zipPath = Path.GetTempFileName() + ".zip";
        await using var zipStream = new FileStream(zipPath, FileMode.Create);
        using var archive = new ZipArchive(zipStream, ZipArchiveMode.Create);
        var entry = archive.CreateEntry(entryPath);
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
    public async Task ImportAsync_JsonFile_ImportsAllEntries()
    {
        var (db, sessions, _) = CreateCapturingMock();
        var parser = new GeminiExportParser(db.Object, NullLogger<GeminiExportParser>.Instance, DefaultOptions);

        var path = await WriteJsonFileAsync(SampleActivityJson);
        try
        {
            var result = await parser.ImportAsync(path);

            Assert.Equal(2, result.SessionsImported);
            Assert.Equal(0, result.Errors);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task ImportAsync_SetsSourceToGemini()
    {
        var (db, sessions, _) = CreateCapturingMock();
        var parser = new GeminiExportParser(db.Object, NullLogger<GeminiExportParser>.Instance, DefaultOptions);

        var path = await WriteJsonFileAsync(SampleActivityJson);
        try
        {
            await parser.ImportAsync(path);
            Assert.All(sessions, s => Assert.Equal(ConversationSource.Gemini, s.Source));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task ImportAsync_TitleIsPromptPrefix()
    {
        var (db, sessions, _) = CreateCapturingMock();
        var parser = new GeminiExportParser(db.Object, NullLogger<GeminiExportParser>.Instance, DefaultOptions);

        var path = await WriteJsonFileAsync(SampleActivityJson);
        try
        {
            await parser.ImportAsync(path);

            var first = sessions.First();
            Assert.Contains("capital of France", first.Title);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task ImportAsync_CreatesUserAndAssistantMessages()
    {
        var (db, _, messages) = CreateCapturingMock();
        var parser = new GeminiExportParser(db.Object, NullLogger<GeminiExportParser>.Instance, DefaultOptions);

        var path = await WriteJsonFileAsync(SampleActivityJson);
        try
        {
            await parser.ImportAsync(path);

            // Each entry creates 1 user + 1 assistant message
            Assert.Equal(4, messages.Count);
            Assert.Equal(2, messages.Count(m => m.Role == "user"));
            Assert.Equal(2, messages.Count(m => m.Role == "assistant"));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task ImportAsync_UserMessageContainsPromptText()
    {
        var (db, _, messages) = CreateCapturingMock();
        var parser = new GeminiExportParser(db.Object, NullLogger<GeminiExportParser>.Instance, DefaultOptions);

        var path = await WriteJsonFileAsync(SampleActivityJson);
        try
        {
            await parser.ImportAsync(path);

            var prompt = messages.FirstOrDefault(m =>
                m.Role == "user" && m.Content.Contains("capital of France"));
            Assert.NotNull(prompt);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task ImportAsync_AssistantMessageContainsResponse()
    {
        var (db, _, messages) = CreateCapturingMock();
        var parser = new GeminiExportParser(db.Object, NullLogger<GeminiExportParser>.Instance, DefaultOptions);

        var path = await WriteJsonFileAsync(SampleActivityJson);
        try
        {
            await parser.ImportAsync(path);

            var response = messages.FirstOrDefault(m =>
                m.Role == "assistant" && m.Content.Contains("Paris"));
            Assert.NotNull(response);
        }
        finally { File.Delete(path); }
    }

    // ── Tests: Prompt-only entries ────────────────────────────────────────────

    [Fact]
    public async Task ImportAsync_PromptOnly_CreatesOneMessage()
    {
        var (db, sessions, messages) = CreateCapturingMock();
        var parser = new GeminiExportParser(db.Object, NullLogger<GeminiExportParser>.Instance, DefaultOptions);

        var path = await WriteJsonFileAsync(SampleActivityNoResponse);
        try
        {
            await parser.ImportAsync(path);

            Assert.Single(sessions);
            Assert.Single(messages);
            Assert.Equal("user", messages[0].Role);
        }
        finally { File.Delete(path); }
    }

    // ── Tests: ZIP import ─────────────────────────────────────────────────────

    [Fact]
    public async Task ImportAsync_TakeoutZip_ExtractsAndImports()
    {
        var (db, sessions, _) = CreateCapturingMock();
        var parser = new GeminiExportParser(db.Object, NullLogger<GeminiExportParser>.Instance, DefaultOptions);

        var zipPath = await WriteTakeoutZipAsync(SampleActivityJson);
        try
        {
            var result = await parser.ImportAsync(zipPath);

            Assert.Equal(2, result.SessionsImported);
            Assert.Equal(0, result.Errors);
        }
        finally { File.Delete(zipPath); }
    }

    [Fact]
    public async Task ImportAsync_ZipMissingActivityJson_ReturnsError()
    {
        var db = new Mock<ILocalDbService>();
        var parser = new GeminiExportParser(db.Object, NullLogger<GeminiExportParser>.Instance, DefaultOptions);

        var zipPath = await WriteTakeoutZipAsync("{}", entryPath: "Takeout/other/SomeFile.json");
        try
        {
            var result = await parser.ImportAsync(zipPath);
            Assert.True(result.Errors > 0);
        }
        finally { File.Delete(zipPath); }
    }

    // ── Tests: External ID generation ────────────────────────────────────────

    [Fact]
    public void ComputeExternalId_IsDeterministic()
    {
        var entry = new GeminiActivityEntry
        {
            Time = "2024-03-01T10:00:00.000Z",
            Subtitles = [new GeminiSubtitle { Name = "Prompt", Value = "Hello" }]
        };

        var id1 = GeminiExportParser.ComputeExternalId(entry);
        var id2 = GeminiExportParser.ComputeExternalId(entry);

        Assert.Equal(id1, id2);
    }

    [Fact]
    public void ComputeExternalId_DiffersForDifferentPrompts()
    {
        var entry1 = new GeminiActivityEntry
        {
            Time = "2024-03-01T10:00:00.000Z",
            Subtitles = [new GeminiSubtitle { Name = "Prompt", Value = "Hello" }]
        };
        var entry2 = new GeminiActivityEntry
        {
            Time = "2024-03-01T10:00:00.000Z",
            Subtitles = [new GeminiSubtitle { Name = "Prompt", Value = "Goodbye" }]
        };

        Assert.NotEqual(
            GeminiExportParser.ComputeExternalId(entry1),
            GeminiExportParser.ComputeExternalId(entry2));
    }

    [Fact]
    public void ComputeExternalId_StartsWithGeminiPrefix()
    {
        var entry = new GeminiActivityEntry
        {
            Time = "2024-03-01T10:00:00.000Z",
            Subtitles = [new GeminiSubtitle { Name = "Prompt", Value = "Test" }]
        };

        Assert.StartsWith("gemini-", GeminiExportParser.ComputeExternalId(entry));
    }

    // ── Tests: Title building ─────────────────────────────────────────────────

    [Fact]
    public void BuildTitle_UsesPromptText()
    {
        var entry = new GeminiActivityEntry
        {
            Subtitles = [new GeminiSubtitle { Name = "Prompt", Value = "What is 42?" }]
        };
        Assert.Equal("What is 42?", GeminiExportParser.BuildTitle(entry));
    }

    [Fact]
    public void BuildTitle_TruncatesLongPrompt()
    {
        var longPrompt = new string('x', 100);
        var entry = new GeminiActivityEntry
        {
            Subtitles = [new GeminiSubtitle { Name = "Prompt", Value = longPrompt }]
        };
        var title = GeminiExportParser.BuildTitle(entry);
        Assert.True(title.Length <= 82); // 80 + "…"
        Assert.EndsWith("…", title);
    }

    [Fact]
    public void BuildTitle_FallsBackToEntryTitle_WhenNoPrompt()
    {
        var entry = new GeminiActivityEntry { Title = "Fallback Title", Subtitles = null };
        Assert.Equal("Fallback Title", GeminiExportParser.BuildTitle(entry));
    }

    [Fact]
    public void BuildTitle_DefaultsToGenericLabel_WhenNoTitleOrPrompt()
    {
        var entry = new GeminiActivityEntry { Title = null, Subtitles = null };
        Assert.Equal("Gemini conversation", GeminiExportParser.BuildTitle(entry));
    }

    // ── Tests: Time parsing ───────────────────────────────────────────────────

    [Fact]
    public void ParseTime_ParsesIso8601AsUtc()
    {
        var dt = GeminiExportParser.ParseTime("2024-03-01T10:00:00.000Z");
        Assert.Equal(DateTimeKind.Utc, dt.Kind);
        Assert.Equal(2024, dt.Year);
        Assert.Equal(3, dt.Month);
        Assert.Equal(1, dt.Day);
    }

    [Fact]
    public void ParseTime_ReturnsNow_ForNull()
    {
        var before = DateTime.UtcNow.AddSeconds(-1);
        var dt = GeminiExportParser.ParseTime(null);
        Assert.True(dt >= before);
    }

    // ── Tests: Error handling ─────────────────────────────────────────────────

    [Fact]
    public async Task ImportAsync_FileNotFound_ThrowsFileNotFoundException()
    {
        var db = new Mock<ILocalDbService>();
        var parser = new GeminiExportParser(db.Object, NullLogger<GeminiExportParser>.Instance, DefaultOptions);

        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            parser.ImportAsync("/nonexistent/path/activity.json"));
    }

    [Fact]
    public async Task ImportAsync_InvalidJson_ReturnsErrorCount()
    {
        var db = new Mock<ILocalDbService>();
        var parser = new GeminiExportParser(db.Object, NullLogger<GeminiExportParser>.Instance, DefaultOptions);

        var path = await WriteJsonFileAsync("{ not valid json");
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
        var parser = new GeminiExportParser(db.Object, NullLogger<GeminiExportParser>.Instance, DefaultOptions);

        var path = await WriteJsonFileAsync("[]");
        try
        {
            var result = await parser.ImportAsync(path);
            Assert.Equal(0, result.SessionsImported);
            Assert.Equal(0, result.Errors);
        }
        finally { File.Delete(path); }
    }

    // ── Tests: Filter options ─────────────────────────────────────────────────

    [Fact]
    public async Task ImportAsync_FilterExcludeBefore_FiltersOldEntries()
    {
        var (db, sessions, _) = CreateCapturingMock();
        var parser = new GeminiExportParser(db.Object, NullLogger<GeminiExportParser>.Instance, DefaultOptions);

        var filter = new ImportFilterOptions
        {
            ExcludeBefore = new DateTime(2024, 3, 2, 0, 0, 0, DateTimeKind.Utc)
        };

        var path = await WriteJsonFileAsync(SampleActivityJson);
        try
        {
            var result = await parser.ImportAsync(path, importOptions: filter);

            // Only the 2024-03-02 entry should pass
            Assert.Equal(1, result.SessionsImported);
            Assert.Equal(1, result.Filtered);
        }
        finally { File.Delete(path); }
    }

    // ── Tests: Progress reporting ─────────────────────────────────────────────

    [Fact]
    public async Task ImportAsync_ReportsProgress_ForEachEntry()
    {
        var (db, _, _) = CreateCapturingMock();
        var parser = new GeminiExportParser(db.Object, NullLogger<GeminiExportParser>.Instance, DefaultOptions);

        var reports = new List<ImportProgress>();
        var progress = new Progress<ImportProgress>(p => reports.Add(p));

        var path = await WriteJsonFileAsync(SampleActivityJson);
        try
        {
            await parser.ImportAsync(path, progress);
            await Task.Delay(50);

            Assert.Equal(2, reports.Count);
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
            var (resolved, isTemp) = await GeminiExportParser.ResolveJsonPathAsync(path, default);
            Assert.Equal(path, resolved);
            Assert.False(isTemp);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task ResolveJsonPath_TakeoutZip_ExtractsTempFile()
    {
        var zipPath = await WriteTakeoutZipAsync(SampleActivityJson);
        string? tempPath = null;
        try
        {
            var (resolved, isTemp) = await GeminiExportParser.ResolveJsonPathAsync(zipPath, default);
            tempPath = resolved;

            Assert.True(isTemp);
            Assert.True(File.Exists(resolved));
        }
        finally
        {
            File.Delete(zipPath);
            if (tempPath is not null) File.Delete(tempPath);
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
