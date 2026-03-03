using Microsoft.Extensions.Logging;
using Ses.Local.Core.Enums;
using Ses.Local.Core.Interfaces;
using Ses.Local.Core.Models;

namespace Ses.Local.Workers.Services;

/// <summary>
/// Heuristic cross-session conversation linker (WI-986).
/// For a given session, finds related sessions from any surface (ClaudeCode, Desktop, Import)
/// using time-windowed, project-scoped heuristics — no AI required.
///
/// Heuristics applied:
///   same_project  (0.8) — CC project directory name matches another session's title
///   continuation  (0.7) — Titles are very similar (token Jaccard ≥ threshold)
///   same_topic    (var) — Shared file paths (overlap ratio) or concept overlap (Jaccard)
///   temporal      (0.6) — Different surfaces within 30 minutes of each other
///
/// All links are stored canonically with session_id_a &lt; session_id_b.
/// The DB UNIQUE constraint (session_id_a, session_id_b, relationship_type) ensures idempotency.
/// </summary>
public sealed partial class ConversationLinker
{
    /// <summary>How far back/forward from the session timestamp to search for candidates.</summary>
    private static readonly TimeSpan CandidateWindow = TimeSpan.FromDays(7);

    /// <summary>Maximum gap between session start times to qualify as "temporal".</summary>
    private static readonly TimeSpan TemporalWindow = TimeSpan.FromMinutes(30);

    /// <summary>Minimum token Jaccard similarity between normalized titles to create a "continuation" link.</summary>
    private const double TitleSimilarityThreshold = 0.5;

    /// <summary>Minimum Jaccard similarity between concept sets to create a "same_topic" link.</summary>
    private const double ConceptJaccardThreshold = 0.15;

    /// <summary>Minimum file path overlap ratio (shared / total in current session) for "same_topic".</summary>
    private const double FileOverlapThreshold = 0.1;

    private readonly ILocalDbService _db;
    private readonly ILogger<ConversationLinker> _logger;

    public ConversationLinker(ILocalDbService db, ILogger<ConversationLinker> logger)
    {
        _db     = db;
        _logger = logger;
    }

