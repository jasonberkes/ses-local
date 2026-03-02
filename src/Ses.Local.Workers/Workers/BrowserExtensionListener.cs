using System.Net;
using System.Security.Cryptography;
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

namespace Ses.Local.Workers.Workers;

/// <summary>
/// Hosts a minimal HTTP listener on localhost:37780 for the browser extension
/// and OAuth loopback callback. All IPC (status, signout, shutdown) is handled
/// by the daemon's Kestrel listener on a Unix domain socket.
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
        _db       = db;
        _auth     = auth;
        _logger   = logger;
        _options  = options.Value;
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

            // RFC 8252 §7.3 loopback redirect — OAuth callback from identity server
            if (req.HttpMethod == "GET" && req.Url?.AbsolutePath == "/auth/callback")
            {
                await HandleAuthCallbackAsync(req, resp, ct);
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

    /// <summary>
    /// Handles the OAuth loopback redirect from the identity server.
    /// Per RFC 8252 §7.3, the identity server redirects to http://localhost:{port}/auth/callback
    /// with refresh and access tokens as query parameters.
    /// </summary>
    private async Task HandleAuthCallbackAsync(HttpListenerRequest req, HttpListenerResponse resp, CancellationToken ct)
    {
        try
        {
            var query = req.QueryString;
            var refresh = query["refresh"];
            var access  = query["access"];

            if (string.IsNullOrEmpty(refresh) || string.IsNullOrEmpty(access))
            {
                _logger.LogWarning("Auth callback missing required tokens");
                resp.StatusCode = 400;
                await WriteHtmlAsync(resp, "Authentication Failed",
                    "Missing required tokens. Please try signing in again.",
                    "https://identity.tm.supereasysoftware.com/api/v1/install/login?reauth=true");
                return;
            }

            await _auth.HandleAuthCallbackAsync(refresh, access, ct);
            _logger.LogInformation("OAuth loopback callback handled — tokens stored");

            resp.StatusCode = 200;
            await WriteHtmlAsync(resp, "Authentication Successful",
                "You are now signed in to ses-local. You can close this tab.",
                retryLink: null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to handle auth callback");
            resp.StatusCode = 500;
            await WriteHtmlAsync(resp, "Authentication Error",
                "Something went wrong. Please try again.",
                "https://identity.tm.supereasysoftware.com/api/v1/install/login?reauth=true");
        }
    }

    private static async Task WriteHtmlAsync(HttpListenerResponse resp, string title, string message, string? retryLink)
    {
        var encodedTitle   = WebUtility.HtmlEncode(title);
        var encodedMessage = WebUtility.HtmlEncode(message);
        var headingColor   = resp.StatusCode == 200 ? "#2e7d32" : "#c62828";
        var retryHtml      = retryLink is not null
            ? "<a href=\"" + WebUtility.HtmlEncode(retryLink) + "\" style=\"color:#1a73e8;text-decoration:none;\">Try again</a>"
            : "";

        var html = "<!DOCTYPE html><html><head><meta charset=\"utf-8\"/>"
            + "<title>" + encodedTitle + "</title>"
            + "<style>"
            + "body{font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',sans-serif;background:#f5f5f5;display:flex;align-items:center;justify-content:center;min-height:100vh;margin:0}"
            + ".card{background:#fff;border-radius:8px;box-shadow:0 2px 12px rgba(0,0,0,.08);padding:40px;max-width:400px;text-align:center}"
            + "h2{color:" + headingColor + ";margin-bottom:12px}"
            + "p{color:#555;margin-bottom:24px}"
            + "</style></head><body><div class=\"card\">"
            + "<h2>" + encodedTitle + "</h2>"
            + "<p>" + encodedMessage + "</p>"
            + retryHtml
            + "</div></body></html>";

        resp.ContentType = "text/html; charset=utf-8";
        var bytes = Encoding.UTF8.GetBytes(html);
        resp.ContentLength64 = bytes.Length;
        await resp.OutputStream.WriteAsync(bytes);
        resp.Close();
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

public sealed class DaemonStatusResponse
{
    [JsonPropertyName("authenticated")] public bool Authenticated { get; set; }
    [JsonPropertyName("needsReauth")]   public bool NeedsReauth   { get; set; }
    [JsonPropertyName("uptime")]        public string Uptime       { get; set; } = string.Empty;
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ExtensionSyncPayload))]
[JsonSerializable(typeof(DaemonStatusResponse))]
internal partial class ExtensionPayloadJsonContext : JsonSerializerContext { }
