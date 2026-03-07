namespace Ses.Local.Workers.Workers;

/// <summary>
/// Resolves paths for the ChatGPT native desktop application.
///
/// Investigation results (2026-03-06):
/// - macOS native app bundle: com.openai.chat
/// - Data path: ~/Library/Application Support/com.openai.chat/
/// - Conversations: conversations-v3-{user-id}/{uuid}.data (one file per conversation)
/// - The ~/Library/Application Support/ChatGPT/ directory exists but is always empty.
/// - .data files use a proprietary encrypted binary format (key stored in OS Keychain).
///   Content is inaccessible without the app's decryption key.
/// - We can detect installation, count conversations, and watch for new ones,
///   but cannot extract conversation content from local storage.
/// </summary>
public static class ChatGptDesktopPaths
{
    // macOS native app (com.openai.chat) — actual conversation storage
    internal const string MacOSBundleDir = "com.openai.chat";

    // Windows Electron app path
    internal const string WindowsAppDir = "ChatGPT";

    // Conversations directory prefix within the app data dir
    internal const string ConversationsDirPrefix = "conversations-v3-";

    public static string? GetDataPath()
    {
        if (OperatingSystem.IsMacOS())
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Library", "Application Support", MacOSBundleDir);
            return Directory.Exists(path) ? path : null;
        }

        if (OperatingSystem.IsWindows())
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                WindowsAppDir);
            return Directory.Exists(path) ? path : null;
        }

        return null;
    }

    internal static bool IsInstalled() => GetDataPath() is not null;

    /// <summary>
    /// Returns all conversations-v3-* directories found within the data path.
    /// Each directory corresponds to one user account.
    /// </summary>
    public static IReadOnlyList<string> GetConversationDirs(string dataPath) =>
        Directory.GetDirectories(dataPath, $"{ConversationsDirPrefix}*");

    /// <summary>
    /// Counts the total number of encrypted .data conversation files across all user directories.
    /// </summary>
    public static int CountConversationFiles(string dataPath)
    {
        var dirs = GetConversationDirs(dataPath);
        return dirs.Sum(dir => Directory.GetFiles(dir, "*.data").Length);
    }
}
