using System.Runtime.InteropServices;

namespace Ses.Local.Tray;

/// <summary>
/// Registers a macOS Apple Event handler for the ses-local:// URL scheme.
/// When macOS delivers a ses-local:// URL (e.g. the browser redirecting after OAuth),
/// the callback registered via <see cref="Register"/> is invoked with the full URL string.
///
/// Uses the Carbon framework's <c>AEInstallEventHandler</c> to subscribe to
/// <c>kInternetEventClass</c>/<c>kAEGetURL</c> events (both four-char code 'GURL').
/// </summary>
internal static class MacUrlHandler
{
    // kInternetEventClass = kAEGetURL = 'GURL' = 0x4755524C
    private const uint kInternetEventClass = 0x4755524C;
    private const uint kAEGetURL           = 0x4755524C;

    // keyDirectObject = '----' = 0x2D2D2D2D (direct parameter keyword)
    private const uint keyDirectObject     = 0x2D2D2D2D;

    // typeChar = 'TEXT' = 0x54455854
    private const uint typeChar            = 0x54455854;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GetUrlEventHandler(IntPtr handlerCallRef, IntPtr eventRef, IntPtr userData);

    [DllImport("/System/Library/Frameworks/Carbon.framework/Carbon")]
    private static extern int AEInstallEventHandler(
        uint eventClass, uint eventID,
        GetUrlEventHandler handler,
        IntPtr handlerRefCon,
        [MarshalAs(UnmanagedType.I1)] bool isSysHandler);

    [DllImport("/System/Library/Frameworks/Carbon.framework/Carbon")]
    private static extern int AEGetParamPtr(
        IntPtr theEvent, uint theAEKeyword, uint desiredType,
        out uint actualType, byte[] dataPtr, uint maxSize, out uint actualSize);

    // Hold strong references to prevent GC collection of the delegate
    private static GetUrlEventHandler? _nativeHandler;
    private static Action<string>?     _onUrl;

    /// <summary>
    /// Registers the Apple Event handler for ses-local:// URL activation.
    /// Safe to call multiple times; subsequent calls replace the handler.
    /// </summary>
    public static void Register(Action<string> onUrl)
    {
        _onUrl          = onUrl;
        _nativeHandler  = OnGetUrl;
        AEInstallEventHandler(kInternetEventClass, kAEGetURL, _nativeHandler, IntPtr.Zero, false);
    }

    private static int OnGetUrl(IntPtr handlerCallRef, IntPtr eventRef, IntPtr userData)
    {
        try
        {
            const uint maxSize = 4096;
            var buffer = new byte[maxSize];
            var result = AEGetParamPtr(
                eventRef, keyDirectObject, typeChar,
                out _, buffer, maxSize, out var actualSize);

            if (result == 0 && actualSize > 0)
            {
                var url = System.Text.Encoding.UTF8.GetString(buffer, 0, (int)actualSize);
                _onUrl?.Invoke(url);
            }
        }
        catch
        {
            // Never throw from a native callback — the run loop won't handle it gracefully
        }

        return 0; // noErr
    }
}
