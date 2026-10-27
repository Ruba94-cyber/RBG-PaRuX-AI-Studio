using System.Text.Json;

namespace PaRuX.Server;

/// <summary>
/// Models that finished downloading and still await their automatic per-PC calibration. Persisted so a restart keeps the
/// promise; entries whose file vanished are dropped. Only chat-capable GGUF files qualify: projectors, speculative heads and
/// the media assistant are not tuned as chat models.
/// </summary>
internal sealed class PostDownloadOptimizationQueue
{
    private static readonly object Gate = new();
    /// <summary>A model that was never calibrated within a day stops counting as "fresh", so one stuck entry cannot keep the idle gate short.</summary>
    internal static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);
    private readonly Func<DateTime> _utcNow;

    internal PostDownloadOptimizationQueue(Func<DateTime>? utcNow = null) => _utcNow = utcNow ?? (() => DateTime.UtcNow);

    private sealed record Entry(string Path, DateTime EnqueuedUtc);
    private static string FilePath => Path.Combine(LocalAiPaths.StateDir, "post-download-optimization.json");

    internal static bool Qualifies(string modelPath)
    {
        string name = Path.GetFileName(modelPath);
        return name.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase)
            && !name.Contains("mmproj", StringComparison.OrdinalIgnoreCase)
            && !PaRuX.Intelligence.GgufFileNaming.IsAuxiliaryHead(name)
            && !LocalAiPaths.IsFallbackOnlyModel(modelPath)
            && File.Exists(modelPath);
    }

    internal void Enqueue(string modelPath)
    {
        if (!Qualifies(modelPath)) return;
        lock (Gate)
        {
            List<Entry> all = Read();
            if (all.Any(e => string.Equals(e.Path, modelPath, StringComparison.OrdinalIgnoreCase))) return;
            all.Add(new Entry(modelPath, _utcNow()));
            Write(all);
        }
    }

    internal IReadOnlyList<string> Pending()
    {
        lock (Gate) return Read().Where(e => _utcNow() - e.EnqueuedUtc < Lifetime && File.Exists(e.Path)).Select(e => e.Path).ToList();
    }

    internal void MarkDone(string modelPath)
    {
        lock (Gate) Write(Read().Where(e => !string.Equals(e.Path, modelPath, StringComparison.OrdinalIgnoreCase)).ToList());
    }

    private static List<Entry> Read()
    {
        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(FilePath)) ?? new List<Entry>()
                : new List<Entry>();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return new List<Entry>(); }
    }

    private static void Write(List<Entry> all)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(all));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best-effort: the download stays valid */ }
    }
}

/// <summary>Download completion: the existing speed benchmark first, then the model joins the automatic calibration queue.</summary>
internal sealed class PostDownloadOptimizationHandler : IModelDownloadCompletionHandler
{
    private readonly IModelDownloadCompletionHandler _inner;
    private readonly PostDownloadOptimizationQueue _queue;
    private readonly ModelTuningTracker? _tuning;
    private readonly Action<string, string>? _queueMedia;
    private readonly Func<string, string?>? _repoIdFor;
    private readonly Func<string, bool> _hasLocalDraft;
    private readonly Func<string, CancellationToken, Task<IReadOnlyList<(string FileName, long SizeBytes)>>>? _listRepoFiles;

    /// <param name="tuning">Visible tuning state; null keeps the hand-off silent.</param>
    /// <param name="queueMedia">Media checkpoint (path, label) joins the media tuning queue.</param>
    /// <param name="repoIdFor">Hugging Face repository a downloaded file came from, for draft-head proposals.</param>
    /// <param name="listRepoFiles">Read-only repository GGUF listing; a proposal is only ever shown, never downloaded.</param>
    internal PostDownloadOptimizationHandler(
        IModelDownloadCompletionHandler inner, PostDownloadOptimizationQueue queue, ModelTuningTracker? tuning = null,
        Action<string, string>? queueMedia = null, Func<string, string?>? repoIdFor = null,
        Func<string, CancellationToken, Task<IReadOnlyList<(string FileName, long SizeBytes)>>>? listRepoFiles = null,
        Func<string, bool>? hasLocalDraft = null)
    {
        _hasLocalDraft = hasLocalDraft ?? (path => SpeculativeDraftDiscovery.Discover(path).Count > 0);
        _inner = inner;
        _queue = queue;
        _tuning = tuning;
        _queueMedia = queueMedia;
        _repoIdFor = repoIdFor;
        _listRepoFiles = listRepoFiles;
    }

