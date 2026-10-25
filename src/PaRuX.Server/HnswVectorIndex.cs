using System.Collections.Concurrent;

namespace PaRuX.Server;

public sealed record VectorSearchHit(string Id, float Similarity);

public interface IVectorSimilarityIndex
{
    int Dimensions { get; }
    int Count { get; }
    void Upsert(string id, ReadOnlySpan<float> normalizedVector);
    bool Remove(string id);
    IReadOnlyList<VectorSearchHit> Search(ReadOnlySpan<float> normalizedQuery, int topK, int? efSearch = null);
    void Clear();
}

/// <summary>
/// PaRuX-owned in-memory HNSW index. Persistence/SQLite ownership remains outside this class so a
/// corrupt or stale ANN sidecar can always be rebuilt from authoritative RAG embeddings. Vectors
/// must be L2-normalized, making dot product equal cosine similarity. The implementation is
/// intentionally deterministic for the same insertion order/seed, which makes recall regressions
/// reproducible in tests and in PaRuX's future RAG index race.
/// </summary>
public sealed class HnswVectorIndex : IVectorSimilarityIndex
{
    private sealed class Node
    {
        public required string Id { get; init; }
        public required float[] Vector { get; init; }
        public required int Level { get; init; }
        public Dictionary<int, HashSet<string>> Neighbors { get; } = new();
    }

    private readonly ReaderWriterLockSlim _gate = new(LockRecursionPolicy.NoRecursion);
    private readonly Dictionary<string, Node> _nodes = new(StringComparer.Ordinal);
    private readonly Random _random;
    private readonly int _m;
    private readonly int _efConstruction;
    private string? _entryPoint;
    private int _maxLevel = -1;

    public HnswVectorIndex(int dimensions, int m = 16, int efConstruction = 100, int seed = 0x50415255)
    {
        if (dimensions <= 0) throw new ArgumentOutOfRangeException(nameof(dimensions));
        if (m < 2 || m > 64) throw new ArgumentOutOfRangeException(nameof(m));
        if (efConstruction < m || efConstruction > 4096) throw new ArgumentOutOfRangeException(nameof(efConstruction));
        Dimensions = dimensions;
        _m = m;
        _efConstruction = efConstruction;
        _random = new Random(seed);
    }

    public int Dimensions { get; }

    public int Count
    {
        get
        {
            _gate.EnterReadLock();
            try { return _nodes.Count; }
            finally { _gate.ExitReadLock(); }
        }
    }

    public void Upsert(string id, ReadOnlySpan<float> normalizedVector)
    {
        ValidateIdAndVector(id, normalizedVector);
        float[] copy = normalizedVector.ToArray();

        _gate.EnterWriteLock();
        try
        {
            // HNSW replacement is easiest to keep correct by removing the old topology first.
            // RAG chunk updates are much less frequent than searches, so this favors query safety.
            if (_nodes.ContainsKey(id)) RemoveUnsafe(id);

            int level = RandomLevel();
            var node = new Node { Id = id, Vector = copy, Level = level };
            for (int layer = 0; layer <= level; layer++) node.Neighbors[layer] = new HashSet<string>(StringComparer.Ordinal);

            if (_entryPoint == null)
            {
                _nodes[id] = node;
                _entryPoint = id;
                _maxLevel = level;
                return;
            }

            string current = _entryPoint;
            float currentSimilarity = Similarity(copy, _nodes[current].Vector);

            // Greedy descent through layers that the new node will not occupy.
            for (int layer = _maxLevel; layer > level; layer--)
                (current, currentSimilarity) = GreedyAtLayer(copy, current, currentSimilarity, layer);

            _nodes[id] = node;
            int highestShared = Math.Min(level, _maxLevel);
            for (int layer = highestShared; layer >= 0; layer--)
            {
                List<(string Id, float Similarity)> candidates = SearchLayer(copy, current, _efConstruction, layer, id);
                List<string> selected = SelectDiverseNeighbors(copy, candidates, MaxConnections(layer));

                foreach (string neighborId in selected)
                {
                    node.Neighbors[layer].Add(neighborId);
                    Node neighbor = _nodes[neighborId];
                    if (!neighbor.Neighbors.TryGetValue(layer, out HashSet<string>? links))
                    {
                        links = new HashSet<string>(StringComparer.Ordinal);
                        neighbor.Neighbors[layer] = links;
                    }
                    links.Add(id);
                    PruneUnsafe(neighbor, layer);
                }

                if (candidates.Count > 0) current = candidates[0].Id;
            }

            if (level > _maxLevel)
            {
                _entryPoint = id;
                _maxLevel = level;
            }
        }
        finally { _gate.ExitWriteLock(); }
    }

