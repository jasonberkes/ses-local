using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Ses.Local.Core.Interfaces;
using Ses.Local.Core.Models;

namespace Ses.Local.Workers.Services;

/// <summary>
/// Detects TaskMaster WorkItem references in Claude Code sessions and persists them
/// as conv_workitem_links entries (WI-987).
///
/// Sources scanned (highest to lowest confidence):
///   1. Branch name read from {cwd}/.git/HEAD             — confidence 1.0
///   2. GitCommit observation content (commit messages)   — confidence 0.9
///   3. Conversation content text (WI-NNN mentions)       — confidence 0.7
///
/// WI pattern: WI-\d+, wi-\d+, workitem-\d+ (case-insensitive)
/// </summary>
public sealed partial class WorkItemLinker
{
    [GeneratedRegex(@"(?:WI|wi|workitem)[_-](\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex WorkItemPattern();

    private readonly ILocalDbService _db;
    private readonly ILogger<WorkItemLinker> _logger;

    public WorkItemLinker(ILocalDbService db, ILogger<WorkItemLinker> logger)
    {
        _db     = db;
        _logger = logger;
    }

    /// <summary>
    /// Scans <paramref name="sessionId"/> for WorkItem references and persists any found links.
    /// Safe to call multiple times — the DB UNIQUE constraint prevents duplicate links.
    /// </summary>
    /// <param name="sessionId">The session to process.</param>
    /// <param name="cwd">The working directory of the session (used to read .git/HEAD).</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task ProcessSessionAsync(long sessionId, string? cwd, CancellationToken ct = default)
    {
        // Tracks best confidence per workitem_id to deduplicate before insert
        var best = new Dictionary<int, WorkItemLink>();

        // ── Source 1: Branch name from .git/HEAD ─────────────────────────────
        if (!string.IsNullOrEmpty(cwd))
        {
            var branchName = ReadGitBranch(cwd);
            if (!string.IsNullOrEmpty(branchName))
            {
                foreach (var id in ExtractWorkItemIds(branchName))
                    Merge(best, sessionId, id, "branch_name", 1.0);
            }
        }

        // ── Sources 2 & 3: Observations ──────────────────────────────────────
        var observations = await _db.GetObservationsAsync(sessionId, ct);

        foreach (var obs in observations)
        {
            if (string.IsNullOrWhiteSpace(obs.Content)) continue;

            // Source 2: GitCommit observations
            if (obs.ObservationType == Core.Enums.ObservationType.GitCommit)
            {
                foreach (var id in ExtractWorkItemIds(obs.Content))
                    Merge(best, sessionId, id, "commit_message", 0.9);
            }

            // Source 3: All text content
            foreach (var id in ExtractWorkItemIds(obs.Content))
                Merge(best, sessionId, id, "conversation_content", 0.7);
        }

        if (best.Count == 0)
            return;

        var links = best.Values.ToList();
        await _db.CreateWorkItemLinksAsync(links, ct);

        LogLinksCreated(_logger, sessionId, links.Count,
            string.Join(", ", links.Select(l => $"WI-{l.WorkItemId}({l.LinkSource})")));
    }

    // ── Static helpers ────────────────────────────────────────────────────────

    /// <summary>
    /// Reads the current branch name from {dir}/.git/HEAD.
    /// Returns null if the file doesn't exist or the repo is in detached HEAD state.
    /// </summary>
    internal static string? ReadGitBranch(string cwd)
    {
        try
        {
            var headPath = Path.Combine(cwd, ".git", "HEAD");
            if (!File.Exists(headPath)) return null;

            // Content: "ref: refs/heads/claude/wi-987-linking\n" or a commit SHA
            var content = File.ReadAllText(headPath).Trim();
            const string prefix = "ref: refs/heads/";
            if (!content.StartsWith(prefix, StringComparison.Ordinal)) return null;

            return content[prefix.Length..];
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Extracts all unique WorkItem IDs from a string using the WI-NNN pattern.</summary>
    internal static IEnumerable<int> ExtractWorkItemIds(string text)
    {
        var seen = new HashSet<int>();
        foreach (Match m in WorkItemPattern().Matches(text))
        {
            if (int.TryParse(m.Groups[1].Value, out var id) && seen.Add(id))
                yield return id;
        }
    }

    /// <summary>
    /// Merges a detected WorkItem link into the best-confidence dictionary.
    /// For the same (session, workitem_id) pair, keeps the highest confidence entry,
    /// updating the link_source to match.
    /// </summary>
    private static void Merge(
        Dictionary<int, WorkItemLink> best,
        long sessionId, int workItemId,
        string source, double confidence)
    {
        if (best.TryGetValue(workItemId, out var existing))
        {
            if (confidence > existing.Confidence)
            {
                existing.Confidence = confidence;
                existing.LinkSource = source;
            }
        }
        else
        {
            best[workItemId] = new WorkItemLink
            {
                SessionId  = sessionId,
                WorkItemId = workItemId,
                LinkSource = source,
                Confidence = confidence,
                CreatedAt  = DateTime.UtcNow
            };
        }
    }

    // ── LoggerMessage source generators ──────────────────────────────────────

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "WorkItemLinker: session {SessionId} → {Count} WorkItem link(s): {Details}")]
    private static partial void LogLinksCreated(ILogger logger, long sessionId, int count, string details);
}
