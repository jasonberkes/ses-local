namespace Ses.Local.Core.Models;

/// <summary>A document successfully pulled and parsed from the cloud DocumentService (WI-991).</summary>
public sealed record PulledDocument(
    ConversationSession Session,
    IReadOnlyList<ConversationMessage> Messages,
    string? CloudDocId);
