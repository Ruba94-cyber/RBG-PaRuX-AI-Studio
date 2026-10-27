using System.Diagnostics;
using System.Net.WebSockets;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>
/// Native replacement for the external Generate-Media.ps1 script: builds a ComfyUI workflow from
/// the JSON templates in LocalAiPaths.ConfigDir, submits it over ComfyUI's real HTTP API
/// (/prompt, /history, /upload/image), and returns the finished file - no PowerShell dependency.
/// The workflow templates themselves are unchanged (still plain ComfyUI "API format" JSON), so
/// this is a faithful in-process port of the same real, working pipeline, not a redesign.
/// </summary>
public sealed class ComfyWorkflowRunner
{
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private static readonly Uri DefaultComfyUiBaseAddress = new("http://127.0.0.1:8188/");
    private readonly Uri _comfyUiBaseAddress;
    private readonly LocalAiProcessManager _runtime;
    private readonly ChatProxyService _chat;
    private readonly CustomWorkflowService _customWorkflows;
    private readonly ComfyWorkflowImportService? _imported;
    private readonly ConcurrentDictionary<string, string> _refinedPromptCache = new(StringComparer.Ordinal);
    private CancellationTokenSource? _pendingTextRestore;
    private static readonly TimeSpan MediaBurstIdleDelay = TimeSpan.FromSeconds(45);

    public ComfyWorkflowRunner(LocalAiProcessManager runtime, ChatProxyService chat, CustomWorkflowService customWorkflows,
        ComfyWorkflowImportService? importedWorkflows = null)
        : this(runtime, chat, customWorkflows, DefaultComfyUiBaseAddress)
    {
        _imported = importedWorkflows;
    }

    internal ComfyWorkflowRunner(Uri comfyUiBaseAddress)
        : this(null!, null!, null!, comfyUiBaseAddress)
    {
    }

    private ComfyWorkflowRunner(LocalAiProcessManager runtime, ChatProxyService chat, CustomWorkflowService customWorkflows, Uri comfyUiBaseAddress)
    {
        _runtime = runtime;
        _chat = chat;
        _customWorkflows = customWorkflows;
        _comfyUiBaseAddress = NormalizeComfyUiBaseAddress(comfyUiBaseAddress);
        MediaBaseUrl = _comfyUiBaseAddress.GetLeftPart(UriPartial.Authority);
    }

    public sealed record MediaResult(string Path, long Seed, int Width, int Height, double DurationSeconds, int Fps, int Steps);

    /// <summary>Turns the user's raw chat message into a proper image/video-generation prompt
    /// using the small media-assistant model StartMediaAssistantAsync already loads for exactly
    /// this purpose ("a small Vulkan LLM used to help shape media prompts") - which nothing was
    /// actually calling, so a conversational request (filler words, negations, non-English text)
    /// went straight to the diffusion model's text encoder unchanged. Best-effort: any failure
    /// (model not ready yet, empty/malformed response) falls back to the original raw prompt
    /// rather than blocking generation on a refinement step that isn't essential.</summary>
    private async Task<string> RefinePromptAsync(string rawPrompt, CancellationToken ct)
    {
        string cacheKey = rawPrompt.Trim();
        if (_refinedPromptCache.TryGetValue(cacheKey, out string? cached)) return cached;

        try
        {
            string instruction =
                "Rewrite the following request as a single, concise image-generation prompt in English. "
                + "Describe the subject, setting, style, lighting and other important visual details "
                + "directly and positively - for example turn \"not a real dragon, a bearded dragon\" into "
                + "\"a bearded dragon lizard\", never a negation a diffusion model can't parse. "
                + "Output ONLY the prompt text on a single line: no preamble, no quotes, no explanation.\n\n"
                + "Request: " + rawPrompt;
            string refined = await _chat.CompleteOnceAsync(instruction, preferLowLatency: true, cancellationToken: ct);
            refined = FirstLine(refined).Trim().Trim('"', '\'');
            string result = refined.Length > 0 ? refined : rawPrompt;
            // Regenerate and seed-variation flows commonly reuse the exact request. Prompt
            // refinement is deterministic enough for that flow and costs several seconds even
            // with the small model, so never pay it twice during the same app session.
            if (_refinedPromptCache.Count >= 128) _refinedPromptCache.Clear();
            _refinedPromptCache.TryAdd(cacheKey, result);
            return result;
        }
        catch
        {
            return rawPrompt; // degrade to the raw prompt - never let refinement block generation
        }
    }

    private static string FirstLine(string text)
    {
        int newline = text.IndexOf('\n');
        return newline >= 0 ? text[..newline] : text;
    }

    /// <summary>Runs the silent refinement unless the user already reviewed this exact prompt, in which case it is used verbatim.</summary>
    internal static Task<string> RefineUnlessReviewedAsync(
        bool alreadyReviewed, string prompt, Func<string, CancellationToken, Task<string>> refine, CancellationToken ct) =>
        alreadyReviewed ? Task.FromResult(prompt) : refine(prompt, ct);

    public async Task<MediaResult> RunImageAsync(string prompt, long seed, int width, int height, CancellationToken ct, IProgress<int>? progressPercent = null, bool skipPromptRefinement = false)
    {
        await EnsureMediaReadyAsync(ct);
        try
        {
            string refinedPrompt = await RefineUnlessReviewedAsync(skipPromptRefinement, prompt, RefinePromptAsync, ct);
            // One resolution per job: it picks the checkpoint AND the graph of that checkpoint's family. Every image
            // template shares the node contract patched below (4 prompt, 8 sampler seed, 6 latent size, 10 save).
            MediaCheckpointEffective effective = MediaCheckpointResolution.ResolveForJob("image");
            string path = await SubmitAndWaitAsync(MediaCheckpointResolution.TemplatePathFor(effective), request =>
            {
                SetIn(request, "4", "text", refinedPrompt);
                SetIn(request, "8", "seed", seed);
                SetIn(request, "6", "width", width);
                SetIn(request, "6", "height", height);
                SetIn(request, "10", "filename_prefix", "LocalAI_images/AI_Studio");
                ApplyResolvedCheckpoint(request, effective);
            }, saveNode: "10", timeout: TimeSpan.FromMinutes(10), ct, progressPercent, mediaType: "image");
            return new MediaResult(path, seed, width, height, 0, 0, 0);
        }
        finally { TriggerBackgroundTextRestore(); }
    }

    public async Task<MediaResult> RunEditAsync(string instruction, long seed, byte[] sourceImage, string sourceFileName, CancellationToken ct)
    {
        await EnsureMediaReadyAsync(ct);
        try
        {
            string uploadedName = await UploadImageAsync(sourceImage, sourceFileName, ct);
            // One resolution per job picks the checkpoint and the graph of its family. Every edit template shares the node
            // contract patched below: 4 source image, 7 instruction, 13 the noise/sampler node that holds the seed, 19 save.
            MediaCheckpointEffective effective = MediaCheckpointResolution.ResolveForJob("edit");
            string path = await SubmitAndWaitAsync(MediaCheckpointResolution.TemplatePathFor(effective), request =>
            {
                SetIn(request, "4", "image", uploadedName);
                SetEditInstruction(request, instruction);
                SetEditSeed(request, seed);
                ApplyResolvedCheckpoint(request, effective);
            }, saveNode: "19", timeout: TimeSpan.FromMinutes(10), ct, mediaType: "edit");
            return new MediaResult(path, seed, 0, 0, 0, 0, 0);
        }
        finally { TriggerBackgroundTextRestore(); }
    }

