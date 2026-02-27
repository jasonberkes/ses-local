using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;


namespace Ses.Local.Workers.Services;

/// <summary>
/// Extracts Claude conversation UUIDs from Claude Desktop's Local Storage LevelDB files.
///
/// The LDB files store Local Storage keys as plaintext, including entries like:
///   LSS-{conversationUuid}:textInput
///   LSS-{conversationUuid}:files
///   LSS-{conversationUuid}:syncSourceUuids
///
/// These cover ALL conversations Claude Desktop has locally:
///   - Desktop-native conversations
///   - Conversations synced from iPhone
///   - Conversations synced from web
///
/// No binary LevelDB parsing is required — the keys appear as readable strings
/// in the .ldb files and can be extracted with a simple byte scan.
/// </summary>
public sealed class LevelDbUuidExtractor
{
    private readonly ILogger<LevelDbUuidExtractor> _logger;

    // Matches LSS-{uuid}: where uuid is a standard hyphenated UUID
    private static readonly Regex UuidPattern = new(
        @"LSS-([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}):",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public LevelDbUuidExtractor(ILogger<LevelDbUuidExtractor> logger) => _logger = logger;

    /// <summary>
    /// Scans all .ldb files in the given directory and returns unique conversation UUIDs.
    /// Copies files to temp before reading to avoid lock contention with Electron.
    /// </summary>
    public IReadOnlyList<string> ExtractUuids(string levelDbPath)
    {
        if (!Directory.Exists(levelDbPath))
            return Array.Empty<string>();

        var uuids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ldbFiles = Directory.GetFiles(levelDbPath, "*.ldb");

        foreach (var file in ldbFiles)
        {
            try
            {
                ExtractFromFile(file, uuids);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not read LDB file {File} — skipping", Path.GetFileName(file));
            }
        }

        _logger.LogDebug("LevelDB scan: {Count} unique UUIDs found in {Files} files",
            uuids.Count, ldbFiles.Length);
        return uuids.ToList();
    }

    private static void ExtractFromFile(string filePath, HashSet<string> uuids)
    {
        // Copy to temp to avoid lock contention with Electron (same pattern as cookie extractor)
        var tempPath = Path.Combine(Path.GetTempPath(), $"ses-ldb-{Guid.NewGuid()}.tmp");
        try
        {
            File.Copy(filePath, tempPath, overwrite: true);
            var bytes = File.ReadAllBytes(tempPath);

            // Scan for printable ASCII strings of length >= 12 (minimum UUID length fragment)
            // and run the UUID pattern against them
            var text = ExtractPrintableStrings(bytes);
            foreach (Match m in UuidPattern.Matches(text))
            {
                uuids.Add(m.Groups[1].Value.ToLowerInvariant());
            }
        }
        finally
        {
            try { File.Delete(tempPath); } catch { }
        }
    }

    /// <summary>
    /// Extracts printable ASCII sequences (length >= 8) from raw bytes.
    /// Equivalent to the Unix `strings` command.
    /// </summary>
    private static string ExtractPrintableStrings(byte[] bytes)
    {
        var sb      = new StringBuilder(bytes.Length);
        var current = new StringBuilder(64);

        foreach (byte b in bytes)
        {
            if (b >= 0x20 && b < 0x7F) // printable ASCII
            {
                current.Append((char)b);
            }
            else
            {
                if (current.Length >= 8)
                    sb.Append(current).Append('\n');
                current.Clear();
            }
        }
        if (current.Length >= 8)
            sb.Append(current);

        return sb.ToString();
    }

    public static string GetLevelDbPath()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsWindows())
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return Path.Combine(appData, "Claude", "Local Storage", "leveldb");
        }
        return Path.Combine(home, "Library", "Application Support", "Claude",
            "Local Storage", "leveldb");
    }
}
