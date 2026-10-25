using System.Text.Json;

namespace PaRuX.Server;

public sealed class RagVectorAccelerationState
{
    public string EmbeddingProviderKey { get; set; } = string.Empty;
    public string CorpusFingerprint { get; set; } = string.Empty;
    public string IndexKind { get; set; } = "hnsw";
    public bool Promoted { get; set; }
    public double RecallAtK { get; set; }
    public double Speedup { get; set; }
    public int TopK { get; set; }
    public DateTime MeasuredUtc { get; set; }
    public string Reason { get; set; } = string.Empty;
}

/// <summary>Durable promotion evidence for the ANN sidecar. A promotion is valid only for the exact
/// embedding identity and corpus fingerprint that were raced against brute force. Mutations call
/// Invalidate, while fingerprint comparison also protects correctness across process restarts.</summary>
public sealed class RagVectorAccelerationPolicyStore
{
    private readonly string _path;
    private readonly object _gate = new();

    public RagVectorAccelerationPolicyStore() : this(Path.Combine(
        ServerState.StateDirectory, "rag-vector-acceleration.json")) { }

    internal RagVectorAccelerationPolicyStore(string path) => _path = path;

    public RagVectorAccelerationState? GetCurrent(string embeddingProviderKey, string corpusFingerprint)
    {
        lock (_gate)
        {
            RagVectorAccelerationState? state = LoadUnsafe();
            if (state == null || !state.Promoted) return null;
            if (!string.Equals(state.EmbeddingProviderKey, embeddingProviderKey, StringComparison.Ordinal)
                || !string.Equals(state.CorpusFingerprint, corpusFingerprint, StringComparison.Ordinal))
                return null;
            return Clone(state);
        }
    }

    public RagVectorAccelerationState Save(
        string embeddingProviderKey,
        string corpusFingerprint,
        VectorIndexRaceResult race)
    {
        if (string.IsNullOrWhiteSpace(embeddingProviderKey))
            throw new ArgumentException("Embedding provider key is required.", nameof(embeddingProviderKey));
        if (string.IsNullOrWhiteSpace(corpusFingerprint))
            throw new ArgumentException("Corpus fingerprint is required.", nameof(corpusFingerprint));
        ArgumentNullException.ThrowIfNull(race);
        var state = new RagVectorAccelerationState
        {
            EmbeddingProviderKey = embeddingProviderKey,
            CorpusFingerprint = corpusFingerprint,
            IndexKind = "hnsw",
            Promoted = race.Promote,
            RecallAtK = race.RecallAtK,
            Speedup = race.Speedup,
            TopK = race.TopK,
            MeasuredUtc = DateTime.UtcNow,
            Reason = race.Reason
        };
        lock (_gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            string temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(state, JsonOptions));
            File.Move(temp, _path, overwrite: true);
        }
        return Clone(state);
    }

    public void Invalidate()
    {
        lock (_gate)
        {
            try { File.Delete(_path); } catch { }
        }
    }

    private RagVectorAccelerationState? LoadUnsafe()
    {
        try
        {
            if (!File.Exists(_path)) return null;
            return JsonSerializer.Deserialize<RagVectorAccelerationState>(File.ReadAllText(_path), JsonOptions);
        }
        catch { return null; }
    }

    private static RagVectorAccelerationState Clone(RagVectorAccelerationState state) => new()
    {
        EmbeddingProviderKey = state.EmbeddingProviderKey,
        CorpusFingerprint = state.CorpusFingerprint,
        IndexKind = state.IndexKind,
        Promoted = state.Promoted,
        RecallAtK = state.RecallAtK,
        Speedup = state.Speedup,
        TopK = state.TopK,
        MeasuredUtc = state.MeasuredUtc,
        Reason = state.Reason
    };

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
}

internal sealed class RagHnswSidecar
{
    private readonly object _gate = new();
    private HnswVectorIndex? _index;
    private string? _providerKey;
    private string? _corpusFingerprint;

    public void Invalidate()
    {
        lock (_gate)
        {
            _index = null;
            _providerKey = null;
            _corpusFingerprint = null;
        }
    }

    public IReadOnlyList<VectorSearchHit> Search(
        string providerKey,
        string corpusFingerprint,
        int dimensions,
        Func<IReadOnlyList<(string Id, float[] Vector)>> loadAuthoritativeVectors,
        ReadOnlySpan<float> query,
        int topK)
    {
        ArgumentNullException.ThrowIfNull(loadAuthoritativeVectors);
        lock (_gate)
        {
            if (_index == null
                || !string.Equals(_providerKey, providerKey, StringComparison.Ordinal)
                || !string.Equals(_corpusFingerprint, corpusFingerprint, StringComparison.Ordinal))
            {
                IReadOnlyList<(string Id, float[] Vector)> authoritativeVectors = loadAuthoritativeVectors();
                var rebuilt = new HnswVectorIndex(dimensions);
                foreach ((string id, float[] vector) in authoritativeVectors)
                {
                    if (vector.Length != dimensions || vector.Any(value => !float.IsFinite(value))) continue;
                    rebuilt.Upsert(id, vector);
                }
                _index = rebuilt;
                _providerKey = providerKey;
                _corpusFingerprint = corpusFingerprint;
            }
            return _index.Search(query, topK);
        }
    }
}
