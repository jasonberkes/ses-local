using System.Net.Sockets;
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
    /// Returns true if the daemon is actively listening on the socket.
    /// On Unix, attempts a real connection to distinguish a live daemon from a stale
    /// socket file left by a crash. On Windows, named pipes auto-clean on process exit
    /// so file existence is sufficient.
    /// </summary>
    public static bool IsConnectable() => IsConnectable(GetPath());

    /// <summary>Checks connectivity at the given path. Overload for testability.</summary>
    public static bool IsConnectable(string path)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return File.Exists(path);

        // Fast exit: skip socket allocation when the file doesn't exist yet
        // (common during daemon startup polling).
        if (!File.Exists(path)) return false;

        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            socket.Connect(new UnixDomainSocketEndPoint(path));
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Remove stale socket file left by a previous daemon crash.
    /// Named pipes on Windows are kernel objects and auto-clean on process exit.
    /// </summary>
    public static void CleanupStaleSocket() => CleanupStaleSocket(GetPath());

    /// <summary>Removes a stale socket file at the given path. Overload for testability.</summary>
    public static void CleanupStaleSocket(string path)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return;

        if (File.Exists(path))
            File.Delete(path);
    }
}
