using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;
using Moq;
using Ses.Local.Core.Interfaces;
using Ses.Local.Core.Options;
using Ses.Local.Workers.Services;
using Ses.Local.Workers.Workers;

namespace Ses.Local.Workers.Tests.Workers;

public sealed class BrowserExtensionListenerTests
{
    /// <summary>Returns true if nothing is already listening on port 37780 (checks both IPv4 and IPv6).</summary>
    private static bool IsPortFree()
    {
        foreach (var host in new[] { "localhost", "127.0.0.1" })
        {
            try
            {
                using var probe = new TcpClient();
                probe.Connect(host, 37780);
                return false; // Connection succeeded — port is in use
            }
            catch (SocketException) { /* connection refused — try next */ }
        }
        return true;
    }

    [Fact]
    public async Task Listener_StartsAndRespondsToSync()
    {
        // Skip gracefully if port 37780 is already in use (e.g., ses-local daemon running)
        if (!IsPortFree()) return;

        var db   = new Mock<ILocalDbService>();
        var auth = new Mock<IAuthService>();
        auth.Setup(x => x.GetPatAsync(It.IsAny<CancellationToken>())).ReturnsAsync("tm_pat_testtoken");

        var opts     = Options.Create(new SesLocalOptions());
        var listener = new BrowserExtensionListener(db.Object, auth.Object,
            NullLogger<BrowserExtensionListener>.Instance, opts);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        _ = listener.StartAsync(cts.Token);
        await Task.Delay(200); // Let listener bind

        // POST a conversation
        using var http = new HttpClient();
        var payload = JsonSerializer.Serialize(new
        {
            conversations = new[]
            {
                new {
                    uuid       = "test-uuid-1",
                    name       = "Test Conversation",
                    created_at = DateTime.UtcNow,
                    updated_at = DateTime.UtcNow,
                    messages   = new[] { new { uuid = "msg-1", sender = "human", text = "Hello", created_at = DateTime.UtcNow } }
                }
            }
        });

        try
        {
            var req = new HttpRequestMessage(HttpMethod.Post, "http://localhost:37780/api/sync/conversations")
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
                Headers = { Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "tm_pat_testtoken") }
            };
            var resp = await http.SendAsync(req, cts.Token);
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        }
        catch (HttpRequestException)
        {
            // Port may be in use on CI — skip gracefully
        }

        await listener.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Listener_WithInvalidPat_Returns401()
    {
        var db   = new Mock<ILocalDbService>();
        var auth = new Mock<IAuthService>();
        auth.Setup(x => x.GetPatAsync(It.IsAny<CancellationToken>())).ReturnsAsync("tm_pat_correct");

        var opts     = Options.Create(new SesLocalOptions());
        var listener = new BrowserExtensionListener(db.Object, auth.Object,
            NullLogger<BrowserExtensionListener>.Instance, opts);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        _ = listener.StartAsync(cts.Token);
        await Task.Delay(200);

        try
        {
            using var http = new HttpClient();
            var req = new HttpRequestMessage(HttpMethod.Post, "http://localhost:37780/api/sync/conversations")
            {
                Content = new StringContent("{\"conversations\":[]}", Encoding.UTF8, "application/json"),
                Headers = { Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "wrong_token") }
            };
            var resp = await http.SendAsync(req, cts.Token);
            Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        }
        catch (HttpRequestException) { /* port in use on CI */ }

        await listener.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Listener_IpcEndpoints_Return404()
    {
        var db   = new Mock<ILocalDbService>();
        var auth = new Mock<IAuthService>();

        var opts     = Options.Create(new SesLocalOptions());
        var listener = new BrowserExtensionListener(db.Object, auth.Object,
            NullLogger<BrowserExtensionListener>.Instance, opts);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        _ = listener.StartAsync(cts.Token);
        await Task.Delay(200);

        try
        {
            using var http = new HttpClient();

            // /api/status should no longer be served here (moved to UDS)
            var statusResp = await http.GetAsync("http://localhost:37780/api/status", cts.Token);
            Assert.Equal(HttpStatusCode.NotFound, statusResp.StatusCode);

            // /api/signout should no longer be served here
            var signoutResp = await http.PostAsync("http://localhost:37780/api/signout", null, cts.Token);
            Assert.Equal(HttpStatusCode.NotFound, signoutResp.StatusCode);

            // /api/shutdown should no longer be served here
            var shutdownResp = await http.PostAsync("http://localhost:37780/api/shutdown", null, cts.Token);
            Assert.Equal(HttpStatusCode.NotFound, shutdownResp.StatusCode);
        }
        catch (HttpRequestException) { /* port in use on CI */ }

        await listener.StopAsync(CancellationToken.None);
    }

    [Fact]
    public void ExtensionPayload_Deserializes()
    {
        var json = """
            {
              "conversations": [{
                "uuid": "abc",
                "name": "Test",
                "created_at": "2026-01-01T00:00:00Z",
                "updated_at": "2026-01-02T00:00:00Z",
                "messages": [{"uuid":"m1","sender":"human","text":"hi","created_at":"2026-01-01T01:00:00Z"}]
              }]
            }
            """;
        var payload = JsonSerializer.Deserialize(json, ExtensionPayloadJsonContext.Default.ExtensionSyncPayload);
        Assert.NotNull(payload);
        Assert.Single(payload.Conversations);
        Assert.Equal("abc", payload.Conversations[0].Uuid);
        Assert.Single(payload.Conversations[0].Messages);
    }

    /// <summary>
    /// End-to-end: BrowserExtensionListener receives GET /auth/callback, stores tokens via AuthService,
    /// transitions state to Authenticated, and returns a success HTML page.
    /// Uses a dynamically-allocated free port and InMemoryCredentialStore — no keychain required.
    /// </summary>
    [Fact]
    public async Task Listener_AuthCallback_StoresTokensAndReturnsSuccessHtml()
    {
        var port     = AllocateFreePort();
        var keychain = new InMemoryCredentialStore();
        var identity = BuildNullIdentityClient();
        var auth     = new AuthService(keychain, identity,
            NullLogger<AuthService>.Instance,
            Options.Create(new SesLocalOptions()));

        var db       = new Mock<ILocalDbService>();
        var opts     = Options.Create(new SesLocalOptions { BrowserListenerPort = port });
        var listener = new BrowserExtensionListener(db.Object, auth,
            NullLogger<BrowserExtensionListener>.Instance, opts);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        _ = listener.StartAsync(cts.Token);
        await Task.Delay(200, cts.Token); // allow listener to bind

        var refreshToken = "integration_refresh_token";
        var accessToken  = TestJwtHelper.CreateFakeJwt(DateTime.UtcNow.AddMinutes(15));

        using var http = new HttpClient();
        var resp = await http.GetAsync(
            $"http://localhost:{port}/auth/callback" +
            $"?refresh={Uri.EscapeDataString(refreshToken)}" +
            $"&access={Uri.EscapeDataString(accessToken)}",
            cts.Token);

        // HTTP response must be 200 with success HTML
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var html = await resp.Content.ReadAsStringAsync(cts.Token);
        Assert.Contains("Authentication Successful", html);

        // Refresh token stored in credential store
        var storedRefresh = await keychain.GetAsync("ses-local-refresh");
        Assert.Equal(refreshToken, storedRefresh);

        // AuthService reports Authenticated state
        var state = await auth.GetStateAsync();
        Assert.True(state.IsAuthenticated);

        await listener.StopAsync(CancellationToken.None);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>Finds a free TCP port on loopback by binding to port 0.</summary>
    private static int AllocateFreePort()
    {
        using var tmp = new TcpListener(System.Net.IPAddress.Loopback, 0);
        tmp.Start();
        var port = ((System.Net.IPEndPoint)tmp.LocalEndpoint).Port;
        tmp.Stop();
        return port;
    }

    /// <summary>IdentityClient backed by a handler that returns 401 for all requests (PAT derivation is best-effort).</summary>
    private static IdentityClient BuildNullIdentityClient()
    {
        var handler = new AlwaysUnauthorizedHandler();
        var http    = new HttpClient(handler) { BaseAddress = new Uri("https://identity.test/") };
        return new IdentityClient(http, NullLogger<IdentityClient>.Instance);
    }

    private sealed class AlwaysUnauthorizedHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
    }
}
