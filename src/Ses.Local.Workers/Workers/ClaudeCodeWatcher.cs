using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Ses.Local.Core.Enums;
using Ses.Local.Core.Interfaces;
using Ses.Local.Core.Models;
using Ses.Local.Core.Options;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ses.Local.Workers.Workers;

/// <summary>
/// Watches ~/.claude/projects/**/*.jsonl for new/updated Claude Code sessions.
/// On change: reads new lines from last known file position, parses JSONL events,
/// stores sessions and messages to ILocalDbService.
/// Also watches subagent files at {sessionDir}/subagents/*.jsonl.
/// DEVELOPER SCOPE ONLY — only runs when EnableClaudeCodeSync = true.
/// Full scope-gate via PAT implemented in WI-936.
/// </summary>
public sealed class ClaudeCodeWatcher : BackgroundService
{
    private readonly ILocalDbService _db;
    private readonly ILogger<ClaudeCodeWatcher> _logger;
    private readonly SesLocalOptions _options;

    // Tracks byte offset read per file to enable incremental parsing
    private readonly Dictionary<string, long> _filePositions = new();
    private readonly List<FileSystemWatcher> _watchers = [];

    public ClaudeCodeWatcher(
        ILocalDbService db,
        ILogger<ClaudeCodeWatcher> logger,
        IOptions<SesLocalOptions> options)
    {
        _db      = db;
        _logger  = logger;
        _options = options.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.EnableClaudeCodeSync)
        {
            _logger.LogInformation("ClaudeCodeWatcher disabled via options");
            return;
        }

        var projectsRoot = GetProjectsRoot();
        if (!Directory.Exists(projectsRoot))
        {
            _logger.LogInformation("Claude Code projects directory not found: {Path}. Watcher idle.", projectsRoot);
            return;
        }

        _logger.LogInformation("ClaudeCodeWatcher starting. Watching: {Path}", projectsRoot);

        // Do an initial scan of all existing files
        await ScanAllAsync(projectsRoot, stoppingToken);

        // Watch for new files and changes
        StartWatching(projectsRoot);

