using Microsoft.Extensions.Logging;
using Ses.Local.Core.Enums;
using Ses.Local.Core.Interfaces;
using Ses.Local.Core.Models;
using System.Text;
using System.Text.RegularExpressions;

namespace Ses.Local.Workers.Services;

/// <summary>
/// Layer 1 of the observation compression pipeline.
/// Extracts structured facts from session observations using purely rule-based heuristics —
/// no network calls, no AI, always free.
/// Implements <see cref="IObservationCompressor"/> as the extension point for Layers 2 and 3.
/// </summary>
public sealed partial class RuleBasedCompressor : IObservationCompressor
{
    private readonly ILogger<RuleBasedCompressor> _logger;

    // Matches common test-result summary lines in two formats:
    //   "Passed: 12"  (dotnet test — label before count)
    //   "12 passed"   (pytest-style — count before label)
    [GeneratedRegex(@"(?:Passed[:\s]+(\d+)|(\d+)\s+passed)", RegexOptions.IgnoreCase)]
    private static partial Regex PassedPattern();

    [GeneratedRegex(@"(?:Failed[:\s]+(\d+)|(\d+)\s+failed)", RegexOptions.IgnoreCase)]
    private static partial Regex FailedPattern();

    public RuleBasedCompressor(ILogger<RuleBasedCompressor> logger)
    {
        _logger = logger;
    }

    public Task<SessionSummary> CompressAsync(
        long sessionId,
        IReadOnlyList<ConversationObservation> observations,
        CancellationToken ct = default)
    {
        _logger.LogDebug("RuleBasedCompressor: compressing session {SessionId} ({Count} observations)", sessionId, observations.Count);

        var fileReferences = ExtractFileReferences(observations);
        var gitCommits     = ExtractGitCommitMessages(observations);
        var (testsRun, passed, failed) = ExtractTestResults(observations);
        int errorCount    = observations.Count(o => o.ObservationType == ObservationType.Error);
        int toolUseCount  = observations.Count(o => o.ObservationType == ObservationType.ToolUse);
        var concepts      = ExtractConcepts(fileReferences);
        var category      = DetermineCategory(observations, gitCommits, fileReferences);
        var narrative     = BuildNarrative(sessionId, category, fileReferences, gitCommits, toolUseCount, errorCount, testsRun, passed, failed);

        var summary = new SessionSummary
        {
            SessionId         = sessionId,
            Category          = category,
            Narrative         = narrative,
            Concepts          = concepts.Count > 0 ? string.Join(", ", concepts) : null,
            FileReferences    = fileReferences.Count > 0 ? string.Join(", ", fileReferences) : null,
            GitCommitMessages = gitCommits.Count > 0 ? string.Join(" | ", gitCommits) : null,
            TestsRun          = testsRun,
            TestsPassed       = passed,
            TestsFailed       = failed,
            ErrorCount        = errorCount,
            ToolUseCount      = toolUseCount,
            CompressionLayer  = 1,
            CreatedAt         = DateTime.UtcNow
        };

        return Task.FromResult(summary);
    }

    // ── Extraction helpers ────────────────────────────────────────────────────

