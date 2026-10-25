using System.Collections.Concurrent;

namespace PaRuX.Server;

public sealed class ExLlamaV3InstallJobStatus
{
    public string Id { get; set; } = string.Empty;
    public string Tag { get; set; } = string.Empty;
    public string State { get; set; } = "queued";
    public string? Progress { get; set; }
    public string? InstalledPath { get; set; }
    public string? Error { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
}

public sealed class ExLlamaV3InstallRequest
{
    public string Tag { get; set; } = string.Empty;
}

/// <summary>
/// Background job wrapper for the managed ExLlamaV3 pack. Installation can download hundreds of
/// megabytes plus CUDA PyTorch, so the UI/API receives a job id and can cancel. User-visible
/// lifecycle milestones are also written to the common Update Center: a successful pack install is
/// deliberately only STAGED, never INSTALLED/ACTIVE, until Engine Race promotion happens later.
/// </summary>
public sealed class ExLlamaV3InstallJobService : IDisposable
{
    private sealed class Entry
    {
        public readonly object Gate = new();
        public readonly ExLlamaV3InstallJobStatus Status;
        public readonly CancellationTokenSource Cancellation = new();
        public Entry(ExLlamaV3InstallJobStatus status) => Status = status;
    }

    private readonly ExLlamaV3ManagedPackService _packs;
    private readonly ManagedAiUpdateStore _updates;
    private readonly ConcurrentDictionary<string, Entry> _jobs = new();
    private readonly SemaphoreSlim _installGate = new(1, 1);

    /// <summary>Gaming Mode signal; production reads the persisted marker, tests inject.</summary>
    internal Func<bool> GamingActive { get; set; } = GamingHardOffWatch.MarkerActive;

    public ExLlamaV3InstallJobService(
        ExLlamaV3ManagedPackService packs,
        ManagedAiUpdateStore? updates = null)
    {
        _packs = packs ?? throw new ArgumentNullException(nameof(packs));
        _updates = updates ?? new ManagedAiUpdateStore();
    }

    public ExLlamaV3InstallJobStatus Start(string tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) throw new InvalidDataException("ExLlamaV3 release tag is required.");
        tag = tag.Trim();
        ManagedAiAssetIdentity asset = Asset();
        _updates.UpsertAvailable(
            asset,
            _packs.GetActiveTag(),
            tag,
            tag,
            $"https://github.com/{ExLlamaV3BuildCatalog.Owner}/{ExLlamaV3BuildCatalog.Repo}/releases/tag/{tag}",
            null,
            message: "ExLlamaV3 engine pack selected for managed installation.");

        var status = new ExLlamaV3InstallJobStatus
        {
            Id = Guid.NewGuid().ToString("N"),
            Tag = tag,
            State = "queued",
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        };
        var entry = new Entry(status);
        _jobs[status.Id] = entry;
        _ = Task.Run(() => RunAsync(entry));
        return Clone(status);
    }

    public ExLlamaV3InstallJobStatus? Get(string id) =>
        _jobs.TryGetValue(id, out Entry? entry) ? Snapshot(entry) : null;

    public IReadOnlyList<ExLlamaV3InstallJobStatus> List() =>
        _jobs.Values.Select(Snapshot).OrderByDescending(item => item.CreatedUtc).ToList();

    public bool Cancel(string id)
    {
        if (!_jobs.TryGetValue(id, out Entry? entry)) return false;
        lock (entry.Gate)
        {
            if (entry.Status.State is "completed" or "failed" or "cancelled") return false;
            entry.Cancellation.Cancel();
            entry.Status.Progress = "Cancellation requested…";
            entry.Status.UpdatedUtc = DateTime.UtcNow;
            return true;
        }
    }

