using Microsoft.Extensions.Logging;
using Ses.Local.Core.Enums;
using Ses.Local.Core.Interfaces;
using Ses.Local.Core.Models;

namespace Ses.Local.Workers.Services;

/// <summary>
/// Heuristic-based causal link detector for conversation observations (WI-983).
/// Detects four patterns:
///   a) Error → Write same file    → "fixes"
///   b) Read  → Write same file    → "causes"
///   c) TestResult(fail) → … → TestResult(pass) → "fixes"
///   d) Same FilePath across sessions → "related"
/// All detection is heuristic — no AI required.
/// </summary>
public sealed class CausalityLinker
{
    private readonly ILocalDbService _db;
    private readonly ILogger<CausalityLinker> _logger;

    // Keywords that indicate a failed test result
    private static readonly string[] FailKeywords = ["failed", "failure", "error", "not ok"];

    // Keywords that indicate a passing test result
    private static readonly string[] PassKeywords = ["passed", "succeeded", "ok", "success", "All tests passed"];

    public CausalityLinker(ILocalDbService db, ILogger<CausalityLinker> logger)
    {
        _db     = db;
        _logger = logger;
    }

    /// <summary>
    /// Detects within-session causal links from an ordered list of observations.
    /// Returns discovered links without persisting them.
    /// </summary>
    public IReadOnlyList<ObservationLink> DetectLinks(IReadOnlyList<ConversationObservation> observations)
    {
        // Observations must be ordered by SequenceNumber for the patterns to make sense
        var ordered = observations.OrderBy(o => o.SequenceNumber).ToList();
        var links   = new List<ObservationLink>();
        var now     = DateTime.UtcNow;

        // Pattern a: Error → Write same file = "fixes"
        links.AddRange(DetectErrorToWriteLinks(ordered, now));

        // Pattern b: Read → Write same file = "causes"
        links.AddRange(DetectReadToWriteLinks(ordered, now));

        // Pattern c: TestResult(fail) → ... → TestResult(pass) = "fixes"
        links.AddRange(DetectTestResultLinks(ordered, now));

        _logger.LogDebug("CausalityLinker detected {Count} links from {ObsCount} observations",
            links.Count, observations.Count);

        return links;
    }

    /// <summary>
    /// Detects cross-session "related" links for observations that share the same FilePath.
    /// Queries the database for recent observations on the same paths.
    /// </summary>
    public async Task<IReadOnlyList<ObservationLink>> DetectCrossSessionLinksAsync(
        IReadOnlyList<ConversationObservation> observations,
        CancellationToken ct = default)
    {
        var links     = new List<ObservationLink>();
        var now       = DateTime.UtcNow;
        var filePaths = observations
            .Where(o => o.FilePath is not null && o.Id > 0)
            .GroupBy(o => o.FilePath!)
            .ToList();

        foreach (var group in filePaths)
        {
            // Search for other observations with the same file path in other sessions
            var searchResults = await _db.SearchObservationsAsync(group.Key, limit: 20, ct: ct);
            var localIds      = new HashSet<long>(group.Select(o => o.Id));

            foreach (var remote in searchResults)
            {
                // Skip if it's the same observation or from the same batch
                if (localIds.Contains(remote.Id)) continue;
                if (remote.FilePath != group.Key) continue;

                foreach (var local in group)
                {
                    if (local.Id == 0 || local.Id == remote.Id) continue;

                    links.Add(new ObservationLink
                    {
                        SourceObservationId = local.Id,
                        TargetObservationId = remote.Id,
                        LinkType            = "related",
                        Confidence          = 0.5,
                        CreatedAt           = now
                    });
                }
            }
        }

        return links;
    }

    /// <summary>
    /// Detects links and persists them via <see cref="ILocalDbService.CreateObservationLinksAsync"/>.
    /// </summary>
    public async Task DetectAndPersistLinksAsync(
        IReadOnlyList<ConversationObservation> observations,
        bool includeCrossSession = false,
        CancellationToken ct = default)
    {
        var links = new List<ObservationLink>(DetectLinks(observations));

        if (includeCrossSession)
        {
            var crossLinks = await DetectCrossSessionLinksAsync(observations, ct);
            links.AddRange(crossLinks);
        }

        if (links.Count > 0)
            await _db.CreateObservationLinksAsync(links, ct);
    }

