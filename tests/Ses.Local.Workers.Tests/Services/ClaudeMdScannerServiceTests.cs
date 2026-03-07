using Ses.Local.Tray.Services;
using Xunit;

namespace Ses.Local.Workers.Tests.Services;

public sealed class ClaudeMdScannerServiceTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"claude-md-test-{Guid.NewGuid():N}");

    public ClaudeMdScannerServiceTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    // ── FindClaudeMd ──────────────────────────────────────────────────────────

    [Fact]
    public void FindClaudeMd_FindsUppercaseCLAUDEmd()
    {
        File.WriteAllText(Path.Combine(_tempDir, "CLAUDE.md"), "# Test");

        var result = ClaudeMdScannerService.FindClaudeMd(_tempDir);

        Assert.NotNull(result);
        Assert.Equal(Path.Combine(_tempDir, "CLAUDE.md"), result);
    }

    [Fact]
    public void FindClaudeMd_FindsLowercaseClaudemd()
    {
        File.WriteAllText(Path.Combine(_tempDir, "claude.md"), "# Test");

        var result = ClaudeMdScannerService.FindClaudeMd(_tempDir);

        // Path comparison is case-insensitive on macOS; just verify a path was found
        Assert.NotNull(result);
        Assert.EndsWith("claude.md", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FindClaudeMd_FindsInDotClaudeSubdir()
    {
        var dotClaude = Path.Combine(_tempDir, ".claude");
        Directory.CreateDirectory(dotClaude);
        File.WriteAllText(Path.Combine(dotClaude, "CLAUDE.md"), "# From .claude/");

        var result = ClaudeMdScannerService.FindClaudeMd(_tempDir);

        Assert.NotNull(result);
        Assert.Equal(Path.Combine(dotClaude, "CLAUDE.md"), result);
    }

    [Fact]
    public void FindClaudeMd_ReturnsNullWhenMissing()
    {
        var result = ClaudeMdScannerService.FindClaudeMd(_tempDir);

        Assert.Null(result);
    }

    [Fact]
    public void FindClaudeMd_ReturnsNullForNonexistentDirectory()
    {
        var result = ClaudeMdScannerService.FindClaudeMd(Path.Combine(_tempDir, "nonexistent"));

        Assert.Null(result);
    }

    // ── BuildEntry ────────────────────────────────────────────────────────────

    [Fact]
    public void BuildEntry_WithClaudeMd_HasClaudeMdTrue()
    {
        File.WriteAllText(Path.Combine(_tempDir, "CLAUDE.md"), "# Test");

        var entry = ClaudeMdScannerService.BuildEntry(_tempDir);

        Assert.True(entry.HasClaudeMd);
        Assert.NotNull(entry.ClaudeMdPath);
        Assert.NotNull(entry.LastModified);
        Assert.True(entry.FileSizeBytes > 0);
    }

    [Fact]
    public void BuildEntry_WithoutClaudeMd_HasClaudeMdFalse()
    {
        var entry = ClaudeMdScannerService.BuildEntry(_tempDir);

        Assert.False(entry.HasClaudeMd);
        Assert.Null(entry.ClaudeMdPath);
        Assert.Null(entry.LastModified);
    }

    [Fact]
    public void BuildEntry_ProjectNameIsDirectoryName()
    {
        var dir = Path.Combine(_tempDir, "my-project");
        Directory.CreateDirectory(dir);

        var entry = ClaudeMdScannerService.BuildEntry(dir);

        Assert.Equal("my-project", entry.ProjectName);
        Assert.Equal(dir, entry.ProjectPath);
    }

    [Fact]
    public void BuildEntry_CaseInsensitiveLookup_FindsClaudeMd()
    {
        // Write lowercase; FindClaudeMd should still find it
        File.WriteAllText(Path.Combine(_tempDir, "claude.md"), "# hello");

        var entry = ClaudeMdScannerService.BuildEntry(_tempDir);

        Assert.True(entry.HasClaudeMd);
    }
}
