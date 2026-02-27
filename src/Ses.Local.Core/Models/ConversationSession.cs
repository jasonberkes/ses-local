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
}