    /// <summary>Classic strength-blended img2img: LoadImage → VAEEncode → KSampler(denoise=strength),
    /// distinct from RunEditAsync's Flux2/Kontext instruction-edit graph, which has no scalar
    /// denoise input at all (conditioning-only editing). <paramref name="strength"/> is ComfyUI's
    /// own "denoise" - how much the sampler is allowed to deviate from the source image's latent:
    /// near 0 stays close to the source, near 1 mostly ignores it and follows the prompt.</summary>
    public async Task<MediaResult> RunImg2ImgAsync(string prompt, long seed, byte[] sourceImage, string sourceFileName, double strength, CancellationToken ct)
    {
        await EnsureMediaReadyAsync(ct);
        try
        {
            string refinedPrompt = await RefinePromptAsync(prompt, ct);
            string uploadedName = await UploadImageAsync(sourceImage, sourceFileName, ct);
            string path = await SubmitAndWaitAsync(LocalAiPaths.Img2ImgWorkflowTemplate, request =>
            {
                SetIn(request, "6", "image", uploadedName);
                SetIn(request, "4", "text", refinedPrompt);
                SetIn(request, "9", "seed", seed);
                SetIn(request, "9", "denoise", Math.Clamp(strength, 0.05, 1.0));
                SetIn(request, "11", "filename_prefix", "LocalAI_images/AI_Studio_img2img");
                ApplyCheckpointOverrideIfSet(request, "image");
            }, saveNode: "11", timeout: TimeSpan.FromMinutes(10), ct, mediaType: "img2img");
            return new MediaResult(path, seed, 0, 0, 0, 0, 0);
        }
        finally { TriggerBackgroundTextRestore(); }
    }

    /// <summary>Runs a user-imported custom workflow (see CustomWorkflowService). Every override
    /// below is conditional on that workflow actually having a detected node for it - an
    /// undetected axis is simply left untouched, so the workflow's own baked-in value runs
    /// unchanged rather than failing or guessing at a node id.</summary>
    public async Task<MediaResult> RunCustomAsync(string workflowId, string prompt, long seed,
        byte[]? sourceImage, string? sourceFileName, CancellationToken ct, IProgress<int>? progressPercent = null)
    {
        await EnsureMediaReadyAsync(ct);
        try
        {
            CustomWorkflowService.RunnableWorkflow workflow = _customWorkflows.GetRunnable(workflowId);
            if (workflow.LoadImageNodeId != null && (sourceImage == null || sourceImage.Length == 0))
                throw new InvalidOperationException("This custom workflow needs a source image.");

            string? uploadedName = workflow.LoadImageNodeId != null && sourceImage != null
                ? await UploadImageAsync(sourceImage, sourceFileName ?? "source.png", ct) : null;
            string? refinedPrompt = workflow.PromptNodeId != null && !string.IsNullOrWhiteSpace(prompt)
                ? await RefinePromptAsync(prompt, ct) : null;

            string path = await SubmitAndWaitAsync(workflow.TemplatePath, request =>
            {
                if (workflow.PromptNodeId != null && refinedPrompt != null)
                    SetIn(request, workflow.PromptNodeId, "text", refinedPrompt);
                if (workflow.SeedNodeId != null && workflow.SeedField != null)
                    SetIn(request, workflow.SeedNodeId, workflow.SeedField, seed);
                if (workflow.LoadImageNodeId != null && uploadedName != null)
                    SetIn(request, workflow.LoadImageNodeId, "image", uploadedName);
            }, saveNode: workflow.SaveNodeId, timeout: TimeSpan.FromMinutes(10), ct, progressPercent);
            return new MediaResult(path, seed, 0, 0, 0, 0, 0);
        }
        finally { TriggerBackgroundTextRestore(); }
    }

    /// <summary>Runs a stored user template (see ComfyWorkflowImportService) with validated parameter overrides. It takes the
    /// same path as every other media type: the caller holds the GPU lease and Gaming gate, cancellation settles ComfyUI, and
    /// the submission is reported to the job observer so a restart can re-attach.</summary>
    public async Task<MediaResult> RunImportedAsync(string templateId, string prompt, long? seed,
        IReadOnlyDictionary<string, System.Text.Json.JsonElement>? overrides, byte[]? sourceImage, string? sourceFileName,
        CancellationToken ct, IProgress<int>? progressPercent = null)
    {
        await EnsureMediaReadyAsync(ct);
        try { return await RunImportedCoreAsync(templateId, prompt, seed, overrides, sourceImage, sourceFileName, ct, progressPercent); }
        finally { TriggerBackgroundTextRestore(); }
    }

    internal async Task<MediaResult> RunImportedCoreAsync(string templateId, string prompt, long? seed,
        IReadOnlyDictionary<string, System.Text.Json.JsonElement>? overrides, byte[]? sourceImage, string? sourceFileName,
        CancellationToken ct, IProgress<int>? progressPercent = null)
    {
        ComfyWorkflowImportService imported = _imported ?? throw new InvalidOperationException("Imported workflows are not available.");
        ImportedComfyTemplateInfo info = imported.Get(templateId) ?? throw new InvalidDataException("Unknown imported workflow.");
        var values = new Dictionary<string, System.Text.Json.Nodes.JsonNode?>(StringComparer.Ordinal);
        foreach (var (key, element) in overrides ?? new Dictionary<string, System.Text.Json.JsonElement>())
            values[key] = System.Text.Json.Nodes.JsonNode.Parse(element.GetRawText());
        // Convenience axes: only where the template exposes them and the caller did not set that exact key.
        ComfyWorkflowParameter? promptParam = info.Parameters.FirstOrDefault(p => p.Kind == "prompt");
        if (!string.IsNullOrWhiteSpace(prompt) && promptParam != null && !values.ContainsKey(promptParam.Key))
            values[promptParam.Key] = prompt;
        long usedSeed = seed ?? Random.Shared.NextInt64(1, long.MaxValue);
        foreach (ComfyWorkflowParameter p in info.Parameters.Where(p => p.Kind == "seed"))
            if (!values.ContainsKey(p.Key)) values[p.Key] = usedSeed;
        ComfyWorkflowParameter? imageParam = info.Parameters.FirstOrDefault(p => p.Kind == "source_image");
        if (imageParam != null)
        {
            if (sourceImage == null || sourceImage.Length == 0) throw new InvalidDataException("This workflow needs a source image.");
            values[imageParam.Key] = await UploadImageAsync(sourceImage, sourceFileName ?? "source.png", ct);
        }
        // Validated before anything is queued; a bad override fails here, not on the GPU queue.
        var run = imported.PrepareRun(templateId, values);
        string path = await SubmitAndWaitAsync(run.TemplatePath, run.Mutate, run.SaveNodeId,
            timeout: TimeSpan.FromMinutes(info.WorkflowType == "video" ? 30 : 10), ct, progressPercent);
        return new MediaResult(path, usedSeed, 0, 0, 0, 0, 0);
    }

    public async Task<MediaResult> RunUpscaleAsync(byte[] sourceImage, string sourceFileName, CancellationToken ct)
    {
        await EnsureMediaReadyAsync(ct);
        try
        {
            string uploadedName = await UploadImageAsync(sourceImage, sourceFileName, ct);
            string path = await SubmitAndWaitAsync(LocalAiPaths.UpscaleWorkflowTemplate, request =>
            {
                SetIn(request, "1", "image", uploadedName);
            }, saveNode: "4", timeout: TimeSpan.FromMinutes(5), ct);
            return new MediaResult(path, -1, 0, 0, 0, 0, 0);
        }
        finally { TriggerBackgroundTextRestore(); }
    }