    /// <summary>
    /// Finds and persists all relationship links for <paramref name="sessionId"/>.
    /// Safe to call multiple times — the DB UNIQUE constraint prevents duplicate links.
    /// </summary>
    public async Task ProcessSessionAsync(long sessionId, CancellationToken ct = default)
    {
        var session = await _db.GetSessionByIdAsync(sessionId, ct);
        if (session is null)
        {
            LogSessionNotFound(_logger, sessionId);
            return;
        }

        // Gather observations for file-path heuristics
        var observations = await _db.GetObservationsAsync(sessionId, ct);
        var filePaths = observations
            .Where(o => !string.IsNullOrEmpty(o.FilePath))
            .Select(o => o.FilePath!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Gather summary for concept-overlap heuristic
        var summary = await _db.GetSessionSummaryAsync(sessionId, ct);
        var concepts = ParseConcepts(summary?.Concepts);

        var links = new List<ConversationRelationship>();

        // ── Step 1: Time-window candidates ──────────────────────────────────
        var from       = session.CreatedAt - CandidateWindow;
        var to         = session.CreatedAt + CandidateWindow;
        var candidates = await _db.GetSessionsInTimeWindowAsync(from, to, sessionId, ct: ct);

        if (candidates.Count > 0)
        {
            var normalizedTitle = NormalizeTitle(session.Title);
            var projectName     = ExtractProjectName(session);

            foreach (var candidate in candidates)
            {
                var deltaMinutes = Math.Abs((candidate.CreatedAt - session.CreatedAt).TotalMinutes);

                // a. TEMPORAL: different surface, within 30 min
                if (candidate.Source != session.Source && deltaMinutes <= TemporalWindow.TotalMinutes)
                {
                    links.Add(MakeLink(sessionId, candidate.Id, "temporal", 0.6,
                        $"Different surfaces within {(int)deltaMinutes}min of each other"));
                }

                // b. CONTINUATION: same or very similar titles
                if (!string.IsNullOrEmpty(candidate.Title))
                {
                    var sim = TitleSimilarity(normalizedTitle, NormalizeTitle(candidate.Title));
                    if (sim >= TitleSimilarityThreshold)
                    {
                        links.Add(MakeLink(sessionId, candidate.Id, "continuation", 0.7,
                            $"Similar titles (token Jaccard={sim:F2})"));
                    }
                }

                // c. SAME PROJECT (CC → other surfaces)
                if (projectName is not null && candidate.Source != ConversationSource.ClaudeCode)
                {
                    if (ContainsProjectName(candidate.Title, projectName))
                    {
                        links.Add(MakeLink(sessionId, candidate.Id, "same_project", 0.8,
                            $"CC project '{projectName}' referenced in cross-surface title"));
                    }
                }
            }

            // c. SAME PROJECT (non-CC session referencing a CC project name)
            if (projectName is null && session.Source != ConversationSource.ClaudeCode)
            {
                foreach (var cc in candidates.Where(c => c.Source == ConversationSource.ClaudeCode))
                {
                    var ccProject = ExtractProjectName(cc);
                    if (ccProject is not null && ContainsProjectName(session.Title, ccProject))
                    {
                        links.Add(MakeLink(sessionId, cc.Id, "same_project", 0.8,
                            $"CC project '{ccProject}' referenced in this session's title"));
                    }
                }
            }

            // d. CONCEPT OVERLAP: compare summary concept sets (Jaccard)
            if (concepts.Count > 0)
            {
                var candidateIds      = candidates.Select(c => c.Id).ToList();
                var candidateSummaries = await _db.GetBulkSessionSummariesAsync(candidateIds, ct);

                foreach (var cs in candidateSummaries)
                {
                    var csConcepts = ParseConcepts(cs.Concepts);
                    if (csConcepts.Count == 0) continue;

                    var jaccard = JaccardSimilarity(concepts, csConcepts);
                    if (jaccard >= ConceptJaccardThreshold)
                    {
                        links.Add(MakeLink(sessionId, cs.SessionId, "same_topic",
                            jaccard, $"Concept overlap (Jaccard={jaccard:F2})"));
                    }
                }
            }
        }

        // ── Step 2: File-path overlap (one SQL query, no O(n²)) ─────────────
        if (filePaths.Count > 0)
        {
            var sharedSessions = await _db.GetSessionsWithSharedFilesAsync(
                filePaths, sessionId, from, ct);

            foreach (var (candidateId, sharedCount) in sharedSessions)
            {
                var overlapRatio = (double)sharedCount / filePaths.Count;
                if (overlapRatio >= FileOverlapThreshold)
                {
                    links.Add(MakeLink(sessionId, candidateId, "same_topic",
                        overlapRatio,
                        $"File path overlap ({sharedCount}/{filePaths.Count} files)"));
                }
            }
        }

        // ── Step 3: Deduplicate and persist ─────────────────────────────────
        if (links.Count == 0)
            return;

        // Keep the highest-confidence entry per (sessionA, sessionB, type) triple
        var deduped = links
            .GroupBy(l => (l.SessionIdA, l.SessionIdB, l.RelationshipType))
            .Select(g => g.MaxBy(l => l.Confidence)!)
            .ToList();

        await _db.CreateConversationLinksAsync(deduped, ct);
        LogLinksCreated(_logger, sessionId, deduped.Count);
    }

    // ── Static helpers ────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a canonical relationship link (session_id_a &lt; session_id_b).
    /// </summary>
    internal static ConversationRelationship MakeLink(
        long sessionIdA, long sessionIdB,
        string type, double confidence, string evidence) => new()
    {
        SessionIdA       = Math.Min(sessionIdA, sessionIdB),
        SessionIdB       = Math.Max(sessionIdA, sessionIdB),
        RelationshipType = type,
        Confidence       = Math.Round(Math.Clamp(confidence, 0.0, 1.0), 4),
        Evidence         = evidence,
        CreatedAt        = DateTime.UtcNow
    };

    /// <summary>
    /// Extracts the directory name used as a project identifier from a ClaudeCode session title.
    /// CC title format: "[subagent] {dirName}/{sessionId[..8]}" or "{dirName}/{sessionId[..8]}".
    /// Returns null for non-CC sessions or titles without a recognizable project prefix.
    /// </summary>
    internal static string? ExtractProjectName(ConversationSession session)
    {
        if (session.Source != ConversationSource.ClaudeCode) return null;

        var title = session.Title;
        if (string.IsNullOrEmpty(title)) return null;

        // Strip optional "[subagent] " prefix
        if (title.StartsWith("[subagent] ", StringComparison.OrdinalIgnoreCase))
            title = title["[subagent] ".Length..];

        var slashIdx = title.IndexOf('/');
        if (slashIdx <= 1) return null; // Require at least 2 chars for project name

        var projectName = title[..slashIdx];
        return projectName;
    }

    /// <summary>
    /// Normalizes a title for similarity comparison by stripping the CC session-ID suffix
    /// and converting to lowercase.
    /// </summary>
    internal static string NormalizeTitle(string title)
    {
        if (string.IsNullOrEmpty(title)) return string.Empty;

        // Strip "[subagent] " prefix
        if (title.StartsWith("[subagent] ", StringComparison.OrdinalIgnoreCase))
            title = title["[subagent] ".Length..];

        // For CC titles like "ses-local/abc12345", strip the session-ID hash suffix
        var lastSlash = title.LastIndexOf('/');
        if (lastSlash >= 0 && lastSlash < title.Length - 1)
        {
            var suffix = title[(lastSlash + 1)..];
            // A CC session-ID suffix is short (≤12 chars) and hex-like
            if (suffix.Length <= 12 && suffix.All(c => char.IsAsciiLetterOrDigit(c) || c == '-' || c == '_'))
                title = title[..lastSlash];
        }

        return title.Trim().ToLowerInvariant();
    }

    /// <summary>
    /// Token-based Jaccard similarity between two normalized title strings.
    /// Tokens shorter than 3 characters are ignored to reduce noise.
    /// </summary>
    internal static double TitleSimilarity(string a, string b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return 0.0;
        if (a == b) return 1.0;

        static HashSet<string> Tokenize(string s) =>
            s.Split([' ', '/', '-', '_', '.', ','], StringSplitOptions.RemoveEmptyEntries)
             .Where(t => t.Length >= 3)
             .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var tokA = Tokenize(a);
        var tokB = Tokenize(b);

        if (tokA.Count == 0 || tokB.Count == 0) return 0.0;

        var intersection = tokA.Count(tokB.Contains);
        var union        = tokA.Count + tokB.Count - intersection;
        return union == 0 ? 0.0 : (double)intersection / union;
    }

    private static bool ContainsProjectName(string title, string projectName) =>
        title.Contains(projectName, StringComparison.OrdinalIgnoreCase);

    private static HashSet<string> ParseConcepts(string? concepts)
    {
        if (string.IsNullOrWhiteSpace(concepts)) return [];
        return concepts
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(c => c.ToLowerInvariant())
            .ToHashSet();
    }

    private static double JaccardSimilarity(HashSet<string> a, HashSet<string> b)
    {
        var intersection = a.Count(b.Contains);
        var union        = a.Count + b.Count - intersection;
        return union == 0 ? 0.0 : (double)intersection / union;
    }

    // ── LoggerMessage source generators ──────────────────────────────────────

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "ConversationLinker: session {SessionId} not found; skipping")]
    private static partial void LogSessionNotFound(ILogger logger, long sessionId);

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "ConversationLinker: session {SessionId} → {Count} link(s) created/updated")]
    private static partial void LogLinksCreated(ILogger logger, long sessionId, int count);
}
