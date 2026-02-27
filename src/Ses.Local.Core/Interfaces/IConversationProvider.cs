using Ses.Local.Core.Models;

namespace Ses.Local.Core.Interfaces;

/// <summary>
/// Abstraction over conversation data sources (claude.ai, ChatGPT, etc).
/// </summary>
public interface IConversationProvider
{
    Task<IReadOnlyList<ConversationSession>> GetSessionsAsync(DateTime? since = null, CancellationToken ct = default);
    Task<IReadOnlyList<ConversationMessage>> GetMessagesAsync(string externalSessionId, CancellationToken ct = default);
}
