using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>
/// Generates images/video/edits/upscales natively via ComfyWorkflowRunner. Every public generation
/// enters the process-wide MediaExecutionCoordinator so synchronous endpoints and queued jobs cannot
/// overlap with each other, Engine Race, or already-admitted text inference on the same GPU.
/// </summary>
public sealed class MediaGenerationService
{
    private readonly LocalRuntimeService _runtime;
    private readonly ComfyWorkflowRunner _comfy;

    public MediaGenerationService(LocalRuntimeService runtime, ComfyWorkflowRunner comfy)
    {
        _runtime = runtime;
        _comfy = comfy;
    }

    public async Task<ImageGenerationResponse> GenerateImageAsync(ImageGenerationRequest request, CancellationToken ct)
    {
        MediaAsset asset = await GenerateAsync(new MediaJobRequest
        {
            Type = "image", Prompt = request.Prompt, Width = request.Width,
            Height = request.Height, Seed = request.Seed
        }, null, ct);
        return new ImageGenerationResponse
        {
            FileName = asset.FileName, MediaType = asset.MediaType, Base64Data = asset.Base64Data,
            Seed = asset.Seed, Width = asset.Width, Height = asset.Height
        };
    }

    public async Task<MediaGenerationOutcome> GenerateForJobAsync(
        MediaJobRequest request,
        IProgress<(string Stage, int Percent, int? Eta)>? progress,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        // The same prompt scope as GenerateAsync, so the model named in the provenance is the one the router will pick for this request.
        using IDisposable routingScope = MediaRoutingContext.Begin(request.Prompt);
        MediaJobProvenance provenance = CreateManagedEngineProvenance(request);
        try
        {
            MediaAsset asset = await GenerateAsync(request, progress, ct).ConfigureAwait(false);
            return MediaGenerationOutcome.Success(asset, provenance);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            MediaDependencyError? detail = (ex as MediaDependencyException)?.Error;
            return MediaGenerationOutcome.Failure(
                ex.Message,
                MediaJobProvenanceFactory.WithFailure(provenance, ex),
                detail);
        }
    }

    private MediaJobProvenance CreateManagedEngineProvenance(MediaJobRequest request)
    {
        ComfyBuildIdentity? active = null;
        try { active = new ComfyRuntimeStore().GetActiveIdentity(); }
        catch (Exception) { }

        string runtimeIdentity = active == null
            ? "ComfyUI|legacy"
            : "ComfyUI|" + active.Tag + "|" + active.Variant;
        string? modelFileName = null;
        try { modelFileName = _comfy.ModelFileNameForProvenance(request); }
        catch (Exception) { }

        string? selectionReason = null;
        try { selectionReason = ComfyWorkflowRunner.ModelSelectionReasonForProvenance(request); }
        catch (Exception) { }

        return MediaJobProvenanceFactory.Create("ComfyUI", runtimeIdentity, modelFileName, modelSelectionReason: selectionReason);
    }

