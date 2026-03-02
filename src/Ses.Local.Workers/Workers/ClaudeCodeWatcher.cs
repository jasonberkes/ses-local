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

    // Tracks byte offset read per file to enable incremental parsing.
    // Persisted to disk so restarts resume from the correct position.
    private readonly Dictionary<string, long> _filePositions = new();
    private readonly List<FileSystemWatcher> _watchers = [];

    private static readonly string PositionsFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".ses", "watcher-positions.json");

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

        // Load persisted positions so restarts resume from the correct offset
        LoadPositions();

        // Brief delay to allow DI/DB to fully initialize before first scan
        await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);

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

        var newMessages     = new List<ConversationMessage>();
        var newObservations = new List<ConversationObservation>();

        // Maps Claude's internal tool_use id → index in newObservations (for parent linking)
        var toolUseClaudeIds = new Dictionary<string, int>();
        // Maps newObservations index → Claude tool_use_id that the tool_result references
        var pendingParentRefs = new Dictionary<int, string>();

        ConversationSession? session = null;
        string? cwd = null;
        int nextSequence = 0;

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
                    cwd    = node["cwd"]?.GetValue<string>();
                    var ts = ParseTimestamp(node["timestamp"]);

                    session = new ConversationSession
                    {
                        Source     = ConversationSource.ClaudeCode,
                        ExternalId = sessionId,
                        Title      = BuildTitle(cwd, sessionId, isSubagent),
                        CreatedAt  = ts,
                        UpdatedAt  = ts
                    };
                }

                // Parse legacy flat messages (unchanged — backward compatible)
                var msg = ParseMessage(node, type);
                if (msg is not null)
                {
                    newMessages.Add(msg);
                    if (session is not null && msg.CreatedAt > session.UpdatedAt)
                        session.UpdatedAt = msg.CreatedAt;
                }

                // Parse structured observations from content blocks
                var timestamp = ParseTimestamp(node["timestamp"]);
                ExtractObservations(
                    node, type, timestamp,
                    newObservations, toolUseClaudeIds, pendingParentRefs,
                    ref nextSequence);
            }
            catch (JsonException ex)
            {
                _logger.LogDebug(ex, "Skipping malformed JSONL line in {File}", filePath);
            }
        }

        // Save new position and persist to disk
        _filePositions[filePath] = stream.Position;
        SavePositions();

        if (session is null || newMessages.Count == 0) return;

        // Upsert session first to get Id
        await _db.UpsertSessionAsync(session, ct);

        // Assign session Id to all messages and observations
        foreach (var msg in newMessages)
            msg.SessionId = session.Id;

        foreach (var obs in newObservations)
            obs.SessionId = session.Id;

        await _db.UpsertMessagesAsync(newMessages, ct);

        if (newObservations.Count > 0)
        {
            // UpsertObservationsAsync syncs back Id on each observation
            await _db.UpsertObservationsAsync(newObservations, ct);

            // Resolve tool_result → tool_use parent links
            var parentUpdates = ResolveParentLinks(newObservations, toolUseClaudeIds, pendingParentRefs);
            if (parentUpdates.Count > 0)
                await _db.UpdateObservationParentsAsync(parentUpdates, ct);
        }

        _logger.LogDebug("Processed {MsgCount} messages, {ObsCount} observations from {File}",
            newMessages.Count, newObservations.Count, filePath);
    }

    // ── Observation Extraction ────────────────────────────────────────────────

    /// <summary>
    /// Extracts structured observations from a single JSONL line's content blocks,
    /// appending to <paramref name="observations"/> and updating tracking dictionaries.
    /// </summary>
    private static void ExtractObservations(
        JsonNode node,
        string? type,
        DateTime timestamp,
        List<ConversationObservation> observations,
        Dictionary<string, int> toolUseClaudeIds,
        Dictionary<int, string> pendingParentRefs,
        ref int nextSequence)
    {
        if (type != "user" && type != "assistant") return;

        var msgNode    = node["message"];
        // content may be a plain string (user text) — only process array-form blocks
        if (msgNode?["content"] is not JsonArray contentArr) return;

        foreach (var block in contentArr)
        {
            if (block is null) continue;
            var blockType = block["type"]?.GetValue<string>();

            switch (blockType)
            {
                case "tool_use":
                {
                    var toolName    = block["name"]?.GetValue<string>() ?? string.Empty;
                    var inputNode   = block["input"];
                    var inputJson   = inputNode?.ToJsonString() ?? "{}";
                    var filePath    = ExtractFilePath(inputNode);
                    var obsType     = ClassifyToolUse(toolName, inputNode);
                    var claudeId    = block["id"]?.GetValue<string>();

                    var obs = new ConversationObservation
                    {
                        ObservationType = obsType,
                        ToolName        = toolName,
                        FilePath        = filePath,
                        Content         = inputJson.Trim(),
                        SequenceNumber  = nextSequence,
                        CreatedAt       = timestamp
                    };

                    var idx = observations.Count;
                    observations.Add(obs);

                    if (!string.IsNullOrEmpty(claudeId))
                        toolUseClaudeIds[claudeId] = idx;

                    nextSequence++;
                    break;
                }

                case "tool_result":
                {
                    var rawContent = block["content"];
                    var content    = rawContent is JsonValue
                        ? rawContent.GetValue<string>()
                        : rawContent?.ToJsonString() ?? string.Empty;
                    content = content.Trim();

                    var obsType    = ClassifyToolResult(content);
                    var parentRef  = block["tool_use_id"]?.GetValue<string>();

                    var obs = new ConversationObservation
                    {
                        ObservationType = obsType,
                        Content         = content,
                        SequenceNumber  = nextSequence,
                        CreatedAt       = timestamp
                    };

                    var idx = observations.Count;
                    observations.Add(obs);

                    if (!string.IsNullOrEmpty(parentRef))
                        pendingParentRefs[idx] = parentRef;

                    nextSequence++;
                    break;
                }

                case "text":
                {
                    var text = block["text"]?.GetValue<string>() ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(text)) continue;

                    observations.Add(new ConversationObservation
                    {
                        ObservationType = ObservationType.Text,
                        Content         = text.Trim(),
                        SequenceNumber  = nextSequence,
                        CreatedAt       = timestamp
                    });
                    nextSequence++;
                    break;
                }

                case "thinking":
                {
                    var thinking = block["thinking"]?.GetValue<string>() ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(thinking)) continue;

                    observations.Add(new ConversationObservation
                    {
                        ObservationType = ObservationType.Thinking,
                        Content         = thinking.Trim(),
                        SequenceNumber  = nextSequence,
                        CreatedAt       = timestamp
                    });
                    nextSequence++;
                    break;
                }
            }
        }
    }

    /// <summary>
    /// After UpsertObservationsAsync has synced back DB Ids, builds the list of
    /// (tool_result observationId, tool_use parentId) pairs for UpdateObservationParentsAsync.
    /// </summary>
    private static List<(long observationId, long parentId)> ResolveParentLinks(
        List<ConversationObservation> observations,
        Dictionary<string, int> toolUseClaudeIds,
        Dictionary<int, string> pendingParentRefs)
    {
        // Build reverse map: claudeToolUseId → DB id (now available after upsert)
        var claudeIdToDbId = new Dictionary<string, long>(toolUseClaudeIds.Count);
        foreach (var (claudeId, idx) in toolUseClaudeIds)
        {
            var dbId = observations[idx].Id;
            if (dbId > 0)
                claudeIdToDbId[claudeId] = dbId;
        }

        var result = new List<(long, long)>();
        foreach (var (resultIdx, parentClaudeId) in pendingParentRefs)
        {
            if (!claudeIdToDbId.TryGetValue(parentClaudeId, out var parentDbId)) continue;
            var resultDbId = observations[resultIdx].Id;
            if (resultDbId > 0)
                result.Add((resultDbId, parentDbId));
        }
        return result;
    }

    // ── Classification helpers ────────────────────────────────────────────────

    private static ObservationType ClassifyToolUse(string toolName, JsonNode? inputNode)
    {
        if (!string.Equals(toolName, "Bash", StringComparison.OrdinalIgnoreCase))
            return ObservationType.ToolUse;

        var command = inputNode?["command"]?.GetValue<string>() ?? string.Empty;

        if (command.Contains("git commit", StringComparison.OrdinalIgnoreCase))
            return ObservationType.GitCommit;

        if (command.Contains("dotnet test", StringComparison.OrdinalIgnoreCase) ||
            command.Contains("npm test",    StringComparison.OrdinalIgnoreCase) ||
            command.Contains("pytest",      StringComparison.OrdinalIgnoreCase) ||
            command.Contains("yarn test",   StringComparison.OrdinalIgnoreCase))
            return ObservationType.TestResult;

        return ObservationType.ToolUse;
    }

    private static ObservationType ClassifyToolResult(string content)
    {
        if (content.Contains("error",     StringComparison.OrdinalIgnoreCase) ||
            content.Contains("exception", StringComparison.OrdinalIgnoreCase) ||
            content.Contains("failed",    StringComparison.OrdinalIgnoreCase))
            return ObservationType.Error;

        return ObservationType.ToolResult;
    }

    /// <summary>
    /// Extracts a file path from a tool_use input node by checking common key names.
    /// </summary>
    internal static string? ExtractFilePath(JsonNode? inputNode)
    {
        if (inputNode is null) return null;
        foreach (var key in new[] { "path", "file_path", "filename" })
        {
            var val = inputNode[key]?.GetValue<string>();
            if (!string.IsNullOrEmpty(val)) return val;
        }
        return null;
    }

    // ── Position Persistence ──────────────────────────────────────────────────

    private void LoadPositions()
    {
        try
        {
            if (!File.Exists(PositionsFilePath)) return;
            var json = File.ReadAllText(PositionsFilePath);
            var loaded = JsonSerializer.Deserialize<Dictionary<string, long>>(json);
            if (loaded is null) return;
            foreach (var (k, v) in loaded)
                _filePositions[k] = v;
            _logger.LogInformation("Loaded {Count} file positions from disk", loaded.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load watcher positions — starting from 0");
        }
    }

    private void SavePositions()
    {
        try
        {
            var json = JsonSerializer.Serialize(_filePositions);
            File.WriteAllText(PositionsFilePath, json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save watcher positions");
        }
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
