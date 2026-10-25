using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;

namespace PaRuX.Server;

public interface IAsyncRetrievalEmbeddingProvider
{
    RetrievalEmbeddingIdentity Identity { get; }
    Task<bool> IsAvailableAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<float[]?>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken);
}

public interface IAsyncRetrievalRerankerProvider
{
    RetrievalRerankerIdentity Identity { get; }
    Task<bool> IsAvailableAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<float?>> ScoreBatchAsync(string query, IReadOnlyList<string> documents, CancellationToken cancellationToken);
}

internal enum LlamaRetrievalMode { Embedding, Reranking }

/// <summary>
/// Dedicated llama-server lifecycle for retrieval models. It is deliberately separate from Chat's
/// interactive llama-server process: loading an embedding/reranker must not mutate Chat state,
/// prompt cache, model lock or conversation slots. The future VRAM scheduler decides whether this
/// process may stay hot; this class owns only one model/mode process and its loopback transport.
/// </summary>
internal sealed class LlamaRetrievalServer : IAsyncDisposable
{
    private readonly LocalAiProcessManager _runtime;
    private readonly string _modelPath;
    private readonly LlamaRetrievalMode _mode;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process? _process;
    private HttpClient? _http;
    private string? _baseUrl;

    public LlamaRetrievalServer(LocalAiProcessManager runtime, string modelPath, LlamaRetrievalMode mode)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _modelPath = Path.GetFullPath(modelPath ?? throw new ArgumentNullException(nameof(modelPath)));
        _mode = mode;
    }

    public bool ModelExists => File.Exists(_modelPath) && _modelPath.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase);

    public async Task<HttpClient> EnsureReadyAsync(CancellationToken cancellationToken)
    {
        if (!ModelExists) throw new FileNotFoundException("Retrieval GGUF model was not found.", _modelPath);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_process is { HasExited: false } && _http != null && await IsHealthyAsync(_http, cancellationToken).ConfigureAwait(false))
                return _http;

            StopUnsafe();
            bool cuda = await _runtime.HasCudaHeavyGpuAsync(cancellationToken).ConfigureAwait(false);
            string runtimePath = cuda ? LocalAiPaths.RuntimeCudaPath : LocalAiPaths.RuntimeVulkanPath;
            string executable = Path.Combine(runtimePath, LocalAiPaths.LlamaServerExecutable);
            if (!File.Exists(executable))
                throw new FileNotFoundException("PaRuX llama-server runtime is not installed.", executable);

            int port = ReserveLoopbackPort();
            var start = new ProcessStartInfo
            {
                FileName = executable,
                WorkingDirectory = runtimePath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            Add(start,
                "--model", _modelPath,
                "--host", "127.0.0.1",
                "--port", port.ToString(),
                "--no-webui",
                "--parallel", "1");
            if (_mode == LlamaRetrievalMode.Embedding)
                Add(start, "--embedding");
            else
                Add(start, "--reranking");
            if (!cuda) Add(start, "--device", "Vulkan0");

            var process = new Process { StartInfo = start, EnableRaisingEvents = true };
            if (!process.Start()) throw new InvalidOperationException("Could not start llama-server retrieval process.");
            ChildProcessJob.Assign(process);
            _ = process.StandardOutput.ReadToEndAsync();
            Task<string> stderr = process.StandardError.ReadToEndAsync();

            // The port was reserved and released before launch, so another process could race to bind
            // it. Pin the exact launched identity; every connection must be answered by this process.
            ExpectedPeerIdentity? launched = ConnectedPeerAttestation.TryCaptureIdentity(process.Id);
            if (launched is null)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                throw new InvalidOperationException("Could not verify the identity of the retrieval llama-server process.");
            }
            ExpectedPeerIdentity expectedPeer = launched.Value;
            var http = new HttpClient(AttestedLoopbackHttp.CreateHandler(
                _ => new[] { expectedPeer }, allowUnattestedWhenPlatformUnsupported: false))
            { Timeout = TimeSpan.FromMinutes(5) };
            _baseUrl = "http://127.0.0.1:" + port;
            _process = process;
            _http = http;
            await WaitUntilHealthyAsync(process, http, stderr, cancellationToken).ConfigureAwait(false);
            return http;
        }
        catch
        {
            StopUnsafe();
            throw;
        }
        finally { _gate.Release(); }
    }

    public string BaseUrl => _baseUrl ?? throw new InvalidOperationException("Retrieval server is not ready.");

    public async Task StopAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { StopUnsafe(); }
        finally { _gate.Release(); }
    }

    private async Task WaitUntilHealthyAsync(
        Process process,
        HttpClient http,
        Task<string> stderr,
        CancellationToken cancellationToken)
    {
        DateTime deadline = DateTime.UtcNow.AddMinutes(5);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (process.HasExited)
            {
                string error = stderr.IsCompletedSuccessfully ? await stderr.ConfigureAwait(false) : string.Empty;
                throw new InvalidOperationException("llama-server retrieval process exited during startup: " + Trim(error));
            }
            if (await IsHealthyAsync(http, cancellationToken).ConfigureAwait(false)) return;
            await Task.Delay(200, cancellationToken).ConfigureAwait(false);
        }
        throw new TimeoutException("llama-server retrieval process did not become healthy within 5 minutes.");
    }

    private async Task<bool> IsHealthyAsync(HttpClient http, CancellationToken cancellationToken)
    {
        if (_baseUrl == null) return false;
        try
        {
            using var probe = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            probe.CancelAfter(TimeSpan.FromMilliseconds(500));
            using HttpResponseMessage response = await http.GetAsync(_baseUrl + "/health", probe.Token).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    private void StopUnsafe()
    {
        Process? process = _process;
        _process = null;
        HttpClient? http = _http;
        _http = null;
        _baseUrl = null;
        try { if (process is { HasExited: false }) process.Kill(entireProcessTree: true); } catch { }
        try { process?.Dispose(); } catch { }
        try { http?.Dispose(); } catch { }
    }

    private static int ReserveLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static void Add(ProcessStartInfo info, params string[] args)
    {
        foreach (string arg in args) info.ArgumentList.Add(arg);
    }

    private static string Trim(string text)
    {
        text = (text ?? string.Empty).Trim();
        return text.Length <= 1200 ? text : text[..1200] + "…";
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _gate.Dispose();
    }
}

/// <summary>Batch embedding provider backed by llama.cpp's OpenAI-compatible /v1/embeddings API.</summary>
public sealed class LlamaCppEmbeddingProvider : IAsyncRetrievalEmbeddingProvider, IAsyncDisposable
{
    private readonly LlamaRetrievalServer _server;

    public LlamaCppEmbeddingProvider(
        LocalAiProcessManager runtime,
        string modelPath,
        string modelId,
        string revision,
        int dimensions,
        string instructionProfile = "retrieval")
    {
        if (dimensions <= 0) throw new ArgumentOutOfRangeException(nameof(dimensions));
        _server = new LlamaRetrievalServer(runtime, modelPath, LlamaRetrievalMode.Embedding);
        Identity = new RetrievalEmbeddingIdentity(
            "llama-cpp-embedding", modelId, revision, dimensions, instructionProfile);
    }

    public RetrievalEmbeddingIdentity Identity { get; }

    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken)
    {
        if (!_server.ModelExists) return false;
        try { await _server.EnsureReadyAsync(cancellationToken).ConfigureAwait(false); return true; }
        catch { return false; }
    }

    public async Task<IReadOnlyList<float[]?>> EmbedBatchAsync(
        IReadOnlyList<string> texts,
        CancellationToken cancellationToken)
    {
        if (texts == null) throw new ArgumentNullException(nameof(texts));
        if (texts.Count == 0) return Array.Empty<float[]?>();
        if (texts.Count > 128) throw new InvalidDataException("A single retrieval embedding batch is limited to 128 inputs.");
        if (texts.Any(string.IsNullOrWhiteSpace))
            throw new InvalidDataException("Embedding batch contains an empty input.");

        HttpClient http = await _server.EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
        using HttpResponseMessage response = await http.PostAsJsonAsync(
            _server.BaseUrl + "/v1/embeddings",
            new { input = texts, model = Identity.ModelId, encoding_format = "float" },
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        if (!json.RootElement.TryGetProperty("data", out JsonElement data) || data.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("llama.cpp embeddings response did not contain a data array.");

        var result = Enumerable.Repeat<float[]?>(null, texts.Count).ToArray();
        foreach (JsonElement item in data.EnumerateArray())
        {
            if (!item.TryGetProperty("index", out JsonElement indexElement)
                || !item.TryGetProperty("embedding", out JsonElement vectorElement)
                || indexElement.ValueKind != JsonValueKind.Number
                || vectorElement.ValueKind != JsonValueKind.Array) continue;
            int index = indexElement.GetInt32();
            if (index < 0 || index >= result.Length) continue;
            float[] vector = vectorElement.EnumerateArray().Select(value => value.GetSingle()).ToArray();
            if (vector.Length != Identity.Dimensions)
                throw new InvalidDataException(
                    $"Embedding dimension mismatch: expected {Identity.Dimensions}, received {vector.Length}.");
            if (vector.Any(value => !float.IsFinite(value)))
                throw new InvalidDataException("Embedding response contained a non-finite component.");
            Normalize(vector);
            result[index] = vector;
        }
        if (result.Any(vector => vector == null))
            throw new InvalidDataException("llama.cpp embedding batch returned fewer vectors than requested.");
        return result;
    }

    private static void Normalize(float[] vector)
    {
        double sum = 0;
        foreach (float value in vector) sum += value * value;
        double norm = Math.Sqrt(sum);
        if (norm <= 1e-12) throw new InvalidDataException("Embedding vector has zero norm.");
        for (int i = 0; i < vector.Length; i++) vector[i] = (float)(vector[i] / norm);
    }

    public ValueTask DisposeAsync() => _server.DisposeAsync();
}

/// <summary>Batch reranker provider backed by llama.cpp's /v1/rerank endpoint.</summary>
public sealed class LlamaCppRerankerProvider : IAsyncRetrievalRerankerProvider, IAsyncDisposable
{
    private readonly LlamaRetrievalServer _server;

    public LlamaCppRerankerProvider(
        LocalAiProcessManager runtime,
        string modelPath,
        string modelId,
        string revision,
        string? instructionProfile = "retrieval")
    {
        _server = new LlamaRetrievalServer(runtime, modelPath, LlamaRetrievalMode.Reranking);
        Identity = new RetrievalRerankerIdentity("llama-cpp-reranker", modelId, revision, instructionProfile);
    }

    public RetrievalRerankerIdentity Identity { get; }

    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken)
    {
        if (!_server.ModelExists) return false;
        try { await _server.EnsureReadyAsync(cancellationToken).ConfigureAwait(false); return true; }
        catch { return false; }
    }

    public async Task<IReadOnlyList<float?>> ScoreBatchAsync(
        string query,
        IReadOnlyList<string> documents,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query)) throw new InvalidDataException("Reranker query is required.");
        if (documents == null) throw new ArgumentNullException(nameof(documents));
        if (documents.Count == 0) return Array.Empty<float?>();
        if (documents.Count > 128) throw new InvalidDataException("A single reranker batch is limited to 128 documents.");
        if (documents.Any(string.IsNullOrWhiteSpace)) throw new InvalidDataException("Reranker batch contains an empty document.");

        HttpClient http = await _server.EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
        using HttpResponseMessage response = await http.PostAsJsonAsync(
            _server.BaseUrl + "/v1/rerank",
            new { model = Identity.ModelId, query, top_n = documents.Count, documents },
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));

        JsonElement array = json.RootElement.ValueKind == JsonValueKind.Array
            ? json.RootElement
            : json.RootElement.TryGetProperty("results", out JsonElement results) && results.ValueKind == JsonValueKind.Array
                ? results
                : throw new InvalidDataException("llama.cpp reranker response did not contain a results array.");

        var scores = Enumerable.Repeat<float?>(null, documents.Count).ToArray();
        foreach (JsonElement item in array.EnumerateArray())
        {
            if (!TryReadIndex(item, out int index) || index < 0 || index >= scores.Length) continue;
            if (!TryReadScore(item, out float score) || !float.IsFinite(score)) continue;
            scores[index] = score;
        }
        if (scores.All(score => !score.HasValue))
            throw new InvalidDataException("llama.cpp reranker response contained no usable scores.");
        return scores;
    }

    private static bool TryReadIndex(JsonElement item, out int index)
    {
        index = -1;
        if (!item.TryGetProperty("index", out JsonElement value) || value.ValueKind != JsonValueKind.Number) return false;
        return value.TryGetInt32(out index);
    }

    private static bool TryReadScore(JsonElement item, out float score)
    {
        score = 0;
        foreach (string name in new[] { "relevance_score", "score" })
        {
            if (item.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number)
            {
                score = value.GetSingle();
                return true;
            }
        }
        return false;
    }

    public ValueTask DisposeAsync() => _server.DisposeAsync();
}
