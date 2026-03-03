namespace Ses.Local.Core.Models;

/// <summary>
/// A heuristic-detected relationship between two conversation sessions from any surface
/// (ClaudeCode, Desktop, Import). Canonical form: SessionIdA &lt; SessionIdB.
/// Stored in conv_relationships.
/// </summary>
public sealed class ConversationRelationship
{
    public long Id { get; set; }

    /// <summary>The lower of the two session IDs (canonical ordering).</summary>
    public long SessionIdA { get; set; }

    /// <summary>The higher of the two session IDs (canonical ordering).</summary>
    public long SessionIdB { get; set; }

    /// <summary>
    /// The type of relationship detected.
    /// Values: "same_project", "same_topic", "temporal", "continuation"
    /// </summary>
    public string RelationshipType { get; set; } = string.Empty;

    /// <summary>Heuristic confidence in the relationship, 0.0–1.0.</summary>
    public double Confidence { get; set; } = 0.5;

    /// <summary>Human-readable explanation of why these sessions were linked.</summary>
    public string? Evidence { get; set; }

    public DateTime CreatedAt { get; set; }
}
