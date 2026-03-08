using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Ses.Local.Core.Enums;
using Ses.Local.Core.Interfaces;
using Ses.Local.Core.Models;
using Ses.Local.Core.Options;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ses.Local.Core.Services;
using Ses.Local.Workers.Services;
using Ses.Local.Workers.Telemetry;

namespace Ses.Local.Workers.Workers;

/// <summary>
/// Watches ~/.claude/projects/**/*.jsonl for new/updated Claude Code sessions.
/// On change: reads new lines from last known file position, parses JSONL events,
/// stores sessions and messages to ILocalDbService.
/// Also watches subagent files at {sessionDir}/subagents/*.jsonl.
/// DEVELOPER SCOPE ONLY — only runs when EnableClaudeCodeSync = true.
/// Full scope-gate via PAT implemented in WI-936.
/// </summary>
public sealed partial class ClaudeCodeWatcher : BackgroundService
{
    private readonly ILocalDbService _db;
    private readonly IClaudeMdGenerator _claudeMdGenerator;
    private readonly WorkItemLinker _workItemLinker;
    private readonly ILogger<ClaudeCodeWatcher> _logger;
    private readonly SesLocalOptions _options;

    // Tracks byte offset read per file to enable incremental parsing.
    // Persisted to disk so restarts resume from the correct position.
    private readonly Dictionary<string, long> _filePositions = new();
    private readonly List<FileSystemWatcher> _watchers = [];

    // Tracks files that failed to parse — skip on subsequent scans until mtime changes.
    // ConcurrentDictionary because FileSystemWatcher callbacks fire on ThreadPool threads.
    private readonly ConcurrentDictionary<string, DateTime> _failedFiles = new();

