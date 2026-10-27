using PaRuX.Contracts;

namespace PaRuX.Server;

internal enum MediaSetupStepState { Done, Pending, Blocked }

/// <summary>One thing a clean PC still needs before Create can generate. Download fields are set only for pending steps that download.</summary>
internal sealed record MediaSetupStep(
    string Id, string Title, MediaSetupStepState State, long SizeBytes, string Detail,
    string? FileName = null, string? RepoId = null, string? DownloadUrl = null, string? Revision = null, string License = "",
    string? Sha256 = null);

internal sealed record MediaSetupPlan(IReadOnlyList<MediaSetupStep> Steps)
{
    internal bool Ready => Steps.All(s => s.State == MediaSetupStepState.Done);
}

/// <summary>What exists on this PC right now (all read from disk by the caller; this type never touches it).</summary>
/// <param name="MissingCompanionBytes">Bytes of the active image checkpoint's missing text encoder and VAE; 0 when none are
/// missing; null when it cannot be known yet because no image checkpoint is installed.</param>
internal sealed record MediaSetupFacts(
    bool ComfyInstalled, bool AssistantModelPresent, ISet<string> InstalledCheckpoints, long? MissingCompanionBytes = null)
{
    /// <summary>
    /// Missing companion bytes for a companion plan: null when the plan cannot say (family not supported/identified, so "nothing missing" would be
    /// a false all-clear); 0 only for a supported family with nothing missing; at least 1 when something is missing but has no recorded size.
    /// </summary>
    internal static long? CompanionBytes(PaRuX.Contracts.MediaCompanionPlan plan) =>
        plan.Support != "supported" ? null
        : plan.Missing.Count == 0 ? 0
        : Math.Max(1, plan.Missing.Sum(m => m.PinnedSizeBytes ?? m.ApproxSizeBytes ?? 0));
}

/// <summary>
/// Pure planner for the guided "Set up image generation" flow. Only catalogue variants that really have a download source are
/// proposed: the catalogue's bundled defaults (nvfp4, flux-2-klein-4b, wan2.2) have none and cannot exist on a clean PC.
/// </summary>
internal static class MediaSetupPlanner
{
    internal const string ComfyStepId = "comfyui";
    internal const string AssistantStepId = "assistant-model";
    internal const string CheckpointStepId = "image-checkpoint";
    internal const string CompanionsStepId = "image-companions";
    internal const string ComfyLicense = "GPL-3.0";
    internal const string AssistantLicense = "Apache-2.0";
    /// <summary>The licence recorded for the pinned text encoders and VAEs in the companion registry (checked against each repository card).</summary>
    internal const string CompanionLicense = "Apache-2.0";

    internal static string CheckpointStepIdFor(string type) => type + "-checkpoint";
    internal static string CompanionsStepIdFor(string type) => type + "-companions";

    internal static string CapabilityFor(string type) => type switch
    {
        "video" => PaRuX.Contracts.ModelCapabilityIds.VideoGenerate,
        "edit" => PaRuX.Contracts.ModelCapabilityIds.ImageEdit,
        _ => PaRuX.Contracts.ModelCapabilityIds.ImageGenerate
    };

    /// <summary>Approximate sizes shown before the real download plan is read; the live start re-plans exactly.</summary>
    internal const long ComfyApproxBytes = 2_018_444_114;
    internal const long AssistantApproxBytes = 2_740_937_888;

