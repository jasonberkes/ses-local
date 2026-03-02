namespace Ses.Local.Core.Interfaces;

/// <summary>
/// Downloads the ONNX embedding model on first use.
/// Reports progress for tray UI display.
/// </summary>
public interface IModelDownloadService
{
    /// <summary>
    /// Ensures the embedding model exists at the configured path.
    /// Downloads from Hugging Face if not present. Reports progress via callback.
    /// </summary>
    Task EnsureModelAsync(IProgress<ModelDownloadProgress>? progress = null, CancellationToken ct = default);
}

/// <summary>Progress information for model download.</summary>
public sealed record ModelDownloadProgress(long BytesDownloaded, long TotalBytes, string Status);