    public void OnGgufDownloaded(string modelPath, string label)
    {
        try { _inner.OnGgufDownloaded(modelPath, label); }
        finally { EnqueueForTuning(modelPath, label); }
    }

    public void OnMediaCheckpointDownloaded(string modelPath, string label)
    {
        try { _inner.OnMediaCheckpointDownloaded(modelPath, label); }
        finally { _queueMedia?.Invoke(modelPath, label); }
    }

    /// <summary>Queues a chat GGUF (any family) and publishes it as "queued". Projectors, speculative heads and fallback models are ignored.</summary>
    internal bool EnqueueForTuning(string modelPath, string label, string workload = "chat")
    {
        if (!PostDownloadOptimizationQueue.Qualifies(modelPath)) return false;
        _queue.Enqueue(modelPath);
        string id = _tuning?.Queued("text", modelPath, label, workload, "Starts shortly if the PC is idle") ?? string.Empty;
        if (_tuning is not null && _repoIdFor is not null && _listRepoFiles is not null)
            _ = Task.Run(() => ProposeDraftHeadAsync(modelPath, id));
        return true;
    }

    internal async Task ProposeDraftHeadAsync(string modelPath, string tuningId)
    {
        try
        {
            string? repo = _repoIdFor!(modelPath);
            if (repo is null || _hasLocalDraft(modelPath)) return;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            IReadOnlyList<(string FileName, long SizeBytes)> files = await _listRepoFiles!(repo, timeout.Token).ConfigureAwait(false);
            if (DraftHeadProposals.Pick(files, Path.GetFileName(modelPath)) is { } pick)
                _tuning!.SetProposal(tuningId, new PaRuX.Contracts.ModelTuningProposalDto { RepoId = repo, FileName = pick.FileName, SizeBytes = pick.SizeBytes });
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { /* a proposal is optional; offline or unknown repo means none */ }
    }
}

/// <summary>Chooses a speculative "draft head" sidecar from a repository listing. Pure: no network, no download.</summary>
internal static class DraftHeadProposals
{
    internal static (string FileName, long SizeBytes)? Pick(IEnumerable<(string FileName, long SizeBytes)> repoFiles, string targetFileName)
    {
        string targetStem = SpeculativeDraftDiscovery.NormalizeTargetStem(Path.GetFileNameWithoutExtension(targetFileName));
        var heads = repoFiles
            .Where(f => f.FileName.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase)
                && PaRuX.Intelligence.GgufFileNaming.IsAuxiliaryHead(f.FileName)
                && !f.FileName.Contains("mmproj", StringComparison.OrdinalIgnoreCase)
                && !(PaRuX.Intelligence.GgufFileNaming.TryParseSplitPart(f.FileName, out PaRuX.Intelligence.GgufFileNaming.SplitPart part) && part.Total > 1)
                && f.SizeBytes > 0)
            .ToList();
        if (heads.Count == 0) return null;
        bool Matches((string FileName, long SizeBytes) f)
        {
            string stem = SpeculativeDraftDiscovery.NormalizeTargetStem(Path.GetFileNameWithoutExtension(f.FileName));
            return targetStem.Length >= 4 && (stem.Contains(targetStem, StringComparison.Ordinal) || targetStem.Contains(stem, StringComparison.Ordinal));
        }
        return heads.OrderByDescending(Matches).ThenBy(f => f.SizeBytes).First();
    }
}