    internal static MediaSetupPlan Plan(
        MediaCatalogue catalogue, MediaHardware hw, MediaSetupFacts facts, Func<string, double?> lastJobSeconds, string type = "image")
    {
        var steps = new List<MediaSetupStep>
        {
            facts.ComfyInstalled
                ? new(ComfyStepId, "Image engine (ComfyUI)", MediaSetupStepState.Done, 0, "Installed.", License: ComfyLicense)
                : new(ComfyStepId, "Image engine (ComfyUI)", MediaSetupStepState.Pending, ComfyApproxBytes,
                    "PaRuX installs the official portable ComfyUI build with CUDA PyTorch.", License: ComfyLicense),
            facts.AssistantModelPresent
                ? new(AssistantStepId, "Prompt assistant model", MediaSetupStepState.Done, 0, "Installed.", License: AssistantLicense)
                : new(AssistantStepId, "Prompt assistant model", MediaSetupStepState.Pending, AssistantApproxBytes,
                    "A small local text model (Qwen3.5-4B Q4_K_M) that prepares prompts for the image engine.",
                    "Qwen3.5-4B-Q4_K_M.gguf", "unsloth/Qwen3.5-4B-GGUF", License: AssistantLicense),
            CheckpointStep(catalogue, hw, facts, lastJobSeconds, type)
        };
        steps.Add(CompanionsStep(catalogue, hw, facts, steps[^1], type));
        return new MediaSetupPlan(steps);
    }

    private static MediaSetupStep CompanionsStep(
        MediaCatalogue catalogue, MediaHardware hw, MediaSetupFacts facts, MediaSetupStep checkpoint, string type)
    {
        const string title = "Text encoder and VAE";
        string companionsId = CompanionsStepIdFor(type);
        if (checkpoint.State == MediaSetupStepState.Blocked)
            return new(companionsId, title, MediaSetupStepState.Blocked, 0, "Needs a supported model first.", License: CompanionLicense);
        if (checkpoint.State == MediaSetupStepState.Done && facts.MissingCompanionBytes == 0)
            return new(companionsId, title, MediaSetupStepState.Done, 0, "Installed.", License: CompanionLicense);
        long bytes = facts.MissingCompanionBytes is > 0 and var known ? known : EstimateCompanionBytes(catalogue, checkpoint, type);
        return new(companionsId, title, MediaSetupStepState.Pending, bytes,
            "The text encoder and VAE this model needs; downloaded from their pinned, checksum-verified sources.", License: CompanionLicense);
    }

    private static long EstimateCompanionBytes(MediaCatalogue catalogue, MediaSetupStep checkpoint, string type)
    {
        MediaCatalogueFamily? family = catalogue.VariantsFor(CapabilityFor(type))
            .Where(x => string.Equals(x.Variant.FileName, checkpoint.FileName, StringComparison.OrdinalIgnoreCase))
            .Select(x => x.Family).FirstOrDefault();
        return (long)(((family?.TextEncoderGb ?? 8.0) + (family?.VaeGb ?? 0.33)) * 1024 * 1024 * 1024);
    }

    private static MediaSetupStep CheckpointStep(
        MediaCatalogue catalogue, MediaHardware hw, MediaSetupFacts facts, Func<string, double?> lastJobSeconds, string type)
    {
        string title = type switch { "video" => "Video model", "edit" => "Image editing model", _ => "Image model" };
        string checkpointId = CheckpointStepIdFor(type);
        MediaSelection selection = MediaSelectionEngine.Select(
            catalogue, CapabilityFor(type), "standard", hw, facts.InstalledCheckpoints, lastJobSeconds);

        MediaEvaluatedVariant? installed = selection.Ranked.FirstOrDefault(v => v.Family.IsSupported && v.Installed);
        if (installed is not null)
            return new(checkpointId, title, MediaSetupStepState.Done, 0, installed.Variant.Title + " is installed.", installed.Variant.FileName,
                License: installed.Variant.License);

        MediaEvaluatedVariant? pick = selection.Ranked.FirstOrDefault(v =>
            v.Selectable && v.Variant.ListingVerified && !v.Variant.Bundled && v.Variant.DownloadUrl is not null
            && v.Variant.Sha256 is not null);
        if (pick is null)
            return new(checkpointId, title, MediaSetupStepState.Blocked, 0,
                "No supported image model fits this PC's memory (" + selection.Explanation + ").");

        return new(checkpointId, title, MediaSetupStepState.Pending, pick.Variant.SizeBytes ?? pick.Fit.WeightBytes,
            pick.Variant.Title + ": " + pick.Fit.Reason, pick.Variant.FileName, pick.Variant.Repository, pick.Variant.DownloadUrl,
            pick.Variant.Revision, pick.Variant.License, pick.Variant.Sha256);
    }
}
