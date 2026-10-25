using System.Security.Cryptography;
using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>
/// Fetches only the companion files a checkpoint is still missing, after the user has reviewed and confirmed the
/// exact plan. Every gate is checked before any byte is requested: consent, an unchanged plan hash, a supported
/// family, and a pinned source for every missing file. Files go through the existing ModelDownloadService
/// (pinned revision, size and digest verification), then into the ComfyUI folder the registry names.
/// The caller retries the media job after a successful fetch; this class never submits work itself.
/// </summary>
internal sealed class MediaCompanionFetcher
{
    public const int MaxAttemptsPerFile = 2;
    public static readonly TimeSpan PerFileTimeout = TimeSpan.FromHours(4);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private static readonly object PlanFetchGatesLock = new();
    private static readonly Dictionary<string, PlanFetchGate> PlanFetchGates = new(StringComparer.Ordinal);

    private sealed class PlanFetchGate
    {
        public readonly SemaphoreSlim Semaphore = new(1, 1);
        public int References;
    }

    private readonly ModelDownloadService _downloads;
    private readonly string _comfyModelsRoot;

    public MediaCompanionFetcher(ModelDownloadService downloads, string comfyModelsRoot)
    {
        _downloads = downloads ?? throw new ArgumentNullException(nameof(downloads));
        _comfyModelsRoot = comfyModelsRoot ?? throw new ArgumentNullException(nameof(comfyModelsRoot));
    }

    /// <summary>The Hugging Face resolve URL for a pinned companion. Only meaningful when the item is fetchable.</summary>
    public static string ResolveUrlFor(MediaCompanionItem item)
    {
        if (item.Repository == null || item.RepositoryPath == null || item.Revision == null)
            throw new InvalidOperationException("The companion " + item.FileName + " has no pinned source.");
        return "https://huggingface.co/" + item.Repository + "/resolve/" + item.Revision + "/" + item.RepositoryPath;
    }

    public async Task<MediaCompanionFetchResult> FetchAsync(MediaCompanionPlan plan, MediaCompanionFetchRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(request);

        if (!request.Consent)
            return Refused("Fetching companions needs your consent. Review the list, then confirm.");
        if (!string.Equals(request.PlanHash, plan.PlanHash, StringComparison.Ordinal))
            return Refused("The companion plan changed since you reviewed it. Review it again before fetching.");
        if (plan.Support != "supported")
            return Refused(plan.Reason ?? "PaRuX cannot install the companions for this checkpoint.");
        if (plan.Missing.Count == 0)
            return new MediaCompanionFetchResult { Ok = true, Message = "Every companion for this checkpoint is already installed." };

        List<MediaCompanionItem> manual = plan.Missing.Where(item => !item.Fetchable).ToList();
        if (manual.Count > 0)
        {
            return Refused("PaRuX has no pinned source for these, so it will not download them. Install them manually: "
                + string.Join("; ", manual.Select(item => item.FileName + " into the ComfyUI " + item.Folder + " folder"
                    + (item.Repository == null ? string.Empty : " (from " + item.Repository + ")"))) + ".");
        }

        // The route creates a fetcher for each request, so the gate must be process-wide. A waiting
        // request rechecks the target after the first fetch finishes and reports the file as installed.
        PlanFetchGate planGate = AcquirePlanFetchGate(plan.PlanHash);
        bool entered = false;
        try
        {
            await planGate.Semaphore.WaitAsync(ct).ConfigureAwait(false);
            entered = true;

            var installed = new List<string>();
            foreach (MediaCompanionItem item in plan.Missing)
            {
                string? failure = await FetchOneAsync(item, ct).ConfigureAwait(false);
                if (failure != null)
                {
                    return new MediaCompanionFetchResult
                    {
                        Ok = false,
                        FailedStep = "fetch " + item.FileName,
                        Message = failure,
                        Installed = installed
                    };
                }
                installed.Add(item.Folder + "/" + item.FileName);
            }

            return new MediaCompanionFetchResult
            {
                Ok = true,
                Message = "Installed " + installed.Count + " companion file(s). Retry the media job now.",
                Installed = installed
            };
        }
        finally
        {
            if (entered) planGate.Semaphore.Release();
            ReleasePlanFetchGate(plan.PlanHash, planGate);
        }
    }