    public async Task<MediaResult> RunVideoAsync(string prompt, long seed, int width, int height, double durationSeconds, int fps, int steps, CancellationToken ct, IProgress<int>? progressPercent = null, byte[]? startImage = null, bool skipPromptRefinement = false)
    {
        await EnsureMediaReadyAsync(ct);
        string frameTempDir = Path.Combine(MediaTempRoot, Guid.NewGuid().ToString("N"));
        var paths = new List<string>();
        string? resultPath = null;
        bool completed = false;
        string jobId = Guid.NewGuid().ToString("N");
        // Every ComfyUI submission of this job, so a cancel between clips can confirm nothing is still queued.
        var clientIds = new List<string>();
        VideoCheckpointStaging staging = VideoCheckpointStaging.None;
        try
        {
            string refinedPrompt = await RefineUnlessReviewedAsync(skipPromptRefinement, prompt, RefinePromptAsync, ct);
            // One resolution per job picks the checkpoint AND the video family of that checkpoint (its graph and node contract).
            MediaCheckpointEffective effective = MediaCheckpointResolution.ResolveForJob("video");
            VideoFamilyContract contract = VideoFamilyContracts.For(effective);
            RegistryFamily videoFamily = VideoFamilyContracts.RegistryFamilyOf(contract);
            (width, height) = contract.FitSize(width, height);
            int requestedFrames = Math.Max(1, (int)Math.Ceiling(durationSeconds * fps));
            int[] clipFrameCounts = contract.PlanClipFrameCounts(requestedFrames);
            (string videoPrompt, long[] clipSeeds) = BuildVideoPlanAndNotifyObserver(refinedPrompt, seed, clipFrameCounts.Length);
            VideoFamilyObserver.Value?.Invoke(contract.FamilyId);
            VideoClipPlanObserver.Value?.Invoke(jobId, videoPrompt, requestedFrames, clipFrameCounts);
            Directory.CreateDirectory(frameTempDir);
            staging = StageCheckpoint(contract, effective);
            // Optional user start image: uploaded once, then attached to clip one the same way clip chaining attaches
            // the previous clip's last frame (AddWanStartImage). Its format and size were checked before queueing.
            string? startImageName = startImage == null ? null
                : await UploadImageAsync(startImage, "parux-start-" + Guid.NewGuid().ToString("N") + MediaStartImageValidator.Inspect(startImage).Extension, ct);
            string? previousImageName = null;
            for (int i = 0; i < clipFrameCounts.Length; i++)
            {
                int clipIndex = i;
                IProgress<int>? clipProgress = progressPercent == null ? null
                    : new Progress<int>(clipRaw => progressPercent.Report(Math.Min(99, (clipIndex * 100 + clipRaw) / clipFrameCounts.Length)));
                // Clip one starts from the user's image when one was given. Later clips continue from the previous last frame.
                string? conditioningImage = i == 0 ? startImageName : previousImageName;
                var clipSettings = new VideoClipSettings(videoPrompt, clipSeeds[i], width, height, clipFrameCounts[i], steps, fps, jobId, i, conditioningImage);
                string clipPath = await SubmitAndWaitAsync(VideoTemplatePath(contract, videoFamily, conditioningImage != null), request =>
                {
                    contract.ApplyClip(request, clipSettings);
                    contract.ApplyCheckpoint(request, effective);
                }, saveNode: contract.OutputNode, timeout: TimeSpan.FromMinutes(25), ct, clipProgress,
                    (filename, subfolder) => ValidateOwnedWanClipOutput(filename, subfolder, jobId, i + 1),
                    submittedClientIds: clientIds, mediaType: "video", videoClipIndex: i);
                paths.Add(clipPath);
                VideoClipObserver.Value?.Invoke(i, null, null, clipPath);

                if (i + 1 < clipFrameCounts.Length)
                {
                    string framePath = Path.Combine(frameTempDir, "continuity-" + (i + 1) + ".png");
                    await ExtractLastVideoFrameAsync(clipPath, framePath, ct);
                    byte[] frameBytes = await File.ReadAllBytesAsync(framePath, ct);
                    previousImageName = await UploadImageAsync(
                        frameBytes, "parux-continuity-" + Guid.NewGuid().ToString("N") + ".png", ct);
                }
            }

            resultPath = await FinalizeVideoAsync(paths, clipFrameCounts, requestedFrames, fps, ct);

            completed = true;
            progressPercent?.Report(100);
            return new MediaResult(resultPath, seed, width, height, durationSeconds, fps, steps);
        }
        catch (OperationCanceledException ex) when (ct.IsCancellationRequested && clientIds.Count > 0 && ex is not ComfyCancellationException)
        {
            // Cancelled between clips, or during the frame extraction or upload that joins them. No clip prompt
            // of this job should remain queued or running: confirm that before the admission lease is released.
            ComfyCancelReport report = await SettleForCancelAsync(clientIds, Array.Empty<string>());
            foreach (string path in paths) TryDeleteFile(path);
            throw new ComfyCancellationException(report, ct);
        }
        catch
        {
            foreach (string path in paths) TryDeleteFile(path);
            if (resultPath != null && !paths.Contains(resultPath, StringComparer.OrdinalIgnoreCase)) TryDeleteFile(resultPath);
            throw;
        }
        finally
        {
            try { if (Directory.Exists(frameTempDir)) Directory.Delete(frameTempDir, recursive: true); } catch { }
            if (!completed && resultPath != null) TryDeleteFile(resultPath);
            staging.Dispose();
            TriggerBackgroundTextRestore();
        }
    }

    internal async Task<string> ResumeVideoAfterRestartAsync(MediaJobRecord intent, int completedClipIndex,
        string promptId, string outputPath, CancellationToken ct)
    {
        if (!Guid.TryParseExact(intent.VideoRunId, "N", out _) || string.IsNullOrWhiteSpace(intent.VideoPrompt)
            || intent.VideoRequestedFrames < 1 || intent.VideoClips.Count is < 1 or > MediaJobStore.MaximumVideoClips
            || completedClipIndex < 0 || completedClipIndex >= intent.VideoClips.Count)
            throw new InvalidDataException("The saved video clip plan is incomplete.");

        if (!string.IsNullOrWhiteSpace(intent.VideoFinalPath) && File.Exists(intent.VideoFinalPath))
            return ValidateRecoveredVideoFinalPath(intent.VideoFinalPath);

        MediaVideoClipRecord completedClip = intent.VideoClips[completedClipIndex];
        if (completedClip.PromptId != null && !string.Equals(completedClip.PromptId, promptId, StringComparison.Ordinal))
            throw new InvalidDataException("The reattached ComfyUI prompt does not match the saved video clip plan.");
        string ownedOutput = ValidateOwnedWanClipPath(outputPath, intent.VideoRunId!, completedClipIndex + 1);
        completedClip.PromptId = promptId;
        completedClip.OutputPath = ownedOutput;
        VideoClipObserver.Value?.Invoke(completedClipIndex, null, promptId, ownedOutput);

        var paths = new List<string>(intent.VideoClips.Count);
        for (int i = 0; i < intent.VideoClips.Count; i++)
        {
            string? saved = intent.VideoClips[i].OutputPath;
            if (string.IsNullOrWhiteSpace(saved))
            {
                if (i <= completedClipIndex)
                    throw new InvalidDataException("A previously generated video clip is missing from the saved job record.");
                break;
            }
            paths.Add(ValidateOwnedWanClipPath(saved, intent.VideoRunId!, i + 1));
        }

        string tempDir = Path.Combine(MediaTempRoot, Guid.NewGuid().ToString("N"));
        var clientIds = intent.VideoClips
            .Where(clip => !string.IsNullOrWhiteSpace(clip.ClientId))
            .Select(clip => clip.ClientId!)
            .ToList();
        VideoCheckpointStaging staging = VideoCheckpointStaging.None;
        try
        {
            Directory.CreateDirectory(tempDir);
            // The family the job started with decides the graph. A job saved before families existed was a Wan job.
            VideoFamilyContract contract = VideoFamilyContracts.ForFamilyId(intent.VideoFamilyId ?? VideoFamilyContracts.Wan.FamilyId)
                ?? throw new InvalidDataException("The saved video job names a video family this build does not know.");
            MediaCheckpointEffective? effective = null;
            RegistryFamily? videoFamily = null;
            if (paths.Count < intent.VideoClips.Count)
            {
                effective = MediaCheckpointResolution.ResolveForJob("video");
                if (!string.Equals(VideoFamilyContracts.For(effective).FamilyId, contract.FamilyId, StringComparison.Ordinal))
                    throw new InvalidOperationException("The active video checkpoint is no longer a " + contract.FamilyId
                        + " model, so the remaining clips of this job cannot continue from the saved ones.");
                videoFamily = VideoFamilyContracts.RegistryFamilyOf(contract);
                staging = StageCheckpoint(contract, effective);
            }
            for (int i = paths.Count; i < intent.VideoClips.Count; i++)
            {
                if (i == 0) throw new InvalidDataException("The first saved video clip is missing.");
                string framePath = Path.Combine(tempDir, "continuity-" + i.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".png");
                await ExtractLastVideoFrameAsync(paths[^1], framePath, ct).ConfigureAwait(false);
                byte[] frameBytes = await File.ReadAllBytesAsync(framePath, ct).ConfigureAwait(false);
                string conditioningImage = await UploadImageAsync(
                    frameBytes, "parux-continuity-" + Guid.NewGuid().ToString("N") + ".png", ct).ConfigureAwait(false);
                int clipIndex = i;
                IProgress<int>? clipProgress = null;
                (int clipWidth, int clipHeight) = contract.FitSize(intent.Width, intent.Height);
                var clipSettings = new VideoClipSettings(intent.VideoPrompt, ClipSeedFor(intent, i), clipWidth, clipHeight,
                    intent.VideoClips[i].FrameCount, intent.Steps, intent.Fps, intent.VideoRunId!, clipIndex, conditioningImage);
                string clipPath = await RunVideoClipAsync(contract, videoFamily!, effective!, clipSettings,
                    ct, clipProgress, clientIds).ConfigureAwait(false);
                intent.VideoClips[i].OutputPath = clipPath;
                paths.Add(clipPath);
                VideoClipObserver.Value?.Invoke(i, null, null, clipPath);
            }

            if (paths.Count != intent.VideoClips.Count)
                throw new InvalidDataException("The saved video clip plan is incomplete.");
            return await FinalizeVideoAsync(paths, intent.VideoClips.Select(clip => clip.FrameCount).ToArray(),
                intent.VideoRequestedFrames, intent.Fps, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (ct.IsCancellationRequested && clientIds.Count > 0 && ex is not ComfyCancellationException)
        {
            ComfyCancelReport report = await SettleForCancelAsync(clientIds, intent.PromptIds).ConfigureAwait(false);
            throw new ComfyCancellationException(report, ct);
        }
        finally
        {
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); } catch { }
            staging.Dispose();
        }
    }