    private static IReadOnlyList<string> ExtractFileReferences(IReadOnlyList<ConversationObservation> observations)
    {
        return observations
            .Where(o => !string.IsNullOrWhiteSpace(o.FilePath))
            .Select(o => o.FilePath!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IReadOnlyList<string> ExtractGitCommitMessages(IReadOnlyList<ConversationObservation> observations)
    {
        return observations
            .Where(o => o.ObservationType == ObservationType.GitCommit && !string.IsNullOrWhiteSpace(o.Content))
            .Select(o => o.Content.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private (bool? testsRun, int? passed, int? failed) ExtractTestResults(IReadOnlyList<ConversationObservation> observations)
    {
        var testObs = observations
            .Where(o => o.ObservationType == ObservationType.TestResult)
            .ToList();

        if (testObs.Count == 0)
            return (null, null, null);

        int totalPassed = 0;
        int totalFailed = 0;
        bool anyParsed  = false;

        foreach (var obs in testObs)
        {
            var passedMatch = PassedPattern().Match(obs.Content);
            var failedMatch = FailedPattern().Match(obs.Content);

            // Group 1 = label-first ("Passed: N"), Group 2 = count-first ("N passed")
            var passedGroup = passedMatch.Success
                ? (passedMatch.Groups[1].Success ? passedMatch.Groups[1] : passedMatch.Groups[2])
                : null;
            var failedGroup = failedMatch.Success
                ? (failedMatch.Groups[1].Success ? failedMatch.Groups[1] : failedMatch.Groups[2])
                : null;

            if (passedGroup is { Success: true } && int.TryParse(passedGroup.Value, out int p))
            {
                totalPassed += p;
                anyParsed    = true;
            }
            if (failedGroup is { Success: true } && int.TryParse(failedGroup.Value, out int f))
            {
                totalFailed += f;
                anyParsed    = true;
            }
        }

        return anyParsed ? (true, totalPassed, totalFailed) : (true, null, null);
    }

    private static IReadOnlyList<string> ExtractConcepts(IReadOnlyList<string> filePaths)
    {
        // Extract meaningful identifiers from file paths:
        // - File stem without extension (PascalCase → kept as-is)
        // - Skip generic names like "Program", "Startup", "appsettings"
        var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Program", "Startup", "appsettings", "GlobalUsings", "AssemblyInfo", "obj", "bin"
        };

        return filePaths
            .Select(p => Path.GetFileNameWithoutExtension(p))
            .Where(name => !string.IsNullOrWhiteSpace(name) && !skip.Contains(name) && name.Length > 2)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string DetermineCategory(
        IReadOnlyList<ConversationObservation> observations,
        IReadOnlyList<string> gitCommits,
        IReadOnlyList<string> fileReferences)
    {
        // Commit message heuristics — highest priority
        foreach (var msg in gitCommits)
        {
            var lower = msg.ToLowerInvariant();
            if (lower.Contains("fix") || lower.Contains("bug"))
                return "bugfix";
            if (lower.Contains("feat") || lower.Contains("add"))
                return "feature";
            if (lower.Contains("refactor"))
                return "refactor";
        }

        // Observation type heuristics
        bool hasWrite = observations.Any(o =>
            o.ObservationType == ObservationType.ToolUse &&
            string.Equals(o.ToolName, "Write", StringComparison.OrdinalIgnoreCase));

        bool hasRead = observations.Any(o =>
            o.ObservationType == ObservationType.ToolUse &&
            string.Equals(o.ToolName, "Read", StringComparison.OrdinalIgnoreCase));

        if (!hasWrite && hasRead && fileReferences.Count > 0)
            return "discovery";

        if (hasWrite)
            return "change";

        return "unknown";
    }

    private static string BuildNarrative(
        long sessionId,
        string category,
        IReadOnlyList<string> fileReferences,
        IReadOnlyList<string> gitCommits,
        int toolUseCount,
        int errorCount,
        bool? testsRun,
        int? passed,
        int? failed)
    {
        var sb = new StringBuilder();

        // Lead with commit message if available
        if (gitCommits.Count > 0)
        {
            sb.Append(gitCommits[0]);
            if (gitCommits.Count > 1)
                sb.Append($" (+{gitCommits.Count - 1} more commits)");
        }
        else
        {
            sb.Append($"Session {sessionId}: {category} activity");
        }

        // Tool usage summary
        if (toolUseCount > 0)
            sb.Append($". {toolUseCount} tool call(s)");

        // Files touched
        if (fileReferences.Count > 0)
        {
            var preview = fileReferences.Take(3).Select(Path.GetFileName).Where(f => f is not null);
            sb.Append($". Files: {string.Join(", ", preview)}");
            if (fileReferences.Count > 3)
                sb.Append($" (+{fileReferences.Count - 3} more)");
        }

        // Test results
        if (testsRun == true)
        {
            if (passed.HasValue && failed.HasValue)
                sb.Append($". Tests: {passed} passed, {failed} failed");
            else
                sb.Append(". Tests ran");
        }

        // Errors
        if (errorCount > 0)
            sb.Append($". {errorCount} error(s)");

        var result = sb.ToString();
        return result.Length > 500 ? result[..500] : result;
    }
}
