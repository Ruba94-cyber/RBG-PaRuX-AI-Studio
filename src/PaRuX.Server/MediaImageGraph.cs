using System.Text.Json.Nodes;

namespace PaRuX.Server;

/// <summary>
/// Applies the active image checkpoint to a ComfyUI API graph, for every image family (Z-Image, SDXL, FLUX.1 schnell, Chroma).
/// All image templates share one node-id contract: "1" main loader, "4" positive prompt, "6" latent size, "8" KSampler, "10" SaveImage.
/// </summary>
internal static class MediaImageGraph
{
    internal const string SamplerNode = "8";

    /// <summary>Writes the activated checkpoint file into the family's own loader input and applies the sampler settings the
    /// checkpoint's catalogue entry recommends. A default (not activated) checkpoint leaves the template untouched.</summary>
    internal static void ApplyEffective(JsonObject request, MediaCheckpointEffective effective, bool applySampling = true)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(effective);
        if (!effective.IsOverridden) return;
        SetInput(request, effective.LoaderNode, effective.LoaderInput, effective.File);
        if (applySampling && SamplingFor(effective) is { } hint) ApplySampling(request, hint);
    }

    /// <summary>The sampler hint of the catalogue variant installed under the effective file name, when the catalogue lists one.</summary>
    internal static MediaSamplingHint? SamplingFor(MediaCheckpointEffective effective) =>
        !effective.IsOverridden ? null
            : MediaCatalogue.Shipped.FindByFileName(effective.File)?.Variant.Sampling
              ?? DistilledSdxlSampling(effective.FamilyId, effective.File);

    /// <summary>
    /// A step-distilled SDXL checkpoint that is not in the catalogue (SDXL-Turbo, SDXL-Lightning, LCM) must not run the base graph's
    /// 28 steps at CFG 6: it was trained for 1 to 8 steps and CFG about 1, which also lets ComfyUI skip the negative-prompt pass.
    /// Running it at base settings is what made SDXL-Turbo (5.7 s) slower than FLUX schnell (3.7 s) in the measured comparison, and it
    /// produces over-baked images besides. Recognised by the file name only, for the SDXL family only; anything else keeps the template.
    /// </summary>
    internal static MediaSamplingHint? DistilledSdxlSampling(string? familyId, string? fileName)
    {
        if (!string.Equals(familyId, "sdxl", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(fileName)) return null;
        string name = Path.GetFileNameWithoutExtension(fileName).ToLowerInvariant();
        if (name.Contains("turbo", StringComparison.Ordinal)) return new MediaSamplingHint(4, 1.0, "euler_ancestral", "simple");
        if (name.Contains("lightning", StringComparison.Ordinal)) return new MediaSamplingHint(4, 1.0, "euler", "sgm_uniform");
        if (name.Contains("lcm", StringComparison.Ordinal)) return new MediaSamplingHint(6, 1.5, "lcm", "sgm_uniform");
        return null;
    }

    internal static void ApplySampling(JsonObject request, MediaSamplingHint hint)
    {
        if (request["prompt"]?[SamplerNode]?["inputs"] is not JsonObject inputs) return;
        if (hint.Steps is { } steps) inputs["steps"] = steps;
        if (hint.Cfg is { } cfg) inputs["cfg"] = cfg;
        if (hint.Sampler is { } sampler) inputs["sampler_name"] = sampler;
        if (hint.Scheduler is { } scheduler) inputs["scheduler"] = scheduler;
    }

    /// <summary>Tag-trained checkpoints (Pony, NoobAI, Animagine) are steered by quality tags their card asks for; the catalogue entry
    /// carries them and they are put in front of the prompt. Natural-language families have none and the prompt is unchanged.</summary>
    internal static string WithPromptPrefix(string prompt, MediaCheckpointEffective effective)
    {
        if (!effective.IsOverridden) return prompt;
        string? prefix = MediaCatalogue.Shipped.FindByFileName(effective.File)?.Variant.PromptPrefix;
        if (string.IsNullOrWhiteSpace(prefix) || prompt.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return prompt;
        return prefix.TrimEnd().TrimEnd(',') + ", " + prompt;
    }

    private static void SetInput(JsonObject request, string node, string input, string value)
    {
        if (request["prompt"]?[node]?["inputs"] is not JsonObject inputs)
            throw new InvalidDataException("The workflow template has no node \"" + node + "\" to load the checkpoint into.");
        inputs[input] = value;
    }
}
