namespace Ses.Local.Core.Interfaces;

/// <summary>
/// Generates 384-dimensional embeddings from text using a local ONNX model (all-MiniLM-L6-v2).
/// The model and inference session are loaded lazily on first use.
/// </summary>
public interface ILocalEmbeddingService : IAsyncDisposable
{
    /// <summary>Embeds a single text string into a 384-dimensional float vector.</summary>
    Task<float[]> EmbedAsync(string text, CancellationToken ct = default);

    /// <summary>Returns true if the ONNX model file exists on disk and is ready to load.</summary>
    bool IsModelAvailable { get; }
}
