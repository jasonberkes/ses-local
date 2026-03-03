using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Ses.Local.Workers.Services;

/// <summary>
/// Detects the export format from file content and delegates to the appropriate
/// <see cref="IConversationImporter"/> implementation.
///
/// Detection rules (evaluated in order):
///   1. ZIP file → inspect contained file names:
///      • Contains "conversations.json"   → ChatGPT export
///      • Contains "My Activity.json" in a Gemini-related path → Gemini export
///   2. JSON file → peek at structure:
///      • Has "mapping" key in first object → ChatGPT conversations.json
///      • Has "chat_messages" or "uuid" key → Claude export
///      • Has "subtitles" or "header" with Gemini Apps → Gemini activity JSON
///      • Array of objects with "mapping" key → ChatGPT
///      • Otherwise → Claude (default)
/// </summary>
public sealed class ConversationImportDispatcher : IConversationImporter
{
    private readonly ClaudeExportParser _claude;
    private readonly ChatGptExportParser _chatGpt;
    private readonly GeminiExportParser _gemini;
    private readonly ILogger<ConversationImportDispatcher> _logger;

    public ConversationImportDispatcher(
        ClaudeExportParser claude,
        ChatGptExportParser chatGpt,
        GeminiExportParser gemini,
        ILogger<ConversationImportDispatcher> logger)
    {
        _claude  = claude;
        _chatGpt = chatGpt;
        _gemini  = gemini;
        _logger  = logger;
    }

    /// <inheritdoc/>
    public async Task<ImportResult> ImportAsync(
        string filePath,
        IProgress<ImportProgress>? progress = null,
        CancellationToken ct = default,
        ImportFilterOptions? importOptions = null)
    {
        var format = DetectFormat(filePath);
        _logger.LogInformation("Detected import format: {Format} for {Path}", format, filePath);

        return format switch
        {
            ImportFormat.ChatGpt  => await _chatGpt.ImportAsync(filePath, progress, ct, importOptions),
            ImportFormat.Gemini   => await _gemini.ImportAsync(filePath, progress, ct, importOptions),
            _                     => await _claude.ImportAsync(filePath, progress, ct, importOptions)
        };
    }

    // ── Format detection ──────────────────────────────────────────────────────

    /// <summary>
    /// Detects the export format without a full parse.
    /// Returns <see cref="ImportFormat.Claude"/> as the safe default for unknown files.
    /// </summary>
    public static ImportFormat DetectFormat(string filePath)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();

        if (ext == ".zip")
            return DetectZipFormat(filePath);

        return DetectJsonFormat(filePath);
    }

    private static ImportFormat DetectZipFormat(string filePath)
    {
        try
        {
            using var zip = ZipFile.OpenRead(filePath);
            var names = zip.Entries.Select(e => e.FullName).ToList();

            // ChatGPT: contains conversations.json at root or in a subfolder
            if (names.Any(n => string.Equals(
                    Path.GetFileName(n), "conversations.json",
                    StringComparison.OrdinalIgnoreCase)))
                return ImportFormat.ChatGpt;

            // Gemini: contains My Activity.json (typically under "Gemini Apps Activity/")
            if (names.Any(n => string.Equals(
                    Path.GetFileName(n), "My Activity.json",
                    StringComparison.OrdinalIgnoreCase)))
                return ImportFormat.Gemini;
        }
        catch
        {
            // Fall through to Claude default
        }
        return ImportFormat.Claude;
    }

    private static ImportFormat DetectJsonFormat(string filePath)
    {
        try
        {
            // Read the first 4 KB — enough to identify the root structure
            Span<char> buffer = stackalloc char[4096];
            int charsRead;

            using var reader = new StreamReader(filePath, Encoding.UTF8,
                detectEncodingFromByteOrderMarks: true, bufferSize: 4096);
            charsRead = reader.Read(buffer);
            var snippet = new string(buffer[..charsRead]);

            // Quick heuristic: look for distinguishing keys
            if (snippet.Contains("\"mapping\"", StringComparison.OrdinalIgnoreCase))
                return ImportFormat.ChatGpt;

            if (snippet.Contains("\"chat_messages\"", StringComparison.OrdinalIgnoreCase) ||
                snippet.Contains("\"uuid\"", StringComparison.OrdinalIgnoreCase))
                return ImportFormat.Claude;

            if (snippet.Contains("\"subtitles\"", StringComparison.OrdinalIgnoreCase) ||
                snippet.Contains("Gemini Apps", StringComparison.OrdinalIgnoreCase))
                return ImportFormat.Gemini;
        }
        catch
        {
            // Unreadable — fall through to Claude default
        }
        return ImportFormat.Claude;
    }

    /// <summary>Returns a human-readable label for progress display.</summary>
    public static string FormatLabel(ImportFormat format) => format switch
    {
        ImportFormat.ChatGpt => "ChatGPT",
        ImportFormat.Gemini  => "Gemini",
        _                    => "Claude"
    };
}

/// <summary>Supported import source formats.</summary>
public enum ImportFormat
{
    Claude,
    ChatGpt,
    Gemini
}
