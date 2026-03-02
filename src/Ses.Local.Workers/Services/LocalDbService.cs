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
            SELECT id, source, external_id, title, created_at, updated_at, synced_at, content_hash
            FROM conv_sessions
            WHERE synced_at IS NULL OR updated_at > synced_at
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
            WHERE conv_messages_fts MATCH @query
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
            WHERE conv_observations_fts MATCH @query
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
        ContentHash = r.IsDBNull(7) ? null : r.GetString(7)
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
