using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Ses.Local.Core.Enums;
using Ses.Local.Core.Interfaces;
using Ses.Local.Core.Models;
using Ses.Local.Core.Options;
using System.Text;

namespace Ses.Local.Workers.Services;

/// <summary>
/// Generates CLAUDE.md files in project root directories, giving Claude Code agents
/// instant historical context at session start — no user action required.
///
/// Safety guarantees:
/// - Never overwrites a user-created CLAUDE.md (detected by absence of our header).
/// - Skips excluded paths (ClaudeMdExcludePaths option).
/// - Adds CLAUDE.md to .gitignore automatically.
/// - Only runs when EnableClaudeMdGeneration = true.
/// </summary>
public sealed class ClaudeMdGenerator : IClaudeMdGenerator
{
    /// <summary>Header line that identifies files we own. If absent, treat as user-created.</summary>
    internal const string GeneratedHeader = "<!-- ses-local generated — do not edit manually (changes will be overwritten) -->";

    private readonly ILocalDbService _db;
    private readonly ILogger<ClaudeMdGenerator> _logger;
    private readonly SesLocalOptions _options;

    public ClaudeMdGenerator(
        ILocalDbService db,
        ILogger<ClaudeMdGenerator> logger,
        IOptions<SesLocalOptions> options)
    {
        _db      = db;
        _logger  = logger;
        _options = options.Value;
    }

