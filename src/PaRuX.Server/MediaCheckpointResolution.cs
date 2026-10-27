using System.Text.Json.Nodes;

using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>The checkpoint a media job of one type will load, and the graph inputs an override replaces.</summary>
internal sealed record MediaCheckpointEffective(
    string Type,
    string TemplateFile,
    string File,
    bool IsOverridden,
    IReadOnlyList<string> ReplacesInputs,
    string? ClearedOverrideFile,
    MediaRoutingDecision? Routing = null,
    string? FamilyId = null,
    string LoaderNode = "1",
    string LoaderClass = "UNETLoader",
    string LoaderInput = "unet_name");

/// <summary>
/// The single answer to "which checkpoint does the next <type> job load". The graph builder, the ComfyUI race
/// and the status API all call <see cref="Resolve"/>, so what the UI reports is what ComfyUI is given. It also
/// hosts the family gate that every override and activation must pass before it is saved.
/// </summary>
internal static class MediaCheckpointResolution
{
    /// <summary>Node that loads the diffusion model in every media template. Each template is patched there.</summary>
    public const string UnetNode = "1";

    public static string TemplatePathFor(string type) => type switch
    {
        "image" => LocalAiPaths.ImageWorkflowTemplate,
        "video" => LocalAiPaths.VideoWorkflowTemplate,
        "edit" => LocalAiPaths.EditWorkflowTemplate,
        _ => throw new InvalidDataException("Type must be \"image\", \"video\", or \"edit\".")
    };

    /// <summary>Resolves the checkpoint for a type. A stored override whose file has gone is cleared here, so the
    /// status and the next job both fall back to the template default and say so.</summary>
    public static MediaCheckpointEffective Resolve(string type) => WithFamily(type, ApplyCapabilityMode(type, ResolveBase(type), forJob: false));

    /// <summary>Resolve for a job about to run: records the routing trace and throws a typed refusal when a Forced
    /// checkpoint cannot be used (no substitution).</summary>
    public static MediaCheckpointEffective ResolveForJob(string type)
    {
        MediaCheckpointEffective effective = ApplyCapabilityMode(type, ResolveBase(type), forJob: true);
        if (effective.Routing is { IsRefusal: true } refusal)
            throw new MediaRoutingRefusedException(refusal.Capability, refusal.RefusalCode!, refusal.Trace, refusal.Explanation);
        return WithFamily(type, effective);
    }

    /// <summary>
    /// Resolves the checkpoint for an image-to-image job. Only families that list the img2img pipeline (Z-Image) have a graph for it, so when
    /// the active image checkpoint is another family the job uses the img2img template's own Z-Image default. It is refused by name, never
    /// substituted silently, when that default is not installed.
    /// </summary>
    public static MediaCheckpointEffective ResolveForImg2Img()
    {
        MediaCheckpointEffective effective = ResolveForJob("image");
        if (effective.FamilyId is null) return effective;
        RegistryFamily? family = MediaCompanionRegistry.Shipped.FindFamily(effective.FamilyId);
        if (family is null || family.Pipelines.Contains("img2img", StringComparer.Ordinal)) return effective;

        ConfigAssets.EnsureAll();
        string defaultFile = ReadTemplateUnet(LocalAiPaths.Img2ImgWorkflowTemplate);
        if (string.IsNullOrEmpty(defaultFile) || !File.Exists(LocalAiPaths.MainModelPath(defaultFile)))
            throw new InvalidOperationException("The active image model (" + family.DisplayName
                + ") cannot do image-to-image yet, and the Z-Image model that can (" + (string.IsNullOrEmpty(defaultFile) ? "the default" : defaultFile)
                + ") is not installed. Install or activate a Z-Image model to use image-to-image.");
        return effective with
        {
            IsOverridden = false,
            File = defaultFile,
            TemplateFile = Path.GetFileName(LocalAiPaths.Img2ImgWorkflowTemplate),
            FamilyId = null,
            LoaderNode = UnetNode,
            LoaderClass = "UNETLoader",
            LoaderInput = "unet_name",
            ReplacesInputs = Array.Empty<string>()
        };
    }

    /// <summary>The checkpoint file an image-to-image job reports as its model (see <see cref="ResolveForImg2Img"/>).</summary>
    public static string? FileNameForImg2ImgProvenance()
    {
        string? file = FileNameForProvenance("image");
        if (string.IsNullOrEmpty(file)) return file;
        string path = LocalAiPaths.MainModelPath(file);
        if (!File.Exists(path)) return file;
        MediaFamilyDetection detection = DetectCached(path, MediaCompanionRegistry.Shipped);
        RegistryFamily? family = detection.FamilyId is null ? null : MediaCompanionRegistry.Shipped.FindFamily(detection.FamilyId);
        if (family is null || family.Pipelines.Contains("img2img", StringComparer.Ordinal)) return file;
        string defaultFile = ReadTemplateUnet(LocalAiPaths.Img2ImgWorkflowTemplate);
        return string.IsNullOrEmpty(defaultFile) ? file : defaultFile;
    }

