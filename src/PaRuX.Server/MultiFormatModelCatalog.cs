using System.Text.Json;
using PaRuX.Contracts;

namespace PaRuX.Server;

public sealed class ManagedModelCatalogEntry
{
    public ManagedModelVariantIdentity Variant { get; set; } = new(
        "local/unknown", "local", "unknown", ManagedModelFormat.Unknown, "unknown", string.Empty);
    public string? RepoId { get; set; }
    public long SizeBytes { get; set; }
    public DateTime InstalledUtc { get; set; }
    public bool UserManaged { get; set; }
    public bool StructureValidated { get; set; }
    public string? ValidationMessage { get; set; }
    public List<string> CompatibleEngineIds { get; set; } = new();
}

/// <summary>
/// Unified PaRuX model catalog above the legacy flat GGUF library. Existing GGUF downloads remain
/// untouched and are projected into the catalog at read time. New EXL3/ONNX/MLC variants are
/// registered explicitly with exact logical-model/revision/variant identity and live in managed
/// subdirectories, allowing several formats/quantizations of the same logical model to coexist.
/// </summary>
public sealed class MultiFormatModelCatalog
{
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string _root;
    private readonly string _manifestPath;
    private readonly ManagedModelRevisionStore _revisions;
    private readonly Func<bool> _hasNvidiaCudaAdapter;

    public MultiFormatModelCatalog(
        string? root = null,
        string? manifestPath = null,
        ManagedModelRevisionStore? revisions = null,
        Func<bool>? hasNvidiaCudaAdapter = null)
    {
        _root = Path.GetFullPath(root ?? ModelDownloadService.ModelsDirectory);
        _manifestPath = manifestPath ?? Path.Combine(_root, ".multi-format-library.json");
        _revisions = revisions ?? new ManagedModelRevisionStore();
        _hasNvidiaCudaAdapter = hasNvidiaCudaAdapter ?? DetectNvidiaCudaAdapter;
    }

    public string GetManagedVariantRoot(ManagedModelFormat format) =>
        Path.Combine(_root, ".formats", FormatFolder(format));

    public ManagedModelCatalogEntry Register(
        ManagedModelVariantIdentity variant,
        string? repoId = null,
        bool userManaged = false)
    {
        ValidateIdentity(variant);
        string artifact = Path.GetFullPath(variant.ArtifactPath);
        if (!IsInsideRoot(artifact, _root))
            throw new InvalidDataException("Managed model variants must live inside the PaRuX model library.");

        (bool valid, string message) = ValidateStructure(variant.Format, artifact);
        if (!valid) throw new InvalidDataException(message);

        ManagedModelVariantIdentity normalized = variant with
        {
            ArtifactPath = artifact,
            Sha256 = NormalizeOptionalSha(variant.Sha256)
        };
        var entry = new ManagedModelCatalogEntry
        {
            Variant = normalized,
            RepoId = string.IsNullOrWhiteSpace(repoId) ? null : repoId.Trim(),
            SizeBytes = ArtifactSize(artifact),
            InstalledUtc = DateTime.UtcNow,
            UserManaged = userManaged,
            StructureValidated = true,
            ValidationMessage = message
        };

        lock (Gate)
        {
            List<ManagedModelCatalogEntry> entries = LoadUnsafe();
            entries.RemoveAll(item => string.Equals(
                item.Variant.StableKey, normalized.StableKey, StringComparison.OrdinalIgnoreCase));
            entries.Add(Clone(entry));
            SaveUnsafe(entries);
        }
        return Clone(entry);
    }

