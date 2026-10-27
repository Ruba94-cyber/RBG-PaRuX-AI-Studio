using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using PaRuX.Contracts;

namespace PaRuX.Server;

public sealed class MediaJobService
{
    private const int MaximumVideoPromptCharacters = 32_000;
    internal const int MaximumRetainedTerminalJobs = 64;
    // Base64 expands binary data by roughly 4/3. Keeping this many encoded characters bounds the
    // retained terminal-job payload heap to about 96 MiB of raw media, plus at most one newest
    // oversized artifact so a just-finished job never disappears before its caller can fetch it.
    internal const long MaximumRetainedPayloadCharacters = 128L * 1024L * 1024L;
    // Restart recovery: a reattached prompt is polled at this interval for at most the reattach bound, and a
    // recovered output is read only up to the size limit below.
    internal static readonly TimeSpan ReattachPollInterval = TimeSpan.FromMilliseconds(500);
    internal static readonly TimeSpan DefaultReattachBound = TimeSpan.FromMinutes(30);
    // Assumption: 64 MiB bounds recovery memory while still allowing large image outputs; not hardware-validated.
    internal const long MaximumRecoveredImageBytes = 64L * 1024L * 1024L;
    internal const long MaximumRecoveredArtifactBytes = 512L * 1024L * 1024L;

    private sealed class Entry
    {
        public readonly object Gate = new();
        public readonly CancellationTokenSource Cancellation = new();
        public required string Owner { get; init; }
        public required MediaJobStatus Status { get; init; }
        // The job's intent and its ComfyUI client and prompt ids. The ids change only under Gate.
        public required MediaJobRecord Intent { get; init; }
    }

    private readonly ConcurrentDictionary<string, Entry> _jobs = new();
    private readonly MediaGenerationService _generator;
    private readonly Func<MediaJobRequest, IProgress<(string Stage, int Percent, int? Eta)>?, CancellationToken, Task<MediaAsset>>? _generateOverride;
    private readonly Func<MediaJobRequest, IProgress<(string Stage, int Percent, int? Eta)>?, CancellationToken, Task<MediaGenerationOutcome>>? _generateOutcomeOverride;
    // Null when no job store is configured (tests, and hosts that keep jobs in memory only).
    private readonly MediaJobStore? _store;
    private readonly TimeSpan _reattachPollInterval;
    private readonly TimeSpan _cancelGateWait;
    private readonly Func<string, byte[]>? _recoveredOutputReader;
    private readonly ILogger<MediaJobService>? _logger;
    // ComfyUI (and the GPU it shares with the text model) can only do one generation at a
    // time - a real crash was found via live testing: two jobs created back-to-back ran
    // concurrently (Create fires each via Task.Run with no queueing), and the VRAM contention
    // between one job's diffusion inference and the other's post-completion text-model reload
    // took ComfyUI's own process down entirely (ECONNREFUSED on its port on the next request).
    // This serializes actual generation while still accepting every job immediately - a queued
    // job just waits its turn here instead of racing onto the GPU.
    private readonly SemaphoreSlim _executionGate = new(1, 1);

    public MediaJobService(MediaGenerationService generator) : this(generator, null) { }

    internal MediaJobService(
        MediaGenerationService generator,
        MediaJobStore? store,
        Func<MediaJobRequest, IProgress<(string Stage, int Percent, int? Eta)>?, CancellationToken, Task<MediaAsset>>? generateOverride = null,
        TimeSpan? reattachPollInterval = null,
        Func<string, byte[]>? recoveredOutputReader = null,
        ILogger<MediaJobService>? logger = null,
        Func<MediaJobRequest, IProgress<(string Stage, int Percent, int? Eta)>?, CancellationToken, Task<MediaGenerationOutcome>>? generateOutcomeOverride = null,
        TimeSpan? cancelGateWait = null)
    {
        _generator = generator;
        _store = store;
        _generateOverride = generateOverride;
        _generateOutcomeOverride = generateOutcomeOverride;
        _reattachPollInterval = reattachPollInterval ?? ReattachPollInterval;
        _cancelGateWait = cancelGateWait ?? ComfyPromptCancellation.DefaultIdleBound;
        if (_cancelGateWait <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(cancelGateWait), "The cancel gate wait must be positive.");
        _recoveredOutputReader = recoveredOutputReader;
        _logger = logger;
        if (_reattachPollInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(reattachPollInterval), "The reattach poll interval must be positive.");
    }

