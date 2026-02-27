using Ses.Local.Core.Models;

namespace Ses.Local.Core.Interfaces;

/// <summary>
/// Local SQLite data access layer shared by all ses-local workers and ses-mcp memory tools.
/// Database: ~/.ses/local.db
/// </summary>
public interface ILocalDbService
{
    Task UpsertSessionAsync(ConversationSession session, CancellationToken ct = default);
    Task UpsertMessagesAsync(IEnumerable<ConversationMessage> messages, CancellationToken ct = default);
    Task<IReadOnlyList<ConversationSession>> GetPendingSyncAsync(int batchSize = 10, CancellationToken ct = default);
    Task MarkSyncedAsync(long sessionId, string? docServiceId, CancellationToken ct = default);
    Task<IReadOnlyList<ConversationMessage>> SearchAsync(string query, int limit = 10, CancellationToken ct = default);
}
