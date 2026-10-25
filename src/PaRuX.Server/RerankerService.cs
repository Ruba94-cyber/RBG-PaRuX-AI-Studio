using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;
using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>
/// Local cross-encoder reranking engine - closes Model Roles' RERANKER role, previously reported
/// as `PaRuX.Contracts.ModelRoleCatalog.NotImplementedRoles` because no local engine backed it at
/// all. Pure ONNX Runtime + Microsoft.ML.Tokenizers, the same architecture as EmbeddingService
/// (no external process, no Python), so RagStore can add a genuine "retrieve broad, then rerank
/// precisely" second pass.
///
/// Model: cross-encoder/ms-marco-MiniLM-L-6-v2, via its ONNX export mirror
/// Xenova/ms-marco-MiniLM-L-6-v2 - a small (~80MB), widely used, real sequence-classification
/// cross-encoder, same "resolve/main/onnx/model.onnx" layout EmbeddingService already downloads
/// its own model from.
///
/// Honest disclosure, not hidden: unlike EmbeddingService's exact tensor names (verified live
/// against a real downloaded model before that feature was written), this checkpoint's sandbox
/// has huggingface.co blocked (confirmed live - a HEAD request during this exact checkpoint
/// returned a real 403), so the ONNX graph's actual output name could not be confirmed the same
/// way here. Sequence-classification ONNX exports overwhelmingly use "logits" as their sole
/// output, by transformers/optimum's own export convention - TryScore resolves the output
/// defensively (falls back to the graph's first output if "logits" isn't present, and reads the
/// first scalar via Tensor&lt;float&gt;.First() so it doesn't care about the exact output rank)
/// rather than hard-failing on a name mismatch. This needs one real load+inference check on the
/// Windows dev machine (which can actually reach huggingface.co) before being field-proven - same
/// disclosed-gap class as this session's other network-blocked features (DPO/Unsloth+CUDA, the
/// llama-server release-asset download).
///
/// Sentence-pair tokenization is NOT guessed: BertTokenizer.BuildInputsWithSpecialTokens /
/// CreateTokenTypeIdsFromSequences were confirmed via a real, isolated run (a hand-built vocab,
/// not the production model) to produce [CLS] query [SEP] document [SEP] with token_type_ids 0
/// over the [CLS]+query+[SEP] span and 1 over the document+[SEP] span - exactly BERT's standard
/// sentence-pair layout, and the same real API this project's other BERT-tokenizer consumer
/// (EmbeddingService) already depends on for the single-sequence case.
///
/// Graceful degradation is load-bearing here too: every public method returns null instead of
/// throwing when the engine isn't installed, hasn't loaded, or a single inference fails, so
/// RagStore's reranking pass is always optional - callers fall back to the existing
/// embedding+keyword ranking exactly as it worked before this feature existed.
/// </summary>
public sealed class RerankerService : IDisposable
{
    private const string RepoId = "Xenova/ms-marco-MiniLM-L-6-v2";

    private static readonly (string Remote, string Local)[] RequiredFiles =
    {
        ("onnx/model.onnx", "model.onnx"),
        ("vocab.txt", "vocab.txt"),
    };

