using Microsoft.Extensions.Logging.Abstractions;
using Ses.Local.Core.Enums;
using Ses.Local.Core.Models;
using Ses.Local.Workers.Services;
using Xunit;

namespace Ses.Local.Workers.Tests.Services;

/// <summary>
/// Unit tests for <see cref="RuleBasedCompressor"/>.
/// All inputs are pure in-memory objects — no DB, no network.
/// </summary>
public sealed class RuleBasedCompressorTests
{
    private readonly RuleBasedCompressor _sut = new(NullLogger<RuleBasedCompressor>.Instance);

    // ── Category heuristics ───────────────────────────────────────────────────

    [Fact]
    public async Task Category_IsBugfix_WhenGitCommitContainsFix()
    {
        var observations = new[]
        {
            GitCommit("fix: null reference in payment processor")
        };

        var summary = await _sut.CompressAsync(1, observations);

        Assert.Equal("bugfix", summary.Category);
    }

    [Fact]
    public async Task Category_IsBugfix_WhenGitCommitContainsBug()
    {
        var observations = new[] { GitCommit("bug: race condition in cache layer") };
        var summary = await _sut.CompressAsync(1, observations);
        Assert.Equal("bugfix", summary.Category);
    }

    [Fact]
    public async Task Category_IsFeature_WhenGitCommitContainsFeat()
    {
        var observations = new[] { GitCommit("feat: add user authentication") };
        var summary = await _sut.CompressAsync(1, observations);
        Assert.Equal("feature", summary.Category);
    }

    [Fact]
    public async Task Category_IsFeature_WhenGitCommitContainsAdd()
    {
        var observations = new[] { GitCommit("add compression pipeline") };
        var summary = await _sut.CompressAsync(1, observations);
        Assert.Equal("feature", summary.Category);
    }

    [Fact]
    public async Task Category_IsRefactor_WhenGitCommitContainsRefactor()
    {
        var observations = new[] { GitCommit("refactor: extract service layer") };
        var summary = await _sut.CompressAsync(1, observations);
        Assert.Equal("refactor", summary.Category);
    }

    [Fact]
    public async Task Category_IsDiscovery_WhenOnlyReadToolsUsed()
    {
        var observations = new[]
        {
            ToolUse("Read", "/src/Foo.cs"),
            ToolUse("Read", "/src/Bar.cs"),
            ToolUse("Grep", null)
        };

        var summary = await _sut.CompressAsync(1, observations);

        Assert.Equal("discovery", summary.Category);
    }

    [Fact]
    public async Task Category_IsChange_WhenWriteToolUsed()
    {
        var observations = new[]
        {
            ToolUse("Read", "/src/Foo.cs"),
            ToolUse("Write", "/src/Foo.cs")
        };

        var summary = await _sut.CompressAsync(1, observations);

        Assert.Equal("change", summary.Category);
    }

    [Fact]
    public async Task Category_IsUnknown_WhenNoHeuristicsMatch()
    {
        var observations = new[]
        {
            TextObservation("Let me think about this"),
            ErrorObservation("Some error occurred")
        };

        var summary = await _sut.CompressAsync(1, observations);

        Assert.Equal("unknown", summary.Category);
    }

    [Fact]
    public async Task Category_GitCommitTakesPrecedenceOverToolHeuristics()
    {
        var observations = new[]
        {
            ToolUse("Write", "/src/Foo.cs"),
            GitCommit("fix: corrected logic error")
        };

        var summary = await _sut.CompressAsync(1, observations);

        Assert.Equal("bugfix", summary.Category);
    }

    // ── FileReferences ────────────────────────────────────────────────────────

    [Fact]
    public async Task FileReferences_AreDeduplicatedAndJoined()
    {
        var observations = new[]
        {
            ToolUse("Read", "/src/Foo.cs"),
            ToolUse("Write", "/src/Foo.cs"),
            ToolUse("Read", "/src/Bar.cs")
        };

        var summary = await _sut.CompressAsync(1, observations);

        Assert.NotNull(summary.FileReferences);
        var files = summary.FileReferences!.Split(", ");
        Assert.Equal(2, files.Length);
        Assert.Contains("/src/Bar.cs", files);
        Assert.Contains("/src/Foo.cs", files);
    }

    [Fact]
    public async Task FileReferences_IsNull_WhenNoFilePaths()
    {
        var observations = new[] { TextObservation("hello") };
        var summary = await _sut.CompressAsync(1, observations);
        Assert.Null(summary.FileReferences);
    }

    // ── GitCommitMessages ─────────────────────────────────────────────────────

    [Fact]
    public async Task GitCommitMessages_ExtractsAllCommits()
    {
        var observations = new[]
        {
            GitCommit("feat: first commit"),
            GitCommit("fix: second commit")
        };

        var summary = await _sut.CompressAsync(1, observations);

        Assert.NotNull(summary.GitCommitMessages);
        Assert.Contains("feat: first commit", summary.GitCommitMessages);
        Assert.Contains("fix: second commit", summary.GitCommitMessages);
    }

    [Fact]
    public async Task GitCommitMessages_IsNull_WhenNoGitCommits()
    {
        var observations = new[] { ToolUse("Read", "/src/Foo.cs") };
        var summary = await _sut.CompressAsync(1, observations);
        Assert.Null(summary.GitCommitMessages);
    }

    // ── Test results ──────────────────────────────────────────────────────────

    [Fact]
    public async Task TestResults_AreParsedFromDotnetTestOutput()
    {
        var observations = new[]
        {
            TestResult("Build succeeded.\nPassed: 12, Failed: 0, Skipped: 1, Total: 13")
        };

        var summary = await _sut.CompressAsync(1, observations);

        Assert.True(summary.TestsRun);
        Assert.Equal(12, summary.TestsPassed);
        Assert.Equal(0, summary.TestsFailed);
    }

