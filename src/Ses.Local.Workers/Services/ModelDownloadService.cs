using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Ses.Local.Core.Interfaces;
using Ses.Local.Core.Options;

namespace Ses.Local.Workers.Services;

/// <summary>
/// Downloads the all-MiniLM-L6-v2 ONNX model and vocab.txt from Hugging Face
/// on first use. Files are stored in ~/.ses/models/. Download is skipped if
/// the files already exist.
/// </summary>
public sealed partial class ModelDownloadService : IModelDownloadService
{
    private const string ModelUrl =
        "https://huggingface.co/sentence-transformers/all-MiniLM-L6-v2/resolve/main/onnx/model.onnx";

    private const string VocabUrl =
        "https://huggingface.co/sentence-transformers/all-MiniLM-L6-v2/resolve/main/vocab.txt";

    private readonly string _modelPath;
    private readonly HttpClient _httpClient;
    private readonly ILogger<ModelDownloadService> _logger;
    private readonly SemaphoreSlim _downloadLock = new(1, 1);

    public ModelDownloadService(
        IOptions<SesLocalOptions> options,
        HttpClient httpClient,
        ILogger<ModelDownloadService> logger)
    {
        _modelPath = options.Value.EmbeddingModelPath;
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task EnsureModelAsync(IProgress<ModelDownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        var modelDir = Path.GetDirectoryName(_modelPath)!;
        var vocabPath = Path.Combine(modelDir, "vocab.txt");

        if (File.Exists(_modelPath) && File.Exists(vocabPath))
            return;

        await _downloadLock.WaitAsync(ct);
        try
        {
            // Double-check after acquiring lock
            if (File.Exists(_modelPath) && File.Exists(vocabPath))
                return;

            Directory.CreateDirectory(modelDir);

            if (!File.Exists(_modelPath))
                await DownloadFileAsync(ModelUrl, _modelPath, "ONNX model", progress, ct);

            if (!File.Exists(vocabPath))
                await DownloadFileAsync(VocabUrl, vocabPath, "vocabulary", progress, ct);

            LogModelDownloadComplete(_logger, modelDir);
        }
        finally
        {
            _downloadLock.Release();
        }
    }

    private async Task DownloadFileAsync(string url, string destPath, string description,
        IProgress<ModelDownloadProgress>? progress, CancellationToken ct)
    {
        LogDownloadingFile(_logger, description, url);
        progress?.Report(new ModelDownloadProgress(0, 0, $"Downloading {description}..."));

        var tempPath = destPath + ".tmp";

        try
        {
            using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? -1;

            await using var contentStream = await response.Content.ReadAsStreamAsync(ct);
            await using var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write,
                FileShare.None, bufferSize: 81920, useAsync: true);

            var buffer = new byte[81920];
            long bytesDownloaded = 0;
            int bytesRead;

            while ((bytesRead = await contentStream.ReadAsync(buffer, ct)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
                bytesDownloaded += bytesRead;

                if (totalBytes > 0)
                {
                    var mb = bytesDownloaded / (1024.0 * 1024);
                    var totalMb = totalBytes / (1024.0 * 1024);
                    progress?.Report(new ModelDownloadProgress(bytesDownloaded, totalBytes,
                        $"Downloading {description}... {mb:F1}MB/{totalMb:F1}MB"));
                }
            }

            // Atomic rename
            File.Move(tempPath, destPath, overwrite: true);

            LogFileDownloaded(_logger, description, bytesDownloaded);
        }
        catch
        {
            // Clean up partial download
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); }
                catch { /* best effort */ }
            }
            throw;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Downloading embedding {Description} from {Url}")]
    private static partial void LogDownloadingFile(ILogger logger, string description, string url);

    [LoggerMessage(Level = LogLevel.Information, Message = "Embedding {Description} downloaded ({Bytes} bytes)")]
    private static partial void LogFileDownloaded(ILogger logger, string description, long bytes);

    [LoggerMessage(Level = LogLevel.Information, Message = "Embedding model download complete: {Directory}")]
    private static partial void LogModelDownloadComplete(ILogger logger, string directory);
}
