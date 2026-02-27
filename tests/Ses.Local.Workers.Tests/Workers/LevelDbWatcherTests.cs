using Microsoft.Extensions.Logging.Abstractions;
using Ses.Local.Workers.Workers;
using Xunit;

namespace Ses.Local.Workers.Tests.Workers;

public sealed class LevelDbWatcherTests
{
    [Fact]
    public async Task LevelDbWatcher_Start_DoesNotThrow()
    {
        var watcher = new LevelDbWatcher(NullLogger<LevelDbWatcher>.Instance);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await watcher.StartAsync(cts.Token);
    }
}
