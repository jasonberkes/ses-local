using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Ses.Local.Core.Events;
using Ses.Local.Core.Interfaces;
using Ses.Local.Core.Options;
using Ses.Local.Workers.Services;
using Ses.Local.Workers.Workers;
using Xunit;

namespace Ses.Local.Workers.Tests.Workers;

public sealed class LevelDbWatcherTests
{
    [Fact]
    public async Task ExecuteAsync_WhenDisabled_DoesNotNotify()
    {
        var extractor = new LevelDbUuidExtractor(NullLogger<LevelDbUuidExtractor>.Instance);
        var notifier  = new Mock<IDesktopActivityNotifier>();
        var options   = Options.Create(new SesLocalOptions { EnableClaudeDesktopSync = false });
        var watcher   = new LevelDbWatcher(extractor, notifier.Object,
            NullLogger<LevelDbWatcher>.Instance, options);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await watcher.StartAsync(cts.Token);
        await Task.Delay(300);
        await watcher.StopAsync(CancellationToken.None);

        notifier.Verify(x => x.NotifyActivity(It.IsAny<DesktopActivityEvent>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_WhenDirectoryMissing_DoesNotThrow()
    {
        var extractor = new LevelDbUuidExtractor(NullLogger<LevelDbUuidExtractor>.Instance);
        var notifier  = new Mock<IDesktopActivityNotifier>();
        var options   = Options.Create(new SesLocalOptions { EnableClaudeDesktopSync = true });
        var watcher   = new LevelDbWatcher(extractor, notifier.Object,
            NullLogger<LevelDbWatcher>.Instance, options);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var ex = await Record.ExceptionAsync(() => watcher.StartAsync(cts.Token));
        Assert.Null(ex);
        await watcher.StopAsync(CancellationToken.None);
    }

    [Fact]
    public void GetLevelDbPath_ReturnsPathContainingLevelDb()
    {
        var path = LevelDbUuidExtractor.GetLevelDbPath();
        Assert.False(string.IsNullOrEmpty(path));
        Assert.Contains("leveldb", path, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Local Storage", path);
    }

    [Fact]
    public void ExtractUuids_FromRealLdbContent_FindsUuids()
    {
        // Create a temp .ldb file with realistic content
        var tempDir = Path.Combine(Path.GetTempPath(), $"ses-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var ldbContent = System.Text.Encoding.ASCII.GetBytes(
                "\x00\x01JUNK\x00" +
                "LSS-002bb01a-b420-4b1e-862a-ec01b9897bd1:attachment\x00" +
                "JUNK\x00\x01\x02" +
                "LSS-0450fa6e-6900-43c7-9327-158813b8b531:files\x00" +
                "LSS-002bb01a-b420-4b1e-862a-ec01b9897bd1:textInput\x00" + // duplicate UUID
                "some other data\x00");

            File.WriteAllBytes(Path.Combine(tempDir, "test.ldb"), ldbContent);

            var extractor = new LevelDbUuidExtractor(NullLogger<LevelDbUuidExtractor>.Instance);
            var uuids     = extractor.ExtractUuids(tempDir);

            Assert.Equal(2, uuids.Count); // deduplicated
            Assert.Contains("002bb01a-b420-4b1e-862a-ec01b9897bd1", uuids);
            Assert.Contains("0450fa6e-6900-43c7-9327-158813b8b531", uuids);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void DesktopActivityNotifier_FiresEventWithUuids()
    {
        var notifier = new DesktopActivityNotifier();
        DesktopActivityEvent? received = null;
        notifier.DesktopActivityDetected += (_, e) => received = e;

        notifier.NotifyActivity(new DesktopActivityEvent
        {
            ConversationUuids = new[] { "uuid-1", "uuid-2" }
        });

        Assert.NotNull(received);
        Assert.Equal(2, received.ConversationUuids.Count);
    }
}
