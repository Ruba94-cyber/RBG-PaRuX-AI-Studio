using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>Companion plan for the checkpoint the next job of a media type will load. Read-only.</summary>
internal static class MediaCompanionPlanReader
{
    internal static MediaCompanionPlan Build(string type)
    {
        MediaCheckpointEffective effective = MediaCheckpointResolution.Resolve(type);
        string checkpointPath = LocalAiPaths.MainModelPath(effective.File);
        MediaFamilyDetection detection = File.Exists(checkpointPath)
            ? MediaFamilyDetector.DetectFile(checkpointPath, MediaCompanionRegistry.Shipped)
            : new MediaFamilyDetection(null, null, "unidentified",
                "The checkpoint " + effective.File + " is not installed in ComfyUI's diffusion_models or checkpoints folder.", false, false, 0);
        string[] folders = MediaCompanionRegistry.Shipped.Families
            .SelectMany(family => family.Companions)
            .Select(companion => companion.Folder)
            .Append("diffusion_models")
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var installed = MediaCompanionResolver.ScanFolders(Path.Combine(LocalAiPaths.ComfyApp, "models"), folders);
        return MediaCompanionResolver.BuildPlan(type, effective.File, detection, MediaCompanionRegistry.Shipped, installed);
    }
}
