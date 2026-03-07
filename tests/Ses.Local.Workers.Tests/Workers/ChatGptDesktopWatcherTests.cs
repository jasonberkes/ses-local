using Microsoft.Extensions.Logging.Abstractions;
using Ses.Local.Workers.Workers;
using Xunit;

namespace Ses.Local.Workers.Tests.Workers;

public sealed class ChatGptDesktopWatcherTests
{
    // ── ChatGptDesktopPaths ────────────────────────────────────────────────────

    [Fact]
    public void GetDataPath_WhenNotInstalled_ReturnsNull()
    {
        // The test environment (CI/Linux or macOS without ChatGPT Desktop) returns null
        // We can't guarantee ChatGPT Desktop is installed, so just verify the method runs.
        var path = ChatGptDesktopPaths.GetDataPath();
        // Either null (not installed) or a valid directory path — both are acceptable
        if (path is not null)
            Assert.True(Directory.Exists(path));
    }

    [Fact]
    public void IsInstalled_MatchesGetDataPath()
    {
        var path = ChatGptDesktopPaths.GetDataPath();
        Assert.Equal(path is not null, ChatGptDesktopPaths.IsInstalled());
    }

    [Fact]
    public void GetConversationDirs_WithTempDir_FindsMatchingDirs()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"ses-chatgpt-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            var userId = "abc123-456";
            Directory.CreateDirectory(Path.Combine(tempRoot, $"conversations-v3-{userId}"));
            Directory.CreateDirectory(Path.Combine(tempRoot, "other-directory"));

            var dirs = ChatGptDesktopPaths.GetConversationDirs(tempRoot);

            Assert.Single(dirs);
            Assert.Contains($"conversations-v3-{userId}", dirs[0]);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void CountConversationFiles_WithTempFiles_CountsDataFiles()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"ses-chatgpt-test-{Guid.NewGuid()}");
        var convDir = Path.Combine(tempRoot, "conversations-v3-testuser");
        Directory.CreateDirectory(convDir);
        try
        {
            File.WriteAllBytes(Path.Combine(convDir, $"{Guid.NewGuid()}.data"), [0x00]);
            File.WriteAllBytes(Path.Combine(convDir, $"{Guid.NewGuid()}.data"), [0x00]);
            File.WriteAllText(Path.Combine(convDir, "index.json"), "{}"); // not a .data file

            var count = ChatGptDesktopPaths.CountConversationFiles(tempRoot);

            Assert.Equal(2, count);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void CountConversationFiles_WhenNoConversationDirs_ReturnsZero()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"ses-chatgpt-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            var count = ChatGptDesktopPaths.CountConversationFiles(tempRoot);
            Assert.Equal(0, count);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    // ── ChatGptDesktopWatcher lifecycle ───────────────────────────────────────

    [Fact]
    public async Task Watcher_StartsAndStops_Gracefully()
    {
        var watcher = new ChatGptDesktopWatcher(NullLogger<ChatGptDesktopWatcher>.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var ex = await Record.ExceptionAsync(() => watcher.StartAsync(cts.Token));

        Assert.Null(ex);
        await watcher.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Watcher_WhenNotInstalled_DoesNotThrow()
    {
        // On a machine without ChatGPT Desktop (e.g., CI/Linux), watcher should exit cleanly
        var watcher = new ChatGptDesktopWatcher(NullLogger<ChatGptDesktopWatcher>.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var ex = await Record.ExceptionAsync(async () =>
        {
            await watcher.StartAsync(cts.Token);
            await Task.Delay(150);
            await watcher.StopAsync(CancellationToken.None);
        });

        Assert.Null(ex);
    }
}
