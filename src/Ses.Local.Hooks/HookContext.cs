using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;

namespace Ses.Local.Hooks;

/// <summary>
/// Shared context available to all hook handlers.
/// Reads PAT from OS keychain, provides HTTP client for ses-local API,
/// and direct SQLite fallback.
/// </summary>
internal sealed class HookContext : IDisposable
{
    private static readonly string DbPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".ses", "local.db");

    private const string SesLocalBaseUrl = "http://localhost:37780";
    private const int HttpTimeoutMs = 3000; // fast timeout — hooks must not block CC

    public HttpClient? Http { get; private set; }
    public bool SesLocalAvailable { get; private set; }
    public string? Pat { get; private set; }

    private HookContext() { }

    public static async Task<HookContext> CreateAsync()
    {
        var ctx = new HookContext();
        ctx.Pat = await GetPatAsync();
        ctx.Http = BuildHttpClient(ctx.Pat);
        ctx.SesLocalAvailable = await CheckSesLocalAsync(ctx.Http);
        return ctx;
    }

    // ── PAT retrieval ─────────────────────────────────────────────────────────

    private static async Task<string?> GetPatAsync()
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                return await GetMacPatAsync();

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return GetWindowsPat();
        }
        catch { }
        return null;
    }

    private static async Task<string?> GetMacPatAsync()
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "/usr/bin/security",
            Arguments = "find-generic-password -w -s \"SuperEasySoftware.TaskMaster\" -a \"ses-local-pat\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        using var proc = System.Diagnostics.Process.Start(psi)!;
        if (!proc.WaitForExit(3000)) { proc.Kill(); return null; }
        var result = (await proc.StandardOutput.ReadToEndAsync()).Trim();
        return string.IsNullOrEmpty(result) ? null : result;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static string? GetWindowsPat()
    {
        // Windows credential vault access via P/Invoke or WinRT not available in AOT without extra packages.
        // Return null; PAT-less operation falls back to unauthenticated HTTP.
        return null;
    }

    // ── HTTP client ───────────────────────────────────────────────────────────

    private static HttpClient BuildHttpClient(string? pat)
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMilliseconds(HttpTimeoutMs) };
        client.BaseAddress = new Uri(SesLocalBaseUrl);
        if (!string.IsNullOrEmpty(pat))
            client.DefaultRequestHeaders.Add("X-PAT", pat);
        return client;
    }

    private static async Task<bool> CheckSesLocalAsync(HttpClient http)
    {
        try
        {
            var resp = await http.GetAsync("/health");
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    // ── ses-local API calls ───────────────────────────────────────────────────

    public async Task<List<SearchResult>> SearchMemoryAsync(string query, int limit = 10)
    {
        if (!SesLocalAvailable || Http is null) return await SearchSqliteAsync(query, limit);
        try
        {
            var body = JsonSerializer.Serialize(new SearchRequest { Query = query, Limit = limit }, HookJsonContext.Default.SearchRequest);
            var resp = await Http.PostAsync("/api/hooks/search",
                new StringContent(body, System.Text.Encoding.UTF8, "application/json"));
            if (!resp.IsSuccessStatusCode) return [];
            var json = await resp.Content.ReadAsStringAsync();
            return JsonSerializer.Deserialize(json, HookJsonContext.Default.SearchResponse)?.Results ?? [];
        }
        catch { return await SearchSqliteAsync(query, limit); }
    }

    public async Task SaveObservationAsync(string sessionId, string content, string type, string? toolName = null, double importance = 0.5)
    {
        if (SesLocalAvailable && Http is not null)
        {
            try
            {
                var body = JsonSerializer.Serialize(
                    new ObservationRequest { SessionId = sessionId, Type = type, Content = content, ToolName = toolName },
                    HookJsonContext.Default.ObservationRequest);
                await Http.PostAsync("/api/hooks/observation",
                    new StringContent(body, System.Text.Encoding.UTF8, "application/json"));
                return;
            }
            catch { }
        }
        // Fallback: write directly to SQLite
        await SaveObservationSqliteAsync(sessionId, content, importance);
    }

    public async Task SaveSummaryAsync(string sessionId, string summary)
    {
        if (SesLocalAvailable && Http is not null)
        {
            try
            {
                var body = JsonSerializer.Serialize(
                    new SummaryRequest { SessionId = sessionId, Summary = summary },
                    HookJsonContext.Default.SummaryRequest);
                await Http.PostAsync("/api/hooks/summary",
                    new StringContent(body, System.Text.Encoding.UTF8, "application/json"));
                return;
            }
            catch { }
        }
        await SaveSummarySqliteAsync(sessionId, summary);
    }

    // ── SQLite fallbacks ──────────────────────────────────────────────────────

    private static async Task<List<SearchResult>> SearchSqliteAsync(string query, int limit)
    {
        if (!File.Exists(DbPath)) return [];
        try
        {
            using var conn = new SqliteConnection($"Data Source={DbPath};Mode=ReadOnly;");
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT m.content
                FROM conv_messages_fts fts
                JOIN conv_messages m ON m.rowid = fts.rowid
                WHERE conv_messages_fts MATCH @q
                ORDER BY rank LIMIT @limit
                """;
            cmd.Parameters.AddWithValue("@q", query);
            cmd.Parameters.AddWithValue("@limit", limit);
            var results = new List<SearchResult>();
            using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
                results.Add(new SearchResult { Content = r.GetString(0), Score = 1.0 });
            return results;
        }
        catch { return []; }
    }

    private static async Task SaveObservationSqliteAsync(string sessionId, string content, double importance)
    {
        if (!File.Exists(DbPath)) return;
        try
        {
            // Ensure session exists first
            long? dbSessionId = await GetOrCreateSessionIdAsync(sessionId);
            if (dbSessionId is null) return;

            using var conn = new SqliteConnection($"Data Source={DbPath}");
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO memory_observations (session_id, content, importance_score, captured_at, synced_to_cloud)
                VALUES (@sid, @content, @importance, @now, 0)
                """;
            cmd.Parameters.AddWithValue("@sid", dbSessionId);
            cmd.Parameters.AddWithValue("@content", content);
            cmd.Parameters.AddWithValue("@importance", importance);
            cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("O"));
            await cmd.ExecuteNonQueryAsync();
        }
        catch { }
    }

    private static async Task SaveSummarySqliteAsync(string sessionId, string summary)
    {
        if (!File.Exists(DbPath)) return;
        try
        {
            long? dbSessionId = await GetOrCreateSessionIdAsync(sessionId);
            if (dbSessionId is null) return;

            using var conn = new SqliteConnection($"Data Source={DbPath}");
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO memory_summaries (session_id, summary, model, created_at)
                VALUES (@sid, @summary, 'hooks', @now)
                """;
            cmd.Parameters.AddWithValue("@sid", dbSessionId);
            cmd.Parameters.AddWithValue("@summary", summary);
            cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("O"));
            await cmd.ExecuteNonQueryAsync();
        }
        catch { }
    }

    private static async Task<long?> GetOrCreateSessionIdAsync(string externalId)
    {
        try
        {
            using var conn = new SqliteConnection($"Data Source={DbPath}");
            await conn.OpenAsync();

            using var selectCmd = conn.CreateCommand();
            selectCmd.CommandText = "SELECT id FROM conv_sessions WHERE external_id = @eid";
            selectCmd.Parameters.AddWithValue("@eid", externalId);
            var existing = await selectCmd.ExecuteScalarAsync();
            if (existing is long id) return id;

            using var insertCmd = conn.CreateCommand();
            insertCmd.CommandText = """
                INSERT INTO conv_sessions (source, external_id, title, created_at, updated_at)
                VALUES ('ClaudeCode', @eid, @eid, @now, @now)
                ON CONFLICT(source, external_id) DO NOTHING
                """;
            insertCmd.Parameters.AddWithValue("@eid", externalId);
            insertCmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("O"));
            await insertCmd.ExecuteNonQueryAsync();

            var idCmd = conn.CreateCommand();
            idCmd.CommandText = "SELECT id FROM conv_sessions WHERE external_id = @eid";
            idCmd.Parameters.AddWithValue("@eid", externalId);
            var result = await idCmd.ExecuteScalarAsync();
            return result is long newId ? newId : null;
        }
        catch { return null; }
    }

    public void Dispose() => Http?.Dispose();
}

// ── DTOs ──────────────────────────────────────────────────────────────────────

internal sealed class SearchResult
{
    [JsonPropertyName("content")] public string Content { get; set; } = string.Empty;
    [JsonPropertyName("score")]   public double Score   { get; set; }
}

internal sealed class SearchResponse
{
    [JsonPropertyName("results")] public List<SearchResult> Results { get; set; } = [];
}

internal sealed class SearchRequest
{
    [JsonPropertyName("query")] public string Query { get; set; } = string.Empty;
    [JsonPropertyName("limit")] public int Limit { get; set; }
}

internal sealed class ObservationRequest
{
    [JsonPropertyName("session_id")] public string SessionId { get; set; } = string.Empty;
    [JsonPropertyName("type")]       public string Type      { get; set; } = string.Empty;
    [JsonPropertyName("content")]    public string Content   { get; set; } = string.Empty;
    [JsonPropertyName("tool_name")]  public string? ToolName { get; set; }
}

internal sealed class SummaryRequest
{
    [JsonPropertyName("session_id")] public string SessionId { get; set; } = string.Empty;
    [JsonPropertyName("summary")]    public string Summary   { get; set; } = string.Empty;
}

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(SearchRequest))]
[JsonSerializable(typeof(SearchResponse))]
[JsonSerializable(typeof(ObservationRequest))]
[JsonSerializable(typeof(SummaryRequest))]
[JsonSerializable(typeof(Dictionary<string, object>))]
[JsonSerializable(typeof(Dictionary<string, string>))]
internal partial class HookJsonContext : JsonSerializerContext { }