    // Persisted seeds are authoritative after a restart; older jobs did not store a per-clip seed.
    // Never silently re-roll seeds: doing so changes the user's video across an interrupted run.
    internal static long ClipSeedFor(MediaJobRecord intent, int clipIndex)
    {
        ArgumentNullException.ThrowIfNull(intent);
        if (clipIndex < 0 || clipIndex >= intent.VideoClips.Count)
            throw new ArgumentOutOfRangeException(nameof(clipIndex));
        if (intent.VideoClipSeeds.Count == intent.VideoClips.Count)
            return intent.VideoClipSeeds[clipIndex];
        return checked(intent.Seed + clipIndex);
    }

    private async Task<string> RunVideoClipAsync(VideoFamilyContract contract, RegistryFamily family,
        MediaCheckpointEffective effective, VideoClipSettings clip, CancellationToken ct,
        IProgress<int>? progressPercent, List<string>? submittedClientIds)
    {
        return await SubmitAndWaitAsync(VideoTemplatePath(contract, family, clip.StartImageName != null), request =>
        {
            contract.ApplyClip(request, clip);
            contract.ApplyCheckpoint(request, effective);
        }, saveNode: contract.OutputNode, timeout: TimeSpan.FromMinutes(25), ct, progressPercent,
            (filename, subfolder) => ValidateOwnedWanClipOutput(filename, subfolder, clip.JobId, clip.ClipIndex + 1, MediaOutputRoot),
            submittedClientIds: submittedClientIds, mediaType: "video", videoClipIndex: clip.ClipIndex).ConfigureAwait(false);
    }

    private static string VideoTemplatePath(VideoFamilyContract contract, RegistryFamily family, bool hasStartImage) =>
        Path.Combine(LocalAiPaths.ConfigDir, contract.TemplateFileFor(family, hasStartImage));

    /// <summary>Exposes the job's checkpoint to ComfyUI's checkpoints folder when the family loads a single-file checkpoint.</summary>
    private static VideoCheckpointStaging StageCheckpoint(VideoFamilyContract contract, MediaCheckpointEffective effective) =>
        contract.StagesCheckpointForComfy
            ? VideoCheckpointStaging.Ensure(Path.Combine(LocalAiPaths.ComfyApp, "models"), effective.File)
            : VideoCheckpointStaging.None;

    private async Task<string> FinalizeVideoAsync(List<string> paths, IReadOnlyList<int> clipFrameCounts,
        int requestedFrames, int fps, CancellationToken ct)
    {
        int generatedUniqueFrames = clipFrameCounts.Sum() - Math.Max(0, clipFrameCounts.Count - 1);
        string resultPath = paths.Count == 1 && generatedUniqueFrames == requestedFrames
            ? paths[0]
            : await ConcatenateWithFfmpegAsync(paths, clipFrameCounts, requestedFrames, fps, ct).ConfigureAwait(false);
        try
        {
            VideoFinalOutputObserver.Value?.Invoke(resultPath);
        }
        catch
        {
            if (!paths.Contains(resultPath, StringComparer.OrdinalIgnoreCase)) TryDeleteFile(resultPath);
            throw;
        }
        if (!paths.Contains(resultPath, StringComparer.OrdinalIgnoreCase))
            foreach (string path in paths) TryDeleteFile(path);
        return resultPath;
    }

    /// <summary>Overrides node "1"'s UNETLoader.unet_name with the user's assigned checkpoint for
    /// this generation type, if one is set (see LocalAiPaths.SetMediaCheckpointOverride) -
    /// otherwise leaves the workflow template's own baked-in default untouched. Every image/video/
    /// edit template here uses that exact node id/class for its checkpoint, confirmed by reading
    /// each template directly rather than assumed.</summary>
    private static void ApplyCheckpointOverrideIfSet(JsonObject request, string type)
    {
        // Same resolution the status API reports (MediaCheckpointResolution.Resolve), so the graph and the UI agree.
        ApplyResolvedCheckpoint(request, MediaCheckpointResolution.ResolveForJob(type));
    }

    /// <summary>The instruction goes to node 7 under the input that node's class names it: <c>text</c> on FLUX.2's
    /// CLIPTextEncode, <c>prompt</c> on Qwen-Image-Edit's TextEncodeQwenImageEdit.</summary>
    internal static void SetEditInstruction(JsonObject request, string instruction) =>
        SetIn(request, "7", EditInstructionInput(request), instruction);

    internal static string EditInstructionInput(JsonObject request) =>
        request["prompt"]?["7"]?["inputs"] is JsonObject inputs && inputs.ContainsKey("prompt") ? "prompt" : "text";

    /// <summary>The seed lives in node 13 under the input that node's class names it: <c>noise_seed</c> on FLUX.2's
    /// RandomNoise, <c>seed</c> on Qwen-Image-Edit's KSampler.</summary>
    internal static void SetEditSeed(JsonObject request, long seed) =>
        SetIn(request, "13", EditSeedInput(request), seed);

    internal static string EditSeedInput(JsonObject request) =>
        request["prompt"]?["13"]?["inputs"] is JsonObject inputs && inputs.ContainsKey("seed") ? "seed" : "noise_seed";

    /// <summary>Same patch as <see cref="ApplyCheckpointOverrideIfSet"/> for a job that already resolved its checkpoint, so
    /// the template it submitted and the file it loads come from one decision.</summary>
    private static void ApplyResolvedCheckpoint(JsonObject request, MediaCheckpointEffective effective)
    {
        if (effective.IsOverridden) SetIn(request, MediaCheckpointResolution.UnetNode, "unet_name", effective.File);
    }

    internal static string? ModelSelectionReasonForProvenance(MediaJobRequest request)
    {
        string type = (request.Type ?? string.Empty).Trim().ToLowerInvariant();
        return MediaCheckpointResolution.SelectionReasonFor(type == "img2img" ? "image" : type);
    }

