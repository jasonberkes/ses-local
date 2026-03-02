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

public sealed class ClaudeMdGeneratorTests : IDisposable
{
    private readonly string _tempDir;
    private readonly Mock<ILocalDbService> _db;
    private readonly SesLocalOptions _options;

    public ClaudeMdGeneratorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(_tempDir);
        _db = new Mock<ILocalDbService>();
        _options = new SesLocalOptions { EnableClaudeMdGeneration = true, ClaudeMdMaxAgeDays = 7 };
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    private ClaudeMdGenerator CreateGenerator() =>
        new(_db.Object, NullLogger<ClaudeMdGenerator>.Instance, Options.Create(_options));

    private static ConversationSession MakeSession(string projectName, string sessionId = "abc12345") => new()
    {
        Id         = 1,
        Source     = ConversationSource.ClaudeCode,
        ExternalId = sessionId,
        Title      = $"{projectName}/{sessionId[..8]}",
        CreatedAt  = DateTime.UtcNow.AddDays(-1),
        UpdatedAt  = DateTime.UtcNow
    };

    // ── GenerateAsync disabled ────────────────────────────────────────────────

    [Fact]
    public async Task GenerateAsync_WhenDisabled_DoesNotWriteFile()
    {
        _options.EnableClaudeMdGeneration = false;
        var generator = CreateGenerator();

        await generator.GenerateAsync(_tempDir);

        Assert.False(File.Exists(Path.Combine(_tempDir, "CLAUDE.md")));
        _db.Verify(x => x.GetRecentSessionsByProjectNameAsync(
            It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GenerateAsync_WhenPathMissing_DoesNotThrow()
    {
        var generator = CreateGenerator();
        var ex = await Record.ExceptionAsync(() => generator.GenerateAsync("/nonexistent/path/xyz"));
        Assert.Null(ex);
    }

    // ── Excluded paths ────────────────────────────────────────────────────────

    [Fact]
    public async Task GenerateAsync_WhenPathExcluded_DoesNotWriteFile()
    {
        // Use a substring of _tempDir as the exclusion so it matches on all platforms
        var exclusionSubstring = Path.GetFileName(_tempDir);
        _options.ClaudeMdExcludePaths = [exclusionSubstring];
        var generator = CreateGenerator();

        await generator.GenerateAsync(_tempDir);

        Assert.False(File.Exists(Path.Combine(_tempDir, "CLAUDE.md")));
    }

    // ── User-created file protection ──────────────────────────────────────────

    [Fact]
    public async Task GenerateAsync_WhenUserCreatedFileExists_DoesNotOverwrite()
    {
        var claudeMdPath = Path.Combine(_tempDir, "CLAUDE.md");
        var userContent = "# My project\nThis is a user file.";
        await File.WriteAllTextAsync(claudeMdPath, userContent);

        var generator = CreateGenerator();
        await generator.GenerateAsync(_tempDir);

        Assert.Equal(userContent, await File.ReadAllTextAsync(claudeMdPath));
    }

    [Fact]
    public async Task GenerateAsync_WhenOurFileExists_Overwrites()
    {
        var projectName = Path.GetFileName(_tempDir);
        var claudeMdPath = Path.Combine(_tempDir, "CLAUDE.md");
        await File.WriteAllTextAsync(claudeMdPath,
            ClaudeMdGenerator.GeneratedHeader + "\nOld content");

        var session = MakeSession(projectName);
        _db.Setup(x => x.GetRecentSessionsByProjectNameAsync(
                projectName, It.IsAny<DateTime>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
           .ReturnsAsync([session]);
        _db.Setup(x => x.GetRecentObservationsForSessionsAsync(
                It.IsAny<IEnumerable<long>>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
           .ReturnsAsync([]);

        var generator = CreateGenerator();
        await generator.GenerateAsync(_tempDir);

        var written = await File.ReadAllTextAsync(claudeMdPath);
        Assert.StartsWith(ClaudeMdGenerator.GeneratedHeader, written);
        Assert.Contains(projectName, written);
        Assert.DoesNotContain("Old content", written);
    }

    // ── No sessions — no file ─────────────────────────────────────────────────

    [Fact]
    public async Task GenerateAsync_WhenNoSessions_DoesNotWriteFile()
    {
        var projectName = Path.GetFileName(_tempDir);
        _db.Setup(x => x.GetRecentSessionsByProjectNameAsync(
                projectName, It.IsAny<DateTime>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
           .ReturnsAsync([]);

        var generator = CreateGenerator();
        await generator.GenerateAsync(_tempDir);

        Assert.False(File.Exists(Path.Combine(_tempDir, "CLAUDE.md")));
    }

    // ── Content generation ────────────────────────────────────────────────────

    [Fact]
    public async Task GenerateAsync_WithSessions_WritesValidFile()
    {
        var projectName = Path.GetFileName(_tempDir);
        var session = MakeSession(projectName);

        _db.Setup(x => x.GetRecentSessionsByProjectNameAsync(
                projectName, It.IsAny<DateTime>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
           .ReturnsAsync([session]);
        _db.Setup(x => x.GetRecentObservationsForSessionsAsync(
                It.IsAny<IEnumerable<long>>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
           .ReturnsAsync([]);

        var generator = CreateGenerator();
        await generator.GenerateAsync(_tempDir);

        var claudeMdPath = Path.Combine(_tempDir, "CLAUDE.md");
        Assert.True(File.Exists(claudeMdPath));

        var content = await File.ReadAllTextAsync(claudeMdPath);
        Assert.StartsWith(ClaudeMdGenerator.GeneratedHeader, content);
        Assert.Contains(projectName, content);
        Assert.Contains("Recent Sessions", content);
    }

    [Fact]
    public async Task GenerateAsync_WithGitCommits_IncludesCommitSection()
    {
        var projectName = Path.GetFileName(_tempDir);
        var session = MakeSession(projectName);

        var gitObs = new ConversationObservation
        {
            ObservationType = ObservationType.GitCommit,
            Content = "{\"command\":\"git commit -m \\\"feat: add new feature\\\"\"}",
            CreatedAt = DateTime.UtcNow,
            SequenceNumber = 0
        };

        _db.Setup(x => x.GetRecentSessionsByProjectNameAsync(
                projectName, It.IsAny<DateTime>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
           .ReturnsAsync([session]);
        _db.Setup(x => x.GetRecentObservationsForSessionsAsync(
                It.IsAny<IEnumerable<long>>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
           .ReturnsAsync([gitObs]);

        var generator = CreateGenerator();
        await generator.GenerateAsync(_tempDir);

        var content = await File.ReadAllTextAsync(Path.Combine(_tempDir, "CLAUDE.md"));
        Assert.Contains("Recent Git Commits", content);
    }

    [Fact]
    public async Task GenerateAsync_WithFilePaths_IncludesFilesSection()
    {
        var projectName = Path.GetFileName(_tempDir);
        var session = MakeSession(projectName);

        var fileObs = new ConversationObservation
        {
            ObservationType = ObservationType.ToolUse,
            ToolName = "Write",
            FilePath = "/src/MyService.cs",
            Content = "{}",
            CreatedAt = DateTime.UtcNow,
            SequenceNumber = 0
        };

        _db.Setup(x => x.GetRecentSessionsByProjectNameAsync(
                projectName, It.IsAny<DateTime>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
           .ReturnsAsync([session]);
        _db.Setup(x => x.GetRecentObservationsForSessionsAsync(
                It.IsAny<IEnumerable<long>>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
           .ReturnsAsync([fileObs]);

        var generator = CreateGenerator();
        await generator.GenerateAsync(_tempDir);

        var content = await File.ReadAllTextAsync(Path.Combine(_tempDir, "CLAUDE.md"));
        Assert.Contains("Files Modified", content);
        Assert.Contains("/src/MyService.cs", content);
    }

    // ── .gitignore ────────────────────────────────────────────────────────────

    [Fact]
    public async Task EnsureGitignoreEntryAsync_WhenNoGitDir_DoesNotCreateGitignore()
    {
        await ClaudeMdGenerator.EnsureGitignoreEntryAsync(_tempDir, CancellationToken.None);
        Assert.False(File.Exists(Path.Combine(_tempDir, ".gitignore")));
    }

    [Fact]
    public async Task EnsureGitignoreEntryAsync_WhenGitDirExists_CreatesGitignoreWithEntry()
    {
        Directory.CreateDirectory(Path.Combine(_tempDir, ".git"));
        await ClaudeMdGenerator.EnsureGitignoreEntryAsync(_tempDir, CancellationToken.None);

        var gitignorePath = Path.Combine(_tempDir, ".gitignore");
        Assert.True(File.Exists(gitignorePath));
        Assert.Contains("CLAUDE.md", await File.ReadAllTextAsync(gitignorePath));
    }

    [Fact]
    public async Task EnsureGitignoreEntryAsync_WhenEntryAlreadyExists_DoesNotDuplicate()
    {
        Directory.CreateDirectory(Path.Combine(_tempDir, ".git"));
        var gitignorePath = Path.Combine(_tempDir, ".gitignore");
        await File.WriteAllTextAsync(gitignorePath, "*.log\nCLAUDE.md\n*.tmp\n");

        await ClaudeMdGenerator.EnsureGitignoreEntryAsync(_tempDir, CancellationToken.None);

        var lines = (await File.ReadAllTextAsync(gitignorePath))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(1, lines.Count(l => l.Trim() == "CLAUDE.md"));
    }

    // ── BuildContent static method ────────────────────────────────────────────

    [Fact]
    public void BuildContent_EmptyObservations_ReturnsValidMarkdown()
    {
        var session = MakeSession("myproject");
        var content = ClaudeMdGenerator.BuildContent("myproject", [session], []);

        Assert.StartsWith(ClaudeMdGenerator.GeneratedHeader, content);
        Assert.Contains("myproject", content);
        Assert.Contains("Recent Sessions", content);
        Assert.DoesNotContain("Recent Git Commits", content);
        Assert.DoesNotContain("Files Modified", content);
    }

    [Fact]
    public void BuildContent_DeduplicatesFilePaths()
    {
        var session = MakeSession("myproject");
        var obs = new[]
        {
            new ConversationObservation { ObservationType = ObservationType.ToolUse, FilePath = "/src/A.cs", Content = "{}", CreatedAt = DateTime.UtcNow },
            new ConversationObservation { ObservationType = ObservationType.ToolUse, FilePath = "/src/A.cs", Content = "{}", CreatedAt = DateTime.UtcNow },
            new ConversationObservation { ObservationType = ObservationType.ToolUse, FilePath = "/src/B.cs", Content = "{}", CreatedAt = DateTime.UtcNow }
        };

        var content = ClaudeMdGenerator.BuildContent("myproject", [session], obs);

        // /src/A.cs should appear exactly once
        var count = 0;
        var idx = 0;
        while ((idx = content.IndexOf("/src/A.cs", idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx++;
        }
        Assert.Equal(1, count);
    }
}