    public async Task<MediaAsset> GenerateAsync(MediaJobRequest request,
        IProgress<(string Stage, int Percent, int? Eta)>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        string type = (request.Type ?? string.Empty).Trim().ToLowerInvariant();
        if (_runtime.GamingMode) throw new InvalidOperationException("Gaming Mode is active, so media generation is unavailable.");
        if (type is not ("image" or "video" or "edit" or "img2img" or "inpaint" or "upscale" or "custom" or "imported"))
            throw new InvalidDataException("Media type must be image, video, edit, img2img, inpaint, upscale, custom, or imported.");
        if (type is not ("upscale" or "custom" or "imported") && string.IsNullOrWhiteSpace(request.Prompt)) throw new InvalidDataException("A media prompt is required.");
        if ((type == "edit" || type == "img2img" || type == "inpaint" || type == "upscale") && string.IsNullOrWhiteSpace(request.SourceImageBase64))
            throw new InvalidDataException(type + " requires sourceImageBase64.");
        if (type is "custom" or "imported" && string.IsNullOrWhiteSpace(request.CustomWorkflowId))
            throw new InvalidDataException(type + " requires customWorkflowId.");

        using IDisposable routingScope = MediaRoutingContext.Begin(request.Prompt);
        progress?.Report(("Waiting for exclusive media GPU", 1, null));
        await using MediaExecutionCoordinator.Lease executionLease = await MediaExecutionCoordinator.Current
            .AcquireAsync("media:" + type, ct).ConfigureAwait(false);

        // The writer lease blocks new text inference, but already-admitted llama.cpp/ExLlama
        // responses are allowed to finish naturally. Prove they are quiescent before ComfyUI's
        // normal StartMediaAsync transition stops the text resident. This is what prevents a media
        // request from truncating a response that happened to be streaming when the job was queued.
        progress?.Report(("Waiting for active text inference to finish", 2, null));
        await _runtime.PrepareForExclusiveMediaAsync(ct).ConfigureAwait(false);
        if (_runtime.GamingMode) throw new InvalidOperationException("Gaming Mode is active, so media generation is unavailable.");

        progress?.Report(("Starting local media runtime", 3, null));
        // Measure the actual incoming assistant + ComfyUI working sets. Subtracting only the
        // outgoing model would over-admit whenever the media runtime itself pushes the machine
        // over its RAM budget. Any failure after this transition schedules normal-profile restore.
        ExecutionResourceDecision resourceAdmission = await PrepareAndAdmitMediaAsync(
            () => _runtime.PrepareMediaRuntimeForAdmissionAsync(ct),
            () => LocalRuntimeService.CheckMediaWorkloadAdmissionAsync(ct),
            _comfy.ScheduleTextRestoreAfterFailedGeneration).ConfigureAwait(false);

        ModelExecutionProfile executionProfile = ExecutionProfileStore.Current.Get().Effective;

        int maxWallClockMinutes = Math.Clamp(executionProfile.Budget.MaxWallClockMinutes, 1, 1_440);
        long maxArtifactBytes = Math.Max(1, executionProfile.Budget.MaxArtifactBytes);
        using var budgetCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budgetCts.CancelAfter(TimeSpan.FromMinutes(maxWallClockMinutes));
        CancellationToken runCt = budgetCts.Token;

        int width = NormalizeDimension(request.Width, type == "video" ? 1280 : 1024);
        int height = NormalizeDimension(request.Height, type == "video" ? 720 : 1024);
        double duration = NormalizeDuration(request.DurationSeconds);
        int fps = Math.Clamp(request.Fps <= 0 ? 24 : request.Fps, 8, 60);
        int steps = Math.Clamp(request.Steps <= 0 ? 30 : request.Steps, 8, 60);
        int estimated = type == "video" ? Math.Max(60, (int)Math.Ceiling(duration * 100)) : 90;
        progress?.Report(("Preparing media generation", 5, estimated));

        long seed = ResolveSeed(request.Seed);

        try
        {
            string stage = type switch
            {
                "video" => "Generating video clips", "edit" => "Editing image",
                "img2img" => "Generating variation", "inpaint" => "Repainting masked area", "upscale" => "Upscaling image",
                "custom" => "Running custom workflow", "imported" => "Running imported workflow", _ => "Generating image"
            };
            progress?.Report((stage, 15, estimated));

            var etaEstimator = new MediaProgressEstimator(estimated);
            var liveProgress = new Progress<int>(rawPercent =>
            {
                int scaled = 15 + (int)(0.77 * Math.Clamp(rawPercent, 0, 100));
                progress?.Report((stage, scaled, etaEstimator.Estimate(rawPercent)));
            });

            ComfyWorkflowRunner.MediaResult result = type switch
            {
                "image" => await _comfy.RunImageAsync(request.Prompt.Trim(), seed, width, height, runCt, liveProgress, skipPromptRefinement: request.PromptAlreadyReviewed, loras: request.Loras),
                "video" => await _comfy.RunVideoAsync(request.Prompt.Trim(), seed, width, height, duration, fps, steps, runCt, liveProgress,
                    skipPromptRefinement: request.PromptAlreadyReviewed,
                    startImage: string.IsNullOrWhiteSpace(request.SourceImageBase64)
                        ? null
                        : MediaStartImageValidator.DecodePayload(request.SourceImageBase64)),
                "edit" => await _comfy.RunEditAsync(request.Prompt.Trim(), seed,
                    Convert.FromBase64String(request.SourceImageBase64), "source.png", runCt),
                "img2img" => await _comfy.RunImg2ImgAsync(request.Prompt.Trim(), seed,
                    Convert.FromBase64String(request.SourceImageBase64), "source.png",
                    request.Strength ?? 0.6, runCt),
                "inpaint" => await _comfy.RunInpaintAsync(request.Prompt.Trim(), seed,
                    Convert.FromBase64String(request.SourceImageBase64), "source.png",
                    request.MaskImageBase64, request.MaskGrowPixels, request.Loras, runCt, liveProgress),
                "upscale" => await _comfy.RunUpscaleAsync(Convert.FromBase64String(request.SourceImageBase64), "source.png", runCt),
                "custom" => await _comfy.RunCustomAsync(request.CustomWorkflowId!, request.Prompt?.Trim() ?? string.Empty, seed,
                    string.IsNullOrWhiteSpace(request.SourceImageBase64) ? null : Convert.FromBase64String(request.SourceImageBase64),
                    "source.png", runCt, liveProgress),
                "imported" => await _comfy.RunImportedAsync(request.CustomWorkflowId!, request.Prompt?.Trim() ?? string.Empty, request.Seed > 0 ? request.Seed : (long?)null,
                    request.WorkflowOverrides, string.IsNullOrWhiteSpace(request.SourceImageBase64) ? null : Convert.FromBase64String(request.SourceImageBase64),
                    "source.png", runCt, liveProgress),
                _ => throw new InvalidDataException("Media type must be image, video, edit, img2img, inpaint, upscale, custom, or imported.")
            };

            progress?.Report(("Finalizing media", 92, 10));
            if (!File.Exists(result.Path)) throw new FileNotFoundException("The generated media could not be found.", result.Path);
            long artifactBytes = new FileInfo(result.Path).Length;
            if (!FitsArtifactBudget(artifactBytes, maxArtifactBytes))
                throw new InvalidOperationException(
                    $"Generated media is {artifactBytes:N0} bytes, exceeding the active execution profile artifact budget of {maxArtifactBytes:N0} bytes.");

            byte[] bytes = await File.ReadAllBytesAsync(result.Path, runCt);
            (int actualWidth, int actualHeight) = TryReadPngSize(bytes) ?? (0, 0);
            return new MediaAsset
            {
                FileName = Path.GetFileName(result.Path), MediaType = MediaTypeFor(result.Path), Base64Data = Convert.ToBase64String(bytes),
                Seed = result.Seed,
                Width = result.Width > 0 ? result.Width : (actualWidth > 0 ? actualWidth : width),
                Height = result.Height > 0 ? result.Height : (actualHeight > 0 ? actualHeight : height),
                DurationSeconds = result.DurationSeconds, Fps = result.Fps, Steps = result.Steps
            };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && budgetCts.IsCancellationRequested)
        {
            _comfy.ScheduleTextRestoreAfterFailedGeneration();
            throw new TimeoutException(
                $"Media generation exceeded the active execution profile wall-clock budget of {maxWallClockMinutes} minute(s).");
        }
        catch (OperationCanceledException)
        {
            _comfy.ScheduleTextRestoreAfterFailedGeneration();
            throw;
        }
        catch (MediaDependencyException)
        {
            // Already typed by the live preflight or by ComfyUI's node errors: keep the reason and its next action.
            _comfy.ScheduleTextRestoreAfterFailedGeneration();
            throw;
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException)
        {
            _comfy.ScheduleTextRestoreAfterFailedGeneration();
            List<string> missingAssets = ComfyUiErrorParser.ExtractMissingAssetNames(ex.Message);
            if (missingAssets.Count > 0) throw new MediaDependencyException(ComfyLivePreflight.MissingFileByName(missingAssets[0]));
            throw new InvalidOperationException(Trim(ex.Message));
        }
        catch
        {
            _comfy.ScheduleTextRestoreAfterFailedGeneration();
            throw;
        }
    }

