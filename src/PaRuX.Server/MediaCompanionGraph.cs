using System.Text.Json.Nodes;
using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>Applies the resolver's registry-derived companion choices to a ComfyUI API graph.</summary>
internal static class MediaCompanionGraph
{
    public static MediaCompanionPlan PrepareSubmissionGraph(
        JsonObject request,
        string type,
        MediaFamilyDetection detection,
        MediaCompanionRegistry registry,
        IReadOnlyDictionary<string, IReadOnlyList<InstalledComfyFile>> installed)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(detection);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(installed);

        RegistryFamily? family = detection.FamilyId == null ? null : registry.FindFamily(detection.FamilyId);
        if (family == null)
            throw new InvalidOperationException(detection.Reason ?? "The selected checkpoint family could not be identified.");
        if (!family.IsSupported || !family.Pipelines.Contains(type, StringComparer.Ordinal))
            throw new InvalidOperationException(family.Reason
                ?? family.DisplayName + " cannot run the " + type + " workflow.");

        string mainFileName = ReadMainFileName(request, type, registry);
        MediaCompanionPlan plan = MediaCompanionResolver.BuildPlan(
            type, mainFileName, detection, registry, installed);
        ApplyResolvedNames(request, family, plan);
        return plan;
    }

    public static string ReadMainFileName(JsonObject request, string type, MediaCompanionRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(registry);

        JsonObject prompt = request["prompt"] as JsonObject
            ?? throw new InvalidDataException("The workflow has no ComfyUI prompt graph.");
        RegistryMainLoader[] loaders = registry.Families
            .Where(family => family.IsSupported && family.Pipelines.Contains(type, StringComparer.Ordinal))
            .Select(family => family.MainLoader)
            .Where(loader => loader != null)
            .Cast<RegistryMainLoader>()
            .GroupBy(loader => (loader.Node, loader.ClassType, loader.Input))
            .Select(group => group.First())
            .ToArray();

        var matches = new List<string>();
        foreach (RegistryMainLoader loader in loaders)
        {
            if (prompt[loader.Node] is not JsonObject node
                || !string.Equals((string?)node["class_type"], loader.ClassType, StringComparison.Ordinal)
                || node["inputs"] is not JsonObject inputs)
                continue;

            string? fileName = (string?)inputs[loader.Input];
            if (!string.IsNullOrWhiteSpace(fileName)) matches.Add(fileName);
        }

        if (matches.Count != 1
            || !string.Equals(Path.GetFileName(matches[0]), matches[0], StringComparison.Ordinal))
            throw new InvalidDataException("The workflow does not contain one valid registry-defined main checkpoint input.");
        return matches[0];
    }

    private static void ApplyResolvedNames(JsonObject request, RegistryFamily family, MediaCompanionPlan plan)
    {
        if (!string.Equals(plan.FamilyId, family.Id, StringComparison.Ordinal)
            || !string.Equals(plan.Support, "supported", StringComparison.Ordinal)
            || plan.Companions.Count != family.Companions.Count)
            throw new InvalidDataException("The resolver plan does not match the selected workflow family.");

        JsonObject prompt = request["prompt"]!.AsObject();
        if (family.Companions.Count(companion => companion.Role == "text_encoder") != 1
            || family.Companions.Count(companion => companion.Role == "vae") != 1)
            throw new InvalidDataException("The workflow requires exactly one registered text encoder and VAE companion.");

        for (int i = 0; i < family.Companions.Count; i++)
        {
            RegistryCompanion declared = family.Companions[i];
            MediaCompanionItem resolved = plan.Companions[i];
            if (!string.Equals(declared.Role, resolved.Role, StringComparison.Ordinal)
                || !string.Equals(declared.Folder, resolved.Folder, StringComparison.Ordinal)
                || !string.Equals(declared.ClipType, resolved.ClipType, StringComparison.Ordinal))
                throw new InvalidDataException("The resolver companion order does not match the registry.");

            if (string.IsNullOrWhiteSpace(resolved.FileName)
                || !string.Equals(Path.GetFileName(resolved.FileName), resolved.FileName, StringComparison.Ordinal))
                throw new InvalidDataException("The resolver returned an invalid companion file name.");

            switch (declared.Role)
            {
                case "text_encoder":
                    if (string.IsNullOrWhiteSpace(resolved.ClipType))
                        throw new InvalidDataException("The registry text encoder has no CLIP type.");
                    SetLoaderInput(prompt, "CLIPLoader", "clip_name", resolved.FileName, resolved.ClipType);
                    break;
                case "vae":
                    SetLoaderInput(prompt, "VAELoader", "vae_name", resolved.FileName, null);
                    break;
                default:
                    throw new InvalidDataException("The workflow does not support the registered companion role " + declared.Role + ".");
            }
        }

    }

    private static void SetLoaderInput(
        JsonObject prompt,
        string classType,
        string fileInput,
        string fileName,
        string? clipType)
    {
        JsonObject[] nodes = prompt
            .Where(pair => pair.Value is JsonObject node
                && string.Equals((string?)node["class_type"], classType, StringComparison.Ordinal))
            .Select(pair => (JsonObject)pair.Value!)
            .ToArray();
        if (nodes.Length != 1)
            throw new InvalidDataException("The workflow must contain exactly one " + classType + " node.");

        JsonObject inputs = nodes[0]["inputs"] as JsonObject
            ?? throw new InvalidDataException("The workflow " + classType + " node has no inputs.");
        inputs[fileInput] = fileName;
        if (clipType != null) inputs["type"] = clipType;
    }
}
