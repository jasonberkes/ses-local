using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Ses.Local.Core.Enums;
using Ses.Local.Core.Interfaces;
using Ses.Local.Core.Models;

namespace Ses.Local.Workers.Services;

/// <summary>
/// SQLite-backed implementation of ILocalDbService.
/// Database: ~/.ses/local.db (Mac) | %USERPROFILE%\.ses\local.db (Windows)
/// WAL mode enabled — supports concurrent reads from ses-mcp memory tools.
/// Schema created/migrated on first access via EnsureSchemaAsync.
/// </summary>
public sealed class LocalDbService : ILocalDbService, IAsyncDisposable
{
    private readonly string _dbPath;
    private readonly ILogger<LocalDbService> _logger;
    private SqliteConnection? _connection;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _initialized;

    public LocalDbService(ILogger<LocalDbService> logger)
    {
        _logger = logger;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var sesDir = Path.Combine(home, ".ses");
        Directory.CreateDirectory(sesDir);
        _dbPath = Path.Combine(sesDir, "local.db");
    }

    /// <summary>Internal constructor for integration tests — uses an explicit db path.</summary>
    internal LocalDbService(string dbPath, ILogger<LocalDbService> logger)
    {
        _logger = logger;
        _dbPath = dbPath;
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
    }

    // ── Public API ────────────────────────────────────────────────────────────