    public IReadOnlyList<ManagedModelCatalogEntry> List(
        IEnumerable<ManagedModelInfo>? legacyGgufs,
        LocalInferenceEngineRegistry engines)
    {
        ArgumentNullException.ThrowIfNull(engines);
        List<ManagedModelCatalogEntry> entries;
        lock (Gate) entries = LoadUnsafe().Select(Clone).ToList();

        foreach (ManagedModelInfo model in legacyGgufs ?? Array.Empty<ManagedModelInfo>())
        {
            if (!File.Exists(model.FilePath)) continue;
            string logical = !string.IsNullOrWhiteSpace(model.RepoId)
                ? model.RepoId!
                : "local/" + Path.GetFileNameWithoutExtension(model.FileName);
            ManagedModelRevisionRecord? revision = !string.IsNullOrWhiteSpace(model.RepoId)
                ? _revisions.Find(model.FileName, model.RepoId!)
                : null;
            string revisionId = revision?.RepoRevision ?? "local";
            string? sha = revision?.LocalSha256;
            var variant = new ManagedModelVariantIdentity(
                logical,
                revisionId,
                model.FileName,
                ManagedModelFormat.Gguf,
                model.Quantization,
                Path.GetFullPath(model.FilePath),
                sha);
            if (entries.Any(item => string.Equals(item.Variant.StableKey, variant.StableKey, StringComparison.OrdinalIgnoreCase)))
                continue;
            entries.Add(new ManagedModelCatalogEntry
            {
                Variant = variant,
                RepoId = model.RepoId,
                SizeBytes = model.SizeBytes,
                InstalledUtc = model.LastWriteTimeUtc,
                StructureValidated = true,
                ValidationMessage = "Legacy managed GGUF projected into the multi-format catalog."
            });
        }

        // Read once, and only when an EXL3 variant needs it: DXGI is not free to enumerate per entry.
        bool hasNvidiaCuda = entries.Any(entry => entry.Variant.Format == ManagedModelFormat.Exl3)
            && _hasNvidiaCudaAdapter();

        foreach (ManagedModelCatalogEntry entry in entries)
        {
            IEnumerable<LocalInferenceEngineDescriptor> compatible = engines
                .CompatibleDescriptors(entry.Variant.Format, HardwareFamilyForFormat(entry.Variant.Format, hasNvidiaCuda), integratedOnly: false);
            // ONNX is a broad container format; Windows ML GenAI needs its generation manifest and
            // chat template, not merely an arbitrary graph. Do not advertise a text engine in Model
            // Hub for an ONNX classifier/encoder that the production worker would correctly reject.
            if (entry.Variant.Format == ManagedModelFormat.Onnx
                && !WindowsMlGenAiProtocol.HasServingLayout(entry.Variant.ArtifactPath, out _))
            {
                compatible = compatible.Where(descriptor => !string.Equals(
                    descriptor.Id, LocalInferenceEngineRegistry.WindowsMlGenAiId, StringComparison.OrdinalIgnoreCase));
            }
            entry.CompatibleEngineIds = compatible.Select(descriptor => descriptor.Id).ToList();
        }

        return entries
            .OrderBy(entry => entry.Variant.LogicalModelId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.Variant.Revision, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.Variant.Format)
            .ThenBy(entry => entry.Variant.VariantId, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public ManagedModelCatalogEntry? FindExact(string stableKey)
    {
        if (string.IsNullOrWhiteSpace(stableKey)) return null;
        lock (Gate)
        {
            ManagedModelCatalogEntry? entry = LoadUnsafe().FirstOrDefault(item =>
                string.Equals(item.Variant.StableKey, stableKey, StringComparison.OrdinalIgnoreCase));
            return entry == null ? null : Clone(entry);
        }
    }

    public void RemoveRegistration(string stableKey)
    {
        if (string.IsNullOrWhiteSpace(stableKey)) return;
        lock (Gate)
        {
            List<ManagedModelCatalogEntry> entries = LoadUnsafe();
            if (entries.RemoveAll(item => string.Equals(
                    item.Variant.StableKey, stableKey, StringComparison.OrdinalIgnoreCase)) > 0)
                SaveUnsafe(entries);
        }
    }

    internal static (bool Valid, string Message) ValidateStructure(ManagedModelFormat format, string artifactPath)
    {
        switch (format)
        {
            case ManagedModelFormat.Gguf:
                return File.Exists(artifactPath) && artifactPath.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase)
                    ? (true, "GGUF file exists.")
                    : (false, "GGUF variant must be an existing .gguf file.");

            case ManagedModelFormat.Exl3:
                if (!Directory.Exists(artifactPath))
                    return (false, "EXL3 variant must be a model directory.");
                if (!File.Exists(Path.Combine(artifactPath, "config.json")))
                    return (false, "EXL3 model directory is missing config.json.");
                if (!SafeFiles(artifactPath, "*.safetensors").Any())
                    return (false, "EXL3 model directory contains no safetensors weight artifact.");
                return (true, "EXL3 directory contains config.json and safetensors weights.");

            case ManagedModelFormat.Onnx:
                if (File.Exists(artifactPath) && artifactPath.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase))
                    return (true, "ONNX model file exists.");
                if (Directory.Exists(artifactPath) && SafeFiles(artifactPath, "*.onnx").Any())
                    return (true, "ONNX model directory contains at least one graph.");
                return (false, "ONNX variant must be an .onnx file or directory containing an ONNX graph.");

            case ManagedModelFormat.Mlc:
                if (!Directory.Exists(artifactPath))
                    return (false, "MLC variant must be a model directory.");
                bool hasConfig = File.Exists(Path.Combine(artifactPath, "mlc-chat-config.json"))
                    || File.Exists(Path.Combine(artifactPath, "ndarray-cache.json"));
                return hasConfig
                    ? (true, "MLC model directory contains a recognized runtime manifest.")
                    : (false, "MLC model directory has no recognized runtime manifest.");

            default:
                return (false, "Unknown model format cannot be registered.");
        }
    }

