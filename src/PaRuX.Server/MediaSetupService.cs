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
    private readonly SpeechService? _speech;
    private readonly ThreedService? _threed;
    private readonly CutoutService? _cutout;
    private (DateTime AtUtc, bool Runtime, bool Model)? _cutoutStatus;
    private (DateTime AtUtc, SpeechEngineStatus Status)? _speechStatus;
    private (DateTime AtUtc, bool Installed)? _threedStatus;
    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private Task? _run;
    private MediaSetupStatus _last = new();
    private string _lastType = "image";

    public MediaSetupService(RuntimeInstallService runtime, ModelDownloadService downloads, SpeechService? speech = null,
        ThreedService? threed = null, CutoutService? cutout = null)
    {
        _cutout = cutout;
        _runtime = runtime;
        _downloads = downloads;
        _speech = speech;
        _threed = threed;
    }

    private bool ReadThreedInstalled(bool fresh = false)
    {
        if (_threed is null) return false;
        lock (_gate)
        {
            if (!fresh && _threedStatus is { } cached && DateTime.UtcNow - cached.AtUtc < TimeSpan.FromSeconds(20)) return cached.Installed;
        }
        bool installed = _threed.GetStatusAsync(CancellationToken.None).GetAwaiter().GetResult().Installed;
        lock (_gate) _threedStatus = (DateTime.UtcNow, installed);
        return installed;
    }

    private (bool Runtime, bool Model) ReadCutoutFacts(bool fresh = false)
    {
        if (_cutout is null) return (false, false);
        lock (_gate)
        {
            if (!fresh && _cutoutStatus is { } cached && DateTime.UtcNow - cached.AtUtc < TimeSpan.FromSeconds(20)) return (cached.Runtime, cached.Model);
        }
        CutoutEngineStatus status = _cutout.GetStatusAsync(CancellationToken.None).GetAwaiter().GetResult();
        lock (_gate) _cutoutStatus = (DateTime.UtcNow, status.RuntimeInstalled, status.ModelInstalled);
        return (status.RuntimeInstalled, status.ModelInstalled);
    }

    /// <summary>The Python probe costs about a second, so polling reads a short-lived cache.</summary>
    private SpeechEngineStatus ReadSpeechStatus(bool fresh = false)
    {
        if (_speech is null) return new SpeechEngineStatus();
        lock (_gate)
        {
            if (!fresh && _speechStatus is { } cached && DateTime.UtcNow - cached.AtUtc < TimeSpan.FromSeconds(20)) return cached.Status;
        }
        SpeechEngineStatus status = _speech.GetStatusAsync(CancellationToken.None).GetAwaiter().GetResult();
        lock (_gate) _speechStatus = (DateTime.UtcNow, status);
        return status;
    }

    private MediaSetupPlan PlanFor(string type, bool freshSpeech = false)
    {
        if (type == "3d") return MediaSetupPlanner.PlanThreed(ReadThreedInstalled(freshSpeech));
        if (type == "cutout")
        {
            (bool runtime, bool model) = ReadCutoutFacts(freshSpeech);
            return MediaSetupPlanner.PlanCutout(runtime, model);
        }
        if (type == "music")
            return MediaSetupPlanner.PlanMusic(
                File.Exists(LocalAiPaths.ComfyMain) && File.Exists(LocalAiPaths.ComfyPython),
                File.Exists(MusicService.CheckpointPath));
        if (type != "speech") return CurrentPlan(type);
        SpeechEngineStatus engine = ReadSpeechStatus(freshSpeech);
        return MediaSetupPlanner.PlanSpeech(engine.SttInstalled, engine.TtsInstalled);
    }

    internal static MediaSetupFacts ReadFacts(string type = "image")
    {
        bool comfy = File.Exists(LocalAiPaths.ComfyMain) && File.Exists(LocalAiPaths.ComfyPython);
        bool assistant = File.Exists(LocalAiPaths.SmallModel) || File.Exists(LocalAiPaths.SmallUncensoredModel);
        HashSet<string> checkpoints = MediaCatalogueLive.InstalledFiles();
        long? missingCompanions = null;
        if (comfy && checkpoints.Count > 0)
        {
            try { missingCompanions = MediaSetupFacts.CompanionBytes(MediaCompanionPlanReader.Build(type)); }
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

    internal static string NormalizeType(string? type) => type is "video" or "edit" or "speech" or "3d" or "music" or "cutout" ? type : "image";

    public MediaSetupStatus GetStatus(string? type = null)
    {
        type = NormalizeType(type);
        MediaSetupPlan plan = PlanFor(type);
        lock (_gate)
        {
            // A run belongs to one media type; other types report only what is installed.
            MediaSetupStatus status = _lastType == type || _last.State == "idle" ? Clone(_last) : new MediaSetupStatus();
            if (_lastType != type && _run is { IsCompleted: false })
            {
                status.State = "waiting";
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
        "speech" => "Speech",
        "3d" => "Image to 3D",
        "music" => "Music",
        "cutout" => "Background removal",
        _ => "Image generation"
    };

    public MediaSetupStatus Start(string? type = null)
    {
        type = NormalizeType(type);
        lock (_gate)
        {
            if (_run is { IsCompleted: false })
                return _lastType == type
                    ? GetStatusUnlocked()
                    : new MediaSetupStatus { State = "waiting", Message = "Another media setup (" + _lastType + ") is running; wait for it to finish." };
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

    /// <summary>Cancels the running setup, but only when it belongs to <paramref name="type"/>: another mode's card must not stop it.</summary>
    public void Cancel(string? type = null)
    {
        lock (_gate)
        {
            if (_lastType == NormalizeType(type) && _run is { IsCompleted: false }) _cts?.Cancel();
        }
    }

    private MediaSetupStatus GetStatusUnlocked() => Clone(_last);

    private async Task RunAsync(string type, CancellationToken ct)
    {
        try
        {
            MediaSetupPlan plan = PlanFor(type, freshSpeech: true);
            foreach (MediaSetupStep step in plan.Steps)
            {
                if (step.State == MediaSetupStepState.Blocked) { Finish("blocked", step.Detail, step.Detail, step.Id); return; }
            }
            foreach (MediaSetupStep step in plan.Steps.Where(s => s.State == MediaSetupStepState.Pending))
            {
                using var stepTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                stepTimeout.CancelAfter(StepTimeout);
                Update(step.Id, "Setting up: " + step.Title, 0, step.SizeBytes);
                try
                {
                    if (step.Id == MediaSetupPlanner.CutoutRuntimeStepId) await InstallCutoutRuntimeAsync(step, stepTimeout.Token);
                    else if (step.Id == MediaSetupPlanner.CutoutModelStepId) await InstallCutoutModelAsync(step, stepTimeout.Token);
                    else if (step.Id == MediaSetupPlanner.MusicStepId) await InstallMusicModelAsync(step, stepTimeout.Token);
                    else if (step.Id == MediaSetupPlanner.ThreedStepId) await InstallThreedAsync(step, stepTimeout.Token);
                    else if (step.Id == MediaSetupPlanner.SpeechStepId) await InstallSpeechAsync(step, stepTimeout.Token);
                    else if (step.Id == MediaSetupPlanner.ComfyStepId) await InstallComfyAsync(step, stepTimeout.Token);
                    else if (step.Id == MediaSetupPlanner.AssistantStepId) await DownloadAssistantAsync(step, stepTimeout.Token);
                    else if (step.Id == MediaSetupPlanner.CheckpointStepIdFor(type)) await DownloadCheckpointAsync(step, type, stepTimeout.Token);
                    else if (step.Id == MediaSetupPlanner.CompanionsStepIdFor(type)) await FetchCompanionsAsync(step, type, stepTimeout.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // Not the user: either the step budget ran out or a network request timed out. Say which.
                    throw new MediaSetupException(step.Id, stepTimeout.IsCancellationRequested
                        ? step.Title + " did not finish within " + StepTimeout.TotalHours + " hours."
                        : "A network request timed out while setting up " + step.Title + ". Check the connection and try again; downloads resume.");
                }
            }

            MediaSetupPlan after = PlanFor(type, freshSpeech: true);
            if (after.Ready) Finish("completed", Label(type) + " is set up.", null, null);
            else Finish("failed", "Setup finished but something is still missing.",
                string.Join(" ", after.Steps.Where(s => s.State != MediaSetupStepState.Done).Select(s => s.Title + ": " + s.Detail)), null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Finish("cancelled", "Media setup was cancelled. Downloads resume where they stopped.", null, null);
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

    private async Task InstallMusicModelAsync(MediaSetupStep step, CancellationToken ct)
    {
        string source = Path.Combine(ModelDownloadService.ModelsDirectory, MusicWorkflow.CheckpointFileName);
        string destination = MusicService.CheckpointPath;
        if (!File.Exists(source) || new FileInfo(source).Length != MusicWorkflow.CheckpointBytes)
        {
            var item = new MediaCompanionItem
            {
                Role = "music", Folder = "checkpoints", FileName = MusicWorkflow.CheckpointFileName, Fetchable = true,
                Repository = MusicWorkflow.Repository, RepositoryPath = MusicWorkflow.RepositoryPath, Revision = MusicWorkflow.Revision,
                PinnedSizeBytes = MusicWorkflow.CheckpointBytes, Sha256 = MusicWorkflow.CheckpointSha256
            };
            ModelDownloadStatus job = await _downloads.CreateCompanionDownloadAsync(item, ct);
            await WaitForDownloadsAsync(step, new List<string> { job.Id }, ct);
        }
        try { MediaCheckpointPlacement.MoveIntoPlace(source, destination, MusicWorkflow.CheckpointBytes); }
        catch (IOException ex) { throw new MediaSetupException(step.Id, step.Title + " could not be installed: " + ex.Message); }
    }

    private async Task InstallCutoutRuntimeAsync(MediaSetupStep step, CancellationToken ct)
    {
        if (_cutout is null) throw new MediaSetupException(step.Id, "Background removal is not available in this build.");
        Update(step.Id, "Installing the background removal engine (onnxruntime, Pillow, NumPy); this takes a few minutes.", 0, 0);
        try { await _cutout.InstallRuntimeAsync(ct); }
        catch (InvalidOperationException ex) { throw new MediaSetupException(step.Id, "The background removal engine could not be installed: " + ex.Message); }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or TimeoutException)
        {
            throw new MediaSetupException(step.Id, "The background removal engine could not be installed: " + ex.Message);
        }
    }

    private async Task InstallCutoutModelAsync(MediaSetupStep step, CancellationToken ct)
    {
        if (_cutout is null) throw new MediaSetupException(step.Id, "Background removal is not available in this build.");
        var progress = new Progress<(long Done, long Total)>(p => Update(step.Id, "Downloading " + step.Title + ".", p.Done, p.Total));
        try { await _cutout.DownloadModelAsync(progress, ct); }
        catch (Exception ex) when (ex is InvalidDataException or IOException or HttpRequestException or TimeoutException)
        {
            throw new MediaSetupException(step.Id, step.Title + " could not be downloaded: " + ex.Message);
        }
    }

    private async Task InstallThreedAsync(MediaSetupStep step, CancellationToken ct)
    {
        if (_threed is null) throw new MediaSetupException(step.Id, "The 3D engine is not available in this build.");
        Update(step.Id, "Installing the image-to-3D engine (PyTorch and mesh tools); this takes several minutes.", 0, 0);
        try { await _threed.InstallEngineAsync(ct); }
        catch (InvalidOperationException ex) { throw new MediaSetupException(step.Id, "The 3D engine could not be installed: " + ex.Message); }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or TimeoutException)
        {
            throw new MediaSetupException(step.Id, "The 3D engine could not be installed: " + ex.Message);
        }
    }

    private async Task InstallSpeechAsync(MediaSetupStep step, CancellationToken ct)
    {
        if (_speech is null) throw new MediaSetupException(step.Id, "The speech engine is not available in this build.");
        Update(step.Id, "Installing the speech engine (PyTorch and speech packages); this takes several minutes.", 0, 0);
        try { await _speech.InstallEngineAsync(ct); }
        catch (InvalidOperationException ex) { throw new MediaSetupException(step.Id, "The speech engine could not be installed: " + ex.Message); }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or TimeoutException)
        {
            throw new MediaSetupException(step.Id, "The speech engine could not be installed: " + ex.Message);
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
            if (ct.IsCancellationRequested) { _runtime.CancelJob(job.Id); ct.ThrowIfCancellationRequested(); }
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
        if (step.Sha256 is null) throw new MediaSetupException(step.Id, step.Title + " has no pinned checksum, so it is not downloaded.");
        string source = Path.Combine(ModelDownloadService.ModelsDirectory, step.FileName!);
        // A previous run may have finished the download but not the move; do not download the same file twice.
        if (!File.Exists(source) || new FileInfo(source).Length != step.SizeBytes)
        {
            ModelDownloadStatus job = await _downloads.CreateMediaCheckpointDownloadAsync(new ModelDownloadRequest
            {
                RepoId = step.RepoId!, Revision = step.Revision, FileName = step.FileName!, DownloadUrl = step.DownloadUrl!,
                SizeBytes = step.SizeBytes, MediaCheckpointType = type
            }, step.Sha256, ct);
            await WaitForDownloadsAsync(step, new List<string> { job.Id }, ct);
        }
        // The download's own header decides the folder (a single-file checkpoint goes to checkpoints, the rest to diffusion_models).
        // When the staged copy is already gone (an earlier run moved it), the installed file keeps its place.
        string destination = File.Exists(source)
            ? Path.Combine(MediaCheckpointResolution.DestinationFolderFor(source), step.FileName!)
            : LocalAiPaths.MainModelPath(step.FileName!);
        try { MediaCheckpointPlacement.MoveIntoPlace(source, destination, step.SizeBytes); }
        catch (IOException ex) { throw new MediaSetupException(step.Id, step.Title + " could not be installed: " + ex.Message); }
        LocalAiPaths.SetMediaCheckpointOverride(type, step.FileName!);
    }

    private async Task FetchCompanionsAsync(MediaSetupStep step, string type, CancellationToken ct)
    {
        var fetcher = new MediaCompanionFetcher(_downloads, Path.Combine(LocalAiPaths.ComfyApp, "models"));
        MediaCompanionPlan plan = MediaCompanionPlanReader.Build(type);
        if (plan.Support != "supported")
            throw new MediaSetupException(step.Id, "The installed " + type + " model is not a family PaRuX can run: " + (plan.Reason ?? plan.Support) + ".");
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
            if (ct.IsCancellationRequested)
            {
                foreach (string id in ids) _downloads.Cancel(id);
                ct.ThrowIfCancellationRequested();
            }
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
        Id = s.Id, Title = s.Title, State = s.State.ToString().ToLowerInvariant(), SizeBytes = s.SizeBytes, Detail = s.Detail, License = s.License
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
