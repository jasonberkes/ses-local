using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Ses.Local.Workers.Workers;

/// <summary>
/// Monitors the ChatGPT Desktop application for new conversations.
///
/// Storage format investigation result: ChatGPT Desktop (com.openai.chat on macOS) stores
/// conversations as encrypted binary .data files in ~/Library/Application Support/com.openai.chat/
/// conversations-v3-{user-id}/. The encryption key is held in the OS Keychain by the app process
/// and is not accessible to third-party readers. Conversation content cannot be extracted locally.
///
/// This watcher detects installation and tracks when new encrypted conversation files appear,
/// allowing the tray UI to prompt users to sync via the manual export path (Settings > Export Data
/// in ChatGPT, then import the ZIP via ses-local). The existing ChatGptExportParser handles import.
/// </summary>
public sealed class ChatGptDesktopWatcher : BackgroundService
{
    private readonly ILogger<ChatGptDesktopWatcher> _logger;

    // Poll every 5 minutes — no need for frequent checks since we only report presence
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(5);

    private int? _lastConversationCount;

    public ChatGptDesktopWatcher(ILogger<ChatGptDesktopWatcher> logger)
    {
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var dataPath = ChatGptDesktopPaths.GetDataPath();
        if (dataPath is null)
        {
            _logger.LogInformation(
                "ChatGPT Desktop not found — watcher idle. " +
                "Install ChatGPT Desktop to enable sync prompts.");
            return;
        }

        _logger.LogInformation(
            "ChatGPT Desktop detected at {DataPath}. " +
            "Note: conversation content is encrypted and cannot be read directly. " +
            "Use Settings > Export Data in ChatGPT, then import the ZIP via ses-local.",
            dataPath);

        ReportConversationCount(dataPath);

        using var timer = new PeriodicTimer(PollInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                ReportConversationCount(dataPath);
            }
        }
        catch (OperationCanceledException) { }
    }

    private void ReportConversationCount(string dataPath)
    {
        try
        {
            var count = ChatGptDesktopPaths.CountConversationFiles(dataPath);
            if (count == _lastConversationCount) return;

            if (_lastConversationCount is { } previous)
            {
                _logger.LogInformation(
                    "ChatGPT Desktop: {Delta} new conversation file(s) detected ({Total} total). " +
                    "To sync, export via Settings > Export Data in ChatGPT and import the ZIP.",
                    count - previous, count);
            }
            else
            {
                _logger.LogInformation(
                    "ChatGPT Desktop: {Total} conversation file(s) found (encrypted, not readable). " +
                    "To sync, export via Settings > Export Data in ChatGPT and import the ZIP.",
                    count);
            }

            _lastConversationCount = count;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "ChatGptDesktopWatcher: could not read conversation directory");
        }
    }
}
