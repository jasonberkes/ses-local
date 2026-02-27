using Ses.Local.Core.Enums;

namespace Ses.Local.Core.Models;

/// <summary>Tracks what has been synced to the TaskMaster cloud.</summary>
public sealed class SyncLedger
{
    public ConversationSource Source { get; set; }
    public string ExternalId { get; set; } = string.Empty;
    public DateTime LastSyncedAt { get; set; }
    public string? DocServiceId { get; set; }
    public bool MemorySynced { get; set; }
}
