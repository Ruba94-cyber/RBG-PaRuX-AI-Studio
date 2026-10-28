using System.Globalization;
using System.Text.Json.Nodes;
using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>One LoRA of a stack after it passed validation.</summary>
internal sealed record ValidatedLora(string Name, double Strength, string? Family);

/// <summary>Result of checking a requested LoRA stack. <see cref="Reason"/> is written for the user and is the
/// text a refusal carries; <see cref="Warnings"/> are accepted-but-unverified facts (for example a LoRA whose
/// family PaRuX cannot read).</summary>
internal sealed record MediaLoraStackCheck(
    bool Ok, string? Code, string? Reason, IReadOnlyList<string> Warnings, IReadOnlyList<ValidatedLora> Loras)
{
    public static MediaLoraStackCheck Refuse(string code, string reason) =>
        new(false, code, reason, Array.Empty<string>(), Array.Empty<ValidatedLora>());
}

/// <summary>Body of POST /api/v1/media/loras/validate.</summary>
public sealed class MediaLoraStackRequest
{
    public List<MediaLoraSelection>? Loras { get; set; }
}

/// <summary>One row of GET /api/v1/media/loras/installed. Compatibility is compatible | incompatible | unknown.</summary>
public sealed record MediaLoraInstalledItem(
    string Name, long SizeBytes, string? BaseModel, string? Family, string FamilyName, string Compatibility, IReadOnlyList<string> TriggerWords);

public sealed record MediaLoraInstalledResponse(
    string? CheckpointFile, string? CheckpointFamily, string CheckpointFamilyName, int MaxLoras, double MaxStrength,
    IReadOnlyList<MediaLoraInstalledItem> Loras);

/// <summary>
/// Multi-LoRA stack for the image pipelines: validation (count, names, strengths, family match) and injection of
/// chained <c>LoraLoader</c> nodes into a ComfyUI API graph. Pure with respect to the file system: callers pass the
/// installed list and the checkpoint family, so every rule is unit-testable. A LoRA is refused only when its family is
/// known and differs from the checkpoint's; a LoRA whose family cannot be read is accepted with a warning, because
/// refusing every hand-copied file would be wrong and guessing a family would be worse.
/// </summary>
internal static class MediaLoraStack
{
    public const int MaxLoras = 3;
    public const double MaxStrength = 2.0;

    /// <summary>Graph node ids start here so they never collide with the numeric ids the templates use.</summary>
    internal const int FirstLoraNodeId = 900;

    /// <summary>Maps a registry checkpoint family id to a LoRA family key, or null when no key applies.</summary>
    public static string? KeyForRegistryFamily(string? registryFamilyId) => registryFamilyId switch
    {
        "z-image" => "zimage",
        "flux2-klein" => "flux2",
        "flux1" => "flux",
        "qwen-image" => "qwen",
        "wan2.2-ti2v-5b" => "wan",
        "sdxl" => "sdxl",
        "flux1-schnell" => "flux",
        "chroma" => "chroma",
        _ => null
    };

    /// <summary>Family key from a free-text label such as a LoRA sidecar "baseModel" or a file name.
    /// Underscores and dashes count as spaces so "z_image_turbo" and "Z-Image" are the same.</summary>
    public static string? KeyForLabel(string? label)
    {
        if (string.IsNullOrWhiteSpace(label)) return null;
        string text = label.Replace('_', ' ').Replace('-', ' ').ToLowerInvariant();
        string? coarse = LoraCompatibility.Family(text);
        if (coarse == "flux")
        {
            bool second = text.Contains("klein") || text.Contains("flux 2") || text.Contains("flux2");
            return second ? "flux2" : "flux";
        }
        return coarse;
    }

    public static string DisplayName(string? key) => key switch
    {
        "zimage" => "Z-Image",
        "flux2" => "FLUX.2",
        "flux" => "FLUX.1",
        "qwen" => "Qwen-Image",
        "wan" => "Wan",
        "sdxl" => "SDXL",
        "chroma" => "Chroma",
        "sd15" => "SD 1.5",
        "sd3" => "SD 3",
        _ => "unknown"
    };