    public async Task UpsertSessionAsync(ConversationSession session, CancellationToken ct = default)
    {
        var conn = await GetConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO conv_sessions (source, external_id, title, created_at, updated_at, synced_at, content_hash)
            VALUES (@source, @external_id, @title, @created_at, @updated_at, @synced_at, @content_hash)
            ON CONFLICT(source, external_id) DO UPDATE SET
                title        = excluded.title,
                updated_at   = excluded.updated_at,
                content_hash = excluded.content_hash
            """;
        cmd.Parameters.AddWithValue("@source", session.Source.ToString());
        cmd.Parameters.AddWithValue("@external_id", session.ExternalId);
        cmd.Parameters.AddWithValue("@title", session.Title);
        cmd.Parameters.AddWithValue("@created_at", session.CreatedAt.ToString("O"));
        cmd.Parameters.AddWithValue("@updated_at", session.UpdatedAt.ToString("O"));
        cmd.Parameters.AddWithValue("@synced_at", session.SyncedAt.HasValue ? (object)session.SyncedAt.Value.ToString("O") : DBNull.Value);
        cmd.Parameters.AddWithValue("@content_hash", session.ContentHash ?? (object)DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);

        // Sync back the generated Id
        if (session.Id == 0)
        {
            await using var idCmd = conn.CreateCommand();
            idCmd.CommandText = "SELECT id FROM conv_sessions WHERE source = @source AND external_id = @external_id";
            idCmd.Parameters.AddWithValue("@source", session.Source.ToString());
            idCmd.Parameters.AddWithValue("@external_id", session.ExternalId);
            var result = await idCmd.ExecuteScalarAsync(ct);
            if (result is long id)
                session.Id = id;
        }
    }

    public async Task UpsertMessagesAsync(IEnumerable<ConversationMessage> messages, CancellationToken ct = default)
    {
        var conn = await GetConnectionAsync(ct);
        await using var tx = conn.BeginTransaction();
        try
        {
            foreach (var msg in messages)
            {
                await using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT INTO conv_messages (session_id, role, content, created_at, token_count)
                    VALUES (@session_id, @role, @content, @created_at, @token_count)
                    ON CONFLICT(session_id, role, created_at) DO UPDATE SET
                        content     = excluded.content,
                        token_count = excluded.token_count
                    """;
                cmd.Parameters.AddWithValue("@session_id", msg.SessionId);
                cmd.Parameters.AddWithValue("@role", msg.Role);
                cmd.Parameters.AddWithValue("@content", msg.Content);
                cmd.Parameters.AddWithValue("@created_at", msg.CreatedAt.ToString("O"));
                cmd.Parameters.AddWithValue("@token_count", msg.TokenCount.HasValue ? (object)msg.TokenCount.Value : DBNull.Value);
                await cmd.ExecuteNonQueryAsync(ct);
            }
            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    public async Task<IReadOnlyList<ConversationSession>> GetPendingSyncAsync(int batchSize = 10, CancellationToken ct = default)
    {
        var conn = await GetConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, source, external_id, title, created_at, updated_at, synced_at, content_hash, excluded
            FROM conv_sessions
            WHERE (synced_at IS NULL OR updated_at > synced_at)
              AND excluded = 0
            ORDER BY updated_at DESC
            LIMIT @batch
            """;
        cmd.Parameters.AddWithValue("@batch", batchSize);

        var results = new List<ConversationSession>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            results.Add(MapSession(reader));

        return results;
    }

    public async Task MarkSyncedAsync(long sessionId, string? docServiceId, CancellationToken ct = default)
    {
        var conn = await GetConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE conv_sessions SET synced_at = @now WHERE id = @id;
            INSERT INTO sync_ledger (source, external_id, last_synced_at, doc_service_id, memory_synced)
            SELECT source, external_id, @now, @doc_service_id, 0
            FROM conv_sessions WHERE id = @id
            ON CONFLICT(source, external_id) DO UPDATE SET
                last_synced_at = excluded.last_synced_at,
                doc_service_id = excluded.doc_service_id
            """;
        cmd.Parameters.AddWithValue("@id", sessionId);
        cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("@doc_service_id", docServiceId ?? (object)DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<ConversationMessage>> GetMessagesAsync(long sessionId, CancellationToken ct = default)
    {
        var conn = await GetConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, session_id, role, content, created_at, token_count
            FROM conv_messages
            WHERE session_id = @session_id
            ORDER BY created_at
            """;
        cmd.Parameters.AddWithValue("@session_id", sessionId);

        var results = new List<ConversationMessage>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            results.Add(MapMessage(reader));

        return results;
    }

    public async Task<IReadOnlyList<ConversationMessage>> SearchAsync(string query, int limit = 10, CancellationToken ct = default)
    {
        var conn = await GetConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT m.id, m.session_id, m.role, m.content, m.created_at, m.token_count
            FROM conv_messages_fts fts
            JOIN conv_messages m ON m.rowid = fts.rowid
            JOIN conv_sessions s ON s.id = m.session_id
            WHERE conv_messages_fts MATCH @query
              AND s.excluded = 0
            ORDER BY rank
            LIMIT @limit
            """;
        cmd.Parameters.AddWithValue("@query", query);
        cmd.Parameters.AddWithValue("@limit", limit);

        var results = new List<ConversationMessage>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            results.Add(MapMessage(reader));

        return results;
    }

    public async Task UpsertObservationsAsync(IEnumerable<ConversationObservation> observations, CancellationToken ct = default)
    {
        var conn = await GetConnectionAsync(ct);
        await using var tx = conn.BeginTransaction();
        try
        {
            foreach (var obs in observations)
            {
                await using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT INTO conv_observations
                        (session_id, observation_type, tool_name, file_path, content, token_count, sequence_number, parent_observation_id, created_at)
                    VALUES
                        (@session_id, @observation_type, @tool_name, @file_path, @content, @token_count, @sequence_number, @parent_observation_id, @created_at)
                    ON CONFLICT(session_id, sequence_number) DO UPDATE SET
                        observation_type     = excluded.observation_type,
                        tool_name            = excluded.tool_name,
                        file_path            = excluded.file_path,
                        content              = excluded.content,
                        token_count          = excluded.token_count,
                        parent_observation_id = excluded.parent_observation_id
                    """;
                cmd.Parameters.AddWithValue("@session_id",            obs.SessionId);
                cmd.Parameters.AddWithValue("@observation_type",      obs.ObservationType.ToString());
                cmd.Parameters.AddWithValue("@tool_name",             obs.ToolName ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@file_path",             obs.FilePath ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@content",               obs.Content);
                cmd.Parameters.AddWithValue("@token_count",           obs.TokenCount.HasValue ? (object)obs.TokenCount.Value : DBNull.Value);
                cmd.Parameters.AddWithValue("@sequence_number",       obs.SequenceNumber);
                cmd.Parameters.AddWithValue("@parent_observation_id", obs.ParentObservationId.HasValue ? (object)obs.ParentObservationId.Value : DBNull.Value);
                cmd.Parameters.AddWithValue("@created_at",            obs.CreatedAt.ToString("O"));
                await cmd.ExecuteNonQueryAsync(ct);

                // Sync back DB-assigned Id
                if (obs.Id == 0)
                {
                    await using var idCmd = conn.CreateCommand();
                    idCmd.Transaction  = tx;
                    idCmd.CommandText  = "SELECT id FROM conv_observations WHERE session_id = @sid AND sequence_number = @seq";
                    idCmd.Parameters.AddWithValue("@sid", obs.SessionId);
                    idCmd.Parameters.AddWithValue("@seq", obs.SequenceNumber);
                    var result = await idCmd.ExecuteScalarAsync(ct);
                    if (result is long id) obs.Id = id;
                }
            }
            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    public async Task<IReadOnlyList<ConversationObservation>> GetObservationsAsync(long sessionId, CancellationToken ct = default)
    {
        var conn = await GetConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, session_id, observation_type, tool_name, file_path, content, token_count,
                   sequence_number, parent_observation_id, created_at
            FROM conv_observations
            WHERE session_id = @session_id
            ORDER BY sequence_number
            """;
        cmd.Parameters.AddWithValue("@session_id", sessionId);

        var results = new List<ConversationObservation>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            results.Add(MapObservation(reader));

        return results;
    }

    public async Task<IReadOnlyList<ConversationObservation>> SearchObservationsAsync(string query, int limit = 10, CancellationToken ct = default)
    {
        var conn = await GetConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT o.id, o.session_id, o.observation_type, o.tool_name, o.file_path, o.content,
                   o.token_count, o.sequence_number, o.parent_observation_id, o.created_at
            FROM conv_observations_fts fts
            JOIN conv_observations o ON o.id = fts.rowid
            JOIN conv_sessions s ON s.id = o.session_id
            WHERE conv_observations_fts MATCH @query
              AND s.excluded = 0
            ORDER BY rank
            LIMIT @limit
            """;
        cmd.Parameters.AddWithValue("@query", query);
        cmd.Parameters.AddWithValue("@limit", limit);

        var results = new List<ConversationObservation>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            results.Add(MapObservation(reader));

        return results;
    }

    public async Task<IReadOnlyList<ConversationSession>> GetRecentSessionsByProjectNameAsync(
        string projectName, DateTime since, int limit = 50, CancellationToken ct = default)
    {
        var conn = await GetConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, source, external_id, title, created_at, updated_at, synced_at, content_hash, excluded
            FROM conv_sessions
            WHERE source = 'ClaudeCode'
              AND title LIKE @prefix
              AND updated_at >= @since
              AND excluded = 0
            ORDER BY updated_at DESC
            LIMIT @limit
            """;
        cmd.Parameters.AddWithValue("@prefix", projectName + "/%");
        cmd.Parameters.AddWithValue("@since", since.ToString("O"));
        cmd.Parameters.AddWithValue("@limit", limit);

        var results = new List<ConversationSession>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            results.Add(MapSession(reader));

        return results;
    }

    public async Task<IReadOnlyList<ConversationObservation>> GetRecentObservationsForSessionsAsync(
        IEnumerable<long> sessionIds, DateTime since, CancellationToken ct = default)
    {
        var ids = string.Join(",", sessionIds);
        if (string.IsNullOrEmpty(ids)) return [];

        var conn = await GetConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT id, session_id, observation_type, tool_name, file_path, content, token_count,
                   sequence_number, parent_observation_id, created_at
            FROM conv_observations
            WHERE session_id IN ({ids})
              AND created_at >= @since
            ORDER BY created_at DESC
            """;
        cmd.Parameters.AddWithValue("@since", since.ToString("O"));

        var results = new List<ConversationObservation>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            results.Add(MapObservation(reader));

        return results;
    }

    public async Task UpdateObservationParentsAsync(IEnumerable<(long observationId, long parentId)> updates, CancellationToken ct = default)
    {
        var conn = await GetConnectionAsync(ct);
        await using var tx = conn.BeginTransaction();
        try
        {
            foreach (var (observationId, parentId) in updates)
            {
                await using var cmd = conn.CreateCommand();
                cmd.Transaction   = tx;
                cmd.CommandText   = "UPDATE conv_observations SET parent_observation_id = @parent WHERE id = @id";
                cmd.Parameters.AddWithValue("@parent", parentId);
                cmd.Parameters.AddWithValue("@id",     observationId);
                await cmd.ExecuteNonQueryAsync(ct);
            }
            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    public async Task CreateObservationLinksAsync(IEnumerable<ObservationLink> links, CancellationToken ct = default)
    {
        var conn = await GetConnectionAsync(ct);
        await using var tx = conn.BeginTransaction();
        try
        {
            foreach (var link in links)
            {
                await using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT INTO conv_observation_links
                        (source_observation_id, target_observation_id, link_type, confidence, created_at)
                    VALUES
                        (@source, @target, @link_type, @confidence, @created_at)
                    ON CONFLICT(source_observation_id, target_observation_id, link_type) DO NOTHING
                    """;
                cmd.Parameters.AddWithValue("@source",     link.SourceObservationId);
                cmd.Parameters.AddWithValue("@target",     link.TargetObservationId);
                cmd.Parameters.AddWithValue("@link_type",  link.LinkType);
                cmd.Parameters.AddWithValue("@confidence", link.Confidence);
                cmd.Parameters.AddWithValue("@created_at", link.CreatedAt.ToString("O"));
                await cmd.ExecuteNonQueryAsync(ct);

                // Sync back the DB-assigned Id
                if (link.Id == 0)
                {
                    await using var idCmd = conn.CreateCommand();
                    idCmd.Transaction = tx;
                    idCmd.CommandText = """
                        SELECT id FROM conv_observation_links
                        WHERE source_observation_id = @source AND target_observation_id = @target AND link_type = @link_type
                        """;
                    idCmd.Parameters.AddWithValue("@source",    link.SourceObservationId);
                    idCmd.Parameters.AddWithValue("@target",    link.TargetObservationId);
                    idCmd.Parameters.AddWithValue("@link_type", link.LinkType);
                    var result = await idCmd.ExecuteScalarAsync(ct);
                    if (result is long id) link.Id = id;
                }
            }
            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    public async Task<IReadOnlyList<ObservationLink>> GetCausalChainAsync(long observationId, int maxDepth = 5, CancellationToken ct = default)
    {
        var conn        = await GetConnectionAsync(ct);
        var visitedObs  = new HashSet<long> { observationId };
        var visitedLinks = new HashSet<long>();
        var queue       = new Queue<long>();
        var result      = new List<ObservationLink>();

        queue.Enqueue(observationId);
        int depth = 0;

        while (queue.Count > 0 && depth < maxDepth)
        {
            int levelCount = queue.Count;
            depth++;

            for (int i = 0; i < levelCount; i++)
            {
                long current = queue.Dequeue();

                await using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    SELECT id, source_observation_id, target_observation_id, link_type, confidence, created_at
                    FROM conv_observation_links
                    WHERE source_observation_id = @id OR target_observation_id = @id
                    """;
                cmd.Parameters.AddWithValue("@id", current);

                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    var link = MapLink(reader);

                    if (!visitedLinks.Add(link.Id))
                        continue; // Already included this link

                    result.Add(link);

                    long neighbor = link.SourceObservationId == current
                        ? link.TargetObservationId
                        : link.SourceObservationId;

                    if (visitedObs.Add(neighbor))
                        queue.Enqueue(neighbor);
                }
            }
        }

        return result;
    }

    // ── Schema ────────────────────────────────────────────────────────────────

    private async Task EnsureSchemaAsync(SqliteConnection conn, CancellationToken ct)
    {
        // Enable WAL for concurrent readers (ses-mcp memory tools read while workers write)
        await ExecuteNonQueryAsync(conn, "PRAGMA journal_mode=WAL;", ct);
        await ExecuteNonQueryAsync(conn, "PRAGMA foreign_keys=ON;", ct);
        await ExecuteNonQueryAsync(conn, "PRAGMA synchronous=NORMAL;", ct);

        int version = await GetUserVersionAsync(conn, ct);
        _logger.LogInformation("Local DB schema version: {Version}", version);

        if (version < 1)
        {
            await ApplyMigration1Async(conn, ct);
            await SetUserVersionAsync(conn, 1, ct);
        }

        if (version < 2)
        {
            await ApplyMigration2Async(conn, ct);
            await SetUserVersionAsync(conn, 2, ct);
        }

        if (version < 3)
        {
            await ApplyMigration3Async(conn, ct);
            await SetUserVersionAsync(conn, 3, ct);
        }

        if (version < 4)
        {
            await ApplyMigration4Async(conn, ct);
            await SetUserVersionAsync(conn, 4, ct);
        }

        if (version < 5)
        {
            await ApplyMigration5Async(conn, ct);
            await SetUserVersionAsync(conn, 5, ct);
        }

        if (version < 6)
        {
            await ApplyMigration6Async(conn, ct);
            await SetUserVersionAsync(conn, 6, ct);
        }

        if (version < 7)
        {
            await ApplyMigration7Async(conn, ct);
            await SetUserVersionAsync(conn, 7, ct);
        }

        if (version < 8)
        {
            await ApplyMigration8Async(conn, ct);
            await SetUserVersionAsync(conn, 8, ct);
        }

        if (version < 9)
        {
            await ApplyMigration9Async(conn, ct);
            await SetUserVersionAsync(conn, 9, ct);
        }
    }

    private static async Task ApplyMigration1Async(SqliteConnection conn, CancellationToken ct)
    {
        // conv_sessions
        await ExecuteNonQueryAsync(conn, """
            CREATE TABLE IF NOT EXISTS conv_sessions (
                id           INTEGER PRIMARY KEY AUTOINCREMENT,
                source       TEXT NOT NULL,
                external_id  TEXT NOT NULL,
                title        TEXT NOT NULL DEFAULT '',
                created_at   TEXT NOT NULL,
                updated_at   TEXT NOT NULL,
                synced_at    TEXT NULL,
                content_hash TEXT NULL,
                UNIQUE(source, external_id)
            )
            """, ct);

        await ExecuteNonQueryAsync(conn, "CREATE INDEX IF NOT EXISTS idx_sessions_source ON conv_sessions(source)", ct);
        await ExecuteNonQueryAsync(conn, "CREATE INDEX IF NOT EXISTS idx_sessions_synced ON conv_sessions(synced_at)", ct);

        // conv_messages
        await ExecuteNonQueryAsync(conn, """
            CREATE TABLE IF NOT EXISTS conv_messages (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                session_id  INTEGER NOT NULL REFERENCES conv_sessions(id) ON DELETE CASCADE,
                role        TEXT NOT NULL,
                content     TEXT NOT NULL,
                created_at  TEXT NOT NULL,
                token_count INTEGER NULL,
                UNIQUE(session_id, role, created_at)
            )
            """, ct);

        await ExecuteNonQueryAsync(conn, "CREATE INDEX IF NOT EXISTS idx_messages_session ON conv_messages(session_id)", ct);

        // FTS5 virtual table for fast local search
        await ExecuteNonQueryAsync(conn, """
            CREATE VIRTUAL TABLE IF NOT EXISTS conv_messages_fts
            USING fts5(content, content='conv_messages', content_rowid='id')
            """, ct);

        // FTS triggers to keep index in sync
        await ExecuteNonQueryAsync(conn, """
            CREATE TRIGGER IF NOT EXISTS conv_messages_ai AFTER INSERT ON conv_messages BEGIN
                INSERT INTO conv_messages_fts(rowid, content) VALUES (new.id, new.content);
            END
            """, ct);

        await ExecuteNonQueryAsync(conn, """
            CREATE TRIGGER IF NOT EXISTS conv_messages_ad AFTER DELETE ON conv_messages BEGIN
                INSERT INTO conv_messages_fts(conv_messages_fts, rowid, content) VALUES ('delete', old.id, old.content);
            END
            """, ct);

        await ExecuteNonQueryAsync(conn, """
            CREATE TRIGGER IF NOT EXISTS conv_messages_au AFTER UPDATE ON conv_messages BEGIN
                INSERT INTO conv_messages_fts(conv_messages_fts, rowid, content) VALUES ('delete', old.id, old.content);
                INSERT INTO conv_messages_fts(rowid, content) VALUES (new.id, new.content);
            END
            """, ct);

        // sync_ledger
        await ExecuteNonQueryAsync(conn, """
            CREATE TABLE IF NOT EXISTS sync_ledger (
                source         TEXT NOT NULL,
                external_id    TEXT NOT NULL,
                last_synced_at TEXT NOT NULL,
                doc_service_id TEXT NULL,
                memory_synced  INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY(source, external_id)
            )
            """, ct);

        // memory_observations
        await ExecuteNonQueryAsync(conn, """
            CREATE TABLE IF NOT EXISTS memory_observations (
                id               INTEGER PRIMARY KEY AUTOINCREMENT,
                session_id       INTEGER NOT NULL REFERENCES conv_sessions(id) ON DELETE CASCADE,
                content          TEXT NOT NULL,
                importance_score REAL NOT NULL DEFAULT 0.5,
                captured_at      TEXT NOT NULL,
                synced_to_cloud  INTEGER NOT NULL DEFAULT 0
            )
            """, ct);

        // memory_summaries
        await ExecuteNonQueryAsync(conn, """
            CREATE TABLE IF NOT EXISTS memory_summaries (
                id         INTEGER PRIMARY KEY AUTOINCREMENT,
                session_id INTEGER NOT NULL REFERENCES conv_sessions(id) ON DELETE CASCADE,
                summary    TEXT NOT NULL,
                model      TEXT NOT NULL,
                created_at TEXT NOT NULL
            )
            """, ct);
    }

    private static async Task ApplyMigration4Async(SqliteConnection conn, CancellationToken ct)
    {
        // conv_observation_links — directed causal/temporal links between observations (WI-983)
        await ExecuteNonQueryAsync(conn, """
            CREATE TABLE IF NOT EXISTS conv_observation_links (
                id                    INTEGER PRIMARY KEY AUTOINCREMENT,
                source_observation_id INTEGER NOT NULL REFERENCES conv_observations(id) ON DELETE CASCADE,
                target_observation_id INTEGER NOT NULL REFERENCES conv_observations(id) ON DELETE CASCADE,
                link_type             TEXT NOT NULL,
                confidence            REAL NOT NULL DEFAULT 1.0,
                created_at            TEXT NOT NULL,
                UNIQUE(source_observation_id, target_observation_id, link_type)
            )
            """, ct);

        await ExecuteNonQueryAsync(conn, "CREATE INDEX IF NOT EXISTS idx_links_source ON conv_observation_links(source_observation_id)", ct);
        await ExecuteNonQueryAsync(conn, "CREATE INDEX IF NOT EXISTS idx_links_target ON conv_observation_links(target_observation_id)", ct);
        await ExecuteNonQueryAsync(conn, "CREATE INDEX IF NOT EXISTS idx_links_type ON conv_observation_links(link_type)", ct);
    }

    private static async Task ApplyMigration5Async(SqliteConnection conn, CancellationToken ct)
    {
        // conv_embeddings — 384-dim float vectors stored as BLOBs for vector search (WI-989)
        await ExecuteNonQueryAsync(conn, """
            CREATE TABLE IF NOT EXISTS conv_embeddings (
                session_id INTEGER PRIMARY KEY REFERENCES conv_sessions(id) ON DELETE CASCADE,
                embedding  BLOB NOT NULL,
                created_at TEXT NOT NULL
            )
            """, ct);
    }

    private static async Task ApplyMigration6Async(SqliteConnection conn, CancellationToken ct)
    {
        // conv_relationships — heuristic cross-session links (WI-986)
        await ExecuteNonQueryAsync(conn, """
            CREATE TABLE IF NOT EXISTS conv_relationships (
                id                INTEGER PRIMARY KEY AUTOINCREMENT,
                session_id_a      INTEGER NOT NULL REFERENCES conv_sessions(id),
                session_id_b      INTEGER NOT NULL REFERENCES conv_sessions(id),
                relationship_type TEXT NOT NULL,
                confidence        REAL NOT NULL DEFAULT 0.5,
                evidence          TEXT NULL,
                created_at        TEXT NOT NULL,
                UNIQUE(session_id_a, session_id_b, relationship_type)
            )
            """, ct);

        await ExecuteNonQueryAsync(conn, "CREATE INDEX IF NOT EXISTS idx_conv_rel_a ON conv_relationships(session_id_a)", ct);
        await ExecuteNonQueryAsync(conn, "CREATE INDEX IF NOT EXISTS idx_conv_rel_b ON conv_relationships(session_id_b)", ct);
    }

    private static async Task ApplyMigration7Async(SqliteConnection conn, CancellationToken ct)
    {
        // conv_workitem_links — session → TaskMaster WorkItem references (WI-987)
        await ExecuteNonQueryAsync(conn, """
            CREATE TABLE IF NOT EXISTS conv_workitem_links (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                session_id  INTEGER NOT NULL REFERENCES conv_sessions(id) ON DELETE CASCADE,
                workitem_id INTEGER NOT NULL,
                link_source TEXT NOT NULL,
                confidence  REAL NOT NULL DEFAULT 1.0,
                created_at  TEXT NOT NULL,
                UNIQUE(session_id, workitem_id)
            )
            """, ct);

        await ExecuteNonQueryAsync(conn, "CREATE INDEX IF NOT EXISTS idx_wi_links_session ON conv_workitem_links(session_id)", ct);
        await ExecuteNonQueryAsync(conn, "CREATE INDEX IF NOT EXISTS idx_wi_links_workitem ON conv_workitem_links(workitem_id)", ct);
    }

    private static async Task ApplyMigration2Async(SqliteConnection conn, CancellationToken ct)
    {
        // conv_observations — one row per content block (tool_use, tool_result, text, thinking)
        await ExecuteNonQueryAsync(conn, """
            CREATE TABLE IF NOT EXISTS conv_observations (
                id                    INTEGER PRIMARY KEY AUTOINCREMENT,
                session_id            INTEGER NOT NULL REFERENCES conv_sessions(id) ON DELETE CASCADE,
                observation_type      TEXT NOT NULL,
                tool_name             TEXT NULL,
                file_path             TEXT NULL,
                content               TEXT NOT NULL DEFAULT '',
                token_count           INTEGER NULL,
                sequence_number       INTEGER NOT NULL,
                parent_observation_id INTEGER NULL REFERENCES conv_observations(id) ON DELETE SET NULL,
                created_at            TEXT NOT NULL,
                UNIQUE(session_id, sequence_number)
            )
            """, ct);

        await ExecuteNonQueryAsync(conn, "CREATE INDEX IF NOT EXISTS idx_obs_session ON conv_observations(session_id)", ct);
        await ExecuteNonQueryAsync(conn, "CREATE INDEX IF NOT EXISTS idx_obs_type ON conv_observations(observation_type)", ct);
        await ExecuteNonQueryAsync(conn, "CREATE INDEX IF NOT EXISTS idx_obs_parent ON conv_observations(parent_observation_id)", ct);

        // FTS5 virtual table — searches content, tool_name, and file_path
        await ExecuteNonQueryAsync(conn, """
            CREATE VIRTUAL TABLE IF NOT EXISTS conv_observations_fts
            USING fts5(content, tool_name, file_path, content='conv_observations', content_rowid='id')
            """, ct);

        // FTS triggers to keep the index in sync (mirrors conv_messages_fts pattern)
        await ExecuteNonQueryAsync(conn, """
            CREATE TRIGGER IF NOT EXISTS conv_observations_ai AFTER INSERT ON conv_observations BEGIN
                INSERT INTO conv_observations_fts(rowid, content, tool_name, file_path)
                VALUES (new.id, new.content, COALESCE(new.tool_name, ''), COALESCE(new.file_path, ''));
            END
            """, ct);

        await ExecuteNonQueryAsync(conn, """
            CREATE TRIGGER IF NOT EXISTS conv_observations_ad AFTER DELETE ON conv_observations BEGIN
                INSERT INTO conv_observations_fts(conv_observations_fts, rowid, content, tool_name, file_path)
                VALUES ('delete', old.id, old.content, COALESCE(old.tool_name, ''), COALESCE(old.file_path, ''));
            END
            """, ct);

        await ExecuteNonQueryAsync(conn, """
            CREATE TRIGGER IF NOT EXISTS conv_observations_au AFTER UPDATE ON conv_observations BEGIN
                INSERT INTO conv_observations_fts(conv_observations_fts, rowid, content, tool_name, file_path)
                VALUES ('delete', old.id, old.content, COALESCE(old.tool_name, ''), COALESCE(old.file_path, ''));
                INSERT INTO conv_observations_fts(rowid, content, tool_name, file_path)
                VALUES (new.id, new.content, COALESCE(new.tool_name, ''), COALESCE(new.file_path, ''));
            END
            """, ct);
    }

    private static async Task ApplyMigration3Async(SqliteConnection conn, CancellationToken ct)
    {
        // conv_session_summaries — one summary per session, produced by the compression pipeline
        await ExecuteNonQueryAsync(conn, """
            CREATE TABLE IF NOT EXISTS conv_session_summaries (
                id                   INTEGER PRIMARY KEY AUTOINCREMENT,
                session_id           INTEGER NOT NULL REFERENCES conv_sessions(id) ON DELETE CASCADE,
                category             TEXT NOT NULL DEFAULT 'unknown',
                narrative            TEXT NOT NULL,
                concepts             TEXT NULL,
                file_references      TEXT NULL,
                git_commit_messages  TEXT NULL,
                tests_run            INTEGER NULL,
                tests_passed         INTEGER NULL,
                tests_failed         INTEGER NULL,
                error_count          INTEGER NOT NULL DEFAULT 0,
                tool_use_count       INTEGER NOT NULL DEFAULT 0,
                compression_layer    INTEGER NOT NULL DEFAULT 1,
                created_at           TEXT NOT NULL,
                UNIQUE(session_id)
            )
            """, ct);

        await ExecuteNonQueryAsync(conn, "CREATE INDEX IF NOT EXISTS idx_session_summaries_session ON conv_session_summaries(session_id)", ct);

        // FTS5 virtual table — searches narrative, concepts, file_references, and category
        await ExecuteNonQueryAsync(conn, """
            CREATE VIRTUAL TABLE IF NOT EXISTS conv_session_summaries_fts
            USING fts5(narrative, concepts, file_references, category, content='conv_session_summaries', content_rowid='id')
            """, ct);

        await ExecuteNonQueryAsync(conn, """
            CREATE TRIGGER IF NOT EXISTS conv_session_summaries_ai AFTER INSERT ON conv_session_summaries BEGIN
                INSERT INTO conv_session_summaries_fts(rowid, narrative, concepts, file_references, category)
                VALUES (new.id, new.narrative, COALESCE(new.concepts, ''), COALESCE(new.file_references, ''), new.category);
            END
            """, ct);

        await ExecuteNonQueryAsync(conn, """
            CREATE TRIGGER IF NOT EXISTS conv_session_summaries_ad AFTER DELETE ON conv_session_summaries BEGIN
                INSERT INTO conv_session_summaries_fts(conv_session_summaries_fts, rowid, narrative, concepts, file_references, category)
                VALUES ('delete', old.id, old.narrative, COALESCE(old.concepts, ''), COALESCE(old.file_references, ''), old.category);
            END
            """, ct);

        await ExecuteNonQueryAsync(conn, """
            CREATE TRIGGER IF NOT EXISTS conv_session_summaries_au AFTER UPDATE ON conv_session_summaries BEGIN
                INSERT INTO conv_session_summaries_fts(conv_session_summaries_fts, rowid, narrative, concepts, file_references, category)
                VALUES ('delete', old.id, old.narrative, COALESCE(old.concepts, ''), COALESCE(old.file_references, ''), old.category);
                INSERT INTO conv_session_summaries_fts(rowid, narrative, concepts, file_references, category)
                VALUES (new.id, new.narrative, COALESCE(new.concepts, ''), COALESCE(new.file_references, ''), new.category);
            END
            """, ct);
    }

    private static async Task ApplyMigration8Async(SqliteConnection conn, CancellationToken ct)
    {
        // Add excluded column to conv_sessions for privacy controls (WI-992)
        await ExecuteNonQueryAsync(conn, """
            ALTER TABLE conv_sessions ADD COLUMN excluded INTEGER NOT NULL DEFAULT 0
            """, ct);

        await ExecuteNonQueryAsync(conn,
            "CREATE INDEX IF NOT EXISTS idx_sessions_excluded ON conv_sessions(excluded)", ct);
    }

    private static async Task ApplyMigration9Async(SqliteConnection conn, CancellationToken ct)
    {
        // sync_metadata — key/value store for pull sync state: last_pull_at, device_id (WI-991)
        await ExecuteNonQueryAsync(conn, """
            CREATE TABLE IF NOT EXISTS sync_metadata (
                key        TEXT PRIMARY KEY,
                value      TEXT NOT NULL,
                updated_at TEXT NOT NULL
            )
            """, ct);
    }

    // ── Session summary methods ───────────────────────────────────────────────

    public async Task UpsertSessionSummaryAsync(SessionSummary summary, CancellationToken ct = default)
    {
        var conn = await GetConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO conv_session_summaries
                (session_id, category, narrative, concepts, file_references, git_commit_messages,
                 tests_run, tests_passed, tests_failed, error_count, tool_use_count, compression_layer, created_at)
            VALUES
                (@session_id, @category, @narrative, @concepts, @file_references, @git_commit_messages,
                 @tests_run, @tests_passed, @tests_failed, @error_count, @tool_use_count, @compression_layer, @created_at)
            ON CONFLICT(session_id) DO UPDATE SET
                category             = excluded.category,
                narrative            = excluded.narrative,
                concepts             = excluded.concepts,
                file_references      = excluded.file_references,
                git_commit_messages  = excluded.git_commit_messages,
                tests_run            = excluded.tests_run,
                tests_passed         = excluded.tests_passed,
                tests_failed         = excluded.tests_failed,
                error_count          = excluded.error_count,
                tool_use_count       = excluded.tool_use_count,
                compression_layer    = excluded.compression_layer,
                created_at           = excluded.created_at
            """;
        cmd.Parameters.AddWithValue("@session_id",          summary.SessionId);
        cmd.Parameters.AddWithValue("@category",            summary.Category);
        cmd.Parameters.AddWithValue("@narrative",           summary.Narrative);
        cmd.Parameters.AddWithValue("@concepts",            summary.Concepts ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@file_references",     summary.FileReferences ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@git_commit_messages", summary.GitCommitMessages ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@tests_run",           summary.TestsRun.HasValue ? (object)(summary.TestsRun.Value ? 1 : 0) : DBNull.Value);
        cmd.Parameters.AddWithValue("@tests_passed",        summary.TestsPassed.HasValue ? (object)summary.TestsPassed.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("@tests_failed",        summary.TestsFailed.HasValue ? (object)summary.TestsFailed.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("@error_count",         summary.ErrorCount);
        cmd.Parameters.AddWithValue("@tool_use_count",      summary.ToolUseCount);
        cmd.Parameters.AddWithValue("@compression_layer",   summary.CompressionLayer);
        cmd.Parameters.AddWithValue("@created_at",          summary.CreatedAt.ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct);

        // Sync back DB-assigned Id
        if (summary.Id == 0)
        {
            await using var idCmd = conn.CreateCommand();
            idCmd.CommandText = "SELECT id FROM conv_session_summaries WHERE session_id = @session_id";
            idCmd.Parameters.AddWithValue("@session_id", summary.SessionId);
            var result = await idCmd.ExecuteScalarAsync(ct);
            if (result is long id)
                summary.Id = id;
        }
    }

    public async Task<SessionSummary?> GetSessionSummaryAsync(long sessionId, CancellationToken ct = default)
    {
        var conn = await GetConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, session_id, category, narrative, concepts, file_references, git_commit_messages,
                   tests_run, tests_passed, tests_failed, error_count, tool_use_count, compression_layer, created_at
            FROM conv_session_summaries
            WHERE session_id = @session_id
            """;
        cmd.Parameters.AddWithValue("@session_id", sessionId);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct))
            return MapSummary(reader);

        return null;
    }

    public async Task<IReadOnlyList<SessionSummary>> SearchSummariesAsync(string query, int limit = 10, CancellationToken ct = default)
    {
        var conn = await GetConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT s.id, s.session_id, s.category, s.narrative, s.concepts, s.file_references,
                   s.git_commit_messages, s.tests_run, s.tests_passed, s.tests_failed,
                   s.error_count, s.tool_use_count, s.compression_layer, s.created_at
            FROM conv_session_summaries_fts fts
            JOIN conv_session_summaries s ON s.id = fts.rowid
            JOIN conv_sessions cs ON cs.id = s.session_id
            WHERE conv_session_summaries_fts MATCH @query
              AND cs.excluded = 0
            ORDER BY rank
            LIMIT @limit
            """;
        cmd.Parameters.AddWithValue("@query", query);
        cmd.Parameters.AddWithValue("@limit", limit);

        var results = new List<SessionSummary>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            results.Add(MapSummary(reader));

        return results;
    }

    public async Task<IReadOnlyList<long>> GetSessionsWithoutSummaryAsync(int batchSize = 10, CancellationToken ct = default)
    {
        var conn = await GetConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT DISTINCT o.session_id
            FROM conv_observations o
            WHERE NOT EXISTS (
                SELECT 1 FROM conv_session_summaries css WHERE css.session_id = o.session_id
            )
            LIMIT @batch
            """;
        cmd.Parameters.AddWithValue("@batch", batchSize);

        var results = new List<long>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            results.Add(reader.GetInt64(0));

        return results;
    }

    // ── Privacy Controls (WI-992) ────────────────────────────────────────────

    public async Task ExcludeSessionAsync(long sessionId, bool excluded, CancellationToken ct = default)
    {
        var conn = await GetConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE conv_sessions SET excluded = @excluded WHERE id = @id";
        cmd.Parameters.AddWithValue("@excluded", excluded ? 1 : 0);
        cmd.Parameters.AddWithValue("@id", sessionId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<bool> IsSessionExcludedAsync(long sessionId, CancellationToken ct = default)
    {
        var conn = await GetConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT excluded FROM conv_sessions WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", sessionId);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is long v && v != 0;
    }

    // ── Vector Embeddings (WI-989) ────────────────────────────────────────────

    public async Task UpsertEmbeddingAsync(long sessionId, float[] embedding, CancellationToken ct = default)
    {
        var conn = await GetConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO conv_embeddings (session_id, embedding, created_at)
            VALUES (@session_id, @embedding, @created_at)
            ON CONFLICT(session_id) DO UPDATE SET
                embedding  = excluded.embedding,
                created_at = excluded.created_at
            """;
        cmd.Parameters.AddWithValue("@session_id", sessionId);
        cmd.Parameters.AddWithValue("@embedding", FloatArrayToBlob(embedding));
        cmd.Parameters.AddWithValue("@created_at", DateTime.UtcNow.ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<(long SessionId, float[] Embedding)>> GetAllEmbeddingsAsync(
        CancellationToken ct = default)
    {
        var conn = await GetConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT session_id, embedding FROM conv_embeddings";

        var results = new List<(long, float[])>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var sessionId = reader.GetInt64(0);
            var blob = (byte[])reader[1];
            results.Add((sessionId, BlobToFloatArray(blob)));
        }

        return results;
    }

    public async Task<IReadOnlyList<long>> GetSessionsWithoutEmbeddingAsync(int batchSize = 10,
        CancellationToken ct = default)
    {
        var conn = await GetConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT s.session_id
            FROM conv_session_summaries s
            LEFT JOIN conv_embeddings e ON e.session_id = s.session_id
            WHERE e.session_id IS NULL
            LIMIT @batch
            """;
        cmd.Parameters.AddWithValue("@batch", batchSize);

        var results = new List<long>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            results.Add(reader.GetInt64(0));

        return results;
    }

    // ── Conversation Relationships (WI-986) ──────────────────────────────────

    public async Task<ConversationSession?> GetSessionByIdAsync(long sessionId, CancellationToken ct = default)
    {
        var conn = await GetConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, source, external_id, title, created_at, updated_at, synced_at, content_hash, excluded
            FROM conv_sessions
            WHERE id = @id
            """;
        cmd.Parameters.AddWithValue("@id", sessionId);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct))
            return MapSession(reader);

        return null;
    }

    public async Task<ConversationSession?> GetSessionBySourceAndExternalIdAsync(
        string source, string externalId, CancellationToken ct = default)
    {
        var conn = await GetConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, source, external_id, title, created_at, updated_at, synced_at, content_hash, excluded
            FROM conv_sessions
            WHERE source = @source AND external_id = @external_id
            """;
        cmd.Parameters.AddWithValue("@source", source);
        cmd.Parameters.AddWithValue("@external_id", externalId);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct))
            return MapSession(reader);

        return null;
    }

    public async Task<IReadOnlyList<ConversationSession>> GetSessionsInTimeWindowAsync(
        DateTime from, DateTime to, long excludeSessionId,
        int limit = 200, CancellationToken ct = default)
    {
        var conn = await GetConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, source, external_id, title, created_at, updated_at, synced_at, content_hash, excluded
            FROM conv_sessions
            WHERE created_at >= @from
              AND created_at <= @to
              AND id != @exclude
              AND excluded = 0
            ORDER BY created_at DESC
            LIMIT @limit
            """;
        cmd.Parameters.AddWithValue("@from",    from.ToString("O"));
        cmd.Parameters.AddWithValue("@to",      to.ToString("O"));
        cmd.Parameters.AddWithValue("@exclude", excludeSessionId);
        cmd.Parameters.AddWithValue("@limit",   limit);

        var results = new List<ConversationSession>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            results.Add(MapSession(reader));

        return results;
    }

    public async Task<IReadOnlyList<(long SessionId, int SharedCount)>> GetSessionsWithSharedFilesAsync(
        IEnumerable<string> filePaths, long excludeSessionId, DateTime since,
        CancellationToken ct = default)
    {
        var fileList = filePaths.ToList();
        if (fileList.Count == 0) return [];

        var conn = await GetConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();

        // Build parameterized IN clause (strings must be parameterized, not interpolated)
        var placeholders = string.Join(", ", fileList.Select((_, i) => $"@fp{i}"));
        cmd.CommandText = $"""
            SELECT o.session_id, COUNT(DISTINCT o.file_path) AS shared_count
            FROM conv_observations o
            WHERE o.file_path IN ({placeholders})
              AND o.session_id != @exclude
              AND o.created_at >= @since
            GROUP BY o.session_id
            """;

        for (int i = 0; i < fileList.Count; i++)
            cmd.Parameters.AddWithValue($"@fp{i}", fileList[i]);
        cmd.Parameters.AddWithValue("@exclude", excludeSessionId);
        cmd.Parameters.AddWithValue("@since",   since.ToString("O"));

        var results = new List<(long, int)>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            results.Add((reader.GetInt64(0), (int)reader.GetInt64(1)));

        return results;
    }

    public async Task<IReadOnlyList<SessionSummary>> GetBulkSessionSummariesAsync(
        IEnumerable<long> sessionIds, CancellationToken ct = default)
    {
        var ids = string.Join(",", sessionIds);
        if (string.IsNullOrEmpty(ids)) return [];

        var conn = await GetConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT id, session_id, category, narrative, concepts, file_references, git_commit_messages,
                   tests_run, tests_passed, tests_failed, error_count, tool_use_count, compression_layer, created_at
            FROM conv_session_summaries
            WHERE session_id IN ({ids})
            """;

        var results = new List<SessionSummary>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            results.Add(MapSummary(reader));

        return results;
    }

    public async Task CreateConversationLinksAsync(
        IEnumerable<ConversationRelationship> links, CancellationToken ct = default)
    {
        var conn = await GetConnectionAsync(ct);
        await using var tx = conn.BeginTransaction();
        try
        {
            foreach (var link in links)
            {
                await using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT INTO conv_relationships
                        (session_id_a, session_id_b, relationship_type, confidence, evidence, created_at)
                    VALUES
                        (@a, @b, @type, @confidence, @evidence, @created_at)
                    ON CONFLICT(session_id_a, session_id_b, relationship_type) DO UPDATE SET
                        confidence = MAX(confidence, excluded.confidence),
                        evidence   = excluded.evidence
                    """;
                cmd.Parameters.AddWithValue("@a",          link.SessionIdA);
                cmd.Parameters.AddWithValue("@b",          link.SessionIdB);
                cmd.Parameters.AddWithValue("@type",       link.RelationshipType);
                cmd.Parameters.AddWithValue("@confidence", link.Confidence);
                cmd.Parameters.AddWithValue("@evidence",   link.Evidence ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@created_at", link.CreatedAt.ToString("O"));
                await cmd.ExecuteNonQueryAsync(ct);

                // Sync back DB-assigned Id
                if (link.Id == 0)
                {
                    await using var idCmd = conn.CreateCommand();
                    idCmd.Transaction = tx;
                    idCmd.CommandText = """
                        SELECT id FROM conv_relationships
                        WHERE session_id_a = @a AND session_id_b = @b AND relationship_type = @type
                        """;
                    idCmd.Parameters.AddWithValue("@a",    link.SessionIdA);
                    idCmd.Parameters.AddWithValue("@b",    link.SessionIdB);
                    idCmd.Parameters.AddWithValue("@type", link.RelationshipType);
                    var result = await idCmd.ExecuteScalarAsync(ct);
                    if (result is long id) link.Id = id;
                }
            }
            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    public async Task<IReadOnlyList<ConversationRelationship>> GetRelatedSessionsAsync(
        long sessionId, CancellationToken ct = default)
    {
        var conn = await GetConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, session_id_a, session_id_b, relationship_type, confidence, evidence, created_at
            FROM conv_relationships
            WHERE session_id_a = @session_id OR session_id_b = @session_id
            ORDER BY confidence DESC
            """;
        cmd.Parameters.AddWithValue("@session_id", sessionId);

        var results = new List<ConversationRelationship>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            results.Add(MapRelationship(reader));

        return results;
    }

    private static ConversationRelationship MapRelationship(SqliteDataReader r) => new()
    {
        Id               = r.GetInt64(0),
        SessionIdA       = r.GetInt64(1),
        SessionIdB       = r.GetInt64(2),
        RelationshipType = r.GetString(3),
        Confidence       = r.GetDouble(4),
        Evidence         = r.IsDBNull(5) ? null : r.GetString(5),
        CreatedAt        = DateTime.Parse(r.GetString(6))
    };

    // ── WorkItem Links (WI-987) ───────────────────────────────────────────────

    public async Task CreateWorkItemLinksAsync(
        IEnumerable<WorkItemLink> links, CancellationToken ct = default)
    {
        var conn = await GetConnectionAsync(ct);
        await using var tx = conn.BeginTransaction();
        try
        {
            foreach (var link in links)
            {
                await using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT INTO conv_workitem_links
                        (session_id, workitem_id, link_source, confidence, created_at)
                    VALUES
                        (@session_id, @workitem_id, @link_source, @confidence, @created_at)
                    ON CONFLICT(session_id, workitem_id) DO UPDATE SET
                        confidence  = MAX(confidence, excluded.confidence),
                        link_source = excluded.link_source
                    """;
                cmd.Parameters.AddWithValue("@session_id",  link.SessionId);
                cmd.Parameters.AddWithValue("@workitem_id", link.WorkItemId);
                cmd.Parameters.AddWithValue("@link_source", link.LinkSource);
                cmd.Parameters.AddWithValue("@confidence",  link.Confidence);
                cmd.Parameters.AddWithValue("@created_at",  link.CreatedAt.ToString("O"));
                await cmd.ExecuteNonQueryAsync(ct);

                // Sync back DB-assigned Id
                if (link.Id == 0)
                {
                    await using var idCmd = conn.CreateCommand();
                    idCmd.Transaction = tx;
                    idCmd.CommandText = """
                        SELECT id FROM conv_workitem_links
                        WHERE session_id = @session_id AND workitem_id = @workitem_id
                        """;
                    idCmd.Parameters.AddWithValue("@session_id",  link.SessionId);
                    idCmd.Parameters.AddWithValue("@workitem_id", link.WorkItemId);
                    var result = await idCmd.ExecuteScalarAsync(ct);
                    if (result is long id) link.Id = id;
                }
            }
            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    public async Task<IReadOnlyList<WorkItemLink>> GetLinkedWorkItemsAsync(
        long sessionId, CancellationToken ct = default)
    {
        var conn = await GetConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, session_id, workitem_id, link_source, confidence, created_at
            FROM conv_workitem_links
            WHERE session_id = @session_id
            ORDER BY confidence DESC
            """;
        cmd.Parameters.AddWithValue("@session_id", sessionId);

        var results = new List<WorkItemLink>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            results.Add(MapWorkItemLink(reader));

        return results;
    }

    public async Task<IReadOnlyList<ConversationSession>> GetSessionsForWorkItemAsync(
        int workItemId, CancellationToken ct = default)
    {
        var conn = await GetConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT s.id, s.source, s.external_id, s.title, s.created_at, s.updated_at, s.synced_at, s.content_hash, s.excluded
            FROM conv_workitem_links wl
            JOIN conv_sessions s ON s.id = wl.session_id
            WHERE wl.workitem_id = @workitem_id
              AND s.excluded = 0
            ORDER BY wl.confidence DESC, s.created_at DESC
            """;
        cmd.Parameters.AddWithValue("@workitem_id", workItemId);

        var results = new List<ConversationSession>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            results.Add(MapSession(reader));

        return results;
    }

    private static WorkItemLink MapWorkItemLink(SqliteDataReader r) => new()
    {
        Id         = r.GetInt64(0),
        SessionId  = r.GetInt64(1),
        WorkItemId = r.GetInt32(2),
        LinkSource = r.GetString(3),
        Confidence = r.GetDouble(4),
        CreatedAt  = DateTime.Parse(r.GetString(5))
    };

    private static byte[] FloatArrayToBlob(float[] vector)
    {
        var bytes = new byte[vector.Length * sizeof(float)];
        Buffer.BlockCopy(vector, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private static float[] BlobToFloatArray(byte[] blob)
    {
        var vector = new float[blob.Length / sizeof(float)];
        Buffer.BlockCopy(blob, 0, vector, 0, blob.Length);
        return vector;
    }

    // ── Connection management ─────────────────────────────────────────────────

    private async Task<SqliteConnection> GetConnectionAsync(CancellationToken ct)
    {
        if (_initialized && _connection is not null)
            return _connection;

        await _initLock.WaitAsync(ct);
        try
        {
            if (_initialized && _connection is not null)
                return _connection;

            _connection = new SqliteConnection($"Data Source={_dbPath}");
            await _connection.OpenAsync(ct);
            await EnsureSchemaAsync(_connection, ct);
            _initialized = true;
            _logger.LogInformation("Local SQLite database opened: {Path}", _dbPath);
            return _connection;
        }
        finally
        {
            _initLock.Release();
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static async Task ExecuteNonQueryAsync(SqliteConnection conn, string sql, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<int> GetUserVersionAsync(SqliteConnection conn, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA user_version";
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is long v ? (int)v : 0;
    }

    private static async Task SetUserVersionAsync(SqliteConnection conn, int version, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA user_version = {version}";
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static ConversationSession MapSession(SqliteDataReader r) => new()
    {
        Id          = r.GetInt64(0),
        Source      = Enum.Parse<ConversationSource>(r.GetString(1)),
        ExternalId  = r.GetString(2),
        Title       = r.GetString(3),
        CreatedAt   = DateTime.Parse(r.GetString(4)),
        UpdatedAt   = DateTime.Parse(r.GetString(5)),
        SyncedAt    = r.IsDBNull(6) ? null : DateTime.Parse(r.GetString(6)),
        ContentHash = r.IsDBNull(7) ? null : r.GetString(7),
        Excluded    = r.FieldCount > 8 && !r.IsDBNull(8) && r.GetInt64(8) != 0
    };

    private static ConversationMessage MapMessage(SqliteDataReader r) => new()
    {
        Id         = r.GetInt64(0),
        SessionId  = r.GetInt64(1),
        Role       = r.GetString(2),
        Content    = r.GetString(3),
        CreatedAt  = DateTime.Parse(r.GetString(4)),
        TokenCount = r.IsDBNull(5) ? null : r.GetInt32(5)
    };

    private static SessionSummary MapSummary(SqliteDataReader r) => new()
    {
        Id                 = r.GetInt64(0),
        SessionId          = r.GetInt64(1),
        Category           = r.GetString(2),
        Narrative          = r.GetString(3),
        Concepts           = r.IsDBNull(4) ? null : r.GetString(4),
        FileReferences     = r.IsDBNull(5) ? null : r.GetString(5),
        GitCommitMessages  = r.IsDBNull(6) ? null : r.GetString(6),
        TestsRun           = r.IsDBNull(7) ? null : r.GetInt64(7) == 1,
        TestsPassed        = r.IsDBNull(8) ? null : r.GetInt32(8),
        TestsFailed        = r.IsDBNull(9) ? null : r.GetInt32(9),
        ErrorCount         = r.GetInt32(10),
        ToolUseCount       = r.GetInt32(11),
        CompressionLayer   = r.GetInt32(12),
        CreatedAt          = DateTime.Parse(r.GetString(13))
    };

    private static ConversationObservation MapObservation(SqliteDataReader r) => new()
    {
        Id                   = r.GetInt64(0),
        SessionId            = r.GetInt64(1),
        ObservationType      = Enum.Parse<ObservationType>(r.GetString(2)),
        ToolName             = r.IsDBNull(3) ? null : r.GetString(3),
        FilePath             = r.IsDBNull(4) ? null : r.GetString(4),
        Content              = r.GetString(5),
        TokenCount           = r.IsDBNull(6) ? null : r.GetInt32(6),
        SequenceNumber       = r.GetInt32(7),
        ParentObservationId  = r.IsDBNull(8) ? null : r.GetInt64(8),
        CreatedAt            = DateTime.Parse(r.GetString(9))
    };

    private static ObservationLink MapLink(SqliteDataReader r) => new()
    {
        Id                   = r.GetInt64(0),
        SourceObservationId  = r.GetInt64(1),
        TargetObservationId  = r.GetInt64(2),
        LinkType             = r.GetString(3),
        Confidence           = r.GetDouble(4),
        CreatedAt            = DateTime.Parse(r.GetString(5))
    };

    // ── Sync Metadata (WI-991) ───────────────────────────────────────────────

    public async Task<string?> GetSyncMetadataAsync(string key, CancellationToken ct = default)
    {
        var conn = await GetConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM sync_metadata WHERE key = @key";
        cmd.Parameters.AddWithValue("@key", key);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is string s ? s : null;
    }

    public async Task SetSyncMetadataAsync(string key, string value, CancellationToken ct = default)
    {
        var conn = await GetConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO sync_metadata (key, value, updated_at)
            VALUES (@key, @value, @updated_at)
            ON CONFLICT(key) DO UPDATE SET
                value      = excluded.value,
                updated_at = excluded.updated_at
            """;
        cmd.Parameters.AddWithValue("@key", key);
        cmd.Parameters.AddWithValue("@value", value);
        cmd.Parameters.AddWithValue("@updated_at", DateTime.UtcNow.ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ── Hook Activity (TRAY-3) ────────────────────────────────────────────────

    public async Task<DateTime?> GetLastHookObservationTimeAsync(CancellationToken ct = default)
    {
        var conn = await GetConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT MAX(o.created_at)
            FROM conv_observations o
            JOIN conv_sessions s ON s.id = o.session_id
            WHERE s.source = @source
            """;
        cmd.Parameters.AddWithValue("@source", ConversationSource.ClaudeCode.ToString());
        var result = await cmd.ExecuteScalarAsync(ct);
        if (result is null || result == DBNull.Value || result is not string raw) return null;
        return DateTime.Parse(raw, null, System.Globalization.DateTimeStyles.RoundtripKind);
    }

    public async Task<IReadOnlyList<ConversationObservation>> GetRecentHookObservationsAsync(int limit = 20, CancellationToken ct = default)
    {
        var conn = await GetConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        // content column is excluded — callers (tray log panel) only need tool_name, file_path, created_at
        cmd.CommandText = """
            SELECT o.id, o.session_id, o.observation_type, o.tool_name, o.file_path, '' AS content,
                   o.token_count, o.sequence_number, o.parent_observation_id, o.created_at
            FROM conv_observations o
            JOIN conv_sessions s ON s.id = o.session_id
            WHERE s.source = @source
            ORDER BY o.created_at DESC
            LIMIT @limit
            """;
        cmd.Parameters.AddWithValue("@source", ConversationSource.ClaudeCode.ToString());
        cmd.Parameters.AddWithValue("@limit", limit);
        var results = new List<ConversationObservation>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            results.Add(MapObservation(reader));
        return results;
    }

    // ── Dashboard Stats (TRAY-8) ──────────────────────────────────────────────

    public async Task<SyncStats> GetSyncStatsAsync(CancellationToken ct = default)
    {
        var conn = await GetConnectionAsync(ct);

        // Per-surface: count + last activity
        var surfaceStats = new Dictionary<string, SurfaceStats>(StringComparer.Ordinal);
        await using var surfaceCmd = conn.CreateCommand();
        surfaceCmd.CommandText = """
            SELECT source, COUNT(*) AS cnt, MAX(updated_at) AS last_activity
            FROM conv_sessions
            WHERE excluded = 0
            GROUP BY source
            """;
        await using (var reader = await surfaceCmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                var src          = reader.GetString(0);
                var count        = reader.GetInt32(1);
                var lastRaw      = reader.IsDBNull(2) ? null : reader.GetString(2);
                var lastActivity = lastRaw is null ? (DateTime?)null
                    : DateTime.Parse(lastRaw, null, System.Globalization.DateTimeStyles.RoundtripKind);
                surfaceStats[src] = new SurfaceStats { Count = count, LastActivity = lastActivity };
            }
        }

        // Total message count
        await using var msgCmd = conn.CreateCommand();
        msgCmd.CommandText = "SELECT COUNT(*) FROM conv_messages";
        var totalMessages = Convert.ToInt32(await msgCmd.ExecuteScalarAsync(ct) ?? 0);

        // Oldest/newest conversation dates
        DateTime? oldest = null, newest = null;
        await using var datesCmd = conn.CreateCommand();
        datesCmd.CommandText = "SELECT MIN(created_at), MAX(created_at) FROM conv_sessions WHERE excluded = 0";
        await using (var reader = await datesCmd.ExecuteReaderAsync(ct))
        {
            if (await reader.ReadAsync(ct))
            {
                if (!reader.IsDBNull(0))
                    oldest = DateTime.Parse(reader.GetString(0), null, System.Globalization.DateTimeStyles.RoundtripKind);
                if (!reader.IsDBNull(1))
                    newest = DateTime.Parse(reader.GetString(1), null, System.Globalization.DateTimeStyles.RoundtripKind);
            }
        }

        // DB file size (best-effort)
        long dbSize = 0;
        try { dbSize = new FileInfo(_dbPath).Length; } catch { /* file may be locked or absent */ }

        var total = surfaceStats.Values.Sum(s => s.Count);
        surfaceStats.TryGetValue("ClaudeChat", out var claudeChat);
        surfaceStats.TryGetValue("ClaudeCode", out var claudeCode);
        surfaceStats.TryGetValue("Cowork",     out var cowork);
        surfaceStats.TryGetValue("ChatGpt",    out var chatGpt);
        surfaceStats.TryGetValue("Gemini",     out var gemini);

        return new SyncStats
        {
            ClaudeChat          = claudeChat ?? new SurfaceStats(),
            ClaudeCode          = claudeCode ?? new SurfaceStats(),
            Cowork              = cowork     ?? new SurfaceStats(),
            ChatGpt             = chatGpt    ?? new SurfaceStats(),
            Gemini              = gemini     ?? new SurfaceStats(),
            TotalConversations  = total,
            TotalMessages       = totalMessages,
            LocalDbSizeBytes    = dbSize,
            OldestConversation  = oldest,
            NewestConversation  = newest,
        };
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
        {
            await _connection.CloseAsync();
            await _connection.DisposeAsync();
        }
        _initLock.Dispose();
    }
}
