using Ses.Local.Core.Models;
using Ses.Local.Tray.Services;
using Xunit;

namespace Ses.Local.Workers.Tests.Services;

public sealed class DaemonAuthProxyTests
{
    [Fact]
    public async Task GetStateAsync_ReturnsUnauthenticated_WhenDaemonNotRunning()
    {
        // DaemonAuthProxy connects to ~/.ses/daemon.sock — with no daemon running,
        // the socket doesn't exist or is refused. Should return unauthenticated gracefully.
        using var proxy = new DaemonAuthProxy();

        var state = await proxy.GetStateAsync();

        Assert.False(state.IsAuthenticated);
        Assert.False(state.NeedsReauth);
    }

    [Fact]
    public async Task SignOutAsync_DoesNotThrow_WhenDaemonNotRunning()
    {
        using var proxy = new DaemonAuthProxy();

        // Should not throw even though daemon is unreachable
        await proxy.SignOutAsync();
    }

    [Fact]
    public async Task ShutdownAsync_DoesNotThrow_WhenDaemonNotRunning()
    {
        using var proxy = new DaemonAuthProxy();

        // Should not throw even though daemon is unreachable
        await proxy.ShutdownAsync();
    }
}
