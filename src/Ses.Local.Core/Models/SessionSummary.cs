namespace Ses.Local.Core.Models;

/// <summary>
/// A compressed representation of a conversation session produced by the
/// three-layer observation compression pipeline.
/// </summary>
public sealed class SessionSummary
{
    public long Id { get; set; }

    /// <summary>Foreign key to conv_sessions.id.</summary>
    public long SessionId { get; set; }

    /// <summary>
    /// Semantic category inferred from observations.
    /// One of: decision, bugfix, feature, refactor, discovery, change, unknown.
    /// </summary>
    public string Category { get; set; } = "unknown";

    /// <summary>Human-readable narrative of what happened in the session. Max 500 chars.</summary>
    public string Narrative { get; set; } = string.Empty;

    /// <summary>Comma-separated key concepts extracted from the session (class names, service names, etc.).</summary>
    public string? Concepts { get; set; }

    /// <summary>Comma-separated file paths touched during the session.</summary>
    public string? FileReferences { get; set; }

    /// <summary>Git commit messages extracted from GitCommit observations.</summary>
    public string? GitCommitMessages { get; set; }

    /// <summary>Whether any test results were detected in the session.</summary>
    public bool? TestsRun { get; set; }

    /// <summary>Number of passing tests detected.</summary>
    public int? TestsPassed { get; set; }

    /// <summary>Number of failing tests detected.</summary>
    public int? TestsFailed { get; set; }

    /// <summary>Count of Error-type observations in the session.</summary>
    public int ErrorCount { get; set; }

    /// <summary>Count of ToolUse-type observations in the session.</summary>
    public int ToolUseCount { get; set; }

    /// <summary>
    /// Which compression layer produced this summary.
    /// 1 = rule-based (always runs, free), 2 = local AI stub, 3 = cloud AI.
    /// </summary>
    public int CompressionLayer { get; set; } = 1;

    public DateTime CreatedAt { get; set; }
}