    internal static long ResolveSeed(long requestedSeed) =>
        requestedSeed <= 0 ? Random.Shared.NextInt64(1, long.MaxValue) : requestedSeed;

    internal static int NormalizeDimension(int value, int fallback)
    {
        value = Math.Clamp(value <= 0 ? fallback : value, 256, 3840);
        return value - value % 8;
    }

    internal static double NormalizeDuration(double value) => Math.Clamp(value <= 0 ? 5 : value, 1, 60);

    internal static bool FitsArtifactBudget(long artifactBytes, long maxArtifactBytes) =>
        artifactBytes >= 0 && maxArtifactBytes > 0 && artifactBytes <= maxArtifactBytes;

    internal static async Task<ExecutionResourceDecision> PrepareAndAdmitMediaAsync(
        Func<Task> startMediaRuntime,
        Func<Task<ExecutionResourceDecision>> checkResources,
        Action scheduleTextRestore)
    {
        ArgumentNullException.ThrowIfNull(startMediaRuntime);
        ArgumentNullException.ThrowIfNull(checkResources);
        ArgumentNullException.ThrowIfNull(scheduleTextRestore);

        try
        {
            await startMediaRuntime().ConfigureAwait(false);
            ExecutionResourceDecision decision = await checkResources().ConfigureAwait(false);
            if (!decision.Allowed) throw new InvalidOperationException(decision.Message);
            return decision;
        }
        catch
        {
            scheduleTextRestore();
            throw;
        }
    }

    private static (int Width, int Height)? TryReadPngSize(byte[] bytes)
    {
        if (bytes.Length < 24) return null;
        ReadOnlySpan<byte> pngSignature = stackalloc byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        if (!bytes.AsSpan(0, 8).SequenceEqual(pngSignature)) return null;
        int width = (bytes[16] << 24) | (bytes[17] << 16) | (bytes[18] << 8) | bytes[19];
        int height = (bytes[20] << 24) | (bytes[21] << 16) | (bytes[22] << 8) | bytes[23];
        return width > 0 && height > 0 ? (width, height) : null;
    }

    internal static string MediaTypeFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg", ".webp" => "image/webp",
        ".mp4" => "video/mp4", ".webm" => "video/webm", _ => "image/png"
    };

    private static string Trim(string value)
    {
        value = value.Trim();
        return value.Length <= 500 ? value : value[..500] + "…";
    }
}
