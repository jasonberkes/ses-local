using Microsoft.Extensions.Logging;
using Ses.Local.Core.Interfaces;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Ses.Local.Workers.Services;

/// <summary>
/// macOS keychain implementation using modern SecItem APIs (Security.framework).
/// Uses SecItemCopyMatching / SecItemAdd / SecItemUpdate — works without code signing.
/// Service: SuperEasySoftware.SesLocal
/// </summary>
[SupportedOSPlatform("macos")]
public sealed class MacCredentialStore : ICredentialStore
{
    private const string ServiceName = "SuperEasySoftware.SesLocal";
    private readonly ILogger<MacCredentialStore> _logger;

    public MacCredentialStore(ILogger<MacCredentialStore> logger) => _logger = logger;

    public Task<string?> GetAsync(string key, CancellationToken ct = default)
    {
        var query = CreateQuery(key);
        CFDictionarySetValue(query, kSecReturnData, kCFBooleanTrue);
        CFDictionarySetValue(query, kSecMatchLimit, kSecMatchLimitOne);

        int status = SecItemCopyMatching(query, out IntPtr result);
        CFRelease(query);

        if (status != 0 || result == IntPtr.Zero)
            return Task.FromResult<string?>(null);

        try
        {
            int len = CFDataGetLength(result);
            IntPtr ptr = CFDataGetBytePtr(result);
            byte[] bytes = new byte[len];
            Marshal.Copy(ptr, bytes, 0, len);
            CFRelease(result);
            return Task.FromResult<string?>(Encoding.UTF8.GetString(bytes));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read keychain item: {Key}", key);
            if (result != IntPtr.Zero) CFRelease(result);
            return Task.FromResult<string?>(null);
        }
    }

    public Task SetAsync(string key, string value, CancellationToken ct = default)
    {
        var valueBytes = Encoding.UTF8.GetBytes(value);
        var data = CFDataCreate(IntPtr.Zero, valueBytes, valueBytes.Length);

        // Try update first
        var query = CreateQuery(key);
        var update = CFDictionaryCreateMutable(IntPtr.Zero, 1, IntPtr.Zero, IntPtr.Zero);
        CFDictionarySetValue(update, kSecValueData, data);

        int status = SecItemUpdate(query, update);
        CFRelease(query);
        CFRelease(update);

        if (status != 0) // Item doesn't exist — add it
        {
            var addQuery = CreateQuery(key);
            CFDictionarySetValue(addQuery, kSecValueData, data);
            status = SecItemAdd(addQuery, IntPtr.Zero);
            CFRelease(addQuery);

            if (status != 0)
                _logger.LogWarning("SecItemAdd failed with status {Status} for key {Key}", status, key);
        }

        CFRelease(data);
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string key, CancellationToken ct = default)
    {
        var query = CreateQuery(key);
        SecItemDelete(query);
        CFRelease(query);
        return Task.CompletedTask;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static IntPtr CreateQuery(string account)
    {
        var dict = CFDictionaryCreateMutable(IntPtr.Zero, 4, IntPtr.Zero, IntPtr.Zero);
        CFDictionarySetValue(dict, kSecClass, kSecClassGenericPassword);
        CFDictionarySetValue(dict, kSecAttrService, CFStringCreateWithCString(IntPtr.Zero, ServiceName, 0x08000100));
        CFDictionarySetValue(dict, kSecAttrAccount, CFStringCreateWithCString(IntPtr.Zero, account,     0x08000100));
        return dict;
    }

    // ── CoreFoundation P/Invokes ──────────────────────────────────────────────

    private const string CF = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const string Sec = "/System/Library/Frameworks/Security.framework/Security";

    [DllImport(CF)] private static extern IntPtr CFDictionaryCreateMutable(IntPtr alloc, long capacity, IntPtr keyCallbacks, IntPtr valueCallbacks);
    [DllImport(CF)] private static extern void   CFDictionarySetValue(IntPtr dict, IntPtr key, IntPtr value);
    [DllImport(CF)] private static extern void   CFRelease(IntPtr cf);
    [DllImport(CF)] private static extern IntPtr CFDataCreate(IntPtr alloc, byte[] bytes, long length);
    [DllImport(CF)] private static extern int    CFDataGetLength(IntPtr data);
    [DllImport(CF)] private static extern IntPtr CFDataGetBytePtr(IntPtr data);
    [DllImport(CF)] private static extern IntPtr CFStringCreateWithCString(IntPtr alloc, string str, uint encoding);

    [DllImport(Sec)] private static extern int SecItemCopyMatching(IntPtr query, out IntPtr result);
    [DllImport(Sec)] private static extern int SecItemAdd(IntPtr attrs, IntPtr result);
    [DllImport(Sec)] private static extern int SecItemUpdate(IntPtr query, IntPtr attrs);
    [DllImport(Sec)] private static extern int SecItemDelete(IntPtr query);

    // ── Security constants (loaded at runtime from Security.framework) ────────

    private static IntPtr Load(string symbol)
    {
        IntPtr lib = dlopen(Sec, 1);
        IntPtr sym = dlsym(lib, symbol);
        return Marshal.ReadIntPtr(sym);
    }

    [DllImport("libdl.dylib")] private static extern IntPtr dlopen(string path, int mode);
    [DllImport("libdl.dylib")] private static extern IntPtr dlsym(IntPtr handle, string symbol);

    private static readonly IntPtr kSecClass                = Load("kSecClass");
    private static readonly IntPtr kSecClassGenericPassword = Load("kSecClassGenericPassword");
    private static readonly IntPtr kSecAttrService          = Load("kSecAttrService");
    private static readonly IntPtr kSecAttrAccount          = Load("kSecAttrAccount");
    private static readonly IntPtr kSecReturnData           = Load("kSecReturnData");
    private static readonly IntPtr kSecValueData            = Load("kSecValueData");
    private static readonly IntPtr kSecMatchLimit           = Load("kSecMatchLimit");
    private static readonly IntPtr kSecMatchLimitOne        = Load("kSecMatchLimitOne");
    private static readonly IntPtr kCFBooleanTrue           = LoadCF("kCFBooleanTrue");

    private static IntPtr LoadCF(string symbol)
    {
        IntPtr lib = dlopen(CF, 1);
        IntPtr sym = dlsym(lib, symbol);
        return Marshal.ReadIntPtr(sym);
    }
}
