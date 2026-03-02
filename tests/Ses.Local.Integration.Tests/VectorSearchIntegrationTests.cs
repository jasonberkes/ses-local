using Ses.Local.Core.Enums;
using Ses.Local.Core.Models;
using Ses.Local.Integration.Tests.Fixtures;
using Xunit;

namespace Ses.Local.Integration.Tests;

/// <summary>
/// Integration tests for vector embedding storage and retrieval in SQLite.
/// Uses real SQLite database — no mocks.
/// </summary>
[Trait("Category", "Integration")]
public sealed class VectorSearchIntegrationTests : IAsyncDisposable
{
    private readonly TestDbFixture _fixture = new();

    // ── Schema migration ──────────────────────────────────────────────────────

    [Fact]
    public async Task Migration5_CreatesConvEmbeddingsTable()
    {
        // Trigger schema creation
        await _fixture.Db.GetSessionsWithoutEmbeddingAsync(1);

        // Table should exist — test by running a query against it
        var results = await _fixture.Db.GetAllEmbeddingsAsync();
        Assert.Empty(results);
    }

    // ── Upsert + retrieval ────────────────────────────────────────────────────

    [Fact]
    public async Task UpsertEmbedding_StoresAndRetrievesVector()
    {
        var session = await CreateTestSessionAsync();
        var vector = CreateTestVector(1.0f);

        await _fixture.Db.UpsertEmbeddingAsync(session.Id, vector);

        var all = await _fixture.Db.GetAllEmbeddingsAsync();
        Assert.Single(all);
        Assert.Equal(session.Id, all[0].SessionId);
        Assert.Equal(384, all[0].Embedding.Length);

        // Verify values are preserved
        for (int i = 0; i < 384; i++)
            Assert.Equal(vector[i], all[0].Embedding[i], precision: 5);
    }

    [Fact]
    public async Task UpsertEmbedding_ReplacesOnConflict()
    {
        var session = await CreateTestSessionAsync();
        var vector1 = CreateTestVector(1.0f);
        var vector2 = CreateTestVector(2.0f);

        await _fixture.Db.UpsertEmbeddingAsync(session.Id, vector1);
        await _fixture.Db.UpsertEmbeddingAsync(session.Id, vector2);

        var all = await _fixture.Db.GetAllEmbeddingsAsync();
        Assert.Single(all);
        Assert.Equal(vector2[0], all[0].Embedding[0], precision: 5);
    }

    [Fact]
    public async Task GetAllEmbeddings_ReturnsMultipleSessions()
    {
        var s1 = await CreateTestSessionAsync("session-1");
        var s2 = await CreateTestSessionAsync("session-2");
        var s3 = await CreateTestSessionAsync("session-3");

        await _fixture.Db.UpsertEmbeddingAsync(s1.Id, CreateTestVector(1.0f));
        await _fixture.Db.UpsertEmbeddingAsync(s2.Id, CreateTestVector(2.0f));
        await _fixture.Db.UpsertEmbeddingAsync(s3.Id, CreateTestVector(3.0f));

        var all = await _fixture.Db.GetAllEmbeddingsAsync();
        Assert.Equal(3, all.Count);
    }

    // ── GetSessionsWithoutEmbedding ───────────────────────────────────────────

    [Fact]
    public async Task GetSessionsWithoutEmbedding_ReturnsSessionsWithSummaryButNoEmbedding()
    {
        var session = await CreateTestSessionAsync();
        var summary = new SessionSummary
        {
            SessionId = session.Id,
            Category = "feature",
            Narrative = "Test narrative",
            CompressionLayer = 1,
            CreatedAt = DateTime.UtcNow
        };
        await _fixture.Db.UpsertSessionSummaryAsync(summary);

        var pending = await _fixture.Db.GetSessionsWithoutEmbeddingAsync();
        Assert.Contains(session.Id, pending);
    }

    [Fact]
    public async Task GetSessionsWithoutEmbedding_ExcludesSessionsWithEmbedding()
    {
        var session = await CreateTestSessionAsync();
        var summary = new SessionSummary
        {
            SessionId = session.Id,
            Category = "feature",
            Narrative = "Test narrative",
            CompressionLayer = 1,
            CreatedAt = DateTime.UtcNow
        };
        await _fixture.Db.UpsertSessionSummaryAsync(summary);
        await _fixture.Db.UpsertEmbeddingAsync(session.Id, CreateTestVector(1.0f));

        var pending = await _fixture.Db.GetSessionsWithoutEmbeddingAsync();
        Assert.DoesNotContain(session.Id, pending);
    }

    // ── Float ↔ BLOB round-trip ───────────────────────────────────────────────

    [Fact]
    public async Task FloatBlobRoundTrip_PreservesSpecialValues()
    {
        var session = await CreateTestSessionAsync();
        var vector = new float[384];
        vector[0] = 0f;
        vector[1] = -1f;
        vector[2] = float.Epsilon;
        vector[3] = 0.123456789f;

        await _fixture.Db.UpsertEmbeddingAsync(session.Id, vector);

        var all = await _fixture.Db.GetAllEmbeddingsAsync();
        Assert.Equal(0f, all[0].Embedding[0]);
        Assert.Equal(-1f, all[0].Embedding[1]);
        Assert.Equal(float.Epsilon, all[0].Embedding[2]);
        Assert.Equal(0.123456789f, all[0].Embedding[3], precision: 6);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task<ConversationSession> CreateTestSessionAsync(string? externalId = null)
    {
        var session = new ConversationSession
        {
            Source = ConversationSource.ClaudeCode,
            ExternalId = externalId ?? Guid.NewGuid().ToString("N"),
            Title = "test-session",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        await _fixture.Db.UpsertSessionAsync(session);
        return session;
    }

    private static float[] CreateTestVector(float seed)
    {
        var vector = new float[384];
        for (int i = 0; i < 384; i++)
            vector[i] = seed + i * 0.001f;
        return vector;
    }

    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();
}
