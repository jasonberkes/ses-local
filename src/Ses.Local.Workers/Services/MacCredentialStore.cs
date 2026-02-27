using Microsoft.Extensions.Logging;
using Ses.Local.Core.Interfaces;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Ses.Local.Workers.Services;

/// <summary>
/// macOS keychain implementation using Security.framework via P/Invoke.
/// Service: SuperEasySoftware.TaskMaster
/// </summary>
[SupportedOSPlatform("macos")]
public sealed class MacCredentialStore : ICredentialStore
{
    private const string ServiceName = "SuperEasySoftware.TaskMaster";
    private readonly ILogger<MacCredentialStore> _logger;

    public MacCredentialStore(ILogger<MacCredentialStore> logger) => _logger = logger;

    public Task<string?> GetAsync(string key, CancellationToken ct = default)
    {
        var serviceBytes = Encoding.UTF8.GetBytes(ServiceName);
        var accountBytes = Encoding.UTF8.GetBytes(key);

        int status = SecKeychainFindGenericPassword(
            IntPtr.Zero,
            (uint)serviceBytes.Length, serviceBytes,
            (uint)accountBytes.Length, accountBytes,
            out uint dataLen, out IntPtr data,
            IntPtr.Zero);

        if (status == 0 && data != IntPtr.Zero)
        {
            try
            {
                var bytes = new byte[dataLen];
                Marshal.Copy(data, bytes, 0, (int)dataLen);
                SecKeychainItemFreeContent(IntPtr.Zero, data);
                return Task.FromResult<string?>(Encoding.UTF8.GetString(bytes));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to read keychain item: {Key}", key);
            }
        }

        return Task.FromResult<string?>(null);
    }

    public Task SetAsync(string key, string value, CancellationToken ct = default)
    {
        var serviceBytes = Encoding.UTF8.GetBytes(ServiceName);
        var accountBytes = Encoding.UTF8.GetBytes(key);
        var valueBytes   = Encoding.UTF8.GetBytes(value);

        // Try update first
        int status = SecKeychainFindGenericPassword(
            IntPtr.Zero,
            (uint)serviceBytes.Length, serviceBytes,
            (uint)accountBytes.Length, accountBytes,
            out _, out _,
            out IntPtr itemRef);

        if (status == 0 && itemRef != IntPtr.Zero)
        {
            SecKeychainItemModifyAttributesAndData(itemRef, IntPtr.Zero, (uint)valueBytes.Length, valueBytes);
        }
        else
        {
            status = SecKeychainAddGenericPassword(
                IntPtr.Zero,
                (uint)serviceBytes.Length, serviceBytes,
                (uint)accountBytes.Length, accountBytes,
                (uint)valueBytes.Length, valueBytes,
                IntPtr.Zero);

            if (status != 0)
                _logger.LogWarning("SecKeychainAddGenericPassword failed with status {Status} for key {Key}", status, key);
        }

        return Task.CompletedTask;
    }

    public Task DeleteAsync(string key, CancellationToken ct = default)
    {
        var serviceBytes = Encoding.UTF8.GetBytes(ServiceName);
        var accountBytes = Encoding.UTF8.GetBytes(key);

        int status = SecKeychainFindGenericPassword(
            IntPtr.Zero,
            (uint)serviceBytes.Length, serviceBytes,
            (uint)accountBytes.Length, accountBytes,
            out _, out _,
            out IntPtr itemRef);

        if (status == 0 && itemRef != IntPtr.Zero)
            SecKeychainItemDelete(itemRef);

        return Task.CompletedTask;
    }

    // P/Invoke declarations
    [DllImport("/System/Library/Frameworks/Security.framework/Security")]
    private static extern int SecKeychainFindGenericPassword(
        IntPtr keychainOrArray,
        uint serviceNameLength, byte[] serviceName,
        uint accountNameLength, byte[] accountName,
        out uint passwordLength, out IntPtr passwordData,
        out IntPtr itemRef);

    [DllImport("/System/Library/Frameworks/Security.framework/Security")]
    private static extern int SecKeychainFindGenericPassword(
        IntPtr keychainOrArray,
        uint serviceNameLength, byte[] serviceName,
        uint accountNameLength, byte[] accountName,
        out uint passwordLength, out IntPtr passwordData,
        IntPtr itemRef);

    [DllImport("/System/Library/Frameworks/Security.framework/Security")]
    private static extern int SecKeychainAddGenericPassword(
        IntPtr keychain,
        uint serviceNameLength, byte[] serviceName,
        uint accountNameLength, byte[] accountName,
        uint passwordLength, byte[] passwordData,
        IntPtr itemRef);

    [DllImport("/System/Library/Frameworks/Security.framework/Security")]
    private static extern int SecKeychainItemModifyAttributesAndData(
        IntPtr itemRef, IntPtr attrList,
        uint passwordLength, byte[] passwordData);

    [DllImport("/System/Library/Frameworks/Security.framework/Security")]
    private static extern int SecKeychainItemDelete(IntPtr itemRef);

    [DllImport("/System/Library/Frameworks/Security.framework/Security")]
    private static extern int SecKeychainItemFreeContent(IntPtr attrList, IntPtr data);
}
