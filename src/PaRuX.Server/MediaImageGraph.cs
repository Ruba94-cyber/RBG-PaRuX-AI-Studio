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
        effective.IsOverridden ? MediaCatalogue.Shipped.FindByFileName(effective.File)?.Variant.Sampling : null;

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
