namespace Ses.Local.Core.Models;

/// <summary>
/// A directed causal or temporal link between two observations.
/// Stored in conv_observation_links.
/// </summary>
public sealed class ObservationLink
{
    public long Id { get; set; }

    /// <summary>The observation that is the source of the link (e.g. the Error, the Read, the failing TestResult).</summary>
    public long SourceObservationId { get; set; }

    /// <summary>The observation that is the target of the link (e.g. the Write that fixes, the passing TestResult).</summary>
    public long TargetObservationId { get; set; }

    /// <summary>
    /// The type of causal relationship.
    /// Values: "causes", "follows", "related", "fixes"
    /// </summary>
    public string LinkType { get; set; } = string.Empty;

    /// <summary>Heuristic confidence in the link, 0.0–1.0.</summary>
    public double Confidence { get; set; } = 1.0;

    public DateTime CreatedAt { get; set; }
}