    public MediaJobStatus Create(MediaJobRequest request, string owner = "desktop")
    {
        string type = request.Type.Trim().ToLowerInvariant();
        if (type is not ("image" or "video" or "edit" or "img2img" or "inpaint" or "upscale" or "custom" or "imported"))
            throw new InvalidDataException("Media type must be image, video, edit, img2img, inpaint, upscale, custom, or imported.");
        if (type is not ("upscale" or "custom" or "imported") && string.IsNullOrWhiteSpace(request.Prompt))
            throw new InvalidDataException("A media prompt is required.");
        if (type == "video" && (request.Prompt?.Trim().Length ?? 0) > MaximumVideoPromptCharacters)
            throw new InvalidDataException("A video prompt cannot exceed 32000 characters.");
        if ((type == "edit" || type == "img2img" || type == "inpaint" || type == "upscale") && string.IsNullOrWhiteSpace(request.SourceImageBase64))
            throw new InvalidDataException(type + " requires sourceImageBase64.");
        // The mask pair and the LoRA stack are checked now, so a bad request fails here and not on the GPU queue.
        if (type == "inpaint")
            MediaInpaintMask.Validate(MediaStartImageValidator.DecodePayload(request.SourceImageBase64), request.MaskImageBase64);
        if (request.Loras is { Count: > 0 })
        {
            if (type is not ("image" or "inpaint"))
                throw new InvalidDataException("LoRAs can only be used with image and inpaint jobs.");
            if (type == "image") MediaLoraStack.ResolveOrRefuse(MediaCheckpointResolution.Resolve("image"), request.Loras);
            else if (MediaLoraStack.Validate(request.Loras, LoraLibrary.List(), null, null) is { Ok: false } bad)
                throw new InvalidDataException(bad.Reason); // structure only: the family is checked against the Z-Image model when the job runs
        }
        if (type is "custom" or "imported" && string.IsNullOrWhiteSpace(request.CustomWorkflowId))
            throw new InvalidDataException(type + " requires customWorkflowId.");
        // An optional video start image is checked now, against the size the job will render at, so a bad
        // reference fails the request instead of failing later on the GPU queue.
        if (type == "video" && !string.IsNullOrWhiteSpace(request.SourceImageBase64))
            MediaStartImageValidator.ValidateForVideo(
                MediaStartImageValidator.DecodePayload(request.SourceImageBase64),
                MediaGenerationService.NormalizeDimension(request.Width, 1280),
                MediaGenerationService.NormalizeDimension(request.Height, 720));

        DateTime now = DateTime.UtcNow;
        string id = Guid.NewGuid().ToString("N");
        var entry = new Entry
        {
            Owner = owner,
            Status = new MediaJobStatus
            {
                Id = id, Type = type,
                CreatedUtc = now, UpdatedUtc = now
            },
            Intent = IntentOf(id, owner, type, request, now)
        };
        _jobs[id] = entry;
        PruneTerminalEntries();
        Persist();
        _ = Task.Run(() => RunAsync(entry, request));
        return Snapshot(entry, includePayload: true);
    }

    // PaRuX is single-user: every paired device belongs to the same person as the
    // desktop, so the job queue is shared rather than partitioned per client. Owner
    // is still recorded on each entry (useful for diagnostics/attribution later),
    // it just no longer gates visibility or cancellation.
    //
    // IMPORTANT: list/poll surfaces intentionally omit Base64Data. Android polls this endpoint every
    // three seconds and a completed video can be tens of megabytes; returning every historical
    // payload on every poll made bandwidth, JSON allocations and managed heap grow with history.
    // Callers that need the actual asset use Get(id) or /api/v1/media/{id} on demand.
    public IReadOnlyList<MediaJobStatus> List() => _jobs.Values
        .Select(entry => Snapshot(entry, includePayload: false))
        .OrderByDescending(x => x.CreatedUtc)
        .ToList();

    public MediaJobStatus? Get(string id) =>
        _jobs.TryGetValue(id, out Entry? entry) ? Snapshot(entry, includePayload: true) : null;

    public bool Cancel(string id)
    {
        if (!_jobs.TryGetValue(id, out Entry? entry)) return false;
        return CancelEntry(entry);
    }

    private bool CancelEntry(Entry entry)
    {
        lock (entry.Gate)
        {
            if (entry.Status.Status is "completed" or "failed" or "cancelled") return false;
            entry.Status.Status = "cancelling";
            entry.Status.Stage = entry.Intent.ClientIds.Count > 0 || entry.Intent.PromptIds.Count > 0
                ? "Stop pending confirmation"
                : "Cancelling";
            entry.Status.UpdatedUtc = DateTime.UtcNow;
        }
        entry.Cancellation.Cancel();
        Persist();
        return true;
    }

    public void CancelAllForGaming()
    {
        foreach (Entry entry in _jobs.Values) CancelEntry(entry);
    }

