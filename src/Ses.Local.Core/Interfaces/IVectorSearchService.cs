namespace Ses.Local.Core.Interfaces;

/// <summary>
/// Orchestrates embedding generation and vector search over session summaries.
/// Combines ONNX embeddings with brute-force cosine similarity search.
/// </summary>
public interface IVectorSearchService
{
    /// <summary>Embeds the summary for a session and stores it in the embeddings table.</summary>
    Task IndexSessionAsync(long sessionId, CancellationToken ct = default);

    /// <summary>
    /// Searches session summaries by semantic similarity to the query text.
    /// Returns ranked session IDs with cosine distance scores (lower = more similar).
    /// </summary>
    Task<IReadOnlyList<VectorSearchResult>> SearchAsync(string query, int limit = 10, CancellationToken ct = default);
}

/// <summary>A single vector search result with session ID and distance score.</summary>
public sealed record VectorSearchResult(long SessionId, float Distance);