    internal string? ModelFileNameForProvenance(MediaJobRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        string type = (request.Type ?? string.Empty).Trim().ToLowerInvariant();
        string checkpointType = type == "img2img" ? "image" : type;
        if (checkpointType is "image" or "video" or "edit")
            return MediaCheckpointResolution.FileNameForProvenance(checkpointType);

        string? templatePath = type switch
        {
            "upscale" => LocalAiPaths.UpscaleWorkflowTemplate,
            "custom" when !string.IsNullOrWhiteSpace(request.CustomWorkflowId) =>
                _customWorkflows.GetRunnable(request.CustomWorkflowId).TemplatePath,
            "imported" when !string.IsNullOrWhiteSpace(request.CustomWorkflowId) =>
                _imported?.TemplatePathFor(request.CustomWorkflowId),
            _ => null
        };
        if (string.IsNullOrWhiteSpace(templatePath) || !File.Exists(templatePath)) return null;
        return FirstModelFileNameFromWorkflowJson(File.ReadAllText(templatePath));
    }

    internal static string? FirstModelFileNameFromWorkflowJson(string workflowJson)
    {
        using JsonDocument document = JsonDocument.Parse(workflowJson);
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return null;

        string? first = null;
        foreach (JsonProperty node in root.EnumerateObject())
        {
            if (!node.Value.TryGetProperty("class_type", out JsonElement classElement)
                || classElement.ValueKind != JsonValueKind.String
                || !node.Value.TryGetProperty("inputs", out JsonElement inputs)
                || inputs.ValueKind != JsonValueKind.Object)
                continue;

            string? inputName = classElement.GetString() switch
            {
                "UNETLoader" => "unet_name",
                "CheckpointLoaderSimple" => "ckpt_name",
                "UpscaleModelLoader" => "model_name",
                "LoraLoader" or "LoraLoaderModelOnly" => "lora_name",
                "CLIPLoader" => "clip_name",
                "DualCLIPLoader" => "clip_name1",
                "VAELoader" => "vae_name",
                "ControlNetLoader" => "control_net_name",
                _ => null
            };
            if (inputName == null || !inputs.TryGetProperty(inputName, out JsonElement model)
                || model.ValueKind != JsonValueKind.String)
                continue;

            string? fileName = model.GetString();
            if (string.IsNullOrWhiteSpace(fileName)) continue;
            if (classElement.GetString() is "UNETLoader" or "CheckpointLoaderSimple" or "UpscaleModelLoader")
                return fileName;
            first ??= fileName;
        }
        return first;
    }

    private async Task EnsureMediaReadyAsync(CancellationToken ct)
    {
        // A follow-up edit/regeneration commonly arrives seconds after the first result. Keep
        // ComfyUI and its media assistant warm for a short burst instead of loading the 27B text
        // model and immediately unloading it again for the next media request.
        CancelPendingTextRestore();
        // Wait out any in-flight text-model restore before touching ComfyUI - see
        // LocalAiProcessManager.WaitForIdleAsync's doc comment for the real crash this avoids.
        await _runtime.WaitForIdleAsync(ct);
        // ComfyUI being healthy is not sufficient: the delayed text restore may already have
        // replaced the small media assistant with the flagship model while leaving ComfyUI up.
        // Returning here then makes prompt refinement wake/load the 27B model and can overlap it
        // with diffusion, producing minute-scale stalls. StartMediaAsync is idempotent and checks
        // both halves in the safe order (media assistant first, then the existing ComfyUI).
        LocalAiProcessManager.RuntimeResult result = await _runtime.StartMediaAsync(ct);
        if (!result.Success) throw new InvalidOperationException("Could not start the local media runtime: " + result.Message);
    }

    /// <summary>
    /// Swaps the text model back to the flagship profile after a short media-idle debounce. A
    /// restore is itself a GPU residency mutation, so it acquires the same exclusive coordinator
    /// writer as media/model swaps. Existing text streams finish; new streams and media requests
    /// queue behind the restore. The slot probe additionally protects loopback clients that bypass
    /// PaRuX's normal request leases.
    /// </summary>
    internal void ScheduleTextRestoreAfterFailedGeneration() => TriggerBackgroundTextRestore();

