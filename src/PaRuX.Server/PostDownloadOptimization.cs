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

    internal PostDownloadOptimizationHandler(IModelDownloadCompletionHandler inner, PostDownloadOptimizationQueue queue)
    {
        _inner = inner;
        _queue = queue;
    }

    public void OnGgufDownloaded(string modelPath, string label)
    {
        try { _inner.OnGgufDownloaded(modelPath, label); }
        finally { _queue.Enqueue(modelPath); }
    }
}
