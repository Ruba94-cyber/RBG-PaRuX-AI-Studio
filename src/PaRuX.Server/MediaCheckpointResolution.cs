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
    MediaRoutingDecision? Routing = null);

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
    public static MediaCheckpointEffective Resolve(string type) => ApplyCapabilityMode(type, ResolveBase(type), forJob: false);

    /// <summary>Resolve for a job about to run: records the routing trace and throws a typed refusal when a Forced
    /// checkpoint cannot be used (no substitution).</summary>
    public static MediaCheckpointEffective ResolveForJob(string type)
    {
        MediaCheckpointEffective effective = ApplyCapabilityMode(type, ResolveBase(type), forJob: true);
        if (effective.Routing is { IsRefusal: true } refusal)
            throw new MediaRoutingRefusedException(refusal.Capability, refusal.RefusalCode!, refusal.Trace, refusal.Explanation);
        return effective;
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
        if (overrideFile != null && !File.Exists(Path.Combine(LocalAiPaths.DiffusionModelsPath, overrideFile)))
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
            && File.Exists(Path.Combine(LocalAiPaths.DiffusionModelsPath, overrideFile)))
            return overrideFile;
        return ReadTemplateUnet(TemplatePathFor(type));
    }

    /// <summary>Installed diffusion files this type's workflow can run (family allowed), for the per-tab Model picker. Never throws.</summary>
    public static List<string> CompatibleFiles(string type)
    {
        try
        {
            MediaRoutingFacts facts = (MediaCapabilityRouting.FactsOverride ?? MediaCapabilityRouting.LiveFacts)(type);
            return facts.InstalledFiles.Where(f => facts.Inspect(f).FamilyAllowed).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { return new List<string>(); }
    }

    /// <summary>Why the model for the next job of this type was chosen, for the job provenance. Read-only (does not record a trace).</summary>
    public static string? SelectionReasonFor(string type)
    {
        if (MediaCapabilityRouting.CapabilityFor(type) is not { } cap) return null;
        try { return MediaCapabilityRouting.Preview(cap).Explanation; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { return null; }
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
        string path = Path.Combine(LocalAiPaths.DiffusionModelsPath, fileName);
        if (!File.Exists(path)) return null;
        MediaFamilyDetection detection = MediaFamilyDetector.DetectFile(path, MediaCompanionRegistry.Shipped);
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