    private void TriggerBackgroundTextRestore()
    {
        var restore = new CancellationTokenSource();
        CancellationTokenSource? previous = Interlocked.Exchange(ref _pendingTextRestore, restore);
        if (previous != null) previous.Cancel();
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(MediaBurstIdleDelay, restore.Token);
                await using MediaExecutionCoordinator.Lease exclusive = await MediaExecutionCoordinator.Current
                    .AcquireAsync("text-exclusive:media-burst-restore", restore.Token).ConfigureAwait(false);
                if (_runtime.GamingActive) return;
                await TextInferenceQuiescence.WaitForIdleAsync(
                    TimeSpan.FromMinutes(10), restore.Token).ConfigureAwait(false);
                await _runtime.RestoreTextProfileAsync(restore.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch { /* best-effort; the next explicit text request will self-heal via StartTextAsync */ }
            finally
            {
                Interlocked.CompareExchange(ref _pendingTextRestore, null, restore);
                restore.Dispose();
            }
        });
    }

    // Clip planning is a property of the video family (see VideoFamilyContract); these two are the Wan 2.2 TI2V-5B figures
    // (4n+1 frames, 5 to 49 per clip) that the first video runner was written around.
    internal static int NormalizeWanFrameCount(int requested) => VideoFamilyContracts.Wan.NormalizeFrameCount(requested);

    internal static int[] PlanWanClipFrameCounts(int requestedFrames) => VideoFamilyContracts.Wan.PlanClipFrameCounts(requestedFrames);

    internal static (string Prompt, long[] ClipSeeds) BuildVideoPlan(string refinedPrompt, long seed, int clipCount)
    {
        if (clipCount < 1) throw new ArgumentOutOfRangeException(nameof(clipCount));

        string prompt = refinedPrompt +
            "\nContinue one coherent shot; preserve the same subject, setting, lighting and visual style across the entire video.";
        long[] clipSeeds = Enumerable.Range(0, clipCount)
            .Select(index => seed + index)
            .ToArray();
        return (prompt, clipSeeds);
    }

    internal static (string Prompt, long[] ClipSeeds) BuildVideoPlanAndNotifyObserver(string refinedPrompt, long seed, int clipCount)
    {
        (string prompt, long[] clipSeeds) = BuildVideoPlan(refinedPrompt, seed, clipCount);
        VideoPlanObserver.Value?.Invoke(prompt, clipSeeds);
        return (prompt, clipSeeds);
    }

    internal static void SetVideoPromptAndSeed(JsonObject request, string prompt, long seed)
    {
        SetIn(request, "4", "text", prompt);
        SetIn(request, "8", "seed", seed);
    }

    internal static string? AddWanStartImage(JsonObject request, string? uploadedImageName)
    {
        if (string.IsNullOrWhiteSpace(uploadedImageName)) return null;
        JsonObject prompt = request["prompt"]?.AsObject()
            ?? throw new InvalidDataException("Wan workflow has no prompt graph.");
        JsonObject latentInputs = prompt["6"]?["inputs"]?.AsObject()
            ?? throw new InvalidDataException("Wan workflow has no image-to-video latent node 6.");

        int nextId = prompt.Select(pair => int.TryParse(pair.Key, out int id) ? id : 0).DefaultIfEmpty(0).Max() + 1;
        string loadImageId = nextId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        prompt[loadImageId] = new JsonObject
        {
            ["class_type"] = "LoadImage",
            ["inputs"] = new JsonObject { ["image"] = uploadedImageName }
        };
        latentInputs["start_image"] = new JsonArray(JsonValue.Create(loadImageId), JsonValue.Create(0));
        return loadImageId;
    }

    internal static string BuildWanFrameSelectFilter(IReadOnlyList<int> clipFrameCounts, int fps)
    {
        if (fps <= 0) throw new ArgumentOutOfRangeException(nameof(fps));
        int frameOffset = 0;
        var repeatedFrameIndices = new List<int>();
        for (int i = 0; i < clipFrameCounts.Count; i++)
        {
            if (clipFrameCounts[i] <= 0) throw new ArgumentOutOfRangeException(nameof(clipFrameCounts));
            if (i > 0) repeatedFrameIndices.Add(frameOffset);
            frameOffset += clipFrameCounts[i];
        }

        string timing = "setpts=N/(" + fps.ToString(System.Globalization.CultureInfo.InvariantCulture) + "*TB)";
        if (repeatedFrameIndices.Count == 0) return timing;
        string repeatedFrames = string.Join("+", repeatedFrameIndices.Select(index => "eq(n\\," + index + ")"));
        return "select=not(" + repeatedFrames + ")," + timing;
    }

    internal static string ValidateOwnedWanClipOutput(string filename, string subfolder, string jobId, int clipIndex) =>
        ValidateOwnedWanClipOutput(filename, subfolder, jobId, clipIndex, LocalAiPaths.ComfyOutputRoot);

    internal static string ValidateOwnedWanClipOutput(string filename, string subfolder, string jobId, int clipIndex, string outputRoot)
    {
        if (!Guid.TryParseExact(jobId, "N", out _) || clipIndex < 1
            || string.IsNullOrWhiteSpace(filename) || string.IsNullOrWhiteSpace(subfolder)
            || string.IsNullOrWhiteSpace(outputRoot))
            throw new InvalidDataException("ComfyUI returned an invalid video output path.");
        if (Path.IsPathRooted(filename) || Path.IsPathRooted(subfolder)
            || filename.IndexOfAny(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }) >= 0
            || subfolder.IndexOfAny(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }) >= 0
            || filename == "." || filename == ".." || subfolder == "." || subfolder == "..")
            throw new InvalidDataException("ComfyUI returned a rooted or non-canonical video output path.");

        string root = Path.GetFullPath(outputRoot);
        string expectedDirectory = Path.GetFullPath(Path.Combine(root, "LocalAI_videos"));
        string fullPath;
        try { fullPath = Path.GetFullPath(Path.Combine(root, subfolder, filename)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new InvalidDataException("ComfyUI returned an invalid video output path.", ex);
        }

        if (!string.Equals(subfolder, "LocalAI_videos", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Path.GetDirectoryName(fullPath), expectedDirectory, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("ComfyUI returned a video outside the PaRuX video output folder.");
        string expectedPrefix = "PaRuX_" + jobId + "_clip_" + clipIndex.ToString(System.Globalization.CultureInfo.InvariantCulture) + "_";
        if (!filename.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("ComfyUI returned a video that does not belong to this generation job.");

        if (!Directory.Exists(expectedDirectory) || !File.Exists(fullPath))
            throw new InvalidDataException("ComfyUI returned a video output that does not exist.");
        if ((File.GetAttributes(expectedDirectory) & FileAttributes.ReparsePoint) != 0
            || (File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("ComfyUI returned a video output through a reparse point.");
        return fullPath;
    }

    private void CancelPendingTextRestore()
    {
        CancellationTokenSource? pending = Interlocked.Exchange(ref _pendingTextRestore, null);
        if (pending != null) pending.Cancel();
    }

    private static void SetIn(JsonObject request, string node, string field, object value)
    {
        JsonObject inputs = (JsonObject)request["prompt"]![node]!["inputs"]!;
        inputs[field] = value switch
        {
            string s => JsonValue.Create(s),
            long l => JsonValue.Create(l),
            int i => JsonValue.Create(i),
            double d => JsonValue.Create(d),
            _ => throw new ArgumentException("Unsupported workflow value type: " + value.GetType())
        };
    }

    internal async Task<string> UploadImageAsync(byte[] bytes, string fileName, CancellationToken ct)
    {
        using var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(bytes);
        fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        content.Add(fileContent, "image", string.IsNullOrWhiteSpace(fileName) ? "source.png" : fileName);
        using HttpResponseMessage response = await Http.PostAsync(new Uri(CurrentComfyUiBaseAddress, "upload/image"), content, ct);
        response.EnsureSuccessStatusCode();
        JsonElement root = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct)).RootElement;
        string? name = root.TryGetProperty("name", out JsonElement n) ? n.GetString() : null;
        if (string.IsNullOrEmpty(name)) throw new InvalidOperationException("Source image upload to ComfyUI failed.");
        return name;
    }

    internal async Task<string> SubmitAndWaitAsync(string templatePath, Action<JsonObject> mutate, string saveNode, TimeSpan timeout, CancellationToken ct, IProgress<int>? progressPercent = null, Func<string, string, string>? validateSavedOutput = null, List<string>? submittedClientIds = null, string? mediaType = null, int? videoClipIndex = null)
    {
        ConfigAssets.EnsureAll();
        if (!File.Exists(templatePath)) throw new InvalidOperationException("Missing workflow template: " + templatePath);
        JsonObject request = (JsonObject)JsonNode.Parse(await File.ReadAllTextAsync(templatePath, ct))!;
        string clientId = "parux-" + Guid.NewGuid().ToString("N");
        // Recorded before the POST: if the response is lost or a cancel lands mid-request, ComfyUI can
        // still be asked about this client id (see ComfyPromptCancellation).
        submittedClientIds?.Add(clientId);
        request["client_id"] = clientId;
        mutate(request);
        if (mediaType != null && CompanionResolutionEnabled) ApplyResolvedCompanionChoices(request, mediaType);
        // Live preflight: a missing file, node or offered value is refused here with one typed reason,
        // before anything is queued. It reads ComfyUI's own catalogue, so it sees what that server really has.
        string? processIdentity = _runtime?.GetMediaProcessIdentityKey();
        if (LivePreflightEnabled)
            await ComfyLivePreflight.EnsureSubmittableAsync(Http, CurrentComfyUiBaseAddress, request, ct,
                MediaCompanionRegistry.Shipped.MinimumComfyUiVersionFor(Path.GetFileName(templatePath)), processIdentity);

        using var progressCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        ClientWebSocket? progressSocket = null;
        Task? progressTask = null;
        if (progressPercent != null)
        {
            try
            {
                progressSocket = new ClientWebSocket();
                await progressSocket.ConnectAsync(
                    new Uri(MediaWebSocketBase + "/ws?clientId=" + clientId), progressCts.Token);
                progressTask = ListenForProgressAsync(progressSocket, progressPercent, progressCts.Token);
            }
            catch
            {
                progressSocket?.Dispose();
                progressSocket = null;
            }
        }
        string? promptId = null;
        try
        {
            // Recorded before the POST, inside the try, so a submission that cannot be recorded is never sent (and is settled if it was).
            if (videoClipIndex.HasValue)
                VideoClipObserver.Value?.Invoke(videoClipIndex.Value, clientId, null, null);
            SubmissionObserver.Value?.Invoke(clientId, null, saveNode);
            using var content = new StringContent(request.ToJsonString(), Encoding.UTF8, "application/json");
            using HttpResponseMessage queueResponse = await Http.PostAsync(new Uri(CurrentComfyUiBaseAddress, "prompt"), content, ct);
            string queueBody = await queueResponse.Content.ReadAsStringAsync(ct);
            if (!queueResponse.IsSuccessStatusCode)
            {
                ComfyLivePreflight.ThrowTypedRefusal(queueBody);
                throw new InvalidOperationException("ComfyUI rejected the workflow: " + queueBody);
            }
            JsonElement queued = JsonDocument.Parse(queueBody).RootElement;
            if (!queued.TryGetProperty("prompt_id", out JsonElement idEl)) throw new InvalidOperationException("ComfyUI did not accept the workflow: " + queueBody);
            if (queued.TryGetProperty("node_errors", out JsonElement nodeErrors) && nodeErrors.ValueKind == JsonValueKind.Object && nodeErrors.EnumerateObject().Any())
            {
                ComfyLivePreflight.ThrowTypedRefusal(queueBody);
                throw new InvalidOperationException("Invalid workflow: " + nodeErrors);
            }
            string id = idEl.GetString()!;
            promptId = id;
            if (videoClipIndex.HasValue)
                VideoClipObserver.Value?.Invoke(videoClipIndex.Value, clientId, id, null);
            SubmissionObserver.Value?.Invoke(clientId, id, saveNode);

            DateTime deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(500, ct);
                using HttpResponseMessage historyResponse = await Http.GetAsync(MediaEndpoint + "/history/" + id, ct);
                if (!historyResponse.IsSuccessStatusCode) continue;
                JsonElement history = JsonDocument.Parse(await historyResponse.Content.ReadAsStringAsync(ct)).RootElement;
                if (!history.TryGetProperty(id, out JsonElement entry)) continue;

                JsonElement status = entry.GetProperty("status");
                if (status.TryGetProperty("status_str", out JsonElement statusStr) && statusStr.GetString() == "error")
                    throw new InvalidOperationException("Generation failed: " + status);
                if (status.TryGetProperty("completed", out JsonElement completed) && completed.GetBoolean())
                {
                    JsonElement outputs = entry.GetProperty("outputs");
                    if (!outputs.TryGetProperty(saveNode, out JsonElement saveOutput)
                        || !ComfySavedOutputValidator.TryFirstSavedMedia(saveOutput, out JsonElement asset))
                        throw new InvalidOperationException("Generation completed with no output file.");
                    string filename = asset.GetProperty("filename").GetString()!;
                    string subfolder = asset.TryGetProperty("subfolder", out JsonElement sub) ? sub.GetString() ?? string.Empty : string.Empty;
                    return validateSavedOutput != null
                        ? validateSavedOutput(filename, subfolder)
                        : ComfySavedOutputValidator.ResolvePath(filename, subfolder, MediaOutputRoot);
                }
            }
            throw new TimeoutException("Timed out waiting for generation to complete.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Cancelled by the user or by the gaming hard-off: drop or interrupt this submission in ComfyUI
            // and wait until it is gone before the caller releases the media admission lease.
            ComfyCancelReport report = await SettleForCancelAsync(new[] { clientId }, PromptIdsOf(promptId));
            throw new ComfyCancellationException(report, ct);
        }
        catch (TimeoutException ex)
        {
            // The deadline passed while the prompt may still be queued or running: stop it rather than
            // letting the next job start beside it.
            ComfyCancelReport report = await SettleForCancelAsync(new[] { clientId }, PromptIdsOf(promptId));
            throw new TimeoutException(report.Notice == null ? ex.Message : ex.Message + " " + report.Notice);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // Failed after the POST may have reached ComfyUI (or its response was lost): make sure no prompt
            // of this submission is left running before the failure is reported.
            await SettleForCancelAsync(new[] { clientId }, PromptIdsOf(promptId));
            throw;
        }
        finally
        {
            progressCts.Cancel();
            if (progressTask != null) { try { await progressTask; } catch { /* best effort */ } }
            progressSocket?.Dispose();
        }
    }

    // Confirms with ComfyUI that none of this job's prompts is still queued or running (ComfyPromptCancellation).
    // It uses its own bounded wait: the job token that triggered the cancel is already signalled.
    internal Task<ComfyCancelReport> SettleForCancelAsync(IReadOnlyCollection<string> clientIds, IReadOnlyCollection<string> promptIds) =>
        ComfyPromptCancellation.SettleAsync(Http, MediaEndpoint, clientIds, promptIds, MediaCancelIdleBound, ComfyPromptCancellation.PollInterval);

    private static string[] PromptIdsOf(string? promptId) => promptId == null ? Array.Empty<string>() : new[] { promptId };

    internal static void ApplyResolvedCompanionChoices(JsonObject request, string type)
    {
        MediaCompanionRegistry registry = MediaCompanionRegistry.Shipped;
        string mainFileName = MediaCompanionGraph.ReadMainFileName(request, type, registry);
        string checkpointPath = Path.Combine(LocalAiPaths.DiffusionModelsPath, mainFileName);
        // A missing main checkpoint gets the same typed refusal as the live preflight, not a raw family error.
        if (!File.Exists(checkpointPath)) throw new MediaDependencyException(ComfyLivePreflight.MissingFileByName(mainFileName));
        MediaFamilyDetection detection = MediaFamilyDetector.DetectFile(checkpointPath, registry);
        RegistryFamily? family = detection.FamilyId == null ? null : registry.FindFamily(detection.FamilyId);
        if (family == null)
            throw new InvalidOperationException(detection.Reason ?? "The selected checkpoint family could not be identified.");
        if (!family.IsSupported || !family.Pipelines.Contains(type, StringComparer.Ordinal))
            throw new InvalidOperationException(family.Reason
                ?? family.DisplayName + " cannot run the " + type + " workflow.");

        string[] folders = family.Companions.Select(companion => companion.Folder)
            .Distinct(StringComparer.Ordinal).ToArray();
        var installed = MediaCompanionResolver.ScanFolders(
            Path.Combine(LocalAiPaths.ComfyApp, "models"), folders);
        MediaCompanionGraph.PrepareSubmissionGraph(request, type, detection, registry, installed);
    }

    private static async Task ListenForProgressAsync(ClientWebSocket socket, IProgress<int> progressPercent, CancellationToken ct)
    {
        try
        {
            byte[] buffer = new byte[32 * 1024];
            while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                using var message = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(buffer, ct);
                    if (result.MessageType == WebSocketMessageType.Close) return;
                    message.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                if (message.Length == 0) continue;
                try
                {
                    int? percent = TryParseProgressPercent(message.ToArray());
                    if (percent.HasValue) progressPercent.Report(percent.Value);
                }
                catch (JsonException) { }
            }
        }
        catch (OperationCanceledException) { }
        catch { }
    }

    internal static int? TryParseProgressPercent(byte[] payload)
    {
        using JsonDocument doc = JsonDocument.Parse(payload);
        JsonElement root = doc.RootElement;
        if (!root.TryGetProperty("type", out JsonElement typeEl)
            || !root.TryGetProperty("data", out JsonElement data)) return null;

        if (typeEl.GetString() == "progress"
            && data.TryGetProperty("value", out JsonElement flatValue)
            && data.TryGetProperty("max", out JsonElement flatMax))
            return Percent(flatValue, flatMax);

        if (typeEl.GetString() != "progress_state"
            || !data.TryGetProperty("nodes", out JsonElement nodes)
            || nodes.ValueKind != JsonValueKind.Object) return null;

        foreach (JsonProperty node in nodes.EnumerateObject())
        {
            if (!node.Value.TryGetProperty("state", out JsonElement stateEl)
                || stateEl.GetString() != "running"
                || !node.Value.TryGetProperty("value", out JsonElement valueEl)
                || !node.Value.TryGetProperty("max", out JsonElement maxEl)) continue;
            int? percent = Percent(valueEl, maxEl);
            if (percent.HasValue) return percent;
        }
        return null;
    }

    private static int? Percent(JsonElement value, JsonElement max)
    {
        double maximum = max.GetDouble();
        if (maximum <= 1) return null;
        return Math.Clamp((int)(100 * value.GetDouble() / maximum), 0, 100);
    }

    internal async Task<string> ConcatenateWithFfmpegAsync(List<string> clipPaths, IReadOnlyList<int> clipFrameCounts, int requestedFrames, int fps, CancellationToken ct)
    {
        if (MediaProcessOverride == null && FindFfmpeg() == null)
            throw new InvalidOperationException("FFmpeg is not available to concatenate clips.");

        string tempDir = Path.Combine(MediaTempRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string resultPath = Path.Combine(tempDir, "PaRuX_" + Guid.NewGuid().ToString("N") + ".mp4");
        try
        {
            string listFile = Path.Combine(tempDir, "clips.txt");
            await File.WriteAllLinesAsync(listFile, clipPaths.Select(p => "file '" + p.Replace("'", "''") + "'"), Encoding.ASCII, ct);
            string filter = BuildWanFrameSelectFilter(clipFrameCounts, fps);
            string[] args = { "-hide_banner", "-loglevel", "error", "-y", "-f", "concat", "-safe", "0", "-i", listFile,
                "-vf", filter, "-frames:v", requestedFrames.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "-fps_mode", "passthrough", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-an", resultPath };
            await RunMediaCommandAsync(args, resultPath, ct).ConfigureAwait(false);
            if (!File.Exists(resultPath)) throw new InvalidOperationException("Video concatenation did not produce an output file.");

            string videoOutDir = Path.Combine(MediaOutputRoot, "LocalAI_videos");
            Directory.CreateDirectory(videoOutDir);
            string finalPath = Path.Combine(videoOutDir, "PaRuX_" + Guid.NewGuid().ToString("N") + ".mp4");
            File.Move(resultPath, finalPath);
            return finalPath;
        }
        finally
        {
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    internal async Task ExtractLastVideoFrameAsync(string videoPath, string framePath, CancellationToken ct)
    {
        if (MediaProcessOverride == null && FindFfmpeg() == null)
            throw new InvalidOperationException("FFmpeg is not available to extract the Wan continuity frame.");
        await RunMediaCommandAsync(new[] { "-hide_banner", "-loglevel", "error", "-y", "-i", videoPath,
            "-vf", "reverse", "-frames:v", "1", "-update", "1", framePath }, framePath, ct).ConfigureAwait(false);
        if (!File.Exists(framePath) || new FileInfo(framePath).Length == 0)
            throw new InvalidOperationException("FFmpeg did not produce the Wan continuity frame.");
    }

    private async Task RunMediaCommandAsync(IReadOnlyList<string> args, string outputPath, CancellationToken ct)
    {
        if (MediaProcessOverride != null)
        {
            await MediaProcessOverride(args, outputPath, ct).ConfigureAwait(false);
            return;
        }
        string? ffmpeg = FindFfmpeg();
        if (ffmpeg == null) throw new InvalidOperationException("FFmpeg is not available.");
        await RunMediaProcessAsync(ffmpeg, args, outputPath, ct).ConfigureAwait(false);
    }

    private string ValidateOwnedWanClipPath(string path, string jobId, int clipIndex)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new InvalidDataException("The saved video clip path is missing.");
        string fullPath;
        try { fullPath = Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        { throw new InvalidDataException("The saved video clip path is invalid.", ex); }
        string? parent = Path.GetDirectoryName(fullPath);
        if (parent == null) throw new InvalidDataException("The saved video clip path is invalid.");
        string subfolder;
        try { subfolder = Path.GetRelativePath(Path.GetFullPath(MediaOutputRoot), parent); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        { throw new InvalidDataException("The saved video clip path is invalid.", ex); }
        string validated = ValidateOwnedWanClipOutput(Path.GetFileName(fullPath), subfolder, jobId, clipIndex, MediaOutputRoot);
        if (!string.Equals(fullPath, validated, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The saved video clip path is not canonical.");
        return validated;
    }

    internal string ValidateRecoveredVideoFinalPath(string path)
    {
        string fullPath;
        try { fullPath = Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        { throw new InvalidDataException("The saved final video path is invalid.", ex); }
        string expectedDirectory = Path.GetFullPath(Path.Combine(MediaOutputRoot, "LocalAI_videos"));
        if (!string.Equals(Path.GetDirectoryName(fullPath), expectedDirectory, StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(fullPath).StartsWith("PaRuX_", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Path.GetExtension(fullPath), ".mp4", StringComparison.OrdinalIgnoreCase)
            || !File.Exists(fullPath))
            throw new InvalidDataException("The saved final video is outside the PaRuX video output folder or is missing.");
        if ((File.GetAttributes(expectedDirectory) & FileAttributes.ReparsePoint) != 0
            || (File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("The saved final video path uses a reparse point.");
        return fullPath;
    }

    internal static async Task RunMediaProcessAsync(string executable, IReadOnlyList<string> args, string? partialOutputPath, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = executable, UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true
        };
        foreach (string arg in args) psi.ArgumentList.Add(arg);
        using var process = new Process { StartInfo = psi };
        Task<string>? stderrTask = null;
        try
        {
            process.Start();
            stderrTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync(ct);
            string stderr = await stderrTask;
            if (process.ExitCode != 0) throw new InvalidOperationException("Media process failed: " + stderr);
        }
        catch
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            try
            {
                using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await process.WaitForExitAsync(cleanupTimeout.Token);
            }
            catch { /* best effort: preserve the original failure and bound cleanup latency */ }
            if (stderrTask != null)
            {
                if (process.HasExited) try { _ = await stderrTask.WaitAsync(TimeSpan.FromSeconds(1)); } catch { }
                else _ = stderrTask.ContinueWith(task => _ = task.Exception,
                    CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
            if (partialOutputPath != null) TryDeleteFile(partialOutputPath);
            throw;
        }
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static string? FindFfmpeg()
    {
        foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(';'))
        {
            string candidate = Path.Combine(dir, "ffmpeg.exe");
            if (File.Exists(candidate)) return candidate;
        }
        string wingetRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Packages");
        if (Directory.Exists(wingetRoot))
        {
            string? found = Directory.EnumerateFiles(wingetRoot, "ffmpeg.exe", SearchOption.AllDirectories).FirstOrDefault();
            if (found != null) return found;
        }
        return null;
    }

    // Production always talks to the managed ComfyUI on its fixed loopback port. Tests point this at a
    // fake loopback server so that no real GPU runtime is ever touched.
    internal string MediaBaseUrl { get; set; } = "http://127.0.0.1:8188";
    internal bool LivePreflightEnabled { get; set; } = true;
    /// <summary>Production always resolves companion files; tests that exercise queueing only turn it off, like <see cref="LivePreflightEnabled"/>.</summary>
    internal bool CompanionResolutionEnabled { get; set; } = true;
    internal string MediaTempRoot { get; set; } = Path.Combine(PaRuXDataRoot.Path, "media-temp");
    internal Func<IReadOnlyList<string>, string, CancellationToken, Task>? MediaProcessOverride { get; set; }
    private Uri CurrentComfyUiBaseAddress => NormalizeComfyUiBaseAddress(new Uri(MediaBaseUrl, UriKind.Absolute));
    private static Uri NormalizeComfyUiBaseAddress(Uri candidate)
    {
        if (!candidate.IsAbsoluteUri || !candidate.IsLoopback
            || !string.Equals(candidate.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(candidate.UserInfo) || !string.IsNullOrEmpty(candidate.Query)
            || !string.IsNullOrEmpty(candidate.Fragment) || candidate.AbsolutePath != "/")
            throw new ArgumentException("The ComfyUI endpoint must be a plain local loopback HTTP address.", nameof(candidate));
        return new Uri(candidate.GetLeftPart(UriPartial.Authority) + "/");
    }
    // Bounded wait for ComfyUI to show none of a cancelled or timed-out job's prompts (ComfyPromptCancellation).
    internal TimeSpan MediaCancelIdleBound { get; set; } = ComfyPromptCancellation.DefaultIdleBound;
    private string MediaEndpoint => MediaBaseUrl;
    // Where ComfyUI writes saved outputs. Production uses the managed ComfyUI output folder; tests point it at a temp folder.
    internal string MediaOutputRoot { get; set; } = LocalAiPaths.ComfyOutputRoot;
    // Set by a media job while it runs: told the ComfyUI client id before each POST and the prompt id after it, so the job
    // store records a submission before ComfyUI can be left holding it (MediaJobService.RecordSubmission).
    internal static readonly AsyncLocal<Action<string, string?, string>?> SubmissionObserver = new();
    // Persist the exact clip prompt and seeds before the first video clip is submitted.
    internal static readonly AsyncLocal<Action<string, IReadOnlyList<long>>?> VideoPlanObserver = new();
    internal static readonly AsyncLocal<Action<string, string, int, IReadOnlyList<int>>?> VideoClipPlanObserver = new();
    /// <summary>Told which video family the job runs, before the clip plan is saved, so a restart continues with the same graph.</summary>
    internal static readonly AsyncLocal<Action<string>?> VideoFamilyObserver = new();
    internal static readonly AsyncLocal<Action<int, string?, string?, string?>?> VideoClipObserver = new();
    internal static readonly AsyncLocal<Action<string>?> VideoFinalOutputObserver = new();
    // What ComfyUI still holds for a job's prompts after a Core restart (ComfyPromptRecovery).
    internal Task<ComfyPromptRecovery.Probe> ProbeRecoveryAsync(
        IReadOnlyCollection<string> clientIds, IReadOnlyCollection<string> promptIds, CancellationToken ct, string? saveNodeId = null) =>
        ComfyPromptRecovery.ProbeAsync(Http, MediaEndpoint, MediaOutputRoot, clientIds, promptIds, ct, saveNodeId);
    private string MediaWebSocketBase => "ws://" + new Uri(MediaEndpoint).Authority;
}
