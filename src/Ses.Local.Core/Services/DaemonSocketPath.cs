using System.Runtime.InteropServices;

namespace Ses.Local.Core.Services;

/// <summary>
/// Cross-platform path resolution for the daemon IPC socket/pipe.
/// macOS/Linux: ~/.ses/daemon.sock (Unix domain socket)
/// Windows: \\.\pipe\ses-local-daemon (named pipe)
/// </summary>
public static class DaemonSocketPath
{
    private const string SocketFileName = "daemon.sock";
    private const string PipeName = "ses-local-daemon";

    public static string GetPath()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return @"\\.\pipe\" + PipeName;

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".ses", SocketFileName);
    }

    public static bool IsAvailable()
        => File.Exists(GetPath());

    /// <summary>
    /// Remove stale socket file left by a previous daemon crash.
    /// Named pipes on Windows are kernel objects and auto-clean on process exit.
    /// </summary>
    public static void CleanupStaleSocket()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return;

        var path = GetPath();
        if (File.Exists(path))
            File.Delete(path);
    }
}