    // ── Pattern implementations ───────────────────────────────────────────────

    private static IEnumerable<ObservationLink> DetectErrorToWriteLinks(
        List<ConversationObservation> ordered, DateTime now)
    {
        // For each Error observation with a FilePath, find the next Write on the same file
        for (int i = 0; i < ordered.Count; i++)
        {
            var error = ordered[i];
            if (error.ObservationType != ObservationType.Error || error.FilePath is null || error.Id == 0)
                continue;

            for (int j = i + 1; j < ordered.Count; j++)
            {
                var candidate = ordered[j];
                if (candidate.Id == 0) continue;

                if (IsWriteToFile(candidate, error.FilePath))
                {
                    yield return new ObservationLink
                    {
                        SourceObservationId = error.Id,
                        TargetObservationId = candidate.Id,
                        LinkType            = "fixes",
                        Confidence          = 0.9,
                        CreatedAt           = now
                    };
                    break; // Only link to the first subsequent write
                }
            }
        }
    }

    private static IEnumerable<ObservationLink> DetectReadToWriteLinks(
        List<ConversationObservation> ordered, DateTime now)
    {
        // For each Read tool_use with a FilePath, find the next Write on the same file
        for (int i = 0; i < ordered.Count; i++)
        {
            var read = ordered[i];
            if (!IsReadObservation(read) || read.FilePath is null || read.Id == 0)
                continue;

            for (int j = i + 1; j < ordered.Count; j++)
            {
                var candidate = ordered[j];
                if (candidate.Id == 0) continue;

                if (IsWriteToFile(candidate, read.FilePath))
                {
                    yield return new ObservationLink
                    {
                        SourceObservationId = read.Id,
                        TargetObservationId = candidate.Id,
                        LinkType            = "causes",
                        Confidence          = 0.7,
                        CreatedAt           = now
                    };
                    break; // Only link to the first subsequent write
                }
            }
        }
    }

    private static IEnumerable<ObservationLink> DetectTestResultLinks(
        List<ConversationObservation> ordered, DateTime now)
    {
        // Find failing TestResult observations, then look for a later passing one
        for (int i = 0; i < ordered.Count; i++)
        {
            var fail = ordered[i];
            if (fail.ObservationType != ObservationType.TestResult || fail.Id == 0)
                continue;
            if (!ContainsFailKeyword(fail.Content))
                continue;

            for (int j = i + 1; j < ordered.Count; j++)
            {
                var pass = ordered[j];
                if (pass.ObservationType != ObservationType.TestResult || pass.Id == 0)
                    continue;
                if (!ContainsPassKeyword(pass.Content))
                    continue;

                yield return new ObservationLink
                {
                    SourceObservationId = fail.Id,
                    TargetObservationId = pass.Id,
                    LinkType            = "fixes",
                    Confidence          = 0.95,
                    CreatedAt           = now
                };
                break; // Link to the first subsequent passing result
            }
        }
    }

    // ── Predicates ────────────────────────────────────────────────────────────

    private static bool IsReadObservation(ConversationObservation obs) =>
        obs.ObservationType == ObservationType.ToolUse &&
        obs.ToolName is not null &&
        obs.ToolName.Equals("Read", StringComparison.OrdinalIgnoreCase);

    private static bool IsWriteToFile(ConversationObservation obs, string filePath) =>
        obs.ObservationType == ObservationType.ToolUse &&
        obs.ToolName is not null &&
        (obs.ToolName.Equals("Write", StringComparison.OrdinalIgnoreCase) ||
         obs.ToolName.Equals("Edit", StringComparison.OrdinalIgnoreCase)) &&
        string.Equals(obs.FilePath, filePath, StringComparison.OrdinalIgnoreCase);

    private static bool ContainsFailKeyword(string content) =>
        FailKeywords.Any(k => content.Contains(k, StringComparison.OrdinalIgnoreCase));

    private static bool ContainsPassKeyword(string content) =>
        PassKeywords.Any(k => content.Contains(k, StringComparison.OrdinalIgnoreCase));
}