    /// <summary>The installed list the Create tab and the Hub show, each LoRA marked against the active checkpoint's family.</summary>
    public static MediaLoraInstalledResponse Describe(IReadOnlyList<InstalledLora> installed, string? checkpointFamilyKey, string? checkpointFile) =>
        new(checkpointFile, checkpointFamilyKey, DisplayName(checkpointFamilyKey), MaxLoras, MaxStrength,
            installed.Select(l =>
            {
                string? key = LoraKey(l);
                string compat = key == null || checkpointFamilyKey == null ? "unknown"
                    : string.Equals(key, checkpointFamilyKey, StringComparison.Ordinal) ? "compatible" : "incompatible";
                return new MediaLoraInstalledItem(l.Name, l.SizeBytes, l.BaseModel, key, DisplayName(key), compat, l.TriggerWords);
            }).ToList());

    /// <summary>The family key of an installed LoRA: the sidecar's base model first, then the file name.</summary>
    public static string? LoraKey(InstalledLora lora) => KeyForLabel(lora.BaseModel) ?? KeyForLabel(lora.Name);

    public static MediaLoraStackCheck Validate(
        IReadOnlyList<MediaLoraSelection>? requested,
        IReadOnlyList<InstalledLora> installed,
        string? checkpointFamilyKey,
        string? checkpointLabel)
    {
        if (requested == null || requested.Count == 0)
            return new MediaLoraStackCheck(true, null, null, Array.Empty<string>(), Array.Empty<ValidatedLora>());
        if (requested.Count > MaxLoras)
            return MediaLoraStackCheck.Refuse("lora_too_many", "A LoRA stack holds at most " + MaxLoras + " LoRAs; " + requested.Count + " were requested.");

        var accepted = new List<ValidatedLora>();
        var warnings = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (MediaLoraSelection selection in requested)
        {
            string name = (selection?.Name ?? string.Empty).Trim();
            if (name.Length == 0)
                return MediaLoraStackCheck.Refuse("lora_name_missing", "A LoRA in the stack has no file name.");
            if (!seen.Add(name))
                return MediaLoraStackCheck.Refuse("lora_duplicate", "\"" + name + "\" appears twice in the LoRA stack; use each LoRA once.");
            double strength = selection!.Strength;
            if (double.IsNaN(strength) || double.IsInfinity(strength) || strength <= 0 || strength > MaxStrength)
                return MediaLoraStackCheck.Refuse("lora_strength_out_of_range",
                    "Strength for \"" + name + "\" must be above 0 and at most " + MaxStrength.ToString("0.0", CultureInfo.InvariantCulture) + ".");

            InstalledLora? lora = installed.FirstOrDefault(l => string.Equals(l.Name, name, StringComparison.Ordinal));
            if (lora == null)
                return MediaLoraStackCheck.Refuse("lora_not_installed",
                    "\"" + name + "\" is not an installed LoRA. Install it first; PaRuX only uses files in ComfyUI's loras folder.");

            string? loraKey = LoraKey(lora);
            if (checkpointFamilyKey != null && loraKey != null && !string.Equals(checkpointFamilyKey, loraKey, StringComparison.Ordinal))
                return MediaLoraStackCheck.Refuse("lora_family_mismatch",
                    "\"" + name + "\" is a " + DisplayName(loraKey) + " LoRA but the active checkpoint"
                    + (string.IsNullOrWhiteSpace(checkpointLabel) ? string.Empty : " (" + checkpointLabel + ")")
                    + " is " + DisplayName(checkpointFamilyKey) + ". A LoRA only works with the model family it was trained for.");
            if (loraKey == null)
                warnings.Add("The family of \"" + name + "\" is not recorded, so PaRuX could not check it against the checkpoint.");
            else if (checkpointFamilyKey == null)
                warnings.Add("The active checkpoint's family could not be identified, so \"" + name + "\" was not checked against it.");
            accepted.Add(new ValidatedLora(name, Math.Round(strength, 3), loraKey));
        }
        return new MediaLoraStackCheck(true, null, null, warnings, accepted);
    }

    /// <summary>Chains one LoraLoader per LoRA between the model/CLIP loaders and everything that consumed them.
    /// Works on the loaders the shipped templates use (UNETLoader or CheckpointLoaderSimple for the model, CLIPLoader,
    /// DualCLIPLoader or the checkpoint for CLIP). Throws when the graph has no such loader, so a LoRA is never silently dropped.</summary>
    public static void Inject(JsonObject request, IReadOnlyList<ValidatedLora> loras)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (loras.Count == 0) return;
        JsonObject graph = request["prompt"] as JsonObject ?? request;