    private List<ManagedModelCatalogEntry> LoadUnsafe()
    {
        try
        {
            if (!File.Exists(_manifestPath)) return new();
            return JsonSerializer.Deserialize<List<ManagedModelCatalogEntry>>(
                File.ReadAllText(_manifestPath), JsonOptions) ?? new();
        }
        catch { return new(); }
    }

    private void SaveUnsafe(List<ManagedModelCatalogEntry> entries)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_manifestPath) ?? _root);
        string temp = _manifestPath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(entries, JsonOptions));
        File.Move(temp, _manifestPath, overwrite: true);
    }

    private static void ValidateIdentity(ManagedModelVariantIdentity variant)
    {
        ArgumentNullException.ThrowIfNull(variant);
        if (string.IsNullOrWhiteSpace(variant.LogicalModelId))
            throw new InvalidDataException("Logical model id is required.");
        if (string.IsNullOrWhiteSpace(variant.Revision))
            throw new InvalidDataException("Model revision is required.");
        if (string.IsNullOrWhiteSpace(variant.VariantId))
            throw new InvalidDataException("Model variant id is required.");
        if (variant.Format == ManagedModelFormat.Unknown)
            throw new InvalidDataException("Model format is required.");
        if (string.IsNullOrWhiteSpace(variant.ArtifactPath))
            throw new InvalidDataException("Model artifact path is required.");
    }

    private static string FormatFolder(ManagedModelFormat format) => format switch
    {
        ManagedModelFormat.Gguf => "gguf",
        ManagedModelFormat.Exl3 => "exl3",
        ManagedModelFormat.Onnx => "onnx",
        ManagedModelFormat.Mlc => "mlc",
        _ => throw new InvalidDataException("Unknown model format has no managed storage root.")
    };

    /// <summary>
    /// The hardware family a format is served on. EXL3 is CUDA-only, so without an NVIDIA adapter it has no
    /// family: a null family matches no engine that declares one, and Model Hub does not offer ExLlamaV3.
    /// </summary>
    private static string? HardwareFamilyForFormat(ManagedModelFormat format, bool hasNvidiaCudaAdapter) => format switch
    {
        ManagedModelFormat.Exl3 => hasNvidiaCudaAdapter ? "nvidia" : null,
        ManagedModelFormat.Onnx => "windows-ml",
        _ => "vulkan"
    };

    private static bool DetectNvidiaCudaAdapter() =>
        GpuInventoryService.HasNvidiaAdapterWithDedicatedVram(new GpuInventoryService().GetStatus());

    private static IEnumerable<string> SafeFiles(string root, string pattern)
    {
        try { return Directory.EnumerateFiles(root, pattern, SearchOption.TopDirectoryOnly).ToList(); }
        catch { return Array.Empty<string>(); }
    }

    private static long ArtifactSize(string path)
    {
        if (File.Exists(path))
        {
            try { return new FileInfo(path).Length; } catch { return 0; }
        }
        if (!Directory.Exists(path)) return 0;
        long total = 0;
        try
        {
            foreach (string file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                try
                {
                    long size = new FileInfo(file).Length;
                    total = size > 0 && long.MaxValue - total < size ? long.MaxValue : total + Math.Max(0, size);
                }
                catch { }
            }
        }
        catch { }
        return total;
    }

    private static bool IsInsideRoot(string path, string root)
    {
        string prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        string full = Path.GetFullPath(path);
        return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static string? NormalizeOptionalSha(string? sha)
    {
        if (string.IsNullOrWhiteSpace(sha)) return null;
        string normalized = sha.Trim().ToLowerInvariant();
        if (normalized.Length != 64 || normalized.Any(ch => !Uri.IsHexDigit(ch)))
            throw new InvalidDataException("Model SHA-256 must contain exactly 64 hexadecimal characters.");
        return normalized;
    }

    private static ManagedModelCatalogEntry Clone(ManagedModelCatalogEntry value) => new()
    {
        Variant = value.Variant,
        RepoId = value.RepoId,
        SizeBytes = value.SizeBytes,
        InstalledUtc = value.InstalledUtc,
        UserManaged = value.UserManaged,
        StructureValidated = value.StructureValidated,
        ValidationMessage = value.ValidationMessage,
        CompatibleEngineIds = value.CompatibleEngineIds.ToList()
    };
}
