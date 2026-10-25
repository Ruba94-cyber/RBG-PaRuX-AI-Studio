using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>
/// The image/video/edit counterpart to RecommendedModelsService - curated "download this instead"
/// picks for the checkpoint-swap feature (LocalAiPaths.SetMediaCheckpointOverride /
/// ComfyWorkflowRunner.ApplyCheckpointOverrideIfSet). Restricted to real, verified files from the
/// SAME model family already wired into each type's workflow template's CLIP/VAE (z-image-smoke-api.json,
/// wan-video-smoke-api.json, flux2-klein-edit-api.json) - an unrelated architecture's diffusion
/// weights would load into UNETLoader fine but produce garbage or an outright crash against a
/// mismatched text encoder/VAE, so this list only ever offers same-family precision/training
/// variants, not a general model catalog.
///
/// Every repo id, file path, and size below was checked live against Hugging Face's API
/// (existence + sibling file list) and a HEAD request (X-Linked-Size) before being added here.
/// Video has no entry: Wan2.2-TI2V-5B's official Comfy-Org repackage ships exactly one diffusion_models
/// precision (the fp16 file already bundled) - there's currently no verified same-family alternative
/// to recommend, and this list would rather say nothing than guess.
/// </summary>
public static class RecommendedCheckpointsService
{
    public static IReadOnlyList<RecommendedCheckpoint> List { get; } = new List<RecommendedCheckpoint>
    {
        new()
        {
            Id = "z-image-turbo-bf16", Type = "image", Title = "Z-Image Turbo (bf16, full precision)",
            CapabilityId = ModelCapabilityIds.ImageGenerate,
            Description = "Workflow-compatible full-precision variant. It uses the paired text encoder and VAE; image-quality and generation-time differences still need a same-prompt local comparison.",
            HuggingFaceRepoId = "Comfy-Org/z_image_turbo",
            FileName = "z_image_turbo_bf16.safetensors",
            DownloadUrl = "https://huggingface.co/Comfy-Org/z_image_turbo/resolve/main/split_files/diffusion_models/z_image_turbo_bf16.safetensors",
            EstimatedSizeBytes = 12_309_866_400
        },
        new()
        {
            Id = "z-image-turbo-int8", Type = "image", Title = "Z-Image Turbo (int8, mid precision)",
            CapabilityId = ModelCapabilityIds.ImageGenerate,
            Description = "Workflow-compatible int8 variant. File size and precision differ from the bundled checkpoint; quality and generation speed are not yet compared on this PC.",
            HuggingFaceRepoId = "Comfy-Org/z_image_turbo",
            FileName = "z_image_turbo_int8_convrot.safetensors",
            DownloadUrl = "https://huggingface.co/Comfy-Org/z_image_turbo/resolve/main/split_files/diffusion_models/z_image_turbo_int8_convrot.safetensors",
            EstimatedSizeBytes = 6_201_001_296
        },
        new()
        {
            Id = "flux2-klein-base-4b", Type = "edit", Title = "FLUX.2 [klein] 4B - base (non-distilled)",
            CapabilityId = ModelCapabilityIds.ImageEdit,
            Description = "The base training checkpoint behind PaRuX's default edit model (which is klein's distilled/turbo variant) - same 4B architecture, same text encoder and VAE pairing, but from before speed-distillation. Similar file size; likely needs more sampling steps than the distilled default for its full quality, so treat it as an experiment rather than a guaranteed upgrade.",
            HuggingFaceRepoId = "fwwrsd/comfy-org-flux2-klein-4b",
            FileName = "flux-2-klein-base-4b.safetensors",
            DownloadUrl = "https://huggingface.co/fwwrsd/comfy-org-flux2-klein-4b/resolve/main/split_files/diffusion_models/flux-2-klein-base-4b.safetensors",
            EstimatedSizeBytes = 7_751_105_712
        }
    };
}
