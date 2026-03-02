using Ses.Local.Core.Enums;

namespace Ses.Local.Core.Models;

/// <summary>
/// A structured observation extracted from a single content block within a Claude Code session.
/// Each tool_use, tool_result, text, and thinking block becomes one observation.
/// </summary>
public sealed class ConversationObservation
{
    public long Id { get; set; }

    /// <summary>Foreign key to conv_sessions.id.</summary>
    public long SessionId { get; set; }

    public ObservationType ObservationType { get; set; }

    /// <summary>Tool name for tool_use blocks (e.g. "Write", "Read", "Bash").</summary>
    public string? ToolName { get; set; }

    /// <summary>File path extracted from tool_use input (from "path", "file_path", or "filename" keys).</summary>
    public string? FilePath { get; set; }

    /// <summary>The actual content of the block, trimmed.</summary>
    public string Content { get; set; } = string.Empty;

    public int? TokenCount { get; set; }

    /// <summary>Monotonically increasing order within the session, for timeline reconstruction.</summary>
    public int SequenceNumber { get; set; }

    /// <summary>For tool_result observations: the Id of the corresponding tool_use observation.</summary>
    public long? ParentObservationId { get; set; }

    public DateTime CreatedAt { get; set; }
}