        try
        {
            // Periodic re-scan to catch any missed events
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_options.PollingIntervalSeconds));
            while (await timer.WaitForNextTickAsync(stoppingToken))
                await ScanAllAsync(projectsRoot, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
        finally
        {
            foreach (var w in _watchers)
            {
                w.EnableRaisingEvents = false;
                w.Dispose();
            }
            _watchers.Clear();
        }
    }

    // ── File System Watching ──────────────────────────────────────────────────

    private void StartWatching(string root)
    {
        // Watch main session files: {root}/**/*.jsonl
        var mainWatcher = new FileSystemWatcher(root, "*.jsonl")
        {
            IncludeSubdirectories = true,
            NotifyFilter          = NotifyFilters.LastWrite | NotifyFilters.FileName,
            EnableRaisingEvents   = true
        };

        mainWatcher.Changed += (_, e) => _ = HandleFileChangedAsync(e.FullPath, CancellationToken.None);
        mainWatcher.Created += (_, e) => _ = HandleFileChangedAsync(e.FullPath, CancellationToken.None);
        _watchers.Add(mainWatcher);

        _logger.LogDebug("FileSystemWatcher active on {Path}", root);
    }

    private async Task HandleFileChangedAsync(string filePath, CancellationToken ct)
    {
        try
        {
            await ProcessFileAsync(filePath, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error processing JSONL file: {Path}", filePath);
        }
    }

    // ── Scanning ──────────────────────────────────────────────────────────────

    private async Task ScanAllAsync(string root, CancellationToken ct)
    {
        var files = Directory.GetFiles(root, "*.jsonl", SearchOption.AllDirectories);
        foreach (var file in files)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                await ProcessFileAsync(file, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error scanning JSONL file: {Path}", file);
            }
        }
    }

    // ── JSONL Processing ──────────────────────────────────────────────────────

    private async Task ProcessFileAsync(string filePath, CancellationToken ct)
    {
        if (!File.Exists(filePath)) return;

        // Extract session ID from filename (e.g. abc123.jsonl -> abc123)
        var sessionId = Path.GetFileNameWithoutExtension(filePath);
        if (string.IsNullOrWhiteSpace(sessionId)) return;

        // Determine if this is a subagent file
        var isSubagent = filePath.Contains(Path.DirectorySeparatorChar + "subagents" + Path.DirectorySeparatorChar);

        long startPos = _filePositions.TryGetValue(filePath, out var pos) ? pos : 0;

        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var fileLength = stream.Length;

        if (fileLength <= startPos) return; // No new content

        stream.Seek(startPos, SeekOrigin.Begin);
        using var reader = new StreamReader(stream);

        var newMessages = new List<ConversationMessage>();
        ConversationSession? session = null;
        string? cwd = null;

        string? line;
        while ((line = await reader.ReadLineAsync(ct)) is not null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            try
            {
                var node = JsonNode.Parse(line);
                if (node is null) continue;

                var type = node["type"]?.GetValue<string>();

                // Extract session metadata from first user message
                if (type == "user" && session is null)
                {
                    cwd       = node["cwd"]?.GetValue<string>();
                    var ts    = ParseTimestamp(node["timestamp"]);

                    session = new ConversationSession
                    {
                        Source     = ConversationSource.ClaudeCode,
                        ExternalId = sessionId,
                        Title      = BuildTitle(cwd, sessionId, isSubagent),
                        CreatedAt  = ts,
                        UpdatedAt  = ts
                    };
                }

                // Parse messages
                var msg = ParseMessage(node, type);
                if (msg is not null)
                {
                    newMessages.Add(msg);
                    if (session is not null && msg.CreatedAt > session.UpdatedAt)
                        session.UpdatedAt = msg.CreatedAt;
                }
            }
            catch (JsonException ex)
            {
                _logger.LogDebug(ex, "Skipping malformed JSONL line in {File}", filePath);
            }
        }

        // Save new position
        _filePositions[filePath] = stream.Position;

        if (session is null || newMessages.Count == 0) return;

        // Upsert session first to get Id
        await _db.UpsertSessionAsync(session, ct);

        // Assign session Id to all messages
        foreach (var msg in newMessages)
            msg.SessionId = session.Id;

        await _db.UpsertMessagesAsync(newMessages, ct);

        _logger.LogDebug("Processed {Count} new messages from {File}", newMessages.Count, filePath);
    }

    // ── Parsing helpers ───────────────────────────────────────────────────────

    private static ConversationMessage? ParseMessage(JsonNode node, string? type)
    {
        if (type != "user" && type != "assistant") return null;

        var msgNode   = node["message"];
        var role      = msgNode?["role"]?.GetValue<string>() ?? type;
        var timestamp = ParseTimestamp(node["timestamp"]);
        var content   = ExtractContent(msgNode);

        if (string.IsNullOrWhiteSpace(content)) return null;

        int? tokenCount = null;
        var usage = msgNode?["usage"];
        if (usage is not null)
        {
            var input  = usage["input_tokens"]?.GetValue<int>() ?? 0;
            var output = usage["output_tokens"]?.GetValue<int>() ?? 0;
            tokenCount = input + output;
        }

        return new ConversationMessage
        {
            Role       = role,
            Content    = content,
            CreatedAt  = timestamp,
            TokenCount = tokenCount
        };
    }

    private static string ExtractContent(JsonNode? msgNode)
    {
        if (msgNode is null) return string.Empty;

        // Simple string content (guard: only call GetValue on a scalar JsonValue node)
        var contentNode = msgNode["content"];
        var contentStr  = contentNode is JsonValue ? contentNode.GetValue<string>() : null;
        if (!string.IsNullOrEmpty(contentStr)) return contentStr;

        // Array of content blocks
        var contentArr = msgNode["content"]?.AsArray();
        if (contentArr is null) return string.Empty;

        var parts = new List<string>();
        foreach (var block in contentArr)
        {
            var blockType = block?["type"]?.GetValue<string>();
            switch (blockType)
            {
                case "text":
                    var text = block?["text"]?.GetValue<string>();
                    if (!string.IsNullOrEmpty(text)) parts.Add(text);
                    break;
                case "tool_use":
                    var toolName  = block?["name"]?.GetValue<string>() ?? "tool";
                    var toolInput = block?["input"]?.ToJsonString() ?? "{}";
                    parts.Add($"[tool_use:{toolName}] {toolInput}");
                    break;
                case "tool_result":
                    var resultContent = block?["content"]?.GetValue<string>()
                                     ?? block?["content"]?.ToJsonString()
                                     ?? string.Empty;
                    parts.Add($"[tool_result] {resultContent}");
                    break;
                case "thinking":
                    var thinking = block?["thinking"]?.GetValue<string>();
                    if (!string.IsNullOrEmpty(thinking))
                        parts.Add($"[thinking] {thinking}");
                    break;
            }
        }

        return string.Join("\n", parts);
    }

    private static DateTime ParseTimestamp(JsonNode? node)
    {
        if (node is null) return DateTime.UtcNow;
        var str = node.GetValue<string>();
        return DateTime.TryParse(str, out var dt) ? dt.ToUniversalTime() : DateTime.UtcNow;
    }

    private static string BuildTitle(string? cwd, string sessionId, bool isSubagent)
    {
        var prefix = isSubagent ? "[subagent] " : string.Empty;
        if (!string.IsNullOrEmpty(cwd))
        {
            var dirName = Path.GetFileName(cwd.TrimEnd(Path.DirectorySeparatorChar));
            return $"{prefix}{dirName}/{sessionId[..Math.Min(8, sessionId.Length)]}";
        }
        return $"{prefix}{sessionId[..Math.Min(8, sessionId.Length)]}";
    }

    private static string GetProjectsRoot()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".claude", "projects");
    }
}
