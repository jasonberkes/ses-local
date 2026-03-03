namespace Ses.Local.Core.Models;

/// <summary>
/// A detected link between a conversation session and a TaskMaster WorkItem.
/// Source indicates how the WorkItem reference was found (branch_name, commit_message, conversation_content).
/// Stored in conv_workitem_links.
/// </summary>
public sealed class WorkItemLink
{
    public long Id { get; set; }

    public long SessionId { get; set; }

    /// <summary>The numeric TaskMaster WorkItem ID (e.g. 987 for WI-987).</summary>
    public int WorkItemId { get; set; }

    /// <summary>
    /// How the WorkItem reference was detected.
    /// Values: "branch_name", "commit_message", "conversation_content"
    /// </summary>
    public string LinkSource { get; set; } = string.Empty;

    /// <summary>Confidence in the link, 0.0–1.0. Branch name = 1.0, commit = 0.9, content = 0.7.</summary>
    public double Confidence { get; set; } = 1.0;

    public DateTime CreatedAt { get; set; }
}
