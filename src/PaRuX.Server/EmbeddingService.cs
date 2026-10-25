using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;
using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>
/// Local sentence-embedding engine used by MemoryStore and RagStore to replace their
/// keyword-overlap-only retrieval with real vector-similarity search. Pure ONNX Runtime +
/// Microsoft.ML.Tokenizers (both real .NET/native NuGet packages) - no Python, no external
/// process, consistent with this session's "no external scripts for the core app" rule.
///
/// Model: sentence-transformers/all-MiniLM-L6-v2 (22M params, 384-dim sentence embeddings),
/// a small, widely used embedding model with a real published ONNX export. Verified live
/// before wiring any of this up (HEAD/GET, following redirects to the actual CDN object):
///   https://huggingface.co/sentence-transformers/all-MiniLM-L6-v2/resolve/main/onnx/model.onnx
///   -> 200 OK, 90,405,214 bytes
/// plus tokenizer_config.json, config.json and vocab.txt from the same repo. The ONNX graph's
/// real input/output names (input_ids/attention_mask/token_type_ids in, last_hidden_state out,
/// shape [1, seqLen, 384]) were confirmed by actually loading the downloaded model and running
/// a real inference, not assumed from documentation.
///
/// Not bundled in the repo - downloaded on request into a managed directory under
/// %LOCALAPPDATA%\PaRuX\ai-runtimes\embeddings\, the same on-demand-install pattern already
/// used by SpeechService/ThreedService/TrainingLabService for their own engines.
///
/// Graceful degradation is load-bearing here, not optional: every public method returns
/// null/false instead of throwing when the engine isn't installed yet, hasn't loaded, or a
/// single inference fails, so MemoryStore/RagStore can fall back to their original
/// keyword-overlap ranking exactly as it worked before this feature existed.
/// </summary>
public sealed class EmbeddingService : IDisposable
{
    public const int Dimensions = 384;

    private const string RepoId = "sentence-transformers/all-MiniLM-L6-v2";

    // (relative path in the HF repo, file name to save it as locally)
    private static readonly (string Remote, string Local)[] RequiredFiles =
    {
        ("onnx/model.onnx", "model.onnx"),
        ("vocab.txt", "vocab.txt"),
        ("tokenizer_config.json", "tokenizer_config.json"),
        ("config.json", "config.json"),
    };

    // Sentence-transformers' own published max sequence length for this model; longer inputs
    // are truncated rather than failing, matching how a real deployment of the model degrades.
    private const int MaxTokens = 256;

