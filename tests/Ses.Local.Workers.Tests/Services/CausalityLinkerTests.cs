using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Ses.Local.Core.Enums;
using Ses.Local.Core.Interfaces;
using Ses.Local.Core.Models;
using Ses.Local.Workers.Services;
using Xunit;

namespace Ses.Local.Workers.Tests.Services;

/// <summary>
/// Unit tests for CausalityLinker heuristic pattern detection (WI-983).
/// </summary>
public sealed class CausalityLinkerTests
{
    private static CausalityLinker CreateLinker(ILocalDbService? db = null) =>
        new(db ?? Mock.Of<ILocalDbService>(), NullLogger<CausalityLinker>.Instance);

    private static ConversationObservation Obs(
        long id,
        ObservationType type,
        string? toolName = null,
        string? filePath = null,
        string content   = "",
        int seq          = 0) => new()
    {
        Id              = id,
        SessionId       = 1,
        ObservationType = type,
        ToolName        = toolName,
        FilePath        = filePath,
        Content         = content,
        SequenceNumber  = seq,
        CreatedAt       = DateTime.UtcNow
    };

    // ── Pattern a: Error → Write same file = "fixes" ─────────────────────────

    [Fact]
    public void DetectLinks_ErrorFollowedByWriteSameFile_ProducesFixesLink()
    {
        var observations = new List<ConversationObservation>
        {
            Obs(1, ObservationType.Error, filePath: "/src/Foo.cs", content: "error: null ref", seq: 1),
            Obs(2, ObservationType.ToolUse, toolName: "Write", filePath: "/src/Foo.cs", seq: 2)
        };

        var links = CreateLinker().DetectLinks(observations);

        var link = Assert.Single(links, l => l.LinkType == "fixes");
        Assert.Equal(1L, link.SourceObservationId);
        Assert.Equal(2L, link.TargetObservationId);
        Assert.True(link.Confidence >= 0.8);
    }

    [Fact]
    public void DetectLinks_ErrorFollowedByWriteDifferentFile_NoFixesLink()
    {
        var observations = new List<ConversationObservation>
        {
            Obs(1, ObservationType.Error, filePath: "/src/Foo.cs", content: "error", seq: 1),
            Obs(2, ObservationType.ToolUse, toolName: "Write", filePath: "/src/Bar.cs", seq: 2)
        };

        var links = CreateLinker().DetectLinks(observations);
        Assert.DoesNotContain(links, l => l.LinkType == "fixes" && l.SourceObservationId == 1);
    }

    [Fact]
    public void DetectLinks_ErrorNoFilePath_NoFixesLink()
    {
        var observations = new List<ConversationObservation>
        {
            Obs(1, ObservationType.Error, filePath: null, content: "error", seq: 1),
            Obs(2, ObservationType.ToolUse, toolName: "Write", filePath: "/src/Foo.cs", seq: 2)
        };

        var links = CreateLinker().DetectLinks(observations);
        Assert.DoesNotContain(links, l => l.LinkType == "fixes");
    }

    // ── Pattern b: Read → Write same file = "causes" ─────────────────────────

    [Fact]
    public void DetectLinks_ReadFollowedByWriteSameFile_ProducesCausesLink()
    {
        var observations = new List<ConversationObservation>
        {
            Obs(10, ObservationType.ToolUse, toolName: "Read",  filePath: "/src/Bar.cs", seq: 1),
            Obs(11, ObservationType.ToolUse, toolName: "Write", filePath: "/src/Bar.cs", seq: 2)
        };

        var links = CreateLinker().DetectLinks(observations);

        var link = Assert.Single(links, l => l.LinkType == "causes");
        Assert.Equal(10L, link.SourceObservationId);
        Assert.Equal(11L, link.TargetObservationId);
        Assert.True(link.Confidence >= 0.6);
    }

    [Fact]
    public void DetectLinks_ReadFollowedByWriteDifferentFile_NoCausesLink()
    {
        var observations = new List<ConversationObservation>
        {
            Obs(10, ObservationType.ToolUse, toolName: "Read",  filePath: "/src/A.cs", seq: 1),
            Obs(11, ObservationType.ToolUse, toolName: "Write", filePath: "/src/B.cs", seq: 2)
        };

        var links = CreateLinker().DetectLinks(observations);
        Assert.DoesNotContain(links, l => l.LinkType == "causes");
    }

    [Fact]
    public void DetectLinks_EditCountsAsWrite_ProducesCausesLink()
    {
        var observations = new List<ConversationObservation>
        {
            Obs(20, ObservationType.ToolUse, toolName: "Read", filePath: "/src/Baz.cs", seq: 1),
            Obs(21, ObservationType.ToolUse, toolName: "Edit", filePath: "/src/Baz.cs", seq: 2)
        };

        var links = CreateLinker().DetectLinks(observations);
        Assert.Contains(links, l => l.LinkType == "causes" && l.SourceObservationId == 20 && l.TargetObservationId == 21);
    }