    private static PlanFetchGate AcquirePlanFetchGate(string planHash)
    {
        lock (PlanFetchGatesLock)
        {
            if (!PlanFetchGates.TryGetValue(planHash, out PlanFetchGate? gate))
            {
                gate = new PlanFetchGate();
                PlanFetchGates.Add(planHash, gate);
            }
            gate.References++;
            return gate;
        }
    }

    private static void ReleasePlanFetchGate(string planHash, PlanFetchGate gate)
    {
        lock (PlanFetchGatesLock)
        {
            gate.References--;
            if (gate.References == 0)
            {
                PlanFetchGates.Remove(planHash);
                gate.Semaphore.Dispose();
            }
        }
    }

    /// <summary>Downloads one pinned companion and moves it into place. Returns null on success, otherwise the reason.</summary>
    private async Task<string?> FetchOneAsync(MediaCompanionItem item, CancellationToken ct)
    {
        string target = Path.Combine(_comfyModelsRoot, item.Folder, item.FileName);
        if (File.Exists(target))
        {
            // Same name and same pinned size: already installed, nothing to fetch (this includes files shared with
            // another family, since the name is the identity). A different size is never silently overwritten.
            if (item.PinnedSizeBytes is long pinned && new FileInfo(target).Length == pinned) return null;
            return item.FileName + " is already in " + item.Folder + " with a different size. Move it aside, then fetch again.";
        }

        string? lastError = null;
        for (int attempt = 1; attempt <= MaxAttemptsPerFile; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            ModelDownloadStatus status;
            try
            {
                status = await _downloads.CreateCompanionDownloadAsync(item, ct).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is InvalidOperationException or InvalidDataException)
            {
                return "The download of " + item.FileName + " could not start: " + exception.Message;
            }

            string terminal;
            string? error;
            try
            {
                (terminal, error) = await WaitForTerminalAsync(status.Id, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // CreateDownloadAsync starts an independent job. Stop and observe that job before releasing
                // the plan gate, or a waiting request could begin a second transfer while it is still writing.
                _downloads.Cancel(status.Id);
                await WaitForTerminalAsync(status.Id, CancellationToken.None).ConfigureAwait(false);
                throw;
            }
            if (terminal == "completed")
            {
                return MoveIntoPlace(item, Path.Combine(ModelDownloadService.ModelsDirectory, item.FileName), target);
            }
            lastError = error ?? "the transfer ended as " + terminal;
        }

        return "The download of " + item.FileName + " failed after " + MaxAttemptsPerFile + " attempt(s): " + lastError;
    }

    /// <summary>Polls the download job until it reaches a terminal state, bounded by <see cref="PerFileTimeout"/>.
    /// Caller cancellation propagates; only the bound itself becomes a reported failure.</summary>
    private async Task<(string Status, string? Error)> WaitForTerminalAsync(string id, CancellationToken ct)
    {
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bounded.CancelAfter(PerFileTimeout);
        while (true)
        {
            ModelDownloadStatus? current = _downloads.Get(id);
            if (current == null) return ("missing", "The download job was not found.");
            if (current.Status is not ("queued" or "downloading")) return (current.Status, current.Error);
            try
            {
                await Task.Delay(PollInterval, bounded.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return ("failed", "The transfer did not finish within " + PerFileTimeout.TotalHours + " hours.");
            }
        }
    }

    private static string? MoveIntoPlace(MediaCompanionItem item, string downloaded, string target)
    {
        if (!File.Exists(downloaded)) return "The download of " + item.FileName + " finished, but its file was not found.";
        long size = new FileInfo(downloaded).Length;
        if (item.PinnedSizeBytes is long pinned && size != pinned)
        {
            File.Delete(downloaded);
            return "The downloaded " + item.FileName + " is " + size + " bytes, not the pinned " + pinned + ". It was discarded.";
        }
        // The digest is the identity of the bytes: a same-size file with other content is discarded, never installed.
        if (item.Sha256 != null && !string.Equals(Sha256Of(downloaded), item.Sha256, StringComparison.Ordinal))
        {
            File.Delete(downloaded);
            return "The downloaded " + item.FileName + " does not match its pinned SHA-256. It was discarded.";
        }
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Move(downloaded, target, overwrite: false);
        return null;
    }

    private static string Sha256Of(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static MediaCompanionFetchResult Refused(string message) => new() { Ok = false, Message = message };
}