    public async Task GenerateAsync(string projectPath, CancellationToken ct = default)
    {
        if (!_options.EnableClaudeMdGeneration) return;
        if (string.IsNullOrWhiteSpace(projectPath)) return;
        if (!Directory.Exists(projectPath)) return;

        // Check exclusions (both CLAUDE.md-specific and privacy-level project exclusions)
        foreach (var excluded in _options.ClaudeMdExcludePaths)
        {
            if (!string.IsNullOrEmpty(excluded) &&
                projectPath.Contains(excluded, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogDebug("Skipping CLAUDE.md generation for excluded path: {Path}", projectPath);
                return;
            }
        }

        foreach (var excluded in _options.ExcludedProjectPaths)
        {
            if (!string.IsNullOrEmpty(excluded) &&
                projectPath.Contains(excluded, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogDebug("Skipping CLAUDE.md generation for privacy-excluded project: {Path}", projectPath);
                return;
            }
        }

        var claudeMdPath = Path.Combine(projectPath, "CLAUDE.md");

        // Never overwrite a user-created CLAUDE.md
        if (File.Exists(claudeMdPath))
        {
            var firstLine = await ReadFirstLineAsync(claudeMdPath);
            if (firstLine != GeneratedHeader)
            {
                _logger.LogDebug("Skipping CLAUDE.md generation — user-created file exists: {Path}", claudeMdPath);
                return;
            }
        }

        var projectName = Path.GetFileName(projectPath.TrimEnd(Path.DirectorySeparatorChar));
        var since = DateTime.UtcNow.AddDays(-_options.ClaudeMdMaxAgeDays);

        var sessions = await _db.GetRecentSessionsByProjectNameAsync(projectName, since, limit: 50, ct);
        if (sessions.Count == 0)
        {
            _logger.LogDebug("No recent sessions for project {Project} — skipping CLAUDE.md generation", projectName);
            return;
        }

        var sessionIds = sessions.Select(s => s.Id);
        var observations = await _db.GetRecentObservationsForSessionsAsync(sessionIds, since, ct);

        var content = BuildContent(projectName, sessions, observations);

        try
        {
            await File.WriteAllTextAsync(claudeMdPath, content, Encoding.UTF8, ct);
            _logger.LogInformation("Generated CLAUDE.md for project {Project} ({Sessions} sessions, {Obs} observations)",
                projectName, sessions.Count, observations.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write CLAUDE.md for {Path}", claudeMdPath);
            return;
        }

        await EnsureGitignoreEntryAsync(projectPath, ct);
    }

    // ── Content Building ──────────────────────────────────────────────────────

    internal static string BuildContent(
        string projectName,
        IReadOnlyList<ConversationSession> sessions,
        IReadOnlyList<ConversationObservation> observations)
    {
        var sb = new StringBuilder();
        var now = DateTime.UtcNow;

        sb.AppendLine(GeneratedHeader);
        sb.AppendLine($"# Claude Code Context — {projectName}");
        sb.AppendLine();
        sb.AppendLine($"*Generated {now:yyyy-MM-dd HH:mm} UTC by ses-local · {sessions.Count} recent session(s)*");
        sb.AppendLine();

        // Git commits
        var commits = observations
            .Where(o => o.ObservationType == ObservationType.GitCommit)
            .Take(10)
            .ToList();

        if (commits.Count > 0)
        {
            sb.AppendLine("## Recent Git Commits");
            foreach (var obs in commits)
            {
                var msg = ExtractCommitMessage(obs.Content);
                if (!string.IsNullOrEmpty(msg))
                    sb.AppendLine($"- {msg}");
            }
            sb.AppendLine();
        }

        // Files changed (unique paths from ToolUse observations with file_path set)
        var changedFiles = observations
            .Where(o => o.ObservationType == ObservationType.ToolUse && !string.IsNullOrEmpty(o.FilePath))
            .Select(o => o.FilePath!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => p)
            .Take(20)
            .ToList();

        if (changedFiles.Count > 0)
        {
            sb.AppendLine("## Files Modified");
            foreach (var file in changedFiles)
                sb.AppendLine($"- `{file}`");
            sb.AppendLine();
        }

        // Test results
        var testResults = observations
            .Where(o => o.ObservationType == ObservationType.TestResult)
            .Take(3)
            .ToList();

        if (testResults.Count > 0)
        {
            sb.AppendLine("## Recent Test Results");
            foreach (var obs in testResults)
            {
                var summary = SummarizeTestResult(obs.Content);
                if (!string.IsNullOrEmpty(summary))
                    sb.AppendLine($"- {obs.CreatedAt:yyyy-MM-dd}: {summary}");
            }
            sb.AppendLine();
        }

        // Key decisions (text blocks from assistant that are substantive)
        var keyDecisions = observations
            .Where(o => o.ObservationType == ObservationType.Text &&
                        o.Content.Length > 50 &&
                        o.Content.Length < 500)
            .Take(5)
            .ToList();

        if (keyDecisions.Count > 0)
        {
            sb.AppendLine("## Key Decisions / Context");
            foreach (var obs in keyDecisions)
            {
                var snippet = obs.Content.Length > 200
                    ? obs.Content[..200].TrimEnd() + "…"
                    : obs.Content.Trim();
                // Collapse newlines in snippet for compact display
                snippet = snippet.Replace('\n', ' ').Replace('\r', ' ');
                sb.AppendLine($"- {snippet}");
            }
            sb.AppendLine();
        }

        // Active sessions (last few)
        var recentSessions = sessions.Take(5).ToList();
        sb.AppendLine("## Recent Sessions");
        foreach (var s in recentSessions)
            sb.AppendLine($"- `{s.ExternalId[..Math.Min(8, s.ExternalId.Length)]}` — {s.UpdatedAt:yyyy-MM-dd HH:mm} UTC");
        sb.AppendLine();

        return sb.ToString();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string ExtractCommitMessage(string bashContent)
    {
        // content is the JSON input to Bash: {"command":"git commit -m \"message\""}
        // Try to extract the -m argument
        var mIdx = bashContent.IndexOf("-m ", StringComparison.Ordinal);
        if (mIdx < 0)
        {
            // Fallback: return first 80 chars of the command
            var trimmed = bashContent.Trim('{', '}', '"', ' ');
            return trimmed.Length > 80 ? trimmed[..80] : trimmed;
        }

        var afterM = bashContent[(mIdx + 3)..].TrimStart('"', '\'', ' ');
        var endIdx = afterM.IndexOfAny(['"', '\'', '\n']);
        return endIdx > 0 ? afterM[..endIdx] : afterM[..Math.Min(100, afterM.Length)];
    }

    private static string SummarizeTestResult(string content)
    {
        // Look for common test result lines
        foreach (var line in content.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("Passed", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("Failed", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Contains("passed", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Contains("failed", StringComparison.OrdinalIgnoreCase))
            {
                return trimmed.Length > 120 ? trimmed[..120] : trimmed;
            }
        }
        return content.Length > 120 ? content[..120].Trim() : content.Trim();
    }

    private static async Task<string> ReadFirstLineAsync(string path)
    {
        try
        {
            await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var reader = new StreamReader(fs);
            return await reader.ReadLineAsync() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    // ── .gitignore management ─────────────────────────────────────────────────

    internal static async Task EnsureGitignoreEntryAsync(string projectPath, CancellationToken ct)
    {
        var gitignorePath = Path.Combine(projectPath, ".gitignore");
        const string entry = "CLAUDE.md";

        try
        {
            if (File.Exists(gitignorePath))
            {
                var lines = await File.ReadAllLinesAsync(gitignorePath, ct);
                if (lines.Any(l => l.Trim() == entry)) return;
                await File.AppendAllTextAsync(gitignorePath, Environment.NewLine + entry + Environment.NewLine, ct);
            }
            else
            {
                // Only create .gitignore if a .git directory exists — we're in a real repo
                var gitDir = Path.Combine(projectPath, ".git");
                if (!Directory.Exists(gitDir)) return;

                await File.WriteAllTextAsync(gitignorePath, entry + Environment.NewLine, ct);
            }
        }
        catch
        {
            // .gitignore update is best-effort; never fail the caller
        }
    }
}
