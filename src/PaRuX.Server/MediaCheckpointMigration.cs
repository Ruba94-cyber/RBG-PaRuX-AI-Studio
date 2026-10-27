namespace PaRuX.Server;

/// <summary>Body of POST /api/v1/media/checkpoints/misplaced/migrate.</summary>
internal sealed class MigrateMisplacedCheckpointsRequest
{
    public bool IncludeUserManaged { get; set; }
}

/// <summary>One media checkpoint found in the text-model folder, with what was (or would be) done about it.</summary>
internal sealed record MisplacedMediaCheckpoint(
    string FileName, string FamilyId, string FamilyName, string TargetFolder, long SizeBytes, bool PaRuXManaged, string Status, string Message);

/// <summary>
/// Finds main-model .safetensors files (SDXL, FLUX, Chroma, ...) that an older build left in the text-model folder, recognised by their
/// safetensors header, and installs them into the ComfyUI folder their family needs. A file PaRuX downloaded (it has a provenance entry)
/// is moved. Any other file is the user's: it is only ever copied, and only when the caller asks for it, so the original stays.
/// .gguf and every unrecognised file are never touched. Nothing is deleted.
/// </summary>
internal static class MediaCheckpointMigration
{
    internal static IReadOnlyList<MisplacedMediaCheckpoint> Find(string? modelsDirectory = null) => Run(modelsDirectory, apply: false, includeUserManaged: false);

    /// <summary>Moves PaRuX-downloaded checkpoints; user-managed ones are copied only when <paramref name="includeUserManaged"/>.</summary>
    internal static IReadOnlyList<MisplacedMediaCheckpoint> Migrate(bool includeUserManaged, string? modelsDirectory = null) =>
        Run(modelsDirectory, apply: true, includeUserManaged);

    private static IReadOnlyList<MisplacedMediaCheckpoint> Run(string? modelsDirectory, bool apply, bool includeUserManaged)
    {
        string dir = modelsDirectory ?? ModelDownloadService.ModelsDirectory;
        var result = new List<MisplacedMediaCheckpoint>();
        if (!Directory.Exists(dir)) return result;
        MediaCompanionRegistry registry = MediaCompanionRegistry.Shipped;
        List<string> files;
        try { files = Directory.EnumerateFiles(dir, "*.safetensors", SearchOption.TopDirectoryOnly).ToList(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return result; }

        foreach (string path in files.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            string name = Path.GetFileName(path);
            long size;
            try { size = new FileInfo(path).Length; } catch (IOException) { continue; }
            if (size <= 0) continue;
            MediaFamilyDetection detection = MediaFamilyDetector.DetectFile(path, registry);
            RegistryFamily? family = detection.FamilyId is null ? null : registry.FindFamily(detection.FamilyId);
            if (family is not { IsSupported: true, MainLoader: not null }) continue;

            bool managed = ModelDownloadProvenanceManifestStore.Get(dir, name) is not null;
            string folder = LocalAiPaths.MainModelFolderPath(family.MainLoader.Folder);
            string folderName = Path.GetFileName(folder);
            string status, message;
            if (!apply)
            {
                status = "found";
                message = managed
                    ? name + " is a " + family.DisplayName + " model PaRuX downloaded but left in the text-model folder. It can be moved into ComfyUI " + folderName + "."
                    : name + " looks like a " + family.DisplayName + " model in the text-model folder. It is yours, so PaRuX would only copy it into ComfyUI " + folderName + " and keep the original.";
            }
            else if (!managed && !includeUserManaged)
            {
                status = "offered";
                message = name + " is not a PaRuX download, so it was left alone. Confirm to copy it into ComfyUI " + folderName + " (the original stays).";
            }
            else
            {
                MediaPlacementResult placed = MediaCheckpointPlacement.PlaceMainModel(path, keepSource: !managed);
                status = placed.Outcome switch
                {
                    MediaPlacementOutcome.Placed => "moved",
                    MediaPlacementOutcome.Copied => "copied",
                    MediaPlacementOutcome.AlreadyInPlace => "already_installed",
                    MediaPlacementOutcome.Deferred => "waiting_for_comfyui",
                    _ => "failed"
                };
                message = placed.Message;
            }
            result.Add(new MisplacedMediaCheckpoint(name, family.Id, family.DisplayName, folder, size, managed, status, message));
        }
        return result;
    }
}
