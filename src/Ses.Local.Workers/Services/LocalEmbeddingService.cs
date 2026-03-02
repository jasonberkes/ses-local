using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;
using Ses.Local.Core.Interfaces;
using Ses.Local.Core.Options;

namespace Ses.Local.Workers.Services;

/// <summary>
/// Generates 384-dimensional embeddings using the all-MiniLM-L6-v2 ONNX model.
/// Loads the model and tokenizer lazily on first use. The InferenceSession is
/// cached as a singleton (expensive to create, ~200ms).
/// </summary>
public sealed partial class LocalEmbeddingService : ILocalEmbeddingService
{
    private const int EmbeddingDimension = 384;
    private const int MaxSequenceLength = 256;

    private readonly string _modelPath;
    private readonly ILogger<LocalEmbeddingService> _logger;
    private readonly SemaphoreSlim _initLock = new(1, 1);

    private InferenceSession? _session;
    private BertTokenizer? _tokenizer;
    private bool _initialized;

    public LocalEmbeddingService(
        IOptions<SesLocalOptions> options,
        ILogger<LocalEmbeddingService> logger)
    {
        _modelPath = options.Value.EmbeddingModelPath;
        _logger = logger;
    }

    /// <summary>Internal constructor for testing.</summary>
    internal LocalEmbeddingService(string modelPath, ILogger<LocalEmbeddingService> logger)
    {
        _modelPath = modelPath;
        _logger = logger;
    }

    public bool IsModelAvailable => File.Exists(_modelPath);

    public async Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        var (session, tokenizer) = await EnsureInitializedAsync(ct);

        // Tokenize
        var encoded = tokenizer.EncodeToIds(text, MaxSequenceLength, out _, out _);

        // Build input tensors — [CLS] tokens [SEP], padded to actual length
        var seqLen = Math.Min(encoded.Count, MaxSequenceLength);
        var inputIds = new long[seqLen];
        var attentionMask = new long[seqLen];

        for (int i = 0; i < seqLen; i++)
        {
            inputIds[i] = encoded[i];
            attentionMask[i] = 1;
        }

        var inputIdsTensor = new DenseTensor<long>(inputIds, [1, seqLen]);
        var attentionMaskTensor = new DenseTensor<long>(attentionMask, [1, seqLen]);
        var tokenTypeIdsTensor = new DenseTensor<long>(new long[seqLen], [1, seqLen]);

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input_ids", inputIdsTensor),
            NamedOnnxValue.CreateFromTensor("attention_mask", attentionMaskTensor),
            NamedOnnxValue.CreateFromTensor("token_type_ids", tokenTypeIdsTensor)
        };

        // Run inference
        using var results = session.Run(inputs);

        // Try sentence_embedding output first, fall back to mean pooling over token_embeddings
        var sentenceEmbedding = results.FirstOrDefault(r => r.Name == "sentence_embedding");
        if (sentenceEmbedding is not null)
        {
            var tensor = sentenceEmbedding.AsTensor<float>();
            var embedding = new float[EmbeddingDimension];
            for (int i = 0; i < EmbeddingDimension; i++)
                embedding[i] = tensor[0, i];
            Normalize(embedding);
            return embedding;
        }

        // Mean pooling over token_embeddings with attention mask
        var tokenEmbeddings = results.First(r =>
            r.Name is "token_embeddings" or "last_hidden_state").AsTensor<float>();

        return MeanPool(tokenEmbeddings, attentionMask, seqLen);
    }

    private static float[] MeanPool(Tensor<float> tokenEmbeddings, long[] attentionMask, int seqLen)
    {
        var result = new float[EmbeddingDimension];
        float maskSum = 0;

        for (int i = 0; i < seqLen; i++)
        {
            if (attentionMask[i] == 0) continue;
            maskSum += 1;
            for (int j = 0; j < EmbeddingDimension; j++)
                result[j] += tokenEmbeddings[0, i, j];
        }

        if (maskSum > 0)
        {
            for (int j = 0; j < EmbeddingDimension; j++)
                result[j] /= maskSum;
        }

        Normalize(result);
        return result;
    }

    private static void Normalize(float[] vector)
    {
        float norm = 0;
        for (int i = 0; i < vector.Length; i++)
            norm += vector[i] * vector[i];
        norm = MathF.Sqrt(norm);

        if (norm > 0)
        {
            for (int i = 0; i < vector.Length; i++)
                vector[i] /= norm;
        }
    }

    private async Task<(InferenceSession session, BertTokenizer tokenizer)> EnsureInitializedAsync(
        CancellationToken ct)
    {
        if (_initialized && _session is not null && _tokenizer is not null)
            return (_session, _tokenizer);

        await _initLock.WaitAsync(ct);
        try
        {
            if (_initialized && _session is not null && _tokenizer is not null)
                return (_session, _tokenizer);

            if (!File.Exists(_modelPath))
                throw new FileNotFoundException(
                    $"ONNX embedding model not found at {_modelPath}. Enable model download or place the model manually.");

            LogLoadingModel(_logger, _modelPath);

            var sessionOptions = new SessionOptions
            {
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                InterOpNumThreads = 1,
                IntraOpNumThreads = Environment.ProcessorCount > 4 ? 4 : Environment.ProcessorCount
            };

            _session = new InferenceSession(_modelPath, sessionOptions);

            // Load vocab.txt from same directory as the model
            var vocabPath = Path.Combine(Path.GetDirectoryName(_modelPath)!, "vocab.txt");
            if (!File.Exists(vocabPath))
                throw new FileNotFoundException(
                    $"Vocabulary file not found at {vocabPath}. It should be downloaded alongside the ONNX model.");

            _tokenizer = BertTokenizer.Create(vocabPath);
            _initialized = true;

            LogModelLoaded(_logger, EmbeddingDimension);
            return (_session, _tokenizer);
        }
        finally
        {
            _initLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _session?.Dispose();
        _initLock.Dispose();
        await Task.CompletedTask;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Loading ONNX embedding model from {Path}")]
    private static partial void LogLoadingModel(ILogger logger, string path);

    [LoggerMessage(Level = LogLevel.Information, Message = "ONNX embedding model loaded ({Dimensions}-dim)")]
    private static partial void LogModelLoaded(ILogger logger, int dimensions);
}
