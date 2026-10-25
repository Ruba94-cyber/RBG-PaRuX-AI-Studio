namespace PaRuX.Server;

/// <summary>Outcome of identifying a checkpoint from its safetensors header. FamilyId is null when the file
/// matches no registered family, or more than one, and Reason then says which.</summary>
internal sealed record MediaFamilyDetection(
    string? FamilyId,
    string? FamilyName,
    string Support,
    string? Reason,
    bool EmbedsTextEncoder,
    bool EmbedsVae,
    long TensorCount);

/// <summary>
/// Identifies a diffusion checkpoint family from the tensor names and the few shape checks declared in the
/// registry. Only the header is read (see <see cref="SafetensorsHeaderReader"/>). A file that matches two
/// families is unidentified, never a guess.
/// </summary>
internal static class MediaFamilyDetector
{
    public static MediaFamilyDetection DetectFile(string path, MediaCompanionRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        string extension = Path.GetExtension(path);
        bool stagedSafetensors = string.Equals(extension, ".partial", StringComparison.OrdinalIgnoreCase)
            && path.EndsWith(".safetensors.partial", StringComparison.OrdinalIgnoreCase);
        if (!string.Equals(extension, ".safetensors", StringComparison.OrdinalIgnoreCase) && !stagedSafetensors)
            return Unidentified(registry, "Only .safetensors headers can be read; this file is " + (extension.Length == 0 ? "unnamed" : extension) + ".");

        SafetensorsHeader header;
        try
        {
            header = SafetensorsHeaderReader.ReadFile(path);
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            // A locked, missing or hostile file is reported as unidentified, so status endpoints still answer.
            return Unidentified(registry, "The file header could not be read: " + exception.Message);
        }
        return Detect(header, registry);
    }

    public static MediaFamilyDetection Detect(SafetensorsHeader header, MediaCompanionRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(registry);

        bool embedsTextEncoder = registry.EmbeddedStems.TextEncoder.Any(stem => AnyTensorStartsWith(header, stem));
        bool embedsVae = registry.EmbeddedStems.Vae.Any(stem => AnyTensorStartsWith(header, stem));
        var matches = registry.Families.Where(family => Matches(family, header)).ToList();

        if (matches.Count == 0)
            return new MediaFamilyDetection(null, null, "unidentified",
                "No registered family matches this file's tensor layout.", embedsTextEncoder, embedsVae, header.Tensors.Count);
        if (matches.Count > 1)
            return new MediaFamilyDetection(null, null, "unidentified",
                "The tensor layout matches more than one family (" + string.Join(", ", matches.Select(family => family.Id)) + ").",
                embedsTextEncoder, embedsVae, header.Tensors.Count);

        RegistryFamily family = matches[0];
        return new MediaFamilyDetection(family.Id, family.DisplayName, family.Support,
            family.IsSupported ? null : family.Reason, embedsTextEncoder, embedsVae, header.Tensors.Count);
    }

    private static bool Matches(RegistryFamily family, SafetensorsHeader header)
    {
        foreach (string stem in family.Signature.RequiredStems)
            if (!AnyTensorStartsWith(header, stem)) return false;
        foreach (string stem in family.Signature.ForbiddenStems)
            if (AnyTensorStartsWith(header, stem)) return false;
        foreach (RegistryShapeCheck check in family.Signature.ShapeChecks)
        {
            SafetensorsTensor? tensor = header.Tensors.FirstOrDefault(candidate => candidate.Name == check.Tensor);
            if (tensor == null || tensor.Shape.Length <= check.Dim || tensor.Shape[check.Dim] != check.Expected) return false;
        }
        return true;
    }

    private static bool AnyTensorStartsWith(SafetensorsHeader header, string stem)
    {
        foreach (SafetensorsTensor tensor in header.Tensors)
            if (tensor.Name.StartsWith(stem, StringComparison.Ordinal)) return true;
        return false;
    }

    private static MediaFamilyDetection Unidentified(MediaCompanionRegistry registry, string reason) =>
        new(null, null, "unidentified", reason, false, false, 0);
}