    // Per-side (query, document) truncation - matches EmbeddingService's own truncate-not-fail
    // policy for inputs longer than the model's real max sequence length.
    private const int MaxTokensPerSequence = 256;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(15) };

    private readonly string _runtimeRoot;
    private readonly object _loadGate = new();
    private InferenceSession? _session;
    private BertTokenizer? _tokenizer;
    private string? _outputName;
    private bool _loadAttempted;

    public RerankerService() : this(DefaultRuntimeRoot)
    {
    }

    /// <summary>Internal so tests can point this at an empty temp directory to exercise the
    /// "engine not installed" fallback path deterministically, matching EmbeddingServiceTests'
    /// own convention.</summary>
    internal RerankerService(string runtimeRoot) => _runtimeRoot = runtimeRoot;

    private static string DefaultRuntimeRoot => Path.Combine(
        PaRuX.Contracts.PaRuXDataRoot.Path, "ai-runtimes", "reranker");

    private string ModelPath => Path.Combine(_runtimeRoot, "model.onnx");
    private string VocabPath => Path.Combine(_runtimeRoot, "vocab.txt");
    private string LastErrorPath => Path.Combine(_runtimeRoot, "last-install-error.txt");

    public bool IsInstalled => File.Exists(ModelPath) && File.Exists(VocabPath);

    public RerankerEngineStatus GetStatus()
    {
        bool installed = IsInstalled;
        return new RerankerEngineStatus
        {
            Installed = installed,
            Loaded = installed && TryEnsureLoaded(),
            LastError = installed ? null : SafeRead(LastErrorPath)
        };
    }

    /// <summary>Real, on-demand install: downloads the ONNX model + vocab from the repo above.
    /// Explicitly requested, never automatic - same policy as EmbeddingService.</summary>
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

            lock (_loadGate) { _session?.Dispose(); _session = null; _tokenizer = null; _outputName = null; _loadAttempted = false; }
            if (!TryEnsureLoaded()) throw new InvalidOperationException("Downloaded files did not load as a valid reranker engine.");

            try { File.Delete(LastErrorPath); } catch { /* best effort */ }
            return "Reranker engine installed at " + _runtimeRoot;
        }
        catch (Exception ex)
        {
            try { await File.WriteAllTextAsync(LastErrorPath, ex.Message, ct); } catch { /* best effort */ }
            throw;
        }
    }

    // HttpClient.Timeout does not reliably bound a streamed body read taken with
    // HttpCompletionOption.ResponseHeadersRead - observed live, a real download stalled partway
    // through (connection went silent, no error, no more bytes) and the copy hung indefinitely,
    // outliving even the caller's own request cancellation. Each individual read below is bounded
    // instead, so a stalled connection surfaces as a clear, recoverable timeout.
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

    /// <summary>Relevance score for one (query, document) pair - higher is more relevant. Null if
    /// the engine isn't installed/loadable, either input is empty, or inference otherwise fails.
    /// Callers MUST treat null as "skip reranking, keep the original order", never as an error.</summary>
    public float? TryScore(string? query, string? document)
    {
        if (string.IsNullOrWhiteSpace(query) || string.IsNullOrWhiteSpace(document)) return null;
        if (!TryEnsureLoaded()) return null;

        try
        {
            IReadOnlyList<int> idsA = _tokenizer!.EncodeToIds(query, addSpecialTokens: false);
            IReadOnlyList<int> idsB = _tokenizer!.EncodeToIds(document, addSpecialTokens: false);
            if (idsA.Count > MaxTokensPerSequence) idsA = idsA.Take(MaxTokensPerSequence).ToList();
            if (idsB.Count > MaxTokensPerSequence) idsB = idsB.Take(MaxTokensPerSequence).ToList();

            IReadOnlyList<int> combined = _tokenizer.BuildInputsWithSpecialTokens(idsA, idsB);
            IReadOnlyList<int> tokenTypeIds = _tokenizer.CreateTokenTypeIdsFromSequences(idsA, idsB);
            int n = combined.Count;

            var inputIds = new DenseTensor<long>(new[] { 1, n });
            var attentionMask = new DenseTensor<long>(new[] { 1, n });
            var typeIds = new DenseTensor<long>(new[] { 1, n });
            for (int i = 0; i < n; i++)
            {
                inputIds[0, i] = combined[i];
                attentionMask[0, i] = 1;
                typeIds[0, i] = tokenTypeIds[i];
            }

            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor("input_ids", inputIds),
                NamedOnnxValue.CreateFromTensor("attention_mask", attentionMask),
                NamedOnnxValue.CreateFromTensor("token_type_ids", typeIds)
            };

            using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs = _session!.Run(inputs);
            DisposableNamedOnnxValue output = outputs.FirstOrDefault(o => o.Name == _outputName) ?? outputs.First();
            Tensor<float> logits = output.AsTensor<float>();
            return logits.Length > 0 ? logits.First() : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Scores every document against the query and returns the top results by score
    /// descending, capped at topK (default: all of them). Documents the engine fails to score
    /// (null from TryScore) are dropped rather than guessed at with a fabricated score - a caller
    /// that gets fewer results back than it sent in should treat that as a partial-degradation
    /// signal, not silently trust a padded ranking.</summary>
    public List<RerankResult> Rerank(string query, IReadOnlyList<string> documents, int? topK = null)
    {
        var scored = new List<RerankResult>();
        for (int i = 0; i < documents.Count; i++)
        {
            float? score = TryScore(query, documents[i]);
            if (score.HasValue) scored.Add(new RerankResult { Index = i, Document = documents[i], Score = score.Value });
        }
        scored.Sort((a, b) => b.Score.CompareTo(a.Score));
        return topK.HasValue && topK.Value >= 0 ? scored.Take(topK.Value).ToList() : scored;
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
                _outputName = _session.OutputMetadata.Keys.FirstOrDefault(k => k == "logits")
                    ?? _session.OutputMetadata.Keys.FirstOrDefault();
                return true;
            }
            catch (Exception ex)
            {
                try { File.WriteAllText(LastErrorPath, "Failed to load reranker engine: " + ex.Message); } catch { /* best effort */ }
                _session?.Dispose();
                _session = null;
                _tokenizer = null;
                return false;
            }
        }
    }

    private static string? SafeRead(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path) : null; }
        catch { return null; }
    }

    public void Dispose() => _session?.Dispose();
}
