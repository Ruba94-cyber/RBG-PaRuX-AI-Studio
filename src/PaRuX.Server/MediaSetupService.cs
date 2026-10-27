using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>
/// Guided first-run media setup: installs ComfyUI, downloads the prompt-assistant model and a checkpoint that has a real
/// download source, activates it and fetches its companions. Every step is observable, cancellable and bounded; a failure
/// stops the run with the step's own reason and never reports Ready.
/// </summary>
public sealed class MediaSetupService
{
    private static readonly TimeSpan StepTimeout = TimeSpan.FromHours(3);
    private readonly RuntimeInstallService _runtime;
    private readonly ModelDownloadService _downloads;
    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private Task? _run;
    private MediaSetupStatus _last = new();
    private string _lastType = "image";

    public MediaSetupService(RuntimeInstallService runtime, ModelDownloadService downloads)
    {
        _runtime = runtime;
        _downloads = downloads;
    }

    internal static MediaSetupFacts ReadFacts(string type = "image")
    {
        bool comfy = File.Exists(LocalAiPaths.ComfyMain) && File.Exists(LocalAiPaths.ComfyPython);
        bool assistant = File.Exists(LocalAiPaths.SmallModel) || File.Exists(LocalAiPaths.SmallUncensoredModel);
        HashSet<string> checkpoints = MediaCatalogueLive.InstalledFiles();
        long? missingCompanions = null;
        if (comfy && checkpoints.Count > 0)
        {
            try { missingCompanions = MediaCompanionPlanReader.Build(type).Missing.Sum(m => m.PinnedSizeBytes ?? m.ApproxSizeBytes ?? 0); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { }
        }
        return new MediaSetupFacts(comfy, assistant, checkpoints, missingCompanions);
    }

    internal static MediaSetupPlan CurrentPlan(string type = "image")
    {
        var (hw, _, digest) = MediaCatalogueLive.DetectHardware();
        return MediaSetupPlanner.Plan(MediaCatalogue.Shipped, hw, ReadFacts(type),
            MediaCatalogueLive.SpeedLookup(MediaSetupPlanner.CapabilityFor(type), digest), type);
    }

    internal static string NormalizeType(string? type) => type is "video" or "edit" ? type : "image";

    public MediaSetupStatus GetStatus(string? type = null)
    {
        type = NormalizeType(type);
        MediaSetupPlan plan = CurrentPlan(type);
        lock (_gate)
        {
            // A run belongs to one media type; other types report only what is installed.
            MediaSetupStatus status = _lastType == type || _last.State == "idle" ? Clone(_last) : new MediaSetupStatus();
            if (_lastType != type && _run is { IsCompleted: false })
            {
                status.State = "idle";
                status.Message = "Another media setup (" + _lastType + ") is running; wait for it to finish.";
            }
            status.Steps = plan.Steps.Select(ToDto).ToList();
            status.Ready = plan.Ready;
            if (status.State == "idle" || status.State == "completed")
            {
                bool blocked = plan.Steps.Any(s => s.State == MediaSetupStepState.Blocked);
                status.State = plan.Ready ? "completed" : blocked ? "blocked" : "idle";
                status.Message = plan.Ready ? Label(type) + " is set up."
                    : blocked ? plan.Steps.First(s => s.State == MediaSetupStepState.Blocked).Detail
                    : Label(type) + " needs " + string.Join(", ", plan.Steps.Where(s => s.State == MediaSetupStepState.Pending).Select(s => s.Title)) + ".";
            }
            return status;
        }
    }

    private static string Label(string type) => type switch
    {
        "video" => "Video generation",
        "edit" => "Image editing",
        _ => "Image generation"
    };

    public MediaSetupStatus Start(string? type = null)
    {
        type = NormalizeType(type);
        lock (_gate)
        {
            if (_run is { IsCompleted: false }) return GetStatusUnlocked();
            _lastType = type;
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            _last = new MediaSetupStatus { State = "running", Message = "Starting media setup." };
            CancellationToken token = _cts.Token;
            string runType = type;
            _run = Task.Run(() => RunAsync(runType, token));
        }
        return GetStatus(type);
    }

    public void Cancel()
    {
        lock (_gate) _cts?.Cancel();
    }

    private MediaSetupStatus GetStatusUnlocked() => Clone(_last);

    private async Task RunAsync(string type, CancellationToken ct)
    {
        try
        {
            MediaSetupPlan plan = CurrentPlan(type);
            foreach (MediaSetupStep step in plan.Steps)
            {
                if (step.State == MediaSetupStepState.Blocked) { Finish("blocked", step.Detail, step.Detail, step.Id); return; }
            }
            foreach (MediaSetupStep step in plan.Steps.Where(s => s.State == MediaSetupStepState.Pending))
            {
                using var stepTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                stepTimeout.CancelAfter(StepTimeout);
                Update(step.Id, "Setting up: " + step.Title, 0, step.SizeBytes);
                if (step.Id == MediaSetupPlanner.ComfyStepId) await InstallComfyAsync(step, stepTimeout.Token);
                else if (step.Id == MediaSetupPlanner.AssistantStepId) await DownloadAssistantAsync(step, stepTimeout.Token);
                else if (step.Id == MediaSetupPlanner.CheckpointStepIdFor(type)) await DownloadCheckpointAsync(step, type, stepTimeout.Token);
                else if (step.Id == MediaSetupPlanner.CompanionsStepIdFor(type)) await FetchCompanionsAsync(step, type, stepTimeout.Token);
            }

            MediaSetupPlan after = CurrentPlan(type);
            if (after.Ready) Finish("completed", Label(type) + " is set up.", null, null);
            else Finish("failed", "Setup finished but something is still missing.",
                string.Join(" ", after.Steps.Where(s => s.State != MediaSetupStepState.Done).Select(s => s.Title + ": " + s.Detail)), null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Finish("cancelled", "Media setup was cancelled. Downloads resume where they stopped.", null, null);
        }
        catch (OperationCanceledException)
        {
            Finish("failed", "A setup step did not finish within " + StepTimeout.TotalHours + " hours.", "Timed out.", null);
        }
        catch (MediaSetupException ex)
        {
            Finish("failed", ex.Message, ex.Message, ex.StepId);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Finish("failed", "Media setup failed: " + ex.Message, ex.Message, null);
        }
    }

    private async Task InstallComfyAsync(MediaSetupStep step, CancellationToken ct)
    {
        List<RuntimeBuildOption> available = await _runtime.ListAvailableAsync("media-comfyui", ct);
        RuntimeBuildOption? pick = available.FirstOrDefault(o => o.Recommended) ?? available.FirstOrDefault();
        if (pick is null) throw new MediaSetupException(step.Id, "No ComfyUI release could be listed. Check the internet connection and try again.");
        RuntimeInstallStatus job = _runtime.CreateInstall(new RuntimeInstallRequest { Backend = "media-comfyui", Tag = pick.Tag, Variant = pick.Variant });
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            RuntimeInstallStatus? now = _runtime.GetJob(job.Id);
            if (now is null) throw new MediaSetupException(step.Id, "The ComfyUI install job disappeared.");
            Update(step.Id, "Installing ComfyUI (" + now.Stage + ").", now.BytesDownloaded, now.TotalBytes);
            if (now.Stage == "completed") return;
            if (now.Stage == "failed" || now.Stage == "cancelled")
                throw new MediaSetupException(step.Id, "ComfyUI could not be installed: " + (now.Error ?? now.Stage));
            await Task.Delay(1500, ct);
        }
    }

    private async Task DownloadAssistantAsync(MediaSetupStep step, CancellationToken ct)
    {
        IReadOnlyList<ModelDownloadStatus> jobs = await _downloads.StartModelDownloadPlanAsync(
            step.RepoId!, "Q4_K_M", null, ct);
        await WaitForDownloadsAsync(step, jobs.Select(j => j.Id).ToList(), ct);
    }

    private async Task DownloadCheckpointAsync(MediaSetupStep step, string type, CancellationToken ct)
    {
        ModelDownloadStatus job = await _downloads.CreateDownloadAsync(new ModelDownloadRequest
        {
            RepoId = step.RepoId!, FileName = step.FileName!, DownloadUrl = step.DownloadUrl!,
            SizeBytes = step.SizeBytes, MediaCheckpointType = type
        }, ct);
        await WaitForDownloadsAsync(step, new List<string> { job.Id }, ct);

        string source = Path.Combine(ModelDownloadService.ModelsDirectory, step.FileName!);
        string destination = Path.Combine(LocalAiPaths.DiffusionModelsPath, step.FileName!);
        if (!File.Exists(destination))
        {
            if (!File.Exists(source)) throw new MediaSetupException(step.Id, step.FileName + " was not found after the download.");
            Directory.CreateDirectory(LocalAiPaths.DiffusionModelsPath);
            try { File.Move(source, destination); }
            catch (IOException) { File.Copy(source, destination, overwrite: false); File.Delete(source); }
        }
        LocalAiPaths.SetMediaCheckpointOverride(type, step.FileName!);
    }

    private async Task FetchCompanionsAsync(MediaSetupStep step, string type, CancellationToken ct)
    {
        var fetcher = new MediaCompanionFetcher(_downloads, Path.Combine(LocalAiPaths.ComfyApp, "models"));
        MediaCompanionPlan plan = MediaCompanionPlanReader.Build(type);
        if (plan.Missing.Count == 0) return;
        DateTime startedUtc = DateTime.UtcNow;
        using var progressStop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task progress = Task.Run(async () =>
        {
            // The fetch call blocks until every file is done, so report progress from the download jobs it created.
            while (!progressStop.IsCancellationRequested)
            {
                var jobs = _downloads.List().Where(j => j.CreatedUtc >= startedUtc.AddSeconds(-1)).ToList();
                Update(step.Id, "Downloading " + step.Title + ".", jobs.Sum(j => j.BytesDownloaded), jobs.Sum(j => j.TotalBytes));
                try { await Task.Delay(1500, progressStop.Token); } catch (OperationCanceledException) { }
            }
        }, CancellationToken.None);
        try
        {
            MediaCompanionFetchResult result = await fetcher.FetchAsync(plan,
                new MediaCompanionFetchRequest { Type = type, PlanHash = plan.PlanHash, Consent = true }, ct);
            if (!result.Ok) throw new MediaSetupException(step.Id, "The model's companion files could not be fetched: " + result.Message);
        }
        finally
        {
            progressStop.Cancel();
            await progress;
        }
    }

    private async Task WaitForDownloadsAsync(MediaSetupStep step, IReadOnlyList<string> ids, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            long done = 0, total = 0;
            bool allDone = true;
            foreach (string id in ids)
            {
                ModelDownloadStatus? job = _downloads.Get(id);
                if (job is null) throw new MediaSetupException(step.Id, "A download job disappeared.");
                if (job.Status is "failed" or "cancelled")
                    throw new MediaSetupException(step.Id, step.Title + " could not be downloaded: " + (job.Error ?? job.Status)
                        + (string.IsNullOrWhiteSpace(job.Remediation) ? string.Empty : " " + job.Remediation));
                done += job.BytesDownloaded;
                total += job.TotalBytes;
                if (job.Status != "completed") allDone = false;
            }
            Update(step.Id, "Downloading " + step.Title + ".", done, total);
            if (allDone) return;
            await Task.Delay(1500, ct);
        }
    }

    private void Update(string stepId, string message, long done, long total)
    {
        lock (_gate)
        {
            _last.State = "running";
            _last.CurrentStepId = stepId;
            _last.Message = message;
            _last.BytesDownloaded = done;
            _last.TotalBytes = total;
        }
    }

    private void Finish(string state, string message, string? error, string? stepId)
    {
        lock (_gate)
        {
            _last = new MediaSetupStatus { State = state, Message = message, Error = error, CurrentStepId = stepId };
        }
    }

    private static MediaSetupStepDto ToDto(MediaSetupStep s) => new()
    {
        Id = s.Id, Title = s.Title, State = s.State.ToString().ToLowerInvariant(), SizeBytes = s.SizeBytes, Detail = s.Detail
    };

    private static MediaSetupStatus Clone(MediaSetupStatus s) => new()
    {
        State = s.State, Ready = s.Ready, CurrentStepId = s.CurrentStepId, Message = s.Message, Error = s.Error,
        BytesDownloaded = s.BytesDownloaded, TotalBytes = s.TotalBytes, Steps = s.Steps.ToList()
    };

    private sealed class MediaSetupException : Exception
    {
        public string StepId { get; }
        public MediaSetupException(string stepId, string message) : base(message) { StepId = stepId; }
    }
}
