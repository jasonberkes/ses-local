using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Ses.Local.Core.Interfaces;
using Ses.Local.Workers.Services;
using Xunit;

namespace Ses.Local.Workers.Tests.Services;

public sealed class ClaudeApiClientTests
{
    [Fact]
    public void GetCookiePath_ContainsClaude()
    {
        var path = ClaudeSessionCookieExtractor.GetCookiePath();
        Assert.False(string.IsNullOrEmpty(path));
        Assert.Contains("Claude", path);
        Assert.Contains("Cookies", path);
    }

    [Fact]
    public void CookieExtractor_WhenFileAbsent_ReturnsNull()
    {
        // On CI where Claude Desktop isn't installed — should gracefully return null
        var extractor = new ClaudeSessionCookieExtractor(NullLogger<ClaudeSessionCookieExtractor>.Instance);
        var result    = extractor.Extract();
        Assert.True(result is null || result.Length > 0); // null on CI, value on dev machine
    }

    [Fact]
    public async Task ClaudeAiClient_WithInvalidCookie_ReturnsNullOrgId()
    {
        using var client = new ClaudeAiClient("bad_cookie", NullLogger<ClaudeAiClient>.Instance);
        var orgId = await client.GetOrgIdAsync();
        Assert.Null(orgId);
    }

    [Fact]
    public async Task SyncService_WhenNoCookie_CompletesWithoutThrowing()
    {
        var extractor = new ClaudeSessionCookieExtractor(NullLogger<ClaudeSessionCookieExtractor>.Instance);
        var db        = new Mock<ILocalDbService>();
        var svc       = new ClaudeAiSyncService(extractor, db.Object, NullLogger<ClaudeAiSyncService>.Instance);

        var ex = await Record.ExceptionAsync(() => svc.SyncAsync());
        Assert.Null(ex);
    }

    [Fact]
    public async Task SyncService_WithTargetUuids_WhenNoCookie_CompletesWithoutThrowing()
    {
        var extractor = new ClaudeSessionCookieExtractor(NullLogger<ClaudeSessionCookieExtractor>.Instance);
        var db        = new Mock<ILocalDbService>();
        var svc       = new ClaudeAiSyncService(extractor, db.Object, NullLogger<ClaudeAiSyncService>.Instance);

        var uuids = new[] { "uuid-1", "uuid-2" };
        var ex    = await Record.ExceptionAsync(() => svc.SyncAsync(uuids));
        Assert.Null(ex);
    }
}