    private static readonly string PositionsFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".ses", "watcher-positions.json");

    public ClaudeCodeWatcher(
        ILocalDbService db,
        IClaudeMdGenerator claudeMdGenerator,
        WorkItemLinker workItemLinker,
        ILogger<ClaudeCodeWatcher> logger,
        IOptions<SesLocalOptions> options)
    {
        _db                 = db;
        _claudeMdGenerator  = claudeMdGenerator;
        _workItemLinker     = workItemLinker;
        _logger             = logger;
        _options            = options.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.EnableClaudeCodeSync)
        {
            LogDisabledViaOptions(_logger);
            return;
        }

        var projectsRoot = GetProjectsRoot();
        if (!Directory.Exists(projectsRoot))
        {
            LogProjectsDirNotFound(_logger, projectsRoot);
            return;
        }

        LogStarting(_logger, projectsRoot);

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

        LogWatcherActive(_logger, root);
    }

    private async Task HandleFileChangedAsync(string filePath, CancellationToken ct)
    {
        // Real-time change event — clear failed status since the file was just modified
        _failedFiles.TryRemove(filePath, out _);

        try
        {
            await ProcessFileAsync(filePath, ct);
        }
        catch (Exception ex)
        {
            if (!_failedFiles.ContainsKey(filePath))
            {
                LogFileProcessingError(_logger, filePath, ex);
                _failedFiles[filePath] = File.GetLastWriteTimeUtc(filePath);
            }
        }
    }

    // ── Scanning ──────────────────────────────────────────────────────────────

    private async Task ScanAllAsync(string root, CancellationToken ct)
    {
        var files = Directory.GetFiles(root, "*.jsonl", SearchOption.AllDirectories);

        // Cache stale-worktree check per directory to avoid N+1 directory scans
        var staleDirectoryCache = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in files)
        {
            if (ct.IsCancellationRequested) break;

            // Skip worktree session files whose parent worktree directory no longer exists
            var dir = Path.GetDirectoryName(file) ?? string.Empty;
            if (!staleDirectoryCache.TryGetValue(dir, out var isStale))
            {
                isStale = IsStaleWorktreeFile(file);
                staleDirectoryCache[dir] = isStale;
            }
            if (isStale) continue;

            // Skip previously-failed files unless their modification time has changed
            if (_failedFiles.TryGetValue(file, out var failedMtime))
            {
                var currentMtime = File.GetLastWriteTimeUtc(file);
                if (currentMtime <= failedMtime) continue;
                _failedFiles.TryRemove(file, out _); // mtime changed — retry
            }

            try
            {
                await ProcessFileAsync(file, ct);
            }
            catch (Exception ex)
            {
                if (!_failedFiles.ContainsKey(file))
                {
                    LogFileScanError(_logger, file, ex);
                    _failedFiles[file] = File.GetLastWriteTimeUtc(file);
                }
                else
                {
                    LogFileScanErrorDebug(_logger, file, ex);
                }
            }
        }
    }

    /// <summary>
    /// Detects JSONL files from Claude Code worktree sessions whose parent worktree
    /// directory has been removed. These files are stale and will never be updated.
    /// </summary>
    private static bool IsStaleWorktreeFile(string filePath)
    {
        // Worktree paths contain "--claude-worktrees-" or "-worktrees-" in the encoded project dir
        var idx = filePath.IndexOf("-worktrees-", StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return false;

        // Extract the worktree root: everything before the first path separator after the worktree marker
        // The encoded dir looks like: -Users-jason-project--claude-worktrees-name
        // We need to check if the actual worktree directory exists
        var projectsDir = Path.GetDirectoryName(Path.GetDirectoryName(filePath));
        if (projectsDir is null) return false;

        // The encoded project directory is the parent of the JSONL file
        var encodedDir = Path.GetDirectoryName(filePath);
        if (encodedDir is null || !Directory.Exists(encodedDir)) return true;

        // If the encoded directory exists (in ~/.claude/projects/) the file is accessible,
        // but check if the actual worktree source directory on disk still exists.
        // We can't easily decode the full path, so just check if ANY files in this
        // encoded dir have been modified recently (within 7 days) — if not, it's stale.
        try
        {
            var dirInfo = new DirectoryInfo(encodedDir);
            var latestWrite = dirInfo.GetFiles("*.jsonl")
                .Select(f => f.LastWriteTimeUtc)
                .DefaultIfEmpty(DateTime.MinValue)
                .Max();
            return latestWrite < DateTime.UtcNow.AddDays(-7);
        }
        catch
        {
            return true;
        }
    }

    // ── JSONL Processing ──────────────────────────────────────────────────────

    private async Task ProcessFileAsync(string filePath, CancellationToken ct)
    {
        if (!File.Exists(filePath)) return;

        // Project-level exclusion — skip files in excluded project paths
        foreach (var excluded in _options.ExcludedProjectPaths)
        {
            if (!string.IsNullOrEmpty(excluded) &&
                filePath.Contains(excluded, StringComparison.OrdinalIgnoreCase))
            {
                LogProjectExcluded(_logger, filePath);
                return;
            }
        }

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
                var msg = ParseMessage(node, type, _options.EnablePrivateTagStripping);
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
                    ref nextSequence, _options.EnablePrivateTagStripping);
            }
            catch (JsonException ex)
            {
                LogMalformedJsonLine(_logger, filePath, ex);
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

            // Regenerate CLAUDE.md for this project so the next session starts with context
            if (!string.IsNullOrEmpty(cwd))
                _ = _claudeMdGenerator.GenerateAsync(cwd, ct);

            // Auto-link session to WorkItems referenced in branch name, commits, or content (WI-987)
            _ = _workItemLinker.ProcessSessionAsync(session.Id, cwd, ct);
        }

        // Record metrics for this session
        SesLocalMetrics.SessionsProcessed.Add(1, new KeyValuePair<string, object?>("source", "CC"));
        foreach (var obs in newObservations)
            SesLocalMetrics.ObservationsExtracted.Add(1,
                new KeyValuePair<string, object?>("type", obs.ObservationType.ToString()));

        LogSessionProcessed(_logger, newMessages.Count, newObservations.Count, filePath);
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
        ref int nextSequence,
        bool stripPrivateTags = false)
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
                    var toolContent = inputJson.Trim();
                    if (stripPrivateTags) toolContent = PrivateTagStripper.Strip(toolContent);

                    var obs = new ConversationObservation
                    {
                        ObservationType = obsType,
                        ToolName        = toolName,
                        FilePath        = filePath,
                        Content         = toolContent,
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
                    if (stripPrivateTags) content = PrivateTagStripper.Strip(content);

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
                    if (stripPrivateTags) text = PrivateTagStripper.Strip(text);

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
            LogPositionsLoaded(_logger, loaded.Count);
        }
        catch (Exception ex)
        {
            LogPositionsLoadFailed(_logger, ex);
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
            LogPositionsSaveFailed(_logger, ex);
        }
    }

    // ── Parsing helpers ───────────────────────────────────────────────────────

    private static ConversationMessage? ParseMessage(JsonNode node, string? type, bool stripPrivateTags)
    {
        if (type != "user" && type != "assistant") return null;

        var msgNode   = node["message"];
        var role      = msgNode?["role"]?.GetValue<string>() ?? type;
        var timestamp = ParseTimestamp(node["timestamp"]);
        var content   = ExtractContent(msgNode);

        if (string.IsNullOrWhiteSpace(content)) return null;

        if (stripPrivateTags)
            content = PrivateTagStripper.Strip(content);

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

    // ── LoggerMessage source generators (high-perf structured logging) ────────

    [LoggerMessage(Level = LogLevel.Information, Message = "ClaudeCodeWatcher disabled via options")]
    private static partial void LogDisabledViaOptions(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Claude Code projects directory not found: {Path}. Watcher idle.")]
    private static partial void LogProjectsDirNotFound(ILogger logger, string path);

    [LoggerMessage(Level = LogLevel.Information, Message = "ClaudeCodeWatcher starting. Watching: {Path}")]
    private static partial void LogStarting(ILogger logger, string path);

    [LoggerMessage(Level = LogLevel.Debug, Message = "FileSystemWatcher active on {Path}")]
    private static partial void LogWatcherActive(ILogger logger, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Error processing JSONL file: {Path}")]
    private static partial void LogFileProcessingError(ILogger logger, string path, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Error scanning JSONL file: {Path}")]
    private static partial void LogFileScanError(ILogger logger, string path, Exception ex);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Skipping malformed JSONL line in {File}")]
    private static partial void LogMalformedJsonLine(ILogger logger, string file, Exception ex);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Processed {MsgCount} messages, {ObsCount} observations from {File}")]
    private static partial void LogSessionProcessed(ILogger logger, int msgCount, int obsCount, string file);

    [LoggerMessage(Level = LogLevel.Information, Message = "Loaded {Count} file positions from disk")]
    private static partial void LogPositionsLoaded(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to load watcher positions — starting from 0")]
    private static partial void LogPositionsLoadFailed(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to save watcher positions")]
    private static partial void LogPositionsSaveFailed(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Skipping excluded project path: {Path}")]
    private static partial void LogProjectExcluded(ILogger logger, string path);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Skipping previously-failed JSONL file: {Path}")]
    private static partial void LogFileScanErrorDebug(ILogger logger, string path, Exception ex);
}
