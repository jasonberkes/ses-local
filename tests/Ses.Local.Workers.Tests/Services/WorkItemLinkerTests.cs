using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Ses.Local.Core.Enums;
using Ses.Local.Core.Interfaces;
using Ses.Local.Core.Models;
using Ses.Local.Workers.Services;
using Xunit;

namespace Ses.Local.Workers.Tests.Services;

/// <summary>
/// Unit tests for WorkItemLinker WI-reference detection and link persistence (WI-987).
/// </summary>
public sealed class WorkItemLinkerTests
{
    private static WorkItemLinker CreateLinker(ILocalDbService db) =>
        new(db, NullLogger<WorkItemLinker>.Instance);

    private static ConversationObservation Obs(ObservationType type, string content) => new()
    {
        ObservationType = type,
        Content         = content,
        SequenceNumber  = 0,
        CreatedAt       = DateTime.UtcNow
    };

    // ── ReadGitBranch ─────────────────────────────────────────────────────────

    [Fact]
    public void ReadGitBranch_ValidRef_ReturnsBranchName()
    {
        var dir     = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var gitDir  = Path.Combine(dir, ".git");
        Directory.CreateDirectory(gitDir);
        File.WriteAllText(Path.Combine(gitDir, "HEAD"), "ref: refs/heads/claude/wi-987-linking\n");

        var branch = WorkItemLinker.ReadGitBranch(dir);

        Assert.Equal("claude/wi-987-linking", branch);

        Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public void ReadGitBranch_DetachedHead_ReturnsNull()
    {
        var dir    = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var gitDir = Path.Combine(dir, ".git");
        Directory.CreateDirectory(gitDir);
        File.WriteAllText(Path.Combine(gitDir, "HEAD"), "abc1234def5678\n");

        var branch = WorkItemLinker.ReadGitBranch(dir);

        Assert.Null(branch);

        Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public void ReadGitBranch_NoGitDir_ReturnsNull()
    {
        var result = WorkItemLinker.ReadGitBranch("/nonexistent/path/xyz");
        Assert.Null(result);
    }

    // ── ExtractWorkItemIds ────────────────────────────────────────────────────

    [Theory]
    [InlineData("claude/wi-987-linking",          new[] { 987 })]
    [InlineData("feat/WI-123-some-feature",        new[] { 123 })]
    [InlineData("WI-100 and WI-200",               new[] { 100, 200 })]
    [InlineData("workitem-42 is done",             new[] { 42 })]
    [InlineData("no-workitem-here",                new int[0])]
    [InlineData("wi-1 wi-1 wi-1",                  new[] { 1 })]   // deduplication
    [InlineData("fixes WI-500, closes WI-600",     new[] { 500, 600 })]
    public void ExtractWorkItemIds_VariousInputs_ReturnsExpectedIds(string text, int[] expected)
    {
        var result = WorkItemLinker.ExtractWorkItemIds(text).ToArray();
        Assert.Equal(expected.OrderBy(x => x), result.OrderBy(x => x));
    }

    // ── ProcessSessionAsync ───────────────────────────────────────────────────

    [Fact]
    public async Task ProcessSession_BranchWithWiRef_CreatesHighConfidenceLink()
    {
        // Arrange — write a temporary .git/HEAD
        var dir    = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var gitDir = Path.Combine(dir, ".git");
        Directory.CreateDirectory(gitDir);
        File.WriteAllText(Path.Combine(gitDir, "HEAD"), "ref: refs/heads/claude/wi-987-linking\n");

        List<WorkItemLink>? captured = null;
        var mock = new Mock<ILocalDbService>(MockBehavior.Strict);
        mock.Setup(d => d.GetObservationsAsync(1L, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        mock.Setup(d => d.CreateWorkItemLinksAsync(It.IsAny<IEnumerable<WorkItemLink>>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<WorkItemLink>, CancellationToken>((links, _) => captured = links.ToList())
            .Returns(Task.CompletedTask);

        var linker = CreateLinker(mock.Object);

        // Act
        await linker.ProcessSessionAsync(1L, dir);

        // Assert
        Assert.NotNull(captured);
        var link = Assert.Single(captured);
        Assert.Equal(987, link.WorkItemId);
        Assert.Equal("branch_name", link.LinkSource);
        Assert.Equal(1.0, link.Confidence);

        Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public async Task ProcessSession_CommitObservation_Creates09ConfidenceLink()
    {
        List<WorkItemLink>? captured = null;
        var mock = new Mock<ILocalDbService>(MockBehavior.Strict);
        mock.Setup(d => d.GetObservationsAsync(2L, It.IsAny<CancellationToken>()))
            .ReturnsAsync([Obs(ObservationType.GitCommit, "feat: implement WI-500 stuff")]);
        mock.Setup(d => d.CreateWorkItemLinksAsync(It.IsAny<IEnumerable<WorkItemLink>>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<WorkItemLink>, CancellationToken>((links, _) => captured = links.ToList())
            .Returns(Task.CompletedTask);

        var linker = CreateLinker(mock.Object);

        // Act — no cwd, so no branch check
        await linker.ProcessSessionAsync(2L, null);

        // Assert
        Assert.NotNull(captured);
        // commit_message = 0.9, conversation_content = 0.7 → best is 0.9
        var link = Assert.Single(captured);
        Assert.Equal(500, link.WorkItemId);
        Assert.Equal("commit_message", link.LinkSource);
        Assert.Equal(0.9, link.Confidence);
    }

    [Fact]
    public async Task ProcessSession_ContentOnly_Creates07ConfidenceLink()
    {
        List<WorkItemLink>? captured = null;
        var mock = new Mock<ILocalDbService>(MockBehavior.Strict);
        mock.Setup(d => d.GetObservationsAsync(3L, It.IsAny<CancellationToken>()))
            .ReturnsAsync([Obs(ObservationType.Text, "Working on WI-300 today")]);
        mock.Setup(d => d.CreateWorkItemLinksAsync(It.IsAny<IEnumerable<WorkItemLink>>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<WorkItemLink>, CancellationToken>((links, _) => captured = links.ToList())
            .Returns(Task.CompletedTask);

        var linker = CreateLinker(mock.Object);
        await linker.ProcessSessionAsync(3L, null);

        Assert.NotNull(captured);
        var link = Assert.Single(captured);
        Assert.Equal(300, link.WorkItemId);
        Assert.Equal("conversation_content", link.LinkSource);
        Assert.Equal(0.7, link.Confidence);
    }

    [Fact]
    public async Task ProcessSession_NoWiRefs_DoesNotCallCreateLinks()
    {
        var mock = new Mock<ILocalDbService>(MockBehavior.Strict);
        mock.Setup(d => d.GetObservationsAsync(4L, It.IsAny<CancellationToken>()))
            .ReturnsAsync([Obs(ObservationType.Text, "No special references here")]);

        var linker = CreateLinker(mock.Object);

        // Should NOT call CreateWorkItemLinksAsync — mock is strict so it would throw if called
        await linker.ProcessSessionAsync(4L, null);

        mock.Verify(d => d.CreateWorkItemLinksAsync(
            It.IsAny<IEnumerable<WorkItemLink>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ProcessSession_BranchAndCommitSameId_KeepsBranchHigherConfidence()
    {
        // Branch gives 1.0, commit gives 0.9 for the same WI-123 — should keep 1.0 branch_name
        var dir    = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var gitDir = Path.Combine(dir, ".git");
        Directory.CreateDirectory(gitDir);
        File.WriteAllText(Path.Combine(gitDir, "HEAD"), "ref: refs/heads/feat/wi-123-fix\n");

        List<WorkItemLink>? captured = null;
        var mock = new Mock<ILocalDbService>(MockBehavior.Strict);
        mock.Setup(d => d.GetObservationsAsync(5L, It.IsAny<CancellationToken>()))
            .ReturnsAsync([Obs(ObservationType.GitCommit, "feat: WI-123 is done")]);
        mock.Setup(d => d.CreateWorkItemLinksAsync(It.IsAny<IEnumerable<WorkItemLink>>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<WorkItemLink>, CancellationToken>((links, _) => captured = links.ToList())
            .Returns(Task.CompletedTask);

        var linker = CreateLinker(mock.Object);
        await linker.ProcessSessionAsync(5L, dir);

        Assert.NotNull(captured);
        var link = Assert.Single(captured);
        Assert.Equal(123, link.WorkItemId);
        Assert.Equal("branch_name", link.LinkSource);
        Assert.Equal(1.0, link.Confidence);

        Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public async Task ProcessSession_MultipleDistinctWiIds_CreatesOneEntryPerWi()
    {
        List<WorkItemLink>? captured = null;
        var mock = new Mock<ILocalDbService>(MockBehavior.Strict);
        mock.Setup(d => d.GetObservationsAsync(6L, It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                Obs(ObservationType.Text, "Fixes WI-10 and WI-20"),
                Obs(ObservationType.Text, "Also relates to WI-10")
            ]);
        mock.Setup(d => d.CreateWorkItemLinksAsync(It.IsAny<IEnumerable<WorkItemLink>>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<WorkItemLink>, CancellationToken>((links, _) => captured = links.ToList())
            .Returns(Task.CompletedTask);

        var linker = CreateLinker(mock.Object);
        await linker.ProcessSessionAsync(6L, null);

        Assert.NotNull(captured);
        Assert.Equal(2, captured.Count);
        Assert.Contains(captured, l => l.WorkItemId == 10);
        Assert.Contains(captured, l => l.WorkItemId == 20);
    }
}
