namespace Ses.Local.Core.Models;

/// <summary>A single message within a conversation session.</summary>
public sealed class ConversationMessage
{
    public long Id { get; set; }
    public long SessionId { get; set; }
    public string Role { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public int? TokenCount { get; set; }
}