    // ── Pattern c: TestResult(fail) → … → TestResult(pass) = "fixes" ─────────

    [Fact]
    public void DetectLinks_FailTestFollowedByPassTest_ProducesFixesLink()
    {
        var observations = new List<ConversationObservation>
        {
            Obs(30, ObservationType.TestResult, content: "3 failed, 1 passed", seq: 1),
            Obs(31, ObservationType.ToolUse,    toolName: "Write", filePath: "/src/X.cs", seq: 2),
            Obs(32, ObservationType.TestResult, content: "All tests passed", seq: 3)
        };

        var links = CreateLinker().DetectLinks(observations);

        var fixLink = Assert.Single(links, l => l.LinkType == "fixes" && l.SourceObservationId == 30);
        Assert.Equal(32L, fixLink.TargetObservationId);
        Assert.True(fixLink.Confidence >= 0.9);
    }

    [Fact]
    public void DetectLinks_FailTestNoSubsequentPass_NoFixesLink()
    {
        var observations = new List<ConversationObservation>
        {
            Obs(40, ObservationType.TestResult, content: "2 failed", seq: 1),
            Obs(41, ObservationType.ToolUse,    toolName: "Write", filePath: "/src/Y.cs", seq: 2)
        };

        var links = CreateLinker().DetectLinks(observations);
        Assert.DoesNotContain(links, l => l.SourceObservationId == 40 && l.LinkType == "fixes");
    }

    [Fact]
    public void DetectLinks_PassTestOnly_NoLinks()
    {
        var observations = new List<ConversationObservation>
        {
            Obs(50, ObservationType.TestResult, content: "All tests passed", seq: 1)
        };

        var links = CreateLinker().DetectLinks(observations);
        Assert.Empty(links);
    }

    // ── Multiple patterns in one session ─────────────────────────────────────

    [Fact]
    public void DetectLinks_MultiplePatterns_DetectsAll()
    {
        var observations = new List<ConversationObservation>
        {
            Obs(1, ObservationType.Error,      filePath: "/src/A.cs",  content: "error",         seq: 1),
            Obs(2, ObservationType.ToolUse,    toolName: "Write",       filePath: "/src/A.cs",   seq: 2),
            Obs(3, ObservationType.ToolUse,    toolName: "Read",        filePath: "/src/B.cs",   seq: 3),
            Obs(4, ObservationType.ToolUse,    toolName: "Write",       filePath: "/src/B.cs",   seq: 4),
            Obs(5, ObservationType.TestResult, content: "1 FAILED",                               seq: 5),
            Obs(6, ObservationType.TestResult, content: "All tests passed",                       seq: 6)
        };

        var links = CreateLinker().DetectLinks(observations);

        Assert.Contains(links, l => l.LinkType == "fixes"  && l.SourceObservationId == 1 && l.TargetObservationId == 2);
        Assert.Contains(links, l => l.LinkType == "causes" && l.SourceObservationId == 3 && l.TargetObservationId == 4);
        Assert.Contains(links, l => l.LinkType == "fixes"  && l.SourceObservationId == 5 && l.TargetObservationId == 6);
    }

    // ── Observations with Id == 0 are skipped ─────────────────────────────────

    [Fact]
    public void DetectLinks_ObservationsWithZeroId_SkippedAsTargets()
    {
        var observations = new List<ConversationObservation>
        {
            Obs(1, ObservationType.Error, filePath: "/src/A.cs", content: "error", seq: 1),
            // Id == 0 means not yet persisted — should not be linked
            Obs(0, ObservationType.ToolUse, toolName: "Write", filePath: "/src/A.cs", seq: 2)
        };

        var links = CreateLinker().DetectLinks(observations);
        Assert.Empty(links);
    }

    // ── Ordering by SequenceNumber ─────────────────────────────────────────────

    [Fact]
    public void DetectLinks_UnorderedInput_OrdersBySequenceNumber()
    {
        // Write comes before Read in the list but AFTER in sequence
        var observations = new List<ConversationObservation>
        {
            Obs(2, ObservationType.ToolUse, toolName: "Write", filePath: "/src/C.cs", seq: 2),
            Obs(1, ObservationType.ToolUse, toolName: "Read",  filePath: "/src/C.cs", seq: 1)
        };

        var links = CreateLinker().DetectLinks(observations);
        Assert.Contains(links, l => l.LinkType == "causes" && l.SourceObservationId == 1 && l.TargetObservationId == 2);
    }
}