    public bool Remove(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;
        _gate.EnterWriteLock();
        try { return RemoveUnsafe(id); }
        finally { _gate.ExitWriteLock(); }
    }

    public IReadOnlyList<VectorSearchHit> Search(ReadOnlySpan<float> normalizedQuery, int topK, int? efSearch = null)
    {
        ValidateVector(normalizedQuery);
        if (topK <= 0) return Array.Empty<VectorSearchHit>();
        int ef = Math.Clamp(efSearch ?? Math.Max(32, topK * 4), topK, 4096);
        float[] query = normalizedQuery.ToArray();

        _gate.EnterReadLock();
        try
        {
            if (_entryPoint == null || _nodes.Count == 0) return Array.Empty<VectorSearchHit>();
            string current = _entryPoint;
            float similarity = Similarity(query, _nodes[current].Vector);
            for (int layer = _maxLevel; layer > 0; layer--)
                (current, similarity) = GreedyAtLayer(query, current, similarity, layer);

            return SearchLayer(query, current, ef, 0, excludeId: null)
                .OrderByDescending(candidate => candidate.Similarity)
                .Take(topK)
                .Select(candidate => new VectorSearchHit(candidate.Id, candidate.Similarity))
                .ToList();
        }
        finally { _gate.ExitReadLock(); }
    }

    public void Clear()
    {
        _gate.EnterWriteLock();
        try
        {
            _nodes.Clear();
            _entryPoint = null;
            _maxLevel = -1;
        }
        finally { _gate.ExitWriteLock(); }
    }

    private (string Id, float Similarity) GreedyAtLayer(
        float[] query, string startId, float startSimilarity, int layer)
    {
        string current = startId;
        float best = startSimilarity;
        bool improved;
        do
        {
            improved = false;
            if (!_nodes[current].Neighbors.TryGetValue(layer, out HashSet<string>? neighbors)) break;
            foreach (string neighborId in neighbors)
            {
                if (!_nodes.TryGetValue(neighborId, out Node? neighbor)) continue;
                float similarity = Similarity(query, neighbor.Vector);
                if (similarity <= best) continue;
                current = neighborId;
                best = similarity;
                improved = true;
            }
        } while (improved);
        return (current, best);
    }

    private List<(string Id, float Similarity)> SearchLayer(
        float[] query,
        string entryId,
        int ef,
        int layer,
        string? excludeId)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var candidates = new PriorityQueue<(string Id, float Similarity), float>();
        var best = new PriorityQueue<(string Id, float Similarity), float>();

        if (!_nodes.TryGetValue(entryId, out Node? entry)) return new();
        float initialSimilarity = Similarity(query, entry.Vector);
        visited.Add(entryId);
        candidates.Enqueue((entryId, initialSimilarity), -initialSimilarity); // max similarity first
        if (!string.Equals(entryId, excludeId, StringComparison.Ordinal))
            best.Enqueue((entryId, initialSimilarity), initialSimilarity); // min similarity first

        while (candidates.TryDequeue(out (string Id, float Similarity) candidate, out _))
        {
            float worstBest = best.TryPeek(out _, out float worst) ? worst : float.NegativeInfinity;
            if (best.Count >= ef && candidate.Similarity < worstBest) break;
            if (!_nodes.TryGetValue(candidate.Id, out Node? node)
                || !node.Neighbors.TryGetValue(layer, out HashSet<string>? neighbors)) continue;

            foreach (string neighborId in neighbors)
            {
                if (!visited.Add(neighborId) || !_nodes.TryGetValue(neighborId, out Node? neighbor)) continue;
                float similarity = Similarity(query, neighbor.Vector);
                if (best.Count < ef || similarity > worstBest)
                {
                    candidates.Enqueue((neighborId, similarity), -similarity);
                    if (!string.Equals(neighborId, excludeId, StringComparison.Ordinal))
                    {
                        best.Enqueue((neighborId, similarity), similarity);
                        if (best.Count > ef) best.Dequeue();
                        worstBest = best.TryPeek(out _, out float updatedWorst) ? updatedWorst : float.NegativeInfinity;
                    }
                }
            }
        }