    [Fact]
    public async Task TestResults_AreParsedFromPytestOutput()
    {
        var observations = new[]
        {
            TestResult("5 passed, 2 failed in 3.14s")
        };

        var summary = await _sut.CompressAsync(1, observations);

        Assert.True(summary.TestsRun);
        Assert.Equal(5, summary.TestsPassed);
        Assert.Equal(2, summary.TestsFailed);
    }

    [Fact]
    public async Task TestResults_AreNull_WhenNoTestObservations()
    {
        var observations = new[] { TextObservation("no tests here") };
        var summary = await _sut.CompressAsync(1, observations);
        Assert.Null(summary.TestsRun);
        Assert.Null(summary.TestsPassed);
        Assert.Null(summary.TestsFailed);
    }

    [Fact]
    public async Task TestResults_TestsRun_TrueButCountsNull_WhenUnparseable()
    {
        var observations = new[] { TestResult("tests ran but output was unrecognised") };
        var summary = await _sut.CompressAsync(1, observations);
        Assert.True(summary.TestsRun);
        Assert.Null(summary.TestsPassed);
        Assert.Null(summary.TestsFailed);
    }

    // ── Error / ToolUse counts ────────────────────────────────────────────────

    [Fact]
    public async Task ErrorCount_CountsErrorTypeObservations()
    {
        var observations = new[]
        {
            ErrorObservation("compilation failed"),
            ErrorObservation("null ref exception"),
            ToolUse("Read", null)
        };

        var summary = await _sut.CompressAsync(1, observations);

        Assert.Equal(2, summary.ErrorCount);
    }

    [Fact]
    public async Task ToolUseCount_CountsToolUseTypeObservations()
    {
        var observations = new[]
        {
            ToolUse("Read", "/a.cs"),
            ToolUse("Write", "/b.cs"),
            ToolUse("Bash", null),
            ErrorObservation("oops")
        };

        var summary = await _sut.CompressAsync(1, observations);

        Assert.Equal(3, summary.ToolUseCount);
    }

    // ── Concepts ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Concepts_ExtractedFromFileStems()
    {
        var observations = new[]
        {
            ToolUse("Read", "/src/PaymentService.cs"),
            ToolUse("Write", "/src/InvoiceRepository.cs")
        };

        var summary = await _sut.CompressAsync(1, observations);

        Assert.NotNull(summary.Concepts);
        Assert.Contains("PaymentService", summary.Concepts);
        Assert.Contains("InvoiceRepository", summary.Concepts);
    }

    [Fact]
    public async Task Concepts_ExcludesGenericNames()
    {
        var observations = new[]
        {
            ToolUse("Read", "/src/Program.cs"),
            ToolUse("Read", "/src/Startup.cs"),
            ToolUse("Read", "/src/MyService.cs")
        };

        var summary = await _sut.CompressAsync(1, observations);

        Assert.NotNull(summary.Concepts);
        Assert.DoesNotContain("Program", summary.Concepts);
        Assert.DoesNotContain("Startup", summary.Concepts);
        Assert.Contains("MyService", summary.Concepts);
    }

    // ── Narrative ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Narrative_LeadsWithCommitMessage_WhenAvailable()
    {
        var observations = new[] { GitCommit("feat: add compression worker") };
        var summary = await _sut.CompressAsync(42, observations);
        Assert.StartsWith("feat: add compression worker", summary.Narrative);
    }

    [Fact]
    public async Task Narrative_MaxLength_Is500Chars()
    {
        var longContent = new string('x', 1000);
        var observations = new[]
        {
            GitCommit(longContent),
            ToolUse("Read", "/" + new string('a', 300) + ".cs")
        };

        var summary = await _sut.CompressAsync(1, observations);

        Assert.True(summary.Narrative.Length <= 500);
    }

    // ── Metadata ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task CompressionLayer_IsAlways1()
    {
        var summary = await _sut.CompressAsync(1, Array.Empty<ConversationObservation>());
        Assert.Equal(1, summary.CompressionLayer);
    }

    [Fact]
    public async Task SessionId_IsPreservedOnSummary()
    {
        var summary = await _sut.CompressAsync(99, Array.Empty<ConversationObservation>());
        Assert.Equal(99, summary.SessionId);
    }

    [Fact]
    public async Task EmptyObservations_ProducesUnknownCategory()
    {
        var summary = await _sut.CompressAsync(1, Array.Empty<ConversationObservation>());
        Assert.Equal("unknown", summary.Category);
        Assert.Equal(0, summary.ToolUseCount);
        Assert.Equal(0, summary.ErrorCount);
    }

    // ── Factory helpers ───────────────────────────────────────────────────────

    private static ConversationObservation ToolUse(string toolName, string? filePath) => new()
    {
        ObservationType = ObservationType.ToolUse,
        ToolName        = toolName,
        FilePath        = filePath,
        Content         = string.Empty,
        CreatedAt       = DateTime.UtcNow
    };

    private static ConversationObservation GitCommit(string message) => new()
    {
        ObservationType = ObservationType.GitCommit,
        Content         = message,
        CreatedAt       = DateTime.UtcNow
    };

    private static ConversationObservation TestResult(string content) => new()
    {
        ObservationType = ObservationType.TestResult,
        Content         = content,
        CreatedAt       = DateTime.UtcNow
    };

    private static ConversationObservation ErrorObservation(string content) => new()
    {
        ObservationType = ObservationType.Error,
        Content         = content,
        CreatedAt       = DateTime.UtcNow
    };

    private static ConversationObservation TextObservation(string content) => new()
    {
        ObservationType = ObservationType.Text,
        Content         = content,
        CreatedAt       = DateTime.UtcNow
    };
}
