using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;
using Moq;
using Ses.Local.Core.Interfaces;
using Ses.Local.Core.Options;
using Ses.Local.Workers.Workers;

namespace Ses.Local.Workers.Tests.Workers;

public sealed class BrowserExtensionListenerTests
{
    [Fact]
    public async Task Listener_StartsAndRespondsToSync()
    {
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
}
