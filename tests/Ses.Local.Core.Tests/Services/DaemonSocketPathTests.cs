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

        // Use a temp directory to avoid interfering with the real (possibly active) daemon socket.
        var dir  = Path.Combine(Path.GetTempPath(), $"ses-test-{Guid.NewGuid():N}");
        var path = Path.Combine(dir, "daemon.sock");
        Directory.CreateDirectory(dir);

        try
        {
            File.WriteAllText(path, "stale");
            Assert.True(File.Exists(path));

            DaemonSocketPath.CleanupStaleSocket(path);
            Assert.False(File.Exists(path));
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void CleanupStaleSocket_NoErrorWhenNoFile()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return;

        // Use a temp path so we don't disturb the real daemon socket.
        var path = Path.Combine(Path.GetTempPath(), $"ses-test-{Guid.NewGuid():N}.sock");

        // Should not throw when the file doesn't exist
        DaemonSocketPath.CleanupStaleSocket(path);
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