        var results = new List<(string Id, float Similarity)>(best.Count);
        while (best.TryDequeue(out (string Id, float Similarity) item, out _)) results.Add(item);
        results.Sort((left, right) => right.Similarity.CompareTo(left.Similarity));
        return results;
    }

    private void PruneUnsafe(Node node, int layer)
    {
        if (!node.Neighbors.TryGetValue(layer, out HashSet<string>? links)) return;
        int max = MaxConnections(layer);
        if (links.Count <= max) return;
        List<(string Id, float Similarity)> candidates = links
            .Where(_nodes.ContainsKey)
            .Select(id => (id, Similarity(node.Vector, _nodes[id].Vector)))
            .ToList();
        List<string> keep = SelectDiverseNeighbors(node.Vector, candidates, max);
        links.Clear();
        foreach (string id in keep) links.Add(id);
    }

    private List<string> SelectDiverseNeighbors(
        float[] origin,
        IEnumerable<(string Id, float Similarity)> candidates,
        int maximum)
    {
        List<(string Id, float Similarity)> ordered = candidates
            .Where(candidate => _nodes.ContainsKey(candidate.Id))
            .OrderByDescending(candidate => candidate.Similarity)
            .ToList();
        var selected = new List<string>(maximum);
        var rejected = new List<string>();

        // Standard HNSW diversity heuristic: retain a candidate when it is at least as close to
        // the origin as it is to every already-selected neighbor. Pure nearest-only pruning makes
        // dense clusters sever the few cross-cluster links and can leave most of the graph
        // unreachable from the entry point.
        foreach ((string id, float similarity) in ordered)
        {
            bool diverse = selected.All(selectedId =>
                Similarity(_nodes[id].Vector, _nodes[selectedId].Vector) <= similarity);
            if (diverse) selected.Add(id);
            else rejected.Add(id);
            if (selected.Count == maximum) return selected;
        }

        // Keep the requested degree when the heuristic alone selects too few neighbors.
        foreach (string id in rejected)
        {
            selected.Add(id);
            if (selected.Count == maximum) break;
        }
        return selected;
    }

    private bool RemoveUnsafe(string id)
    {
        if (!_nodes.Remove(id, out Node? removed)) return false;
        foreach (Node node in _nodes.Values)
            foreach (HashSet<string> neighbors in node.Neighbors.Values)
                neighbors.Remove(id);

        if (string.Equals(_entryPoint, id, StringComparison.Ordinal))
        {
            Node? replacement = _nodes.Values
                .OrderByDescending(node => node.Level)
                .ThenBy(node => node.Id, StringComparer.Ordinal)
                .FirstOrDefault();
            _entryPoint = replacement?.Id;
            _maxLevel = replacement?.Level ?? -1;
        }
        else if (removed.Level == _maxLevel && !_nodes.Values.Any(node => node.Level == _maxLevel))
        {
            _maxLevel = _nodes.Count == 0 ? -1 : _nodes.Values.Max(node => node.Level);
            if (_entryPoint != null && _nodes[_entryPoint].Level < _maxLevel)
                _entryPoint = _nodes.Values.First(node => node.Level == _maxLevel).Id;
        }
        return true;
    }

    private int RandomLevel()
    {
        // Standard exponentially decreasing layer probability, bounded to avoid pathological
        // random topology and excessive memory use on large local knowledge bases.
        int level = 0;
        while (level < 16 && _random.NextDouble() < (1d / Math.E)) level++;
        return level;
    }

    private int MaxConnections(int layer) => layer == 0 ? _m * 2 : _m;

    private static float Similarity(float[] left, float[] right)
    {
        int length = Math.Min(left.Length, right.Length);
        float dot = 0;
        for (int i = 0; i < length; i++) dot += left[i] * right[i];
        return dot;
    }

    private void ValidateIdAndVector(string id, ReadOnlySpan<float> vector)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("Vector id is required.", nameof(id));
        ValidateVector(vector);
    }

    private void ValidateVector(ReadOnlySpan<float> vector)
    {
        if (vector.Length != Dimensions)
            throw new ArgumentException($"Expected a {Dimensions}-dimension vector, received {vector.Length}.");
        for (int i = 0; i < vector.Length; i++)
            if (!float.IsFinite(vector[i])) throw new ArgumentException("Vector contains a non-finite component.");
    }
}
