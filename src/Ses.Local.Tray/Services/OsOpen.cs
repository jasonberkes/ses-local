using System.Diagnostics;

namespace Ses.Local.Tray.Services;

/// <summary>
/// Opens a file or URL with the OS default handler. Works from tray context
/// where shell PATH may not include user-installed tools like 'code'.
/// </summary>
internal static class OsOpen
{
    public static void Launch(string pathOrUrl)
    {
        try
        {
            if (OperatingSystem.IsMacOS())
                Process.Start("open", pathOrUrl);
            else if (OperatingSystem.IsWindows())
                Process.Start(new ProcessStartInfo(pathOrUrl) { UseShellExecute = true });
        }
        catch
        {
            // Non-fatal — user can open manually
        }
    }
}