    /// <summary>The ComfyUI models folder (full path) a downloaded main model belongs in, by its family: a single-file checkpoint goes to
    /// checkpoints, every other main model to diffusion_models. An unidentified file goes to diffusion_models (the family gate refuses it earlier).</summary>
    public static string DestinationFolderFor(string sourcePath)
    {
        MediaCompanionRegistry registry = MediaCompanionRegistry.Shipped;
        MediaFamilyDetection detection = MediaFamilyDetector.DetectFile(sourcePath, registry);
        RegistryFamily? family = detection.FamilyId is null ? null : registry.FindFamily(detection.FamilyId);
        return LocalAiPaths.MainModelFolderPath(family?.MainLoader?.Folder ?? "diffusion_models");
    }

    /// <summary>Full path of the template a resolved checkpoint runs with (the family's own template when the active checkpoint is another family).</summary>
    public static string TemplatePathFor(MediaCheckpointEffective effective) => Path.Combine(LocalAiPaths.ConfigDir, effective.TemplateFile);

    /// <summary>
    /// An activated checkpoint of another supported family (SDXL, FLUX.1 schnell, Chroma) runs that family's own template and loader,
    /// not the Z-Image default. Detected from the file header (cached per size and write time), so the graph, the status API and the
    /// companion plan all describe the same file. A default (not overridden) or unidentified file keeps the template default.
    /// </summary>
    private static MediaCheckpointEffective WithFamily(string type, MediaCheckpointEffective effective)
    {
        if (!effective.IsOverridden || string.IsNullOrEmpty(effective.File)) return effective;
        string path = LocalAiPaths.MainModelPath(effective.File);
        if (!File.Exists(path)) return effective;
        MediaCompanionRegistry registry = MediaCompanionRegistry.Shipped;
        MediaFamilyDetection detection = DetectCached(path, registry);
        RegistryFamily? family = detection.FamilyId is null ? null : registry.FindFamily(detection.FamilyId);
        if (family is not { IsSupported: true, MainLoader: { } loader } || family.TemplateFile is null
            || !family.Pipelines.Contains(type, StringComparer.Ordinal))
            return effective;
        return effective with
        {
            TemplateFile = family.TemplateFile,
            FamilyId = family.Id,
            LoaderNode = loader.Node,
            LoaderClass = loader.ClassType,
            LoaderInput = loader.Input,
            ReplacesInputs = new[] { loader.ClassType + "." + loader.Input + " (node " + loader.Node + " of " + family.TemplateFile + ")" }
        };
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (long Length, DateTime WrittenUtc, MediaFamilyDetection Detection)> DetectionCache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Family detection that reads a file's header once per (size, write time); status polls call Resolve often.</summary>
    internal static MediaFamilyDetection DetectCached(string path, MediaCompanionRegistry registry)
    {
        try
        {
            var info = new FileInfo(path);
            if (DetectionCache.TryGetValue(path, out var hit) && hit.Length == info.Length && hit.WrittenUtc == info.LastWriteTimeUtc)
                return hit.Detection;
            MediaFamilyDetection detection = MediaFamilyDetector.DetectFile(path, registry);
            // A shipped registry is immutable, so the verdict only changes with the file; test registries bypass the cache.
            if (ReferenceEquals(registry, MediaCompanionRegistry.Shipped)) DetectionCache[path] = (info.Length, info.LastWriteTimeUtc, detection);
            return detection;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return MediaFamilyDetector.DetectFile(path, registry);
        }
    }

    private static MediaCheckpointEffective ApplyCapabilityMode(string type, MediaCheckpointEffective baseline, bool forJob)
    {
        string? capability = MediaCapabilityRouting.CapabilityFor(type);
        if (capability is null) return baseline;
        MediaRoutingDecision decision = forJob ? MediaCapabilityRouting.DecideForJob(capability) : MediaCapabilityRouting.Preview(capability);
        if (decision.UsesOverride)
            return baseline with { File = decision.ChosenFile!, IsOverridden = true, Routing = decision };
        return baseline with { Routing = decision };
    }

    private static MediaCheckpointEffective ResolveBase(string type)
    {
        string template = TemplatePathFor(type);
        string templateFile = Path.GetFileName(template);
        string defaultFile = ReadTemplateUnet(template);

        string? overrideFile = LocalAiPaths.GetMediaCheckpointOverride(type);
        string? cleared = null;
        if (overrideFile != null && !File.Exists(LocalAiPaths.MainModelPath(overrideFile)))
        {
            LocalAiPaths.ClearMediaCheckpointOverride(type);
            cleared = overrideFile;
            overrideFile = null;
        }

        return new MediaCheckpointEffective(
            type,
            templateFile,
            overrideFile ?? defaultFile,
            overrideFile != null,
            new[] { "UNETLoader.unet_name (node " + UnetNode + " of " + templateFile + ")" },
            cleared);
    }

    /// <summary>Returns the selected checkpoint basename without changing a stale stored override.</summary>
    public static string? FileNameForProvenance(string type)
    {
        if (type is not ("image" or "video" or "edit")) return null;
        if (MediaCapabilityRouting.CapabilityFor(type) is { } cap
            && MediaCapabilityRouting.Preview(cap) is { UsesOverride: true } routed)
            return routed.ChosenFile;
        string? overrideFile = LocalAiPaths.GetMediaCheckpointOverride(type);
        if (!string.IsNullOrWhiteSpace(overrideFile)
            && File.Exists(LocalAiPaths.MainModelPath(overrideFile)))
            return overrideFile;
        return ReadTemplateUnet(TemplatePathFor(type));
    }

    /// <summary>Refuses a checkpoint whose family the given media type cannot run, and names the families it can.
    /// Runs before an override is saved and before an activation moves a file, so a refused file is never left behind.</summary>
    public static void EnsureFamilyAllowed(string type, string filePath)
        => EnsureFamilyAllowed(type, filePath, out _);

    public static void EnsureFamilyAllowed(string type, string filePath, out MediaDependencyError? refusal)
    {
        refusal = null;
        MediaCompanionRegistry registry = MediaCompanionRegistry.Shipped;
        IReadOnlyList<RegistryFamily> allowed = registry.SupportedFor(type);
        if (allowed.Count == 0)
            throw new InvalidDataException("PaRuX has no workflow for the " + type + " type, so no checkpoint can be assigned to it.");

        string names = string.Join(", ", allowed.Select(family => family.DisplayName));
        MediaFamilyDetection detection = MediaFamilyDetector.DetectFile(filePath, registry);
        if (detection.FamilyId == null)
        {
            string message = "PaRuX could not identify this file's model family (" + (detection.Reason ?? "unknown")
                + "). The " + type + " workflow runs " + names + " only.";
            refusal = BuildFamilyRefusal("checkpoint_family_unidentified", filePath, message,
                "Choose a .safetensors checkpoint from the supported families: " + names + ".");
            throw new InvalidDataException(message);
        }
        if (!allowed.Any(family => string.Equals(family.Id, detection.FamilyId, StringComparison.Ordinal)))
        {
            string message = "This file is a " + detection.FamilyName + " checkpoint, which the " + type
                + " workflow cannot load. The " + type + " workflow runs " + names + " only.";
            refusal = BuildFamilyRefusal("checkpoint_family_unsupported", filePath, message,
                "Choose a " + names + " checkpoint for the " + type + " workflow.");
            throw new InvalidDataException(message);
        }
    }

    private static MediaDependencyError BuildFamilyRefusal(string reason, string filePath, string message, string nextAction) => new()
    {
        Reason = reason,
        Role = "main_model",
        ExpectedFileName = filePath.EndsWith(".safetensors.partial", StringComparison.OrdinalIgnoreCase)
            ? Path.GetFileName(filePath[..^".partial".Length])
            : Path.GetFileName(filePath),
        ComfyFolder = "diffusion_models",
        NextAction = nextAction,
        Message = message
    };

    /// <summary>Family name read from an installed diffusion file's header, or null when it cannot be identified.</summary>
    public static string? FamilyOfInstalledFile(string fileName)
    {
        string path = LocalAiPaths.MainModelPath(fileName);
        if (!File.Exists(path)) return null;
        MediaFamilyDetection detection = DetectCached(path, MediaCompanionRegistry.Shipped);
        return detection.FamilyName;
    }

    private static string ReadTemplateUnet(string template)
    {
        if (!File.Exists(template)) return string.Empty;
        try
        {
            // Templates are ComfyUI API bodies: the graph sits under "prompt", and SetIn patches the same node.
            JsonObject? body = JsonNode.Parse(File.ReadAllText(template))?.AsObject();
            return body?["prompt"]?[UnetNode]?["inputs"]?["unet_name"]?.GetValue<string>() ?? string.Empty;
        }
        catch (Exception exception) when (exception is IOException or System.Text.Json.JsonException or InvalidOperationException)
        {
            return string.Empty;
        }
    }
}
