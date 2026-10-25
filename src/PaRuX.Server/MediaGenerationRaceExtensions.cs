using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>
/// Production-facing media race entry points. The generic overload remains available for tests and
/// future engines. Production image racing compares renderers on the exact same prompt/seed/
/// resolution challenge, with independent ComfyUI and Diffusers implementations plus an offline
/// CPU semantic judge. Rendering is sequential to avoid GPU contention and judge time is measured
/// separately from renderer latency.
/// </summary>
public static class MediaGenerationRaceExtensions
{
    private const int ZImageTurboDiffusersSteps = 9; // official API value: eight DiT forwards
    internal const double SemanticAcceptanceFloor = 0.55;

    public static Task<MediaEngineRaceResult> RunEngineRaceAsync(
        this MediaGenerationService generation,
        IReadOnlyList<MediaEngineRaceCandidate> candidates,
        Func<MediaEngineRaceCandidate, MediaAsset, CancellationToken, Task<MediaEngineQualityEvidence>> qualityEvaluator,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(generation);
        return new MediaEngineRaceService().RunAsync(candidates, qualityEvaluator, cancellationToken);
    }

    /// <summary>
    /// Compatibility overload for lower-level callers. Production HTTP endpoints use the overload
    /// with ManagedComfyImageRaceEngine + ManagedClipImageJudge below.
    /// </summary>
    public static Task<MediaEngineRaceResult> RunManagedImageRaceAsync(
        this MediaGenerationService generation,
        ManagedDiffusersImageEngine diffusers,
        MediaJobRequest request,
        CancellationToken cancellationToken) =>
        RunLegacyManagedImageRaceAsync(generation, diffusers, semanticJudge: null, request, cancellationToken);

    /// <summary>
    /// Compatibility overload retaining semantic ranking while using the interactive Comfy path.
    /// New production code should use the dedicated race-lifecycle overload below.
    /// </summary>
    public static Task<MediaEngineRaceResult> RunManagedImageRaceAsync(
        this MediaGenerationService generation,
        ManagedDiffusersImageEngine diffusers,
        ManagedClipImageJudge semanticJudge,
        MediaJobRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(semanticJudge);
        return RunLegacyManagedImageRaceAsync(generation, diffusers, semanticJudge, request, cancellationToken);
    }

    /// <summary>
    /// Shipping two-engine path. The exact caller prompt is used by both engines: prompt rewriting
    /// is a higher-level product feature and must not warm one renderer's Python/runtime caches or
    /// alter the challenge in an engine-vs-engine benchmark. PaRuX text/media residency is released
    /// after waiting for any in-flight runtime transition, both renderer paths cold-start their own
    /// runtime/model inside their stopwatch, and candidate order alternates deterministically with
    /// seed parity to balance thermal/cache order effects over a corpus. Semantic scoring runs only
    /// after all GPU rendering has completed.
    /// </summary>
    public static async Task<MediaEngineRaceResult> RunManagedImageRaceAsync(
        this MediaGenerationService generation,
        ManagedComfyImageRaceEngine comfyRace,
        ManagedDiffusersImageEngine diffusers,
        ManagedClipImageJudge semanticJudge,
        MediaJobRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(generation);
        ArgumentNullException.ThrowIfNull(comfyRace);
        ArgumentNullException.ThrowIfNull(diffusers);
        ArgumentNullException.ThrowIfNull(semanticJudge);
        ValidateProductionRequest(diffusers, semanticJudge, request);

        long seed = request.Seed <= 0 ? Random.Shared.NextInt64(1, long.MaxValue) : request.Seed;
        string challengePrompt = request.Prompt.Trim();
        try
        {
            await comfyRace.EstablishColdBoundaryAsync(cancellationToken).ConfigureAwait(false);

            MediaJobRequest Challenge() => new()
            {
                Type = "image",
                Prompt = challengePrompt,
                Width = request.Width,
                Height = request.Height,
                Steps = ZImageTurboDiffusersSteps,
                Seed = seed
            };

            ModelExecutionProfile profile = ExecutionProfileStore.Current.Get(ManagedDiffusersImageEngine.ModelId).Effective;
            string profileTag = profile.QualityProfile;
            var comfyCandidate = new MediaEngineRaceCandidate(
                ManagedComfyImageRaceEngine.EngineId,
                ManagedComfyImageRaceEngine.EngineVersion + "-" + profileTag + "-cold-zimage-8step",
                ct => comfyRace.GeneratePreparedAsync(challengePrompt, Challenge(), seed, ct));
            var diffusersCandidate = new MediaEngineRaceCandidate(
                ManagedDiffusersImageEngine.EngineId,
                ManagedDiffusersImageEngine.EngineVersion + "-" + profileTag + "-cold-zimage-9api-8dit",
                ct => diffusers.GenerateAsync(Challenge(), null, ct));

            IReadOnlyList<MediaEngineRaceCandidate> candidates = (seed & 1L) == 0
                ? new[] { comfyCandidate, diffusersCandidate }
                : new[] { diffusersCandidate, comfyCandidate };

            return await new MediaEngineRaceService().RunAsync(
                candidates,
                (candidate, asset, ct) => EvaluateProductionQualityAsync(
                    challengePrompt, Challenge(), candidate, asset, semanticJudge, ct),
                cancellationToken,
                preferredEngineId: ManagedComfyImageRaceEngine.EngineId).ConfigureAwait(false);
        }
        finally
        {
            comfyRace.ReleaseGpuBestEffort();
            comfyRace.RestoreTextProfileInBackground();
        }
    }

