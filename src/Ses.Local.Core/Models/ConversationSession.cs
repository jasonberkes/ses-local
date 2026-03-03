using Ses.Local.Core.Enums;

namespace Ses.Local.Core.Models;

/// <summary>A single conversation session from any supported AI surface.</summary>
public sealed class ConversationSession
{
    public long Id { get; set; }
    public ConversationSource Source { get; set; }
    public string ExternalId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? SyncedAt { get; set; }
    public string? ContentHash { get; set; }

    /// <summary>
    /// When true, this session is excluded from search results, cloud sync, and CLAUDE.md generation.
    /// Stays in the DB so the user can un-exclude later.
    /// </summary>
    public bool Excluded { get; set; }
}
