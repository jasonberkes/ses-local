using Ses.Local.Core.Enums;
using Ses.Local.Core.Models;
using Ses.Local.Integration.Tests.Fixtures;
using Xunit;

namespace Ses.Local.Integration.Tests;

/// <summary>
/// Integration tests for GetActiveClaudeCodeSessionsAsync.
/// Uses a real temp SQLite database.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ActiveSessionsIntegrationTests : IAsyncDisposable
{
    private readonly TestDbFixture _fixture = new();

    // ── Returns recent sessions ───────────────────────────────────────────────

    [Fact]
    public async Task GetActiveSessions_ReturnsRecentCcSessions()
    {
        await _fixture.Db.UpsertSessionAsync(MakeSession("cc-1", "myproject/abc12345"));
        await _fixture.Db.UpsertSessionAsync(MakeSession("cc-2", "otherproject/def67890"));

        var since    = DateTime.UtcNow.AddHours(-25);
        var sessions = await _fixture.Db.GetActiveClaudeCodeSessionsAsync(since);

        Assert.Equal(2, sessions.Count);
        Assert.Contains(sessions, s => s.ProjectName == "myproject");
        Assert.Contains(sessions, s => s.ProjectName == "otherproject");
    }

    [Fact]
    public async Task GetActiveSessions_FiltersOutOldSessions()
    {
        var oldSession = MakeSession("cc-old", "oldproject/aabbccdd");
        oldSession.UpdatedAt = DateTime.UtcNow.AddHours(-25);
        oldSession.CreatedAt = oldSession.UpdatedAt;
        await _fixture.Db.UpsertSessionAsync(oldSession);

        var recentSession = MakeSession("cc-recent", "recentproject/11223344");
        await _fixture.Db.UpsertSessionAsync(recentSession);

        var since    = DateTime.UtcNow.AddHours(-24);
        var sessions = await _fixture.Db.GetActiveClaudeCodeSessionsAsync(since);

        Assert.Single(sessions);
        Assert.Equal("recentproject", sessions[0].ProjectName);
    }

    [Fact]
    public async Task GetActiveSessions_GroupsByProjectName()
    {
        // Two sessions for the same project
        await _fixture.Db.UpsertSessionAsync(MakeSession("cc-a1", "myproject/aabb1111"));
        await _fixture.Db.UpsertSessionAsync(MakeSession("cc-a2", "myproject/ccdd2222"));

        var since    = DateTime.UtcNow.AddHours(-25);
        var sessions = await _fixture.Db.GetActiveClaudeCodeSessionsAsync(since);

        Assert.Single(sessions);
        Assert.Equal("myproject", sessions[0].ProjectName);
    }

    [Fact]
    public async Task GetActiveSessions_ExcludesNonClaudeCodeSessions()
    {
        await _fixture.Db.UpsertSessionAsync(MakeSession("cc-1", "ccproject/abc12345"));
        await _fixture.Db.UpsertSessionAsync(MakeSession("chat-1", "chatproject/xyz99999", ConversationSource.ClaudeChat));

        var since    = DateTime.UtcNow.AddHours(-25);
        var sessions = await _fixture.Db.GetActiveClaudeCodeSessionsAsync(since);

        Assert.Single(sessions);
        Assert.Equal("ccproject", sessions[0].ProjectName);
    }

    [Fact]
    public async Task GetActiveSessions_HandlesSubagentTitles()
    {
        await _fixture.Db.UpsertSessionAsync(MakeSession("cc-sub", "[subagent] myproject/abc12345"));

        var since    = DateTime.UtcNow.AddHours(-25);
        var sessions = await _fixture.Db.GetActiveClaudeCodeSessionsAsync(since);

        Assert.Single(sessions);
        Assert.Equal("myproject", sessions[0].ProjectName);
    }

    [Fact]
    public async Task GetActiveSessions_OrdersByMostRecentFirst()
    {
        var s1 = MakeSession("cc-s1", "alpha-project/aabb1111");
        s1.UpdatedAt = DateTime.UtcNow.AddMinutes(-30);
        s1.CreatedAt = s1.UpdatedAt;
        await _fixture.Db.UpsertSessionAsync(s1);

        var s2 = MakeSession("cc-s2", "beta-project/ccdd2222");
        s2.UpdatedAt = DateTime.UtcNow.AddMinutes(-5);
        s2.CreatedAt = s2.UpdatedAt;
        await _fixture.Db.UpsertSessionAsync(s2);

        var since    = DateTime.UtcNow.AddHours(-25);
        var sessions = await _fixture.Db.GetActiveClaudeCodeSessionsAsync(since);

        Assert.Equal(2, sessions.Count);
        Assert.Equal("beta-project", sessions[0].ProjectName);
        Assert.Equal("alpha-project", sessions[1].ProjectName);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static ConversationSession MakeSession(
        string externalId,
        string title,
        ConversationSource source = ConversationSource.ClaudeCode) =>
        new()
        {
            Source     = source,
            ExternalId = externalId,
            Title      = title,
            CreatedAt  = DateTime.UtcNow,
            UpdatedAt  = DateTime.UtcNow,
        };

    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();
}