        (string Id, int Output)? model = FindSource(graph, new[] { "UNETLoader", "CheckpointLoaderSimple" }, 0);
        (string Id, int Output)? clip = FindSource(graph, new[] { "CLIPLoader", "DualCLIPLoader" }, 0)
            ?? FindSource(graph, new[] { "CheckpointLoaderSimple" }, 1);
        if (model == null || clip == null)
            throw new InvalidOperationException("This workflow has no model and CLIP loader to attach LoRAs to.");

        string modelRef = model.Value.Id, clipRef = clip.Value.Id;
        int modelOut = model.Value.Output, clipOut = clip.Value.Output;
        var created = new HashSet<string>(StringComparer.Ordinal);
        string lastId = string.Empty;
        for (int i = 0; i < loras.Count; i++)
        {
            string id = (FirstLoraNodeId + i).ToString(CultureInfo.InvariantCulture);
            created.Add(id);
            graph[id] = new JsonObject
            {
                ["class_type"] = "LoraLoader",
                ["inputs"] = new JsonObject
                {
                    ["model"] = new JsonArray(modelRef, modelOut),
                    ["clip"] = new JsonArray(clipRef, clipOut),
                    ["lora_name"] = loras[i].Name,
                    ["strength_model"] = loras[i].Strength,
                    ["strength_clip"] = loras[i].Strength
                }
            };
            modelRef = clipRef = id;
            modelOut = 0;
            clipOut = 1;
            lastId = id;
        }

        // Rewire every other consumer of the original loaders to the end of the chain.
        string origModel = model.Value.Id, origClip = clip.Value.Id;
        foreach (KeyValuePair<string, JsonNode?> node in graph)
        {
            if (created.Contains(node.Key) || node.Value?["inputs"] is not JsonObject inputs) continue;
            foreach (string key in inputs.Select(i => i.Key).ToList())
            {
                if (inputs[key] is not JsonArray link || link.Count != 2) continue;
                string? from = link[0] is JsonValue fromValue && fromValue.TryGetValue(out string? fromText) ? fromText : null;
                int index = link[1] is JsonValue idx && idx.TryGetValue(out int parsed) ? parsed : -1;
                if (from == origModel && index == model.Value.Output && IsModelInput(key))
                    inputs[key] = new JsonArray(lastId, 0);
                else if (from == origClip && index == clip.Value.Output && key == "clip")
                    inputs[key] = new JsonArray(lastId, 1);
            }
        }
    }

    private static bool IsModelInput(string key) => key == "model";

    private static (string Id, int Output)? FindSource(JsonObject graph, string[] classTypes, int output)
    {
        foreach (KeyValuePair<string, JsonNode?> node in graph)
        {
            string? classType = node.Value?["class_type"]?.GetValue<string>();
            if (classType != null && classTypes.Contains(classType, StringComparer.Ordinal)) return (node.Key, output);
        }
        return null;
    }

    /// <summary>Reads the installed LoRAs and the checkpoint family the next job of <paramref name="checkpointType"/> loads,
    /// validates the request and returns the accepted stack. Throws <see cref="InvalidDataException"/> with the user-facing
    /// reason when it must be refused.</summary>
    internal static MediaLoraStackCheck ResolveOrRefuse(MediaCheckpointEffective effective, IReadOnlyList<MediaLoraSelection>? requested)
    {
        if (requested == null || requested.Count == 0) return Validate(requested, Array.Empty<InstalledLora>(), null, null);
        string? key = KeyForRegistryFamily(effective.FamilyId) ?? KeyForLabel(effective.File);
        string? label = effective.File;
        MediaLoraStackCheck check = Validate(requested, LoraLibrary.List(), key, label);
        if (!check.Ok) throw new InvalidDataException(check.Reason);
        return check;
    }

    /// <summary>The LoRA family key and file name of the checkpoint the next job of this type would load.</summary>
    internal static (string? Key, string? File) ActiveCheckpointFamily(string checkpointType)
    {
        string file;
        try { file = MediaCheckpointResolution.Resolve(checkpointType).File; }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException)
        { return (null, null); }
        string? key = null;
        try
        {
            string path = LocalAiPaths.MainModelPath(file); // full checkpoints (SDXL) live under models/checkpoints, not diffusion_models
            if (File.Exists(path))
                key = KeyForRegistryFamily(MediaFamilyDetector.DetectFile(path, MediaCompanionRegistry.Shipped).FamilyId);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { }
        return (key ?? KeyForLabel(file), file);
    }
}