    private async Task RunAsync(Entry entry)
    {
        bool entered = false;
        ManagedAiAssetIdentity asset = Asset();
        // Gaming Mode stops the pack download/CUDA install; it is restarted manually afterwards.
        using var gaming = new GamingHardOffWatch(GamingActive, entry.Cancellation.Token);
        try
        {
            await _installGate.WaitAsync(gaming.Token).ConfigureAwait(false);
            entered = true;
            TryTransition(asset, ManagedAiUpdateState.Downloading, "Downloading/installing the managed ExLlamaV3 pack…");
            Set(entry, "installing", "Preparing managed ExLlamaV3 pack…");
            var progress = new Progress<string>(message => Set(entry, "installing", message));
            string path = await _packs.InstallAsync(entry.Status.Tag, progress, gaming.Token).ConfigureAwait(false);
            lock (entry.Gate)
            {
                entry.Status.State = "completed";
                entry.Status.Progress = "Installed side-by-side; waiting for Engine Race/promotion.";
                entry.Status.InstalledPath = path;
                entry.Status.UpdatedUtc = DateTime.UtcNow;
            }
            TryTransition(
                asset,
                ManagedAiUpdateState.Staged,
                "ExLlamaV3 " + entry.Status.Tag + " was downloaded, SHA-256 verified, imported successfully and staged side-by-side. Engine Race promotion is still required.");
        }
        catch (OperationCanceledException)
        {
            bool pausedForGaming = gaming.Triggered && !entry.Cancellation.IsCancellationRequested;
            Set(entry, "cancelled", pausedForGaming
                ? ModelDownloadService.GamingPauseReason + "; start the ExLlamaV3 installation again after Gaming."
                : "ExLlamaV3 installation cancelled.");
            TryTransition(asset, ManagedAiUpdateState.Cancelled, pausedForGaming
                ? "ExLlamaV3 installation paused for Gaming; it will not restart automatically."
                : "ExLlamaV3 installation cancelled by the user.");
        }
        catch (Exception ex)
        {
            lock (entry.Gate)
            {
                entry.Status.State = "failed";
                entry.Status.Error = ex.Message;
                entry.Status.Progress = "ExLlamaV3 installation failed.";
                entry.Status.UpdatedUtc = DateTime.UtcNow;
            }
            TryTransition(asset, ManagedAiUpdateState.Failed, "ExLlamaV3 installation failed: " + ex.Message);
        }
        finally
        {
            if (entered) _installGate.Release();
        }
    }

    private void TryTransition(ManagedAiAssetIdentity asset, ManagedAiUpdateState state, string message)
    {
        try { _updates.Transition(asset, state, message); }
        catch (KeyNotFoundException) { }
        catch (InvalidOperationException) when (state is ManagedAiUpdateState.Failed or ManagedAiUpdateState.Cancelled) { }
    }

    private static ManagedAiAssetIdentity Asset() => new(
        ManagedAiAssetKind.InferenceEngine,
        LocalInferenceEngineRegistry.ExLlamaV3Id,
        "ExLlamaV3");

    private static void Set(Entry entry, string state, string message)
    {
        lock (entry.Gate)
        {
            entry.Status.State = state;
            entry.Status.Progress = message;
            entry.Status.UpdatedUtc = DateTime.UtcNow;
        }
    }

    private static ExLlamaV3InstallJobStatus Snapshot(Entry entry)
    {
        lock (entry.Gate) return Clone(entry.Status);
    }

    private static ExLlamaV3InstallJobStatus Clone(ExLlamaV3InstallJobStatus value) => new()
    {
        Id = value.Id,
        Tag = value.Tag,
        State = value.State,
        Progress = value.Progress,
        InstalledPath = value.InstalledPath,
        Error = value.Error,
        CreatedUtc = value.CreatedUtc,
        UpdatedUtc = value.UpdatedUtc
    };

    public void Dispose()
    {
        foreach (Entry entry in _jobs.Values)
        {
            try { entry.Cancellation.Cancel(); } catch { }
            entry.Cancellation.Dispose();
        }
        _installGate.Dispose();
    }
}