    private async Task RunAsync(Entry entry, MediaJobRequest request)
    {
        Update(entry, "queued", "Waiting for another generation to finish", 0, null);
        try
        {
            await _executionGate.WaitAsync(entry.Cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            Update(entry, "cancelled", "Cancelled", 0, null);
            PruneTerminalEntries();
            return;
        }

        string reservationOwner = "media-job:" + entry.Status.Id;
        try
        {
            long reservedBytes = MediaVramReservationEstimator.EstimateBytes(request);
            GpuResidencyReservationStore.Set(
                reservationOwner,
                "media",
                reservedBytes,
                entry.Status.Type + " generation",
                ttl: TimeSpan.FromHours(2));

            Update(entry, "running", "Starting local media runtime", 2, null);
            var progress = new Progress<(string Stage, int Percent, int? Eta)>(value =>
                Update(entry, "running", value.Stage, value.Percent, value.Eta));
            // Every ComfyUI submission this job makes is recorded: the client id before its POST, the prompt id after it.
            SetJobObservers(entry);
            MediaGenerationOutcome? outcome = null;
            MediaAsset? asset = null;
            if (_generateOutcomeOverride != null)
                outcome = await _generateOutcomeOverride(request, progress, entry.Cancellation.Token).ConfigureAwait(false);
            else if (_generateOverride != null)
                asset = await _generateOverride(request, progress, entry.Cancellation.Token).ConfigureAwait(false);
            else
                outcome = await _generator.GenerateForJobAsync(request, progress, entry.Cancellation.Token).ConfigureAwait(false);

            if (outcome is { Succeeded: false })
            {
                lock (entry.Gate)
                {
                    entry.Status.Status = "failed";
                    entry.Status.Stage = "Failed";
                    entry.Status.Error = outcome.FailureMessage;
                    entry.Status.ErrorCode = outcome.ErrorCode;
                    entry.Status.ErrorDetail = outcome.ErrorDetail;
                    entry.Status.Provenance = CloneProvenance(outcome.Provenance);
                    entry.Status.EtaSeconds = null;
                    entry.Status.UpdatedUtc = DateTime.UtcNow;
                }
                Persist();
                return;
            }

            if (outcome != null) asset = outcome.Asset;
            if (asset == null) throw new InvalidOperationException("The media engine returned no result.");
            lock (entry.Gate)
            {
                entry.Status.Status = "completed";
                entry.Status.Stage = "Completed";
                entry.Status.ProgressPercent = 100;
                entry.Status.EtaSeconds = 0;
                entry.Status.Asset = asset;
                entry.Status.Provenance = outcome == null ? null : CloneProvenance(outcome.Provenance);
                entry.Status.UpdatedUtc = DateTime.UtcNow;
            }
            Persist();
        }
        catch (OperationCanceledException ex)
        {
            // Record whether ComfyUI confirmed the stop before the execution and GPU admission leases are released.
            ComfyCancellationException? cancellation = ex as ComfyCancellationException;
            Update(entry, "cancelled", "Cancelled", entry.Status.ProgressPercent, null,
                cancellation == null ? null : StopNotice(cancellation.Report));
        }
        catch (Exception ex)
        {
            lock (entry.Gate)
            {
                entry.Status.Status = "failed";
                entry.Status.Stage = "Failed";
                entry.Status.Error = ex.Message;
                // Carry the typed reason (missing file, node or value) to the client, not just its text.
                entry.Status.ErrorDetail = (ex as MediaDependencyException)?.Error;
                entry.Status.EtaSeconds = null;
                entry.Status.UpdatedUtc = DateTime.UtcNow;
            }
            Persist();
        }
        finally
        {
            ClearJobObservers();
            GpuResidencyReservationStore.Release(reservationOwner);
            _executionGate.Release();
            PruneTerminalEntries();
        }
    }

    private void Update(Entry entry, string status, string stage, int percent, int? eta, string? error = null)
    {
        bool changed;
        lock (entry.Gate)
        {
            changed = entry.Status.Status != status || entry.Status.Stage != stage;
            entry.Status.Status = status;
            entry.Status.Stage = stage;
            entry.Status.ProgressPercent = Math.Clamp(percent, 0, 100);
            entry.Status.EtaSeconds = eta;
            if (error != null) entry.Status.Error = error;
            entry.Status.UpdatedUtc = DateTime.UtcNow;
        }
        // Progress ticks only move the percentage, so the job file is written on status and stage changes instead.
        if (changed) Persist();
    }

    private void PruneTerminalEntries()
    {
        // Snapshot first; never hold one entry's lock while mutating the concurrent dictionary.
        List<(string Id, DateTime UpdatedUtc, long PayloadCharacters)> terminal = new();
        foreach ((string id, Entry entry) in _jobs)
        {
            lock (entry.Gate)
            {
                if (!IsTerminal(entry.Status.Status)) continue;
                terminal.Add((
                    id,
                    entry.Status.UpdatedUtc,
                    entry.Status.Asset?.Base64Data?.Length ?? 0));
            }
        }

        HashSet<string> keep = SelectTerminalJobsToKeep(terminal);
        foreach ((string id, _, _) in terminal)
        {
            if (keep.Contains(id)) continue;
            if (_jobs.TryRemove(id, out Entry? removed))
            {
                try { removed.Cancellation.Dispose(); } catch { }
            }
        }
    }

    internal static HashSet<string> SelectTerminalJobsToKeep(
        IEnumerable<(string Id, DateTime UpdatedUtc, long PayloadCharacters)> jobs)
    {
        var keep = new HashSet<string>(StringComparer.Ordinal);
        long retainedCharacters = 0;
        int retainedJobs = 0;

        foreach ((string id, _, long payloadCharacters) in jobs
            .OrderByDescending(item => item.UpdatedUtc))
        {
            long boundedPayload = Math.Max(0, payloadCharacters);
            // Always keep the newest terminal result, even if that single artifact is larger than
            // the normal aggregate budget: the creator must get one fair chance to fetch it.
            bool retain = retainedJobs == 0
                || (retainedJobs < MaximumRetainedTerminalJobs
                    && retainedCharacters <= MaximumRetainedPayloadCharacters - boundedPayload);
            if (!retain) continue;

            keep.Add(id);
            retainedJobs++;
            retainedCharacters = boundedPayload > long.MaxValue - retainedCharacters
                ? long.MaxValue
                : retainedCharacters + boundedPayload;
        }
        return keep;
    }

    private static bool IsTerminal(string? status) =>
        status is "completed" or "failed" or "cancelled";

    private static MediaJobStatus Snapshot(Entry entry, bool includePayload)
    {
        lock (entry.Gate) return SnapshotStatus(entry.Status, includePayload);
    }

    internal static MediaJobStatus SnapshotStatus(MediaJobStatus s, bool includePayload)
    {
        MediaAsset? asset = s.Asset == null ? null : new MediaAsset
        {
            FileName = s.Asset.FileName,
            MediaType = s.Asset.MediaType,
            Base64Data = includePayload ? s.Asset.Base64Data : string.Empty,
            FilePath = s.Asset.FilePath,
            Seed = s.Asset.Seed,
            Width = s.Asset.Width,
            Height = s.Asset.Height,
            DurationSeconds = s.Asset.DurationSeconds,
            Fps = s.Asset.Fps,
            Steps = s.Asset.Steps
        };

        return new MediaJobStatus
        {
            Id = s.Id,
            Type = s.Type,
            Status = s.Status,
            Stage = s.Stage,
            ProgressPercent = s.ProgressPercent,
            EtaSeconds = s.EtaSeconds,
            Error = s.Error,
            ErrorCode = s.ErrorCode,
            ErrorDetail = s.ErrorDetail,
            Provenance = CloneProvenance(s.Provenance),
            CreatedUtc = s.CreatedUtc,
            UpdatedUtc = s.UpdatedUtc,
            Asset = asset
        };
    }

    // ---- Durable job state and restart recovery (MediaJobStore, ComfyPromptRecovery) ----

    // What was asked, without the source image: that is needed to start a job, never to recover one.
    private static MediaJobRecord IntentOf(string id, string owner, string type, MediaJobRequest request, DateTime now) => new()
    {
        Id = id,
        Type = type,
        Owner = owner,
        Status = "queued",
        Stage = "Queued",
        CreatedUtc = now,
        UpdatedUtc = now,
        Prompt = type == "video" ? (request.Prompt ?? string.Empty).Trim() : Clip(request.Prompt ?? string.Empty, 2000),
        CustomWorkflowId = request.CustomWorkflowId,
        HasSourceImage = !string.IsNullOrWhiteSpace(request.SourceImageBase64),
        Width = MediaGenerationService.NormalizeDimension(request.Width, type == "video" ? 1280 : 1024),
        Height = MediaGenerationService.NormalizeDimension(request.Height, type == "video" ? 720 : 1024),
        DurationSeconds = MediaGenerationService.NormalizeDuration(request.DurationSeconds),
        Fps = Math.Clamp(request.Fps <= 0 ? 24 : request.Fps, 8, 60),
        Steps = Math.Clamp(request.Steps <= 0 ? 30 : request.Steps, 8, 60),
        Seed = request.Seed
    };

    private static string Clip(string value, int maximum) => value.Length <= maximum ? value : value[..maximum];

    private static MediaJobRecord ToRecord(Entry entry)
    {
        lock (entry.Gate)
        {
            MediaJobRecord intent = entry.Intent;
            return new MediaJobRecord
            {
                Id = entry.Status.Id,
                Type = entry.Status.Type,
                Owner = entry.Owner,
                Status = entry.Status.Status,
                Stage = entry.Status.Stage,
                Error = entry.Status.Error,
                ErrorCode = entry.Status.ErrorCode,
                Provenance = CloneProvenance(entry.Status.Provenance),
                AssetFilePath = entry.Status.Asset?.FilePath,
                CreatedUtc = entry.Status.CreatedUtc,
                UpdatedUtc = entry.Status.UpdatedUtc,
                Prompt = intent.Prompt,
                CustomWorkflowId = intent.CustomWorkflowId,
                SaveNodeId = intent.SaveNodeId,
                HasSourceImage = intent.HasSourceImage,
                Width = intent.Width,
                Height = intent.Height,
                DurationSeconds = intent.DurationSeconds,
                Fps = intent.Fps,
                Steps = intent.Steps,
                Seed = intent.Seed,
                VideoClipSeeds = new List<long>(intent.VideoClipSeeds),
                ClientIds = new List<string>(intent.ClientIds),
                PromptIds = new List<string>(intent.PromptIds),
                VideoFamilyId = intent.VideoFamilyId,
                VideoRunId = intent.VideoRunId,
                VideoPrompt = intent.VideoPrompt,
                VideoRequestedFrames = intent.VideoRequestedFrames,
                VideoFinalPath = intent.VideoFinalPath,
                VideoClips = intent.VideoClips.Select(CloneVideoClip).ToList()
            };
        }
    }

    private static Entry EntryFromRecord(MediaJobRecord record) => new()
    {
        Owner = record.Owner,
        Status = new MediaJobStatus
        {
            Id = record.Id,
            Type = record.Type,
            Status = record.Status,
            Stage = record.Stage,
            Error = record.Error,
            ErrorCode = record.ErrorCode,
            Provenance = CloneProvenance(record.Provenance),
            Asset = record.AssetFilePath == null ? null : OutputReference(record.AssetFilePath, record),
            CreatedUtc = record.CreatedUtc,
            UpdatedUtc = record.UpdatedUtc
        },
        Intent = new MediaJobRecord
        {
            Id = record.Id,
            Type = record.Type,
            Owner = record.Owner,
            Prompt = record.Prompt,
            CustomWorkflowId = record.CustomWorkflowId,
            SaveNodeId = record.SaveNodeId,
            HasSourceImage = record.HasSourceImage,
            Width = record.Width,
            Height = record.Height,
            DurationSeconds = record.DurationSeconds,
            Fps = record.Fps,
            Steps = record.Steps,
            Seed = record.Seed,
            VideoClipSeeds = record.VideoClipSeeds == null ? new List<long>() : new List<long>(record.VideoClipSeeds),
            ClientIds = new List<string>(record.ClientIds),
            PromptIds = new List<string>(record.PromptIds),
            VideoFamilyId = record.VideoFamilyId,
            VideoRunId = record.VideoRunId,
            VideoPrompt = record.VideoPrompt,
            VideoRequestedFrames = record.VideoRequestedFrames,
            VideoFinalPath = record.VideoFinalPath,
            VideoClips = record.VideoClips.Select(CloneVideoClip).ToList()
        }
    };

    private static MediaVideoClipRecord CloneVideoClip(MediaVideoClipRecord clip) => new()
    {
        FrameCount = clip.FrameCount,
        ClientId = clip.ClientId,
        PromptId = clip.PromptId,
        OutputPath = clip.OutputPath
    };

    // Status changes are saved best-effort: a failed write must not stop a job that is already running, and the next
    // save writes the whole set again, so the file catches up once the disk recovers. Submissions are stricter (below).
    private void Persist()
    {
        if (_store == null) return;
        try
        {
            PersistNow();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Intentionally not rethrown; see the comment above.
        }
    }

    private void PersistNow()
    {
        if (_store == null) return;
        _store.Save(() => _jobs.Values.Select(ToRecord).ToList());
    }

    // Called before each ComfyUI POST and after it (ComfyWorkflowRunner.SubmissionObserver). It throws when the record cannot
    // be written, so a submission PaRuX could not record is never sent, and a failed one is settled by the runner.
    private void RecordSubmission(Entry entry, string clientId, string? promptId, string saveNodeId)
    {
        lock (entry.Gate)
        {
            if (!entry.Intent.ClientIds.Contains(clientId)) entry.Intent.ClientIds.Add(clientId);
            if (promptId != null && !entry.Intent.PromptIds.Contains(promptId)) entry.Intent.PromptIds.Add(promptId);
            entry.Intent.SaveNodeId = saveNodeId;
        }
        PersistNow();
    }

    // Called before the first video clip is posted, so the job file contains the exact live prompt and every clip seed.
    private void RecordVideoPlan(Entry entry, string prompt, IReadOnlyList<long> clipSeeds)
    {
        if (clipSeeds.Count == 0)
            throw new InvalidDataException("A video plan must include at least one clip seed.");

        List<long> persistedSeeds = clipSeeds.ToList();
        lock (entry.Gate)
        {
            entry.Intent.Prompt = prompt;
            entry.Intent.Seed = persistedSeeds[0];
            entry.Intent.VideoClipSeeds = persistedSeeds;
        }

        PersistNow();
    }

    private void RecordVideoClipPlan(Entry entry, string runId, string prompt, int requestedFrames, IReadOnlyList<int> frameCounts)
    {
        if (entry.Status.Type != "video" || !Guid.TryParseExact(runId, "N", out _)
            || requestedFrames < 1 || frameCounts.Count is < 1 or > MediaJobStore.MaximumVideoClips
            || frameCounts.Any(count => count < 1) || entry.Intent.VideoClipSeeds.Count != frameCounts.Count)
            throw new InvalidDataException("The video clip plan is invalid and was not submitted.");

        lock (entry.Gate)
        {
            entry.Intent.VideoRunId = runId;
            entry.Intent.VideoPrompt = prompt;
            entry.Intent.VideoRequestedFrames = requestedFrames;
            entry.Intent.VideoFinalPath = null;
            entry.Intent.VideoClips = frameCounts.Select(count => new MediaVideoClipRecord { FrameCount = count }).ToList();
        }
        PersistNow();
    }

    private void RecordVideoClip(Entry entry, int clipIndex, string? clientId, string? promptId, string? outputPath)
    {
        lock (entry.Gate)
        {
            if (clipIndex < 0 || clipIndex >= entry.Intent.VideoClips.Count)
                throw new InvalidDataException("ComfyUI reported a clip outside the saved video plan.");
            MediaVideoClipRecord clip = entry.Intent.VideoClips[clipIndex];
            if (clientId != null) clip.ClientId = clientId;
            if (promptId != null) clip.PromptId = promptId;
            if (outputPath != null) clip.OutputPath = outputPath;
            // Keep the general recovery lookup ids in the same durable snapshot as their clip association.
            if (clientId != null && !entry.Intent.ClientIds.Contains(clientId)) entry.Intent.ClientIds.Add(clientId);
            if (promptId != null && !entry.Intent.PromptIds.Contains(promptId)) entry.Intent.PromptIds.Add(promptId);
        }
        PersistNow();
    }

    private void RecordVideoFinalOutput(Entry entry, string path)
    {
        lock (entry.Gate) entry.Intent.VideoFinalPath = path;
        PersistNow();
    }

    private void SetJobObservers(Entry entry)
    {
        ComfyWorkflowRunner.SubmissionObserver.Value = (clientId, promptId, saveNodeId) =>
            RecordSubmission(entry, clientId, promptId, saveNodeId);
        ComfyWorkflowRunner.VideoPlanObserver.Value = (prompt, seeds) => RecordVideoPlan(entry, prompt, seeds);
        ComfyWorkflowRunner.VideoClipPlanObserver.Value = (runId, prompt, requestedFrames, frameCounts) =>
            RecordVideoClipPlan(entry, runId, prompt, requestedFrames, frameCounts);
        ComfyWorkflowRunner.VideoFamilyObserver.Value = familyId => { lock (entry.Gate) entry.Intent.VideoFamilyId = familyId; };
        ComfyWorkflowRunner.VideoClipObserver.Value = (index, clientId, promptId, outputPath) =>
            RecordVideoClip(entry, index, clientId, promptId, outputPath);
        ComfyWorkflowRunner.VideoFinalOutputObserver.Value = path => RecordVideoFinalOutput(entry, path);
    }

    private static void ClearJobObservers()
    {
        ComfyWorkflowRunner.SubmissionObserver.Value = null;
        ComfyWorkflowRunner.VideoPlanObserver.Value = null;
        ComfyWorkflowRunner.VideoClipPlanObserver.Value = null;
        ComfyWorkflowRunner.VideoFamilyObserver.Value = null;
        ComfyWorkflowRunner.VideoClipObserver.Value = null;
        ComfyWorkflowRunner.VideoFinalOutputObserver.Value = null;
    }

    // Startup recovery, run once when Core starts. Jobs that had finished before the restart are not restored, because
    // their media existed only in memory. In-flight image prompts are reattached or collected; videos with a saved clip plan
    // resume the remaining clips and the same finalizer. Other jobs are failed with their last known stage, and their
    // prompts are stopped so the GPU is not left busy for nobody.
    public async Task RecoverAsync(ComfyWorkflowRunner comfy, CancellationToken ct, TimeSpan? reattachBound = null)
    {
        ArgumentNullException.ThrowIfNull(comfy);
        if (_store == null) return;

        var inFlight = new List<Entry>();
        foreach (MediaJobRecord record in _store.Load())
        {
            bool terminal = IsTerminal(record.Status);
            // Keep terminal path-backed results available across restarts without probing ComfyUI again.
            if (terminal && string.IsNullOrWhiteSpace(record.AssetFilePath)) continue;
            Entry entry = EntryFromRecord(record);
            _jobs[entry.Status.Id] = entry;
            if (!terminal) inFlight.Add(entry);
        }

        foreach (Entry entry in inFlight)
        {
            ct.ThrowIfCancellationRequested();
            await DecideInterruptedAsync(entry, comfy, reattachBound ?? DefaultReattachBound).ConfigureAwait(false);
        }
        // The next save writes in-flight and path-backed terminal jobs, dropping other previous terminal records.
        Persist();
    }

    private async Task DecideInterruptedAsync(Entry entry, ComfyWorkflowRunner comfy, TimeSpan reattachBound)
    {
        string lastStage = StageOf(entry);
        if (entry.Status.Type == "video" && !string.IsNullOrWhiteSpace(entry.Intent.VideoFinalPath)
            && File.Exists(entry.Intent.VideoFinalPath))
        {
            try { Complete(entry, CollectVideoOutput(entry, comfy.ValidateRecoveredVideoFinalPath(entry.Intent.VideoFinalPath))); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                LogRecoveryFailure(entry, "collecting the finished video output", ex);
                FailInterrupted(entry, lastStage,
                    "The finished media output could not be collected after restart.",
                    "media_recovery_output_failed");
            }
            return;
        }
        ComfyPromptRecovery.Probe probe = await comfy
            .ProbeRecoveryAsync(entry.Intent.ClientIds, entry.Intent.PromptIds, CancellationToken.None, entry.Intent.SaveNodeId)
            .ConfigureAwait(false);
        bool collectable = CanCollect(entry);

        switch (probe.State)
        {
            case ComfyPromptRecovery.State.Running when collectable:
                int runningClipIndex = entry.Status.Type == "video"
                    ? FindVideoClipIndex(entry.Intent, probe.PromptId, probe.ClientId)
                    : -1;
                if (entry.Status.Type == "video" && (runningClipIndex < 0 || probe.PromptId == null))
                {
                    FailInterrupted(entry, lastStage, "The running ComfyUI prompt could not be matched to a saved video clip.");
                    return;
                }
                Update(entry, "running", "Reattached to a ComfyUI prompt that is still running", entry.Status.ProgressPercent, null);
                _ = Task.Run(() => ReattachAsync(entry, comfy, probe.PromptId!, runningClipIndex, reattachBound));
                return;
            case ComfyPromptRecovery.State.Finished when collectable:
                try
                {
                    if (entry.Status.Type == "video")
                    {
                        // The remaining clips run like a live run: under the execution gate and the job's own cancel token,
                        // so a user cancel or the gaming hard-off stops them instead of letting them finish.
                        await _executionGate.WaitAsync(entry.Cancellation.Token).ConfigureAwait(false);
                        try
                        {
                            Complete(entry, await CompleteRecoveredVideoAsync(entry, comfy, probe.PromptId, probe.OutputPath, -1, entry.Cancellation.Token).ConfigureAwait(false));
                        }
                        finally
                        {
                            _executionGate.Release();
                        }
                    }
                    else
                        Complete(entry, CollectOutput(entry, probe.OutputPath));
                }
                catch (OperationCanceledException ex) when (entry.Cancellation.IsCancellationRequested)
                {
                    // A user cancel (or hard-off) during a resumed clip ends this job only; recovery of other jobs continues.
                    Update(entry, "cancelled", "Cancelled", entry.Status.ProgressPercent, null,
                        ex is ComfyCancellationException cancellation ? StopNotice(cancellation.Report) : null);
                }
                catch (RecoveredImageTooLargeException)
                {
                    FailInterrupted(entry, lastStage,
                        "The recovered image exceeds the 64 MiB recovery limit and remains available by file path.",
                        "output_too_large_for_recovery",
                        OutputReference(probe.OutputPath!, entry.Intent));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or HttpRequestException)
                {
                    LogRecoveryFailure(entry, "collecting the finished output", ex);
                    FailInterrupted(entry, lastStage,
                        "The finished media output could not be collected after restart.",
                        "media_recovery_output_failed");
                }
                return;
            case ComfyPromptRecovery.State.Finished when entry.Status.Type == "video":
                if (probe.OutputPath == null || !File.Exists(probe.OutputPath))
                {
                    FailInterrupted(entry, lastStage,
                        "ComfyUI finished a video prompt, but its output file is not available after restart.");
                    return;
                }
                Complete(entry, OutputReference(probe.OutputPath, entry.Intent));
                return;
            case ComfyPromptRecovery.State.Running:
                ComfyCancelReport stop = await comfy
                    .SettleForCancelAsync(entry.Intent.ClientIds, entry.Intent.PromptIds)
                    .ConfigureAwait(false);
                FailInterrupted(entry, lastStage,
                    "ComfyUI was still running a prompt for this job, which PaRuX cannot collect after a restart. "
                    + (stop.Notice ?? "It was stopped."));
                return;
            case ComfyPromptRecovery.State.Finished:
                FailInterrupted(entry, lastStage, "ComfyUI finished a prompt for this job, but this job type cannot be collected after a restart.");
                return;
            case ComfyPromptRecovery.State.Failed:
                FailInterrupted(entry, lastStage, probe.Detail ?? "ComfyUI reported an error.");
                return;
            case ComfyPromptRecovery.State.Unreachable:
                FailInterrupted(entry, lastStage,
                    "ComfyUI could not be checked after the restart (" + probe.Detail + "), so its prompt may still be running.");
                return;
            default:
                FailInterrupted(entry, lastStage, "ComfyUI no longer holds a prompt for this job.");
                return;
        }
    }

    // Waits for a prompt that was still running when Core restarted, then collects its output as a normal run would. It holds
    // the execution gate while ComfyUI can be checked, but yields it between retries when ComfyUI is unreachable.
    private async Task ReattachAsync(Entry entry, ComfyWorkflowRunner comfy, string promptId, int clipIndex, TimeSpan bound)
    {
        bool gateHeld = false;
        try
        {
            await _executionGate.WaitAsync(entry.Cancellation.Token).ConfigureAwait(false);
            gateHeld = true;
            DateTime deadline = DateTime.UtcNow + bound;
            while (true)
            {
                ComfyPromptRecovery.Probe probe = await comfy
                    .ProbeRecoveryAsync(entry.Intent.ClientIds, new[] { promptId }, entry.Cancellation.Token, entry.Intent.SaveNodeId)
                    .ConfigureAwait(false);
                if (probe.State == ComfyPromptRecovery.State.Finished)
                {
                    if (entry.Status.Type == "video")
                        Complete(entry, await CompleteRecoveredVideoAsync(entry, comfy, promptId, probe.OutputPath, clipIndex, entry.Cancellation.Token).ConfigureAwait(false));
                    else
                    {
                        try
                        {
                            Complete(entry, CollectOutput(entry, probe.OutputPath));
                        }
                        catch (RecoveredImageTooLargeException)
                        {
                            Fail(entry,
                                "The recovered image exceeds the 64 MiB recovery limit and remains available by file path.",
                                "output_too_large_for_recovery", OutputReference(probe.OutputPath!, entry.Intent));
                        }
                    }
                    return;
                }
                if (probe.State is ComfyPromptRecovery.State.Failed or ComfyPromptRecovery.State.Gone)
                {
                    Fail(entry, probe.Detail ?? "ComfyUI no longer holds the prompt.");
                    return;
                }
                if (DateTime.UtcNow >= deadline)
                {
                    ComfyCancelReport stop = await comfy
                        .SettleForCancelAsync(entry.Intent.ClientIds, new[] { promptId })
                        .ConfigureAwait(false);
                    Fail(entry, "The reattached ComfyUI prompt was still running after " + (int)bound.TotalSeconds + " s. "
                        + (StopNotice(stop) ?? "It was stopped."));
                    return;
                }
                if (probe.State == ComfyPromptRecovery.State.Unreachable)
                {
                    _executionGate.Release();
                    gateHeld = false;
                    await Task.Delay(_reattachPollInterval, entry.Cancellation.Token).ConfigureAwait(false);
                    await _executionGate.WaitAsync(entry.Cancellation.Token).ConfigureAwait(false);
                    gateHeld = true;
                    continue;
                }
                await Task.Delay(_reattachPollInterval, entry.Cancellation.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            if (!gateHeld)
                gateHeld = await _executionGate.WaitAsync(_cancelGateWait).ConfigureAwait(false);
            ComfyCancelReport stop = await comfy
                .SettleForCancelAsync(entry.Intent.ClientIds, entry.Intent.PromptIds)
                .ConfigureAwait(false);
            string? notice = StopNotice(stop);
            // A stop that ran without the gate says so, so it is not read as held against new starts.
            if (!gateHeld && !stop.Idle) notice = "The media GPU gate stayed busy with another job, so this stop ran without holding it. " + notice;
            Update(entry, "cancelled", "Cancelled", entry.Status.ProgressPercent, null, notice);
        }
        catch (Exception ex)
        {
            // Any failure leaves the job failed and visible, never stuck as "running" with nothing waiting for it.
            // The raw exception goes to the local log only; the job status carries a fixed message and a stable code.
            LogRecoveryFailure(entry, "reattaching the ComfyUI prompt", ex);
            Fail(entry, "The reattached media job could not be collected after restart.", "media_recovery_failed");
        }
        finally
        {
            if (gateHeld) _executionGate.Release();
        }
    }

    private static string? StopNotice(ComfyCancelReport report)
    {
        if (report.Idle) return report.Notice;
        return "ComfyUI stop unsettled after the bounded confirmation wait. "
            + (report.Notice ?? "ComfyUI did not confirm that the prompt stopped.");
    }

    // Single-prompt jobs with a saved output node are collectible directly; video also requires its persisted clip plan.
    private static bool CanCollect(Entry entry) => entry.Status.Type is "image" or "edit" or "img2img" or "inpaint" or "upscale"
        || (entry.Status.Type is "custom" or "imported" && !string.IsNullOrWhiteSpace(entry.Intent.CustomWorkflowId)
            && !string.IsNullOrWhiteSpace(entry.Intent.SaveNodeId))
        || (entry.Status.Type == "video" && HasVideoPlan(entry.Intent));

    private static bool HasVideoPlan(MediaJobRecord record) =>
        Guid.TryParseExact(record.VideoRunId, "N", out _)
        && !string.IsNullOrWhiteSpace(record.VideoPrompt)
        && record.VideoRequestedFrames > 0
        && record.VideoClips.Count is > 0 and <= MediaJobStore.MaximumVideoClips
        && record.VideoClips.All(clip => clip.FrameCount > 0);

    private static int FindVideoClipIndex(MediaJobRecord record, string? promptId, string? clientId)
    {
        for (int i = 0; i < record.VideoClips.Count; i++)
        {
            MediaVideoClipRecord clip = record.VideoClips[i];
            if (promptId != null && string.Equals(clip.PromptId, promptId, StringComparison.Ordinal)) return i;
            if (clientId != null && string.Equals(clip.ClientId, clientId, StringComparison.Ordinal)) return i;
        }
        return -1;
    }

    private async Task<MediaAsset> CompleteRecoveredVideoAsync(Entry entry, ComfyWorkflowRunner comfy,
        string? promptId, string? outputPath, int knownClipIndex, CancellationToken ct)
    {
        if (!HasVideoPlan(entry.Intent)) throw new InvalidDataException("The saved video clip plan is incomplete.");
        int clipIndex = knownClipIndex >= 0 ? knownClipIndex : FindVideoClipIndex(entry.Intent, promptId, null);
        if (clipIndex < 0 || promptId == null || outputPath == null)
            throw new InvalidDataException("The completed ComfyUI prompt could not be matched to a saved video clip.");

        SetJobObservers(entry);
        try
        {
            string finalPath = await comfy.ResumeVideoAfterRestartAsync(entry.Intent, clipIndex, promptId, outputPath, ct)
                .ConfigureAwait(false);
            return CollectVideoOutput(entry, finalPath);
        }
        finally
        {
            ClearJobObservers();
        }
    }

    // Reads a finished ComfyUI image the way a normal run does, with the size limit applied. Throws when that is not possible.
    private sealed class RecoveredImageTooLargeException : IOException
    {
        public RecoveredImageTooLargeException(string path)
            : base("The finished ComfyUI image is larger than PaRuX collects after a restart.") { }
    }

    private MediaAsset CollectOutput(Entry entry, string? outputPath)
    {
        if (outputPath == null || !File.Exists(outputPath))
            throw new FileNotFoundException("The finished ComfyUI output is not on disk.", outputPath);
        if (new FileInfo(outputPath).Length > MaximumRecoveredImageBytes)
            throw new RecoveredImageTooLargeException(outputPath);
        byte[] bytes = _recoveredOutputReader?.Invoke(outputPath) ?? ReadRecoveredImage(outputPath);
        (int width, int height) = HeaderSize(bytes, entry.Intent);
        return new MediaAsset
        {
            FileName = Path.GetFileName(outputPath),
            MediaType = MediaGenerationService.MediaTypeFor(outputPath),
            Base64Data = Convert.ToBase64String(bytes),
            FilePath = null,
            // A seed the request did not fix was chosen inside the original run and is not kept, so it is reported as 0.
            Seed = Math.Max(0, entry.Intent.Seed),
            Width = width,
            Height = height
        };
    }

    private static byte[] ReadRecoveredImage(string outputPath)
    {
        using var stream = new FileStream(outputPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > MaximumRecoveredImageBytes)
            throw new RecoveredImageTooLargeException(outputPath);

        var bytes = new byte[(int)stream.Length];
        stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1)
            throw new RecoveredImageTooLargeException(outputPath);
        return bytes;
    }

    private static MediaAsset OutputReference(string outputPath, MediaJobRecord intent) => new()
    {
        FileName = Path.GetFileName(outputPath),
        MediaType = MediaGenerationService.MediaTypeFor(outputPath),
        Base64Data = string.Empty,
        FilePath = outputPath,
        Seed = Math.Max(0, intent.Seed),
        Width = intent.Width,
        Height = intent.Height,
        DurationSeconds = intent.DurationSeconds,
        Fps = intent.Fps,
        Steps = intent.Steps
    };

    private static MediaAsset CollectVideoOutput(Entry entry, string outputPath)
    {
        if (!File.Exists(outputPath)) throw new FileNotFoundException("The finished ComfyUI video output is not on disk.", outputPath);
        // A video above the inline limit is kept on disk and referenced by path, so recovery never reads it into memory.
        if (new FileInfo(outputPath).Length > MaximumRecoveredArtifactBytes)
            return OutputReference(outputPath, entry.Intent);
        byte[] bytes = File.ReadAllBytes(outputPath);
        return new MediaAsset
        {
            FileName = Path.GetFileName(outputPath),
            MediaType = MediaGenerationService.MediaTypeFor(outputPath),
            Base64Data = Convert.ToBase64String(bytes),
            Seed = Math.Max(0, entry.Intent.Seed),
            Width = entry.Intent.Width,
            Height = entry.Intent.Height,
            DurationSeconds = entry.Intent.DurationSeconds,
            Fps = entry.Intent.Fps,
            Steps = entry.Intent.Steps
        };
    }

    private static MediaJobProvenance? CloneProvenance(MediaJobProvenance? provenance) =>
        provenance == null ? null : MediaJobProvenanceFactory.Sanitize(provenance);

    // The pixel size from the image header when it can be read, otherwise the size the job was asked for.
    private static (int Width, int Height) HeaderSize(byte[] bytes, MediaJobRecord intent)
    {
        try
        {
            MediaStartImageValidator.ImageInfo info = MediaStartImageValidator.Inspect(bytes);
            return (info.Width, info.Height);
        }
        catch (InvalidDataException)
        {
            return (intent.Width, intent.Height);
        }
    }

    private void Complete(Entry entry, MediaAsset asset)
    {
        lock (entry.Gate)
        {
            entry.Status.Status = "completed";
            entry.Status.Stage = "Completed";
            entry.Status.ProgressPercent = 100;
            entry.Status.EtaSeconds = 0;
            entry.Status.Error = null;
            entry.Status.ErrorCode = null;
            entry.Status.Asset = asset;
            entry.Status.UpdatedUtc = DateTime.UtcNow;
        }
        Persist();
    }

    private void Fail(Entry entry, string error, string? errorCode = null, MediaAsset? asset = null)
    {
        lock (entry.Gate)
        {
            entry.Status.Status = "failed";
            entry.Status.Stage = "Failed";
            entry.Status.Error = error;
            entry.Status.ErrorCode = errorCode;
            entry.Status.Asset = asset;
            entry.Status.EtaSeconds = null;
            entry.Status.UpdatedUtc = DateTime.UtcNow;
        }
        Persist();
    }

    private void FailInterrupted(Entry entry, string lastStage, string detail, string? errorCode = null, MediaAsset? asset = null)
    {
        string stage = string.IsNullOrWhiteSpace(lastStage) ? "unknown" : lastStage;
        lock (entry.Gate)
        {
            entry.Status.Status = "failed";
            entry.Status.Stage = "Interrupted by restart";
            entry.Status.Error = errorCode == null
                ? "PaRuX restarted before this job finished (last stage: " + stage + "). " + detail
                : detail;
            entry.Status.ErrorCode = errorCode;
            entry.Status.Asset = asset;
            entry.Status.EtaSeconds = null;
            entry.Status.UpdatedUtc = DateTime.UtcNow;
        }
        Persist();
    }

    private void LogRecoveryFailure(Entry entry, string operation, Exception exception) =>
        _logger?.LogError(exception, "Media job {Operation} failed during recovery for job {JobId}", operation, entry.Status.Id);

    private static string StageOf(Entry entry)
    {
        lock (entry.Gate) return entry.Status.Stage;
    }
}
