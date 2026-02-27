using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Ses.Local.Core.Enums;
using Ses.Local.Core.Interfaces;
using Ses.Local.Core.Models;
using Ses.Local.Core.Options;
using Microsoft.Extensions.Options;
using System.Security.Cryptography;

namespace Ses.Local.Workers.Workers;

/// <summary>
/// Hosts a minimal HTTP listener on localhost:37780 for the browser extension.
/// Receives conversation batches and stores them in ILocalDbService.
/// All traffic is localhost-only; no external network exposure.
/// </summary>
public sealed class BrowserExtensionListener : BackgroundService
{
    private readonly ILocalDbService _db;
    private readonly IAuthService _auth;
    private readonly ILogger<BrowserExtensionListener> _logger;
    private readonly SesLocalOptions _options;

    private const int Port = 37780;

    public BrowserExtensionListener(
        ILocalDbService db,
        IAuthService auth,
        ILogger<BrowserExtensionListener> logger,
        IOptions<SesLocalOptions> options)
    {
        _db      = db;
        _auth    = auth;
        _logger  = logger;
        _options = options.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{Port}/");

        try
        {
            listener.Start();
            _logger.LogInformation("BrowserExtensionListener started on http://localhost:{Port}/", Port);
        }
        catch (HttpListenerException ex)
        {
            _logger.LogWarning(ex, "Failed to start HTTP listener on port {Port} — extension sync unavailable", Port);
            return;
        }

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try
                {
                    ctx = await listener.GetContextAsync().WaitAsync(stoppingToken);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Listener accept error");
                    continue;
                }

                _ = HandleRequestAsync(ctx, stoppingToken);
            }
        }
        finally
        {
            listener.Stop();
            listener.Close();
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        var req  = ctx.Request;
        var resp = ctx.Response;

        try
        {
            // CORS for extension popup
            resp.Headers.Add("Access-Control-Allow-Origin", "chrome-extension://*");
            resp.Headers.Add("Access-Control-Allow-Headers", "Authorization, Content-Type");

            if (req.HttpMethod == "OPTIONS")
            {
                resp.StatusCode = 204;
                resp.Close();
                return;
            }

            // Only accept POST /api/sync/conversations
            if (req.HttpMethod != "POST" || req.Url?.AbsolutePath != "/api/sync/conversations")
            {
                resp.StatusCode = 404;
                resp.Close();
                return;
            }

            // Validate PAT
            var auth = req.Headers["Authorization"];
            if (!await ValidatePatAsync(auth, ct))
            {
                resp.StatusCode = 401;
                resp.Close();
                return;
            }

            // Parse body
            using var reader = new StreamReader(req.InputStream, Encoding.UTF8);
            var body    = await reader.ReadToEndAsync(ct);
            var payload = JsonSerializer.Deserialize(body, ExtensionPayloadJsonContext.Default.ExtensionSyncPayload);

            if (payload?.Conversations is null || payload.Conversations.Count == 0)
            {
                resp.StatusCode = 200;
                await WriteJsonAsync(resp, new { synced = 0 });
                return;
            }

            int synced = 0;
            foreach (var conv in payload.Conversations)
            {
                await ProcessConversationAsync(conv, ct);
                synced++;
            }

            _logger.LogDebug("Extension sync: {Count} conversations stored", synced);
            resp.StatusCode = 200;
            await WriteJsonAsync(resp, new { synced });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Extension listener: request handler error");
            resp.StatusCode = 500;
            resp.Close();
        }
    }

    private async Task<bool> ValidatePatAsync(string? authHeader, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(authHeader)) return false;
        const string prefix = "Bearer ";
        if (!authHeader.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;

        var token = authHeader[prefix.Length..].Trim();
        if (string.IsNullOrEmpty(token)) return false;

        // Validate against stored PAT from AuthService
        var storedPat = await _auth.GetPatAsync(ct);
        return storedPat is not null && storedPat == token;
    }

    private async Task ProcessConversationAsync(ExtensionConversation conv, CancellationToken ct)
    {
        var hash = ComputeHash(conv);

        var session = new ConversationSession
        {
            ExternalId  = conv.Uuid,
            Source      = ConversationSource.ClaudeChat,
            Title       = conv.Name,
            ContentHash = hash,
            UpdatedAt   = conv.UpdatedAt,
            CreatedAt   = conv.CreatedAt
        };
        await _db.UpsertSessionAsync(session, ct);

        if (conv.Messages?.Count > 0)
        {
            var messages = conv.Messages.Select(m => new ConversationMessage
            {
                SessionId = session.Id,
                Role      = m.Sender == "human" ? "user" : "assistant",
                Content   = m.Text,
                CreatedAt = m.CreatedAt
            });
            await _db.UpsertMessagesAsync(messages, ct);
        }
    }

    private static string ComputeHash(ExtensionConversation conv)
    {
        var key   = $"{conv.Uuid}:{conv.UpdatedAt:O}:{conv.Messages?.Count ?? 0}";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return Convert.ToHexString(bytes)[..16];
    }

    private static async Task WriteJsonAsync(HttpListenerResponse resp, object data)
    {
        resp.ContentType = "application/json";
        var json  = JsonSerializer.Serialize(data);
        var bytes = Encoding.UTF8.GetBytes(json);
        resp.ContentLength64 = bytes.Length;
        await resp.OutputStream.WriteAsync(bytes);
        resp.Close();
    }
}

// ── DTOs ──────────────────────────────────────────────────────────────────────

public sealed class ExtensionSyncPayload
{
    [JsonPropertyName("conversations")]
    public List<ExtensionConversation> Conversations { get; set; } = [];
}

public sealed class ExtensionConversation
{
    [JsonPropertyName("uuid")]       public string Uuid       { get; set; } = string.Empty;
    [JsonPropertyName("name")]       public string Name       { get; set; } = string.Empty;
    [JsonPropertyName("created_at")] public DateTime CreatedAt { get; set; }
    [JsonPropertyName("updated_at")] public DateTime UpdatedAt { get; set; }
    [JsonPropertyName("messages")]   public List<ExtensionMessage> Messages { get; set; } = [];
}

public sealed class ExtensionMessage
{
    [JsonPropertyName("uuid")]       public string Uuid      { get; set; } = string.Empty;
    [JsonPropertyName("sender")]     public string Sender    { get; set; } = string.Empty;
    [JsonPropertyName("text")]       public string Text      { get; set; } = string.Empty;
    [JsonPropertyName("created_at")] public DateTime CreatedAt { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ExtensionSyncPayload))]
internal partial class ExtensionPayloadJsonContext : JsonSerializerContext { }