    // Below this cosine similarity, a hit is treated as noise rather than a real semantic
    // match (see MemoryStore/RagStore.Retrieve) - keyword overlap is still credited separately.
    public const float RelevanceThreshold = 0.2f;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(15) };

    private readonly string _runtimeRoot;
    private readonly object _loadGate = new();
    private InferenceSession? _session;
    private BertTokenizer? _tokenizer;
    private bool _loadAttempted;

    public EmbeddingService() : this(DefaultRuntimeRoot)
    {
    }

    /// <summary>Internal so tests can point this at an empty temp directory to exercise the
    /// "engine not installed" fallback path deterministically, independent of whether the real
    /// model happens to already be downloaded on the machine running the tests.</summary>
    internal EmbeddingService(string runtimeRoot) => _runtimeRoot = runtimeRoot;

    private static string DefaultRuntimeRoot => Path.Combine(
        PaRuX.Contracts.PaRuXDataRoot.Path, "ai-runtimes", "embeddings");

    private string ModelPath => Path.Combine(_runtimeRoot, "model.onnx");
    private string VocabPath => Path.Combine(_runtimeRoot, "vocab.txt");
    private string LastErrorPath => Path.Combine(_runtimeRoot, "last-install-error.txt");

    public bool IsInstalled => File.Exists(ModelPath) && File.Exists(VocabPath);

    public EmbeddingEngineStatus GetStatus()
    {
        bool installed = IsInstalled;
        return new EmbeddingEngineStatus
        {
            Installed = installed,
            Loaded = installed && TryEnsureLoaded(),
            Dimensions = installed ? Dimensions : null,
            LastError = installed ? null : SafeRead(LastErrorPath)
        };
    }

    /// <summary>Real, on-demand install: downloads the ONNX model + tokenizer files from the
    /// verified Hugging Face repo above. ~90 MB total, so this is explicitly requested, never
    /// automatic.</summary>
    public async Task<string> InstallEngineAsync(IProgress<string>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(_runtimeRoot);
        try
        {
            foreach ((string remote, string local) in RequiredFiles)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report("Downloading " + local + "...");
                await DownloadFileAsync("https://huggingface.co/" + RepoId + "/resolve/main/" + remote,
                    Path.Combine(_runtimeRoot, local), ct);
            }

            lock (_loadGate) { _session?.Dispose(); _session = null; _tokenizer = null; _loadAttempted = false; }
            if (!TryEnsureLoaded()) throw new InvalidOperationException("Downloaded files did not load as a valid embedding engine.");

            try { File.Delete(LastErrorPath); } catch { }
            return "Embedding engine installed at " + _runtimeRoot;
        }
        catch (Exception ex)
        {
            string detail = ex.Message;
            try { await File.WriteAllTextAsync(LastErrorPath, detail, ct); } catch { }
            throw;
        }
    }

    // HttpClient.Timeout does not reliably bound a streamed body read taken with
    // HttpCompletionOption.ResponseHeadersRead - observed live (in RerankerService's identical
    // download path), a real download stalled partway through (connection went silent, no error,
    // no more bytes) and the copy hung indefinitely, outliving even the caller's own request
    // cancellation. Each individual read below is bounded instead, so a stalled connection
    // surfaces as a clear, recoverable timeout.
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(30);

    private static async Task DownloadFileAsync(string url, string destination, CancellationToken ct)
    {
        string partial = destination + ".partial";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using HttpResponseMessage response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        await using (Stream source = await response.Content.ReadAsStreamAsync(ct))
        await using (FileStream target = new(partial, FileMode.Create, FileAccess.Write))
        {
            var buffer = new byte[81920];
            while (true)
            {
                using CancellationTokenSource readGuard = CancellationTokenSource.CreateLinkedTokenSource(ct);
                readGuard.CancelAfter(StallTimeout);
                int read;
                try
                {
                    read = await source.ReadAsync(buffer, readGuard.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new TimeoutException(
                        $"Download stalled: no data received for {StallTimeout.TotalSeconds:0}s from {url}.");
                }
                if (read == 0) break;
                await target.WriteAsync(buffer.AsMemory(0, read), ct);
            }
        }
        if (File.Exists(destination)) File.Delete(destination);
        File.Move(partial, destination);
    }

    /// <summary>
    /// Computes a 384-dim, L2-normalized sentence embedding, or null if the engine isn't
    /// installed/loadable or inference otherwise fails. Callers MUST treat null as "fall back
    /// to keyword search", never as an error - this is the hard graceful-degradation contract.
    /// </summary>
    public float[]? TryEmbed(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (!TryEnsureLoaded()) return null;

        try
        {
            IReadOnlyList<int> ids = _tokenizer!.EncodeToIds(text, addSpecialTokens: true);
            if (ids.Count == 0) return null;
            if (ids.Count > MaxTokens) ids = ids.Take(MaxTokens).ToList();

            int n = ids.Count;
            var inputIds = new DenseTensor<long>(new[] { 1, n });
            var attentionMask = new DenseTensor<long>(new[] { 1, n });
            var tokenTypeIds = new DenseTensor<long>(new[] { 1, n });
            for (int i = 0; i < n; i++)
            {
                inputIds[0, i] = ids[i];
                attentionMask[0, i] = 1;
                tokenTypeIds[0, i] = 0;
            }

            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor("input_ids", inputIds),
                NamedOnnxValue.CreateFromTensor("attention_mask", attentionMask),
                NamedOnnxValue.CreateFromTensor("token_type_ids", tokenTypeIds)
            };

            using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs = _session!.Run(inputs);
            Tensor<float> tokenEmbeddings = outputs.First(o => o.Name == "last_hidden_state").AsTensor<float>();

            // Mean-pool over the real (non-padding) tokens, then L2-normalize - the standard
            // sentence-transformers pooling for this model, confirmed live via the smoke test
            // in this feature's verification (similar sentences ~0.55 cosine, unrelated ~0.05).
            var pooled = new float[Dimensions];
            for (int t = 0; t < n; t++)
                for (int d = 0; d < Dimensions; d++)
                    pooled[d] += tokenEmbeddings[0, t, d];
            for (int d = 0; d < Dimensions; d++) pooled[d] /= n;

            Normalize(pooled);
            return pooled;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Both vectors are expected to be L2-normalized (as TryEmbed always returns), so
    /// the plain dot product already equals cosine similarity.</summary>
    public static float CosineSimilarity(float[] a, float[] b)
    {
        int len = Math.Min(a.Length, b.Length);
        float dot = 0;
        for (int i = 0; i < len; i++) dot += a[i] * b[i];
        return dot;
    }

    public static byte[] ToBytes(float[] vector)
    {
        var bytes = new byte[vector.Length * sizeof(float)];
        Buffer.BlockCopy(vector, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    public static float[]? FromBytes(byte[]? bytes)
    {
        if (bytes == null || bytes.Length == 0 || bytes.Length % sizeof(float) != 0) return null;
        var vector = new float[bytes.Length / sizeof(float)];
        Buffer.BlockCopy(bytes, 0, vector, 0, bytes.Length);
        return vector;
    }

    private bool TryEnsureLoaded()
    {
        if (_session != null && _tokenizer != null) return true;
        if (!IsInstalled) return false;

        lock (_loadGate)
        {
            if (_session != null && _tokenizer != null) return true;
            if (_loadAttempted) return false; // a corrupt/incompatible download shouldn't be retried on every call
            _loadAttempted = true;
            try
            {
                _tokenizer = BertTokenizer.Create(VocabPath);
                _session = new InferenceSession(ModelPath);
                return true;
            }
            catch (Exception ex)
            {
                try { File.WriteAllText(LastErrorPath, "Failed to load embedding engine: " + ex.Message); } catch { }
                _session?.Dispose();
                _session = null;
                _tokenizer = null;
                return false;
            }
        }
    }

    private static void Normalize(float[] v)
    {
        double sumSquares = 0;
        foreach (float x in v) sumSquares += (double)x * x;
        float norm = (float)Math.Sqrt(sumSquares);
        if (norm < 1e-12f) return;
        for (int i = 0; i < v.Length; i++) v[i] /= norm;
    }

    private static string? SafeRead(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path) : null; }
        catch { return null; }
    }

    public void Dispose() => _session?.Dispose();
}
