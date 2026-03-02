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

    /// <summary>
    /// Inserts or replaces the session summary for a session.
    /// Conflict key: session_id (one summary per session at any given compression layer).
    /// Syncs back the DB-assigned Id on the summary.
    /// </summary>
    Task UpsertSessionSummaryAsync(SessionSummary summary, CancellationToken ct = default);

    /// <summary>Returns the summary for a session, or null if none exists yet.</summary>
    Task<SessionSummary?> GetSessionSummaryAsync(long sessionId, CancellationToken ct = default);

    /// <summary>Full-text search across summary narrative, concepts, file references, and category.</summary>
    Task<IReadOnlyList<SessionSummary>> SearchSummariesAsync(string query, int limit = 10, CancellationToken ct = default);

    /// <summary>
    /// Returns session IDs that have observations but no summary yet, up to <paramref name="batchSize"/> items.
    /// Used by the compression worker to find work.
    /// </summary>
    Task<IReadOnlyList<long>> GetSessionsWithoutSummaryAsync(int batchSize = 10, CancellationToken ct = default);

    /// <summary>
    /// Inserts or ignores a batch of causal/temporal links between observations.
    /// Conflict key: (source_observation_id, target_observation_id, link_type).
    /// </summary>
    Task CreateObservationLinksAsync(IEnumerable<ObservationLink> links, CancellationToken ct = default);

    /// <summary>
    /// Walks the observation link graph starting from <paramref name="observationId"/> up to
    /// <paramref name="maxDepth"/> hops and returns all links encountered (BFS).
    /// </summary>
    Task<IReadOnlyList<ObservationLink>> GetCausalChainAsync(long observationId, int maxDepth = 5, CancellationToken ct = default);

    /// <summary>
    /// Returns recent ClaudeCode sessions whose title starts with the given project name prefix
    /// (format: "{projectName}/"), updated within the specified time window.
    /// Used by ClaudeMdGenerator to find sessions belonging to a project directory.
    /// </summary>
    Task<IReadOnlyList<ConversationSession>> GetRecentSessionsByProjectNameAsync(
        string projectName, DateTime since, int limit = 50, CancellationToken ct = default);

    /// <summary>
    /// Returns recent observations for the given sessions, ordered by created_at descending.
    /// </summary>
    Task<IReadOnlyList<ConversationObservation>> GetRecentObservationsForSessionsAsync(
        IEnumerable<long> sessionIds, DateTime since, CancellationToken ct = default);

    // ── Vector Embeddings (WI-989) ──────────────────────────────────────────

    /// <summary>
    /// Inserts or replaces the embedding vector for a session.
    /// The vector is stored as a BLOB (IEEE 754 float[384]).
    /// </summary>
    Task UpsertEmbeddingAsync(long sessionId, float[] embedding, CancellationToken ct = default);

    /// <summary>
    /// Returns all stored embeddings for brute-force cosine similarity search.
    /// Each entry is (sessionId, float[384]).
    /// </summary>
    Task<IReadOnlyList<(long SessionId, float[] Embedding)>> GetAllEmbeddingsAsync(CancellationToken ct = default);

    /// <summary>Returns session IDs that have summaries but no embedding yet.</summary>
    Task<IReadOnlyList<long>> GetSessionsWithoutEmbeddingAsync(int batchSize = 10, CancellationToken ct = default);
}
