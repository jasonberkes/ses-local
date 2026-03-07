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

    // ── Privacy Controls (WI-992) ──────────────────────────────────────────

    /// <summary>
    /// Marks a session as excluded. Excluded sessions are filtered from search, sync, and CLAUDE.md.
    /// The session stays in the DB so the user can un-exclude later.
    /// </summary>
    Task ExcludeSessionAsync(long sessionId, bool excluded, CancellationToken ct = default);

    /// <summary>Returns whether a session is marked as excluded.</summary>
    Task<bool> IsSessionExcludedAsync(long sessionId, CancellationToken ct = default);

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

    // ── Conversation Relationships (WI-986) ──────────────────────────────────

    /// <summary>Returns a session by its primary key, or null if not found.</summary>
    Task<ConversationSession?> GetSessionByIdAsync(long sessionId, CancellationToken ct = default);

    /// <summary>
    /// Returns sessions created within the given time range, excluding the specified session.
    /// Ordered by created_at descending. Used by ConversationLinker to find linking candidates.
    /// </summary>
    Task<IReadOnlyList<ConversationSession>> GetSessionsInTimeWindowAsync(
        DateTime from, DateTime to, long excludeSessionId,
        int limit = 200, CancellationToken ct = default);

    /// <summary>
    /// Returns sessions that have observations touching any of the given file paths,
    /// along with the count of matching distinct file paths per session.
    /// Excludes the specified session. Scoped to sessions created after <paramref name="since"/>.
    /// </summary>
    Task<IReadOnlyList<(long SessionId, int SharedCount)>> GetSessionsWithSharedFilesAsync(
        IEnumerable<string> filePaths, long excludeSessionId, DateTime since,
        CancellationToken ct = default);

    /// <summary>
    /// Returns summaries for all provided session IDs in a single query.
    /// Used by ConversationLinker for bulk concept-overlap analysis.
    /// </summary>
    Task<IReadOnlyList<SessionSummary>> GetBulkSessionSummariesAsync(
        IEnumerable<long> sessionIds, CancellationToken ct = default);

    /// <summary>
    /// Inserts or updates cross-session relationship links.
    /// On conflict (session_id_a, session_id_b, relationship_type), keeps the higher confidence value.
    /// Syncs back the DB-assigned Id on each inserted item.
    /// </summary>
    Task CreateConversationLinksAsync(IEnumerable<ConversationRelationship> links, CancellationToken ct = default);

    /// <summary>
    /// Returns all relationship links involving the given session (as either side),
    /// ordered by confidence descending.
    /// </summary>
    Task<IReadOnlyList<ConversationRelationship>> GetRelatedSessionsAsync(
        long sessionId, CancellationToken ct = default);

    // ── WorkItem Links (WI-987) ───────────────────────────────────────────────

    /// <summary>
    /// Inserts WorkItem links for a session. On conflict (session_id, workitem_id), keeps
    /// the higher confidence value and updates the link source.
    /// Syncs back the DB-assigned Id on each item.
    /// </summary>
    Task CreateWorkItemLinksAsync(IEnumerable<WorkItemLink> links, CancellationToken ct = default);

    /// <summary>Returns all WorkItem links for a session, ordered by confidence descending.</summary>
    Task<IReadOnlyList<WorkItemLink>> GetLinkedWorkItemsAsync(long sessionId, CancellationToken ct = default);

    /// <summary>
    /// Returns all sessions linked to a given WorkItem ID, ordered by confidence descending.
    /// </summary>
    Task<IReadOnlyList<ConversationSession>> GetSessionsForWorkItemAsync(int workItemId, CancellationToken ct = default);

    // ── Cloud Pull (WI-991) ───────────────────────────────────────────────────

    /// <summary>
    /// Returns a session by its (source, externalId) natural key, or null if not found.
    /// Used by CloudPullWorker for content-hash deduplication before merging cloud documents.
    /// </summary>
    Task<ConversationSession?> GetSessionBySourceAndExternalIdAsync(
        string source, string externalId, CancellationToken ct = default);

    // ── Sync Metadata (WI-991) ────────────────────────────────────────────────

    /// <summary>
    /// Returns the value for a sync_metadata key, or null if not found.
    /// Used to store last_pull_at, device_id, and other sync state.
    /// </summary>
    Task<string?> GetSyncMetadataAsync(string key, CancellationToken ct = default);

    /// <summary>
    /// Inserts or replaces a sync_metadata key/value pair.
    /// </summary>
    Task SetSyncMetadataAsync(string key, string value, CancellationToken ct = default);

    // ── Hook Activity (TRAY-3) ────────────────────────────────────────────────

    /// <summary>Returns the most recent created_at timestamp from any ClaudeCode observation, or null if none.</summary>
    Task<DateTime?> GetLastHookObservationTimeAsync(CancellationToken ct = default);

    /// <summary>Returns the most recent ClaudeCode hook observations, ordered by created_at descending.</summary>
    Task<IReadOnlyList<ConversationObservation>> GetRecentHookObservationsAsync(int limit = 20, CancellationToken ct = default);

    // ── Dashboard Stats (TRAY-8) ──────────────────────────────────────────────

    /// <summary>
    /// Returns aggregate sync statistics: per-surface conversation counts, last activity timestamps,
    /// total message count, database file size, and oldest/newest conversation dates.
    /// </summary>
    Task<SyncStats> GetSyncStatsAsync(CancellationToken ct = default);

    // ── Import History (TRAY-5/6) ─────────────────────────────────────────────

    /// <summary>Appends a record of a completed import operation to the import_history table.</summary>
    Task RecordImportHistoryAsync(ImportHistoryRecord record, CancellationToken ct = default);

    /// <summary>Returns the most recent import history record, or null if no imports have been recorded.</summary>
    Task<ImportHistoryRecord?> GetLastImportAsync(CancellationToken ct = default);

    /// <summary>Returns the last <paramref name="limit"/> import history records ordered by imported_at DESC.</summary>
    Task<IReadOnlyList<ImportHistoryRecord>> GetImportHistoryAsync(int limit = 20, CancellationToken ct = default);

    // ── CLAUDE.md Viewer (TRAY-4) ─────────────────────────────────────────────

    /// <summary>
    /// Returns distinct working directory paths from Claude Code sessions by scanning
    /// ~/.claude/projects/ JSONL files for their cwd fields.
    /// </summary>
    Task<IReadOnlyList<string>> GetKnownProjectsAsync(CancellationToken ct = default);
}
