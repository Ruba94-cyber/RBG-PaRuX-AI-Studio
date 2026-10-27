namespace PaRuX.Server;

/// <summary>Puts a finished checkpoint download into ComfyUI's model folder without ever leaving a truncated file under its final name.</summary>
internal static class MediaCheckpointPlacement
{
    /// <summary>
    /// Moves <paramref name="source"/> to <paramref name="destination"/> through a ".part" file and checks the size. A complete file already
    /// at the destination is kept (the duplicate source is removed); a different-sized one (a crashed earlier copy) is replaced.
    /// </summary>
    internal static void MoveIntoPlace(string source, string destination, long expectedSize)
    {
        if (File.Exists(destination))
        {
            if (expectedSize <= 0 || new FileInfo(destination).Length == expectedSize)
            {
                if (File.Exists(source)) File.Delete(source);
                return;
            }
            File.Delete(destination);
        }
        if (!File.Exists(source)) throw new FileNotFoundException("The downloaded checkpoint was not found.", source);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        string part = destination + ".part";
        if (File.Exists(part)) File.Delete(part);
        File.Move(source, part);
        if (expectedSize > 0 && new FileInfo(part).Length != expectedSize)
        {
            long actual = new FileInfo(part).Length;
            File.Delete(part);
            throw new IOException("The checkpoint is " + actual + " bytes but " + expectedSize + " were expected; it was not installed.");
        }
        File.Move(part, destination);
    }
    /// <summary>The ComfyUI model folder a media file of this kind belongs in. A main model ("checkpoint", "diffusion_model", "unet") is decided
    /// by its family (use <see cref="MediaCheckpointResolution.DestinationFolderFor"/> for a file); companions follow the registry folders.</summary>
    internal static string? ComfyFolderForKind(string? kind) => kind?.Trim().ToLowerInvariant() switch
    {
        "checkpoint" or "checkpoints" or "ckpt" => "checkpoints",
        "diffusion_model" or "diffusion_models" or "unet" => "diffusion_models",
        "vae" => "vae",
        "text_encoder" or "text_encoders" or "clip" => "text_encoders",
        "lora" or "loras" => "loras",
        _ => null
    };

    /// <summary>Places a staged main-model .safetensors (downloaded into the text-model folder) into the ComfyUI folder its family needs.
    /// Never replaces a different file already at the destination and never touches anything that is not a .safetensors file.
    /// With <paramref name="keepSource"/> the source stays (a copy) so a file the user manages is never removed.</summary>
    internal static MediaPlacementResult PlaceMainModel(string stagedPath, bool keepSource = false)
    {
        string name = Path.GetFileName(stagedPath);
        if (!name.EndsWith(".safetensors", StringComparison.OrdinalIgnoreCase) || !File.Exists(stagedPath))
            return new MediaPlacementResult(MediaPlacementOutcome.NotApplicable, stagedPath, "Not a .safetensors media checkpoint on disk; left where it is.");
        if (!Directory.Exists(LocalAiPaths.ComfyApp))
            return new MediaPlacementResult(MediaPlacementOutcome.Deferred, stagedPath,
                "ComfyUI is not installed yet, so " + name + " stays in the model folder and moves into ComfyUI when you install it.");

        string destination = Path.Combine(MediaCheckpointResolution.DestinationFolderFor(stagedPath), name);
        long size = new FileInfo(stagedPath).Length;
        if (File.Exists(destination))
        {
            return new FileInfo(destination).Length == size
                ? new MediaPlacementResult(MediaPlacementOutcome.AlreadyInPlace, destination, name + " is already installed in ComfyUI; the copy in the model folder was left untouched.")
                : new MediaPlacementResult(MediaPlacementOutcome.Failed, stagedPath,
                    "A different " + name + " is already in " + Path.GetFileName(Path.GetDirectoryName(destination)) + "; it was not replaced. Rename or remove one of them.");
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            string part = destination + ".part";
            if (File.Exists(part)) File.Delete(part);
            if (keepSource) File.Copy(stagedPath, part); else File.Move(stagedPath, part);
            if (new FileInfo(part).Length != size)
            {
                File.Delete(part);
                return new MediaPlacementResult(MediaPlacementOutcome.Failed, stagedPath, "The copy of " + name + " was incomplete and was discarded; the original is unchanged.");
            }
            File.Move(part, destination);
            return new MediaPlacementResult(keepSource ? MediaPlacementOutcome.Copied : MediaPlacementOutcome.Placed, destination,
                name + (keepSource ? " was copied into ComfyUI " : " was moved into ComfyUI ") + Path.GetFileName(Path.GetDirectoryName(destination)) + ".");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new MediaPlacementResult(MediaPlacementOutcome.Failed, File.Exists(stagedPath) ? stagedPath : destination, "Could not place " + name + ": " + ex.Message);
        }
    }

    /// <summary>The installed path to activate for a file name: the text-model folder copy (not yet placed), otherwise the main-model copy
    /// already in ComfyUI. Null when neither exists.</summary>
    internal static string? FindActivationSource(string fileName)
    {
        string staged = Path.Combine(ModelDownloadService.ModelsDirectory, fileName);
        if (File.Exists(staged)) return staged;
        string installed = LocalAiPaths.MainModelPath(fileName);
        return File.Exists(installed) ? installed : null;
    }
}

internal enum MediaPlacementOutcome { Placed, Copied, AlreadyInPlace, Deferred, NotApplicable, Failed }

internal sealed record MediaPlacementResult(MediaPlacementOutcome Outcome, string Path, string Message)
{
    internal bool Installed => Outcome is MediaPlacementOutcome.Placed or MediaPlacementOutcome.Copied or MediaPlacementOutcome.AlreadyInPlace;
}
