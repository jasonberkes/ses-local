using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Ses.Local.Workers.Services;

/// <summary>
/// Extracts the Claude.ai sessionKey cookie from Claude Desktop's Chromium cookie store.
///
/// Mac decryption flow:
///   1. Get password from Keychain: service="Claude Safe Storage"
///   2. Derive AES key: PBKDF2-SHA1(password, salt="saltysalt", iterations=1003, keyLen=16)
///   3. Decrypt: AES-128-CBC with IV = 16 zero bytes
///   4. Strip 3-byte "v10" prefix from decrypted bytes
///
/// Windows: use DPAPI (ProtectedData.Unprotect).
/// </summary>
public sealed class ClaudeSessionCookieExtractor
{
    private readonly ILogger<ClaudeSessionCookieExtractor> _logger;

    private static readonly string[] CookieNames =
        ["sessionKey", "__Host-next-auth.session-token", "__Secure-next-auth.session-token"];

    public ClaudeSessionCookieExtractor(ILogger<ClaudeSessionCookieExtractor> logger)
        => _logger = logger;

    public string? Extract()
    {
        var cookiePath = GetCookiePath();
        if (!File.Exists(cookiePath))
        {
            _logger.LogDebug("Claude Desktop cookie store not found: {Path}", cookiePath);
            return null;
        }

        // Copy to temp — Electron may have the file open
        var tempPath = Path.Combine(Path.GetTempPath(), $"ses-cookies-{Guid.NewGuid()}.db");
        try
        {
            File.Copy(cookiePath, tempPath, overwrite: true);
            return ExtractFromCopy(tempPath);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to extract Claude Desktop session cookie");
            return null;
        }
        finally
        {
            try { File.Delete(tempPath); } catch { }
        }
    }

    private string? ExtractFromCopy(string tempPath)
    {
        using var conn = new SqliteConnection($"Data Source={tempPath};Mode=ReadOnly;");
        conn.Open();

        foreach (var name in CookieNames)
        {
            using var cmd = new SqliteCommand(
                "SELECT encrypted_value FROM cookies WHERE host_key LIKE '%claude.ai' AND name = @name LIMIT 1",
                conn);
            cmd.Parameters.AddWithValue("@name", name);

            var rawBytes = cmd.ExecuteScalar() as byte[];
            if (rawBytes is null || rawBytes.Length == 0) continue;

            var decrypted = TryDecrypt(rawBytes);
            if (!string.IsNullOrEmpty(decrypted))
            {
                _logger.LogDebug("Session cookie extracted (name={Name}, length={Len})", name, decrypted.Length);
                return decrypted;
            }
        }

        _logger.LogDebug("No usable session cookie found in Claude Desktop cookie store");
        return null;
    }

    private string? TryDecrypt(byte[] encryptedValue)
    {
        if (encryptedValue.Length < 4) return null;

        // Check for v10/v11 Chromium prefix
        bool hasV10Prefix = encryptedValue[0] == 'v' && encryptedValue[1] == '1'
            && (encryptedValue[2] == '0' || encryptedValue[2] == '1');

        if (hasV10Prefix)
        {
            var cipherBytes = encryptedValue[3..]; // strip "v10" or "v11"

            if (OperatingSystem.IsMacOS())
                return TryDecryptMac(cipherBytes);

            if (OperatingSystem.IsWindows())
                return TryDecryptWindows(cipherBytes);

            return null;
        }

        // Plain text (no prefix) — some older Electron builds
        var asString = Encoding.UTF8.GetString(encryptedValue);
        if (asString.Length > 10 && !asString.Contains('\0'))
            return asString;

        return null;
    }

    // ── Mac: PBKDF2 + AES-128-CBC ─────────────────────────────────────────────

    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    private string? TryDecryptMac(byte[] cipherBytes)
    {
        // Skip on CI — no Keychain available
        if (Environment.GetEnvironmentVariable("CI") == "true")
            return null;

        try
        {
            var password = GetMacKeychainPassword();
            if (password is null) return null;

            // PBKDF2-SHA1(password, salt="saltysalt", iterations=1003, keyLen=16)
            var key = Rfc2898DeriveBytes.Pbkdf2(
                password:       Encoding.UTF8.GetBytes(password),
                salt:           Encoding.UTF8.GetBytes("saltysalt"),
                iterations:     1003,
                hashAlgorithm:  HashAlgorithmName.SHA1,
                outputLength:   16);

            // AES-128-CBC, IV = 16 zero bytes
            using var aes = Aes.Create();
            aes.Key  = key;
            aes.IV   = new byte[16]; // all zeros
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;

            using var decryptor = aes.CreateDecryptor();
            var decrypted = decryptor.TransformFinalBlock(cipherBytes, 0, cipherBytes.Length);
            return Encoding.UTF8.GetString(decrypted);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Mac cookie decryption failed");
            return null;
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    private string? GetMacKeychainPassword()
    {
        try
        {
            // Use the `security` command-line tool — simpler than P/Invoke for POSIX
            // and avoids the Keychain access dialog (same process = auto-approved)
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName               = "/usr/bin/security",
                Arguments              = "find-generic-password -w -s \"Claude Safe Storage\"",
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false
            };
            using var proc = System.Diagnostics.Process.Start(psi)!;

            // 5-second timeout — prevents hanging on CI where no Keychain dialog can be answered
            if (!proc.WaitForExit(5000))
            {
                proc.Kill();
                _logger.LogDebug("Keychain access timed out (CI environment?)");
                return null;
            }

            var password = proc.StandardOutput.ReadToEnd().Trim();
            if (proc.ExitCode != 0 || string.IsNullOrEmpty(password))
            {
                _logger.LogDebug("Keychain entry 'Claude Safe Storage' not found");
                return null;
            }

            return password;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to read Keychain password");
            return null;
        }
    }

    // ── Windows: DPAPI ────────────────────────────────────────────────────────

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private string? TryDecryptWindows(byte[] cipherBytes)
    {
        try
        {
            var decrypted = System.Security.Cryptography.ProtectedData.Unprotect(
                cipherBytes, null, System.Security.Cryptography.DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(decrypted);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Windows DPAPI decryption failed");
            return null;
        }
    }

    public static string GetCookiePath()
    {
        if (OperatingSystem.IsWindows())
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return Path.Combine(appData, "Claude", "Cookies");
        }
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, "Library", "Application Support", "Claude", "Cookies");
    }
}
