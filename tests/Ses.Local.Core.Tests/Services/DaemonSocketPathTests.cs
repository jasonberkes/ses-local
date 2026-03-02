using System.Runtime.InteropServices;
using Ses.Local.Core.Services;
using Xunit;

namespace Ses.Local.Core.Tests.Services;

public sealed class DaemonSocketPathTests
{
    [Fact]
    public void GetPath_ReturnsNonEmpty()
    {
        var path = DaemonSocketPath.GetPath();
        Assert.False(string.IsNullOrEmpty(path));
    }

    [Fact]
    public void GetPath_ReturnsPlatformAppropriate()
    {
        var path = DaemonSocketPath.GetPath();

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Assert.StartsWith(@"\\.\pipe\", path);
            Assert.Contains("ses-local-daemon", path);
        }
        else
        {
            Assert.EndsWith("daemon.sock", path);
            Assert.Contains(".ses", path);
        }
    }

    [Fact]
    public void CleanupStaleSocket_RemovesExistingFile()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return; // Named pipes don't leave stale files

        var path = DaemonSocketPath.GetPath();
        var dir  = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);

        // Create a fake stale socket file
        File.WriteAllText(path, "stale");
        Assert.True(File.Exists(path));

        DaemonSocketPath.CleanupStaleSocket();
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void CleanupStaleSocket_NoErrorWhenNoFile()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return;

        var path = DaemonSocketPath.GetPath();
        if (File.Exists(path))
            File.Delete(path);

        // Should not throw
        DaemonSocketPath.CleanupStaleSocket();
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void IsAvailable_ReturnsFalseWhenNoSocket()
    {
        var path = DaemonSocketPath.GetPath();
        if (File.Exists(path))
            File.Delete(path);

        Assert.False(DaemonSocketPath.IsAvailable());
    }
}
