using Ses.Local.Workers.Services;
using Xunit;

namespace Ses.Local.Workers.Tests.Services;

/// <summary>
/// Unit tests for <see cref="VectorSearchService"/> pure logic methods.
/// No ONNX model, no DB — tests CosineDistance and BuildEmbeddingText only.
/// </summary>
public sealed class VectorSearchServiceTests
{
    // ── CosineDistance ─────────────────────────────────────────────────────────

    [Fact]
    public void CosineDistance_IdenticalNormalizedVectors_ReturnsZero()
    {
        var v = Normalize([1f, 0f, 0f]);
        var distance = VectorSearchService.CosineDistance(v, v);
        Assert.True(distance < 1e-5f, $"Expected ~0 but got {distance}");
    }

    [Fact]
    public void CosineDistance_OrthogonalVectors_ReturnsOne()
    {
        var a = Normalize([1f, 0f, 0f]);
        var b = Normalize([0f, 1f, 0f]);
        var distance = VectorSearchService.CosineDistance(a, b);
        Assert.True(Math.Abs(distance - 1f) < 1e-5f, $"Expected ~1 but got {distance}");
    }

    [Fact]
    public void CosineDistance_OppositeVectors_ReturnsTwo()
    {
        var a = Normalize([1f, 0f, 0f]);
        var b = Normalize([-1f, 0f, 0f]);
        var distance = VectorSearchService.CosineDistance(a, b);
        Assert.True(Math.Abs(distance - 2f) < 1e-5f, $"Expected ~2 but got {distance}");
    }

    [Fact]
    public void CosineDistance_SimilarVectors_ReturnSmallValue()
    {
        var a = Normalize([1f, 2f, 3f]);
        var b = Normalize([1f, 2f, 3.1f]);
        var distance = VectorSearchService.CosineDistance(a, b);
        Assert.True(distance < 0.01f, $"Expected small distance but got {distance}");
    }

    [Fact]
    public void CosineDistance_DissimilarVectors_ReturnLargerValue()
    {
        var a = Normalize([1f, 0f, 0f]);
        var b = Normalize([0f, 0f, 1f]);
        var distance = VectorSearchService.CosineDistance(a, b);
        Assert.True(distance > 0.9f, $"Expected large distance but got {distance}");
    }

    // ── BuildEmbeddingText ────────────────────────────────────────────────────

    [Fact]
    public void BuildEmbeddingText_NarrativeOnly()
    {
        var result = VectorSearchService.BuildEmbeddingText("Fixed auth bug", null, null);
        Assert.Equal("Fixed auth bug", result);
    }

    [Fact]
    public void BuildEmbeddingText_WithConceptsAndCategory()
    {
        var result = VectorSearchService.BuildEmbeddingText(
            "Implemented JWT authentication",
            "AuthService, TokenValidator",
            "feature");
        Assert.Equal("Implemented JWT authentication AuthService, TokenValidator feature", result);
    }

    [Fact]
    public void BuildEmbeddingText_SkipsUnknownCategory()
    {
        var result = VectorSearchService.BuildEmbeddingText("Some work", "Foo", "unknown");
        Assert.Equal("Some work Foo", result);
    }

    [Fact]
    public void BuildEmbeddingText_SkipsEmptyConcepts()
    {
        var result = VectorSearchService.BuildEmbeddingText("Refactored code", "", "refactor");
        Assert.Equal("Refactored code refactor", result);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static float[] Normalize(float[] v)
    {
        float norm = 0;
        for (int i = 0; i < v.Length; i++)
            norm += v[i] * v[i];
        norm = MathF.Sqrt(norm);
        var result = new float[v.Length];
        for (int i = 0; i < v.Length; i++)
            result[i] = v[i] / norm;
        return result;
    }
}
