using Microsoft.Extensions.Logging;
using Ses.Local.Core.Interfaces;

namespace Ses.Local.Workers.Services;

/// <summary>
/// Orchestrates embedding generation and cosine similarity search over session summaries.
/// Uses brute-force search over stored embeddings — adequate for the expected scale
/// (hundreds to low thousands of sessions).
/// </summary>
public sealed partial class VectorSearchService : IVectorSearchService
{
    private readonly ILocalDbService _db;
    private readonly ILocalEmbeddingService _embedding;
    private readonly ILogger<VectorSearchService> _logger;

    public VectorSearchService(
        ILocalDbService db,
        ILocalEmbeddingService embedding,
        ILogger<VectorSearchService> logger)
    {
        _db = db;
        _embedding = embedding;
        _logger = logger;
    }

    public async Task IndexSessionAsync(long sessionId, CancellationToken ct = default)
    {
        var summary = await _db.GetSessionSummaryAsync(sessionId, ct);
        if (summary is null)
        {
            LogNoSummary(_logger, sessionId);
            return;
        }

        // Build embedding text from narrative + concepts + category
        var embeddingText = BuildEmbeddingText(summary.Narrative, summary.Concepts, summary.Category);
        var vector = await _embedding.EmbedAsync(embeddingText, ct);

        await _db.UpsertEmbeddingAsync(sessionId, vector, ct);
        LogSessionIndexed(_logger, sessionId);
    }

    public async Task<IReadOnlyList<VectorSearchResult>> SearchAsync(string query, int limit = 10,
        CancellationToken ct = default)
    {
        var queryVector = await _embedding.EmbedAsync(query, ct);
        var allEmbeddings = await _db.GetAllEmbeddingsAsync(ct);

        if (allEmbeddings.Count == 0)
            return [];

        // Brute-force cosine distance ranking
        var scored = new List<VectorSearchResult>(allEmbeddings.Count);
        foreach (var (sessionId, storedVector) in allEmbeddings)
        {
            var distance = CosineDistance(queryVector, storedVector);
            scored.Add(new VectorSearchResult(sessionId, distance));
        }

        scored.Sort((a, b) => a.Distance.CompareTo(b.Distance));
        return scored.Take(limit).ToList();
    }

    internal static string BuildEmbeddingText(string narrative, string? concepts, string? category)
    {
        var parts = new List<string>(3) { narrative };
        if (!string.IsNullOrWhiteSpace(concepts))
            parts.Add(concepts);
        if (!string.IsNullOrWhiteSpace(category) && category != "unknown")
            parts.Add(category);
        return string.Join(" ", parts);
    }

    internal static float CosineDistance(float[] a, float[] b)
    {
        // Vectors are L2-normalized, so cosine similarity = dot product
        // Distance = 1 - similarity
        float dot = 0;
        for (int i = 0; i < a.Length && i < b.Length; i++)
            dot += a[i] * b[i];
        return 1f - dot;
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Session {SessionId} has no summary; skipping vector indexing")]
    private static partial void LogNoSummary(ILogger logger, long sessionId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Session {SessionId} indexed for vector search")]
    private static partial void LogSessionIndexed(ILogger logger, long sessionId);
}