    private static Task<MediaEngineRaceResult> RunLegacyManagedImageRaceAsync(
        MediaGenerationService generation,
        ManagedDiffusersImageEngine diffusers,
        ManagedClipImageJudge? semanticJudge,
        MediaJobRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(generation);
        ArgumentNullException.ThrowIfNull(diffusers);
        ArgumentNullException.ThrowIfNull(request);
        if (!string.Equals((request.Type ?? string.Empty).Trim(), "image", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The production multi-engine race currently supports text-to-image jobs only.");
        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new InvalidDataException("Media Engine Race requires a prompt.");
        if (!diffusers.GetStatus().Ready)
            throw new InvalidOperationException(
                "A genuine Engine Race requires the independent PaRuX Diffusers engine to be installed first; PaRuX will not fake a second candidate.");
        if (semanticJudge != null && !semanticJudge.GetStatus().Ready)
            throw new InvalidOperationException("The managed CLIP semantic judge is not ready.");

        long seed = request.Seed <= 0 ? Random.Shared.NextInt64(1, long.MaxValue) : request.Seed;
        MediaJobRequest Challenge() => new()
        {
            Type = "image",
            Prompt = request.Prompt,
            Width = request.Width,
            Height = request.Height,
            Steps = ZImageTurboDiffusersSteps,
            Seed = seed
        };

        ModelExecutionProfile profile = ExecutionProfileStore.Current.Get(ManagedDiffusersImageEngine.ModelId).Effective;
        string profileTag = profile.QualityProfile;
        var candidates = new List<MediaEngineRaceCandidate>
        {
            new(
                "comfyui",
                "managed-legacy-" + profileTag,
                ct => generation.GenerateAsync(Challenge(), null, ct)),
            new(
                ManagedDiffusersImageEngine.EngineId,
                ManagedDiffusersImageEngine.EngineVersion + "-legacy-" + profileTag,
                ct => diffusers.GenerateAsync(Challenge(), null, ct))
        };

        return new MediaEngineRaceService().RunAsync(
            candidates,
            async (candidate, asset, ct) =>
            {
                MediaEngineQualityEvidence technical = await MediaImageQualityEvaluator
                    .EvaluateAsync(Challenge(), candidate, asset, ct).ConfigureAwait(false);
                if (!technical.Accepted || semanticJudge == null) return technical;
                double semantic = await semanticJudge.ScoreAsync(request.Prompt, asset, ct).ConfigureAwait(false);
                return CombineQuality(technical, semantic);
            },
            cancellationToken,
            preferredEngineId: "comfyui");
    }

    private static void ValidateProductionRequest(
        ManagedDiffusersImageEngine diffusers,
        ManagedClipImageJudge semanticJudge,
        MediaJobRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!string.Equals((request.Type ?? string.Empty).Trim(), "image", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The production multi-engine race currently supports text-to-image jobs only.");
        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new InvalidDataException("Media Engine Race requires a prompt.");
        if (!diffusers.GetStatus().Ready)
            throw new InvalidOperationException(
                "A genuine Engine Race requires the independent PaRuX Diffusers engine to be installed first; PaRuX will not fake a second candidate.");
        if (!semanticJudge.GetStatus().Ready)
            throw new InvalidOperationException(
                "Production Engine Race requires the pinned local CLIP semantic judge; complete managed media setup first.");
    }

    private static async Task<MediaEngineQualityEvidence> EvaluateProductionQualityAsync(
        string originalPrompt,
        MediaJobRequest challenge,
        MediaEngineRaceCandidate candidate,
        MediaAsset asset,
        ManagedClipImageJudge semanticJudge,
        CancellationToken ct)
    {
        MediaEngineQualityEvidence technical = await MediaImageQualityEvaluator
            .EvaluateAsync(challenge, candidate, asset, ct).ConfigureAwait(false);
        if (!technical.Accepted) return technical;

        double semantic = await semanticJudge.ScoreAsync(originalPrompt, asset, ct).ConfigureAwait(false);
        return CombineQuality(technical, semantic);
    }

    internal static MediaEngineQualityEvidence CombineQuality(
        MediaEngineQualityEvidence technical,
        double semantic)
    {
        double boundedSemantic = Math.Clamp(semantic, 0, 1);
        double combined = Math.Clamp(0.45 * technical.Score + 0.55 * boundedSemantic, 0, 1);
        bool semanticAccepted = boundedSemantic >= SemanticAcceptanceFloor;
        string reason = semanticAccepted
            ? $"technical={technical.Score:0.###}; semanticClip={boundedSemantic:0.###}; semanticFloor={SemanticAcceptanceFloor:0.###}; combined={combined:0.###}. {technical.Reason}"
            : $"Rejected for prompt adherence: semanticClip={boundedSemantic:0.###} is below semanticFloor={SemanticAcceptanceFloor:0.###}; technical={technical.Score:0.###}; combined={combined:0.###}. {technical.Reason}";
        return new MediaEngineQualityEvidence(semanticAccepted, combined, reason);
    }
}
