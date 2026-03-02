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
    Task<IReadOnlyList<ConversationMessage>> GetMessagesAsync(long sessionId, CancellationToken ct = default);
    Task<IReadOnlyList<ConversationMessage>> SearchAsync(string query, int limit = 10, CancellationToken ct = default);

    /// <summary>
    /// Upserts a batch of observations for a session. Syncs back the DB-assigned Id on each item.
    /// Conflict key: (session_id, sequence_number).
    /// </summary>
    Task UpsertObservationsAsync(IEnumerable<ConversationObservation> observations, CancellationToken ct = default);

    /// <summary>Retrieves all observations for a session, ordered by sequence_number.</summary>
    Task<IReadOnlyList<ConversationObservation>> GetObservationsAsync(long sessionId, CancellationToken ct = default);

    /// <summary>Full-text search across observation content, tool_name, and file_path.</summary>
    Task<IReadOnlyList<ConversationObservation>> SearchObservationsAsync(string query, int limit = 10, CancellationToken ct = default);

    /// <summary>
    /// Sets parent_observation_id for tool_result observations after their parent tool_use IDs are known.
    /// Called as a post-upsert step to complete parent linking within a batch.
    /// </summary>
    Task UpdateObservationParentsAsync(IEnumerable<(long observationId, long parentId)> updates, CancellationToken ct = default);
}
