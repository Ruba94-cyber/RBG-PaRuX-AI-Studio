using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using PaRuX.Contracts;
using PaRuX.Intelligence;

namespace PaRuX.Server;

/// <summary>
/// Finds the GGUF models that Ollama and LM Studio already store on this machine, and imports one into
/// PaRuX's managed library. Discovery only reads. It never writes to, renames or deletes anything in
/// another application's store.
///
/// Import hard-links the file into the library when both are on one volume, so no bytes are duplicated.
/// A copy happens only when the link is impossible, the caller gave explicit consent, and the free space
/// covers it. Deleting a library entry removes only that link, never the other application's file.
/// </summary>
public static class ExternalModelStoreImporter
{
    public const string OllamaSource = "ollama";
    public const string LmStudioSource = "lmstudio";
    public const string JanSource = "jan";
    public const string KoboldCppSource = "koboldcpp";

    /// <summary>Free space kept after a copy, so a copy can never fill the volume.</summary>
    public const long CopyReserveBytes = 1L << 30;

    private const string OllamaModelMediaType = "application/vnd.ollama.image.model";
    private const int MaximumScanDepth = 5;
    private const long MaximumManifestBytes = 1L << 20;
    private const string OllamaTextOnlyNote =
        "Ollama stores no separate vision projector for this model, so the import runs text-only.";

    /// <summary>The directories to read. Null or missing directories are simply not scanned.</summary>
    public sealed record StoreRoots(
        string? OllamaModelsDirectory,
        IReadOnlyList<string> LmStudioDirectories,
        IReadOnlyList<string>? JanDirectories = null,
        IReadOnlyList<string>? KoboldCppDirectories = null);

    /// <summary>What one store root produced, so an unrecognised layout is reported instead of silently empty.</summary>
    public sealed record StoreReport(string Source, string Root, bool RootExists, int ModelCount, string? Note);

    /// <summary>The default locations: OLLAMA_MODELS or ~/.ollama/models, and the LM Studio model folders.</summary>
    public static StoreRoots DefaultRoots()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string? ollama = Environment.GetEnvironmentVariable("OLLAMA_MODELS");
        if (string.IsNullOrWhiteSpace(ollama)) ollama = Path.Combine(home, ".ollama", "models");
        return new StoreRoots(ollama,
            new[] { Path.Combine(home, ".cache", "lm-studio", "models"), Path.Combine(home, ".lmstudio", "models") },
            Extra(JanDirectoriesVariable, new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Jan", "data", "models"),
                Path.Combine(home, "jan", "data", "models"),
                Path.Combine(home, "jan", "models")
            }),
            // KoboldCpp has no managed store: these are only the folders its users most often keep GGUF files in.
            Extra(KoboldCppDirectoriesVariable, new[] { Path.Combine(home, "koboldcpp"), Path.Combine(home, "KoboldCpp", "models") }));
    }

    public const string JanDirectoriesVariable = "PARUX_JAN_MODEL_DIRS";
    public const string KoboldCppDirectoriesVariable = "PARUX_KOBOLDCPP_MODEL_DIRS";

    /// <summary>Defaults plus any extra folders the user set in the named variable (path-separator list).</summary>
    public static IReadOnlyList<string> Extra(string variable, IReadOnlyList<string> defaults, Func<string, string?>? read = null)
    {
        string? raw = (read ?? Environment.GetEnvironmentVariable)(variable);
        if (string.IsNullOrWhiteSpace(raw)) return defaults;
        return defaults.Concat(raw.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(Path.IsPathRooted)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>One report per configured root: found models, or an honest reason there are none.</summary>
    public static IReadOnlyList<StoreReport> Inspect(StoreRoots roots, string managedDirectory)
    {
        var reports = new List<StoreReport>();
        if (!string.IsNullOrWhiteSpace(roots.OllamaModelsDirectory))
        {
            string root = roots.OllamaModelsDirectory;
            bool exists = Directory.Exists(root);
            int count = DiscoverOllama(root, managedDirectory).Count();
            string? note = !exists ? "Folder not found."
                : !Directory.Exists(Path.Combine(root, "manifests")) || !Directory.Exists(Path.Combine(root, "blobs"))
                    ? "Folder exists but has no manifests/blobs layout, so it was not recognised as an Ollama store."
                    : count == 0 ? "Ollama store recognised, but no readable GGUF model layer was found." : null;
            reports.Add(new StoreReport(OllamaSource, root, exists, count, note));
        }
        void Folders(string source, string label, IReadOnlyList<string>? folders)
        {
            foreach (string root in folders ?? Array.Empty<string>())
            {
                bool exists = Directory.Exists(root);
                int count = exists ? DiscoverGgufFolder(root, source, managedDirectory, VisionProjectorManifest.Default).Count() : 0;
                reports.Add(new StoreReport(source, root, exists, count,
                    !exists ? "Folder not found." : count == 0 ? "No valid GGUF files found in this " + label + " folder (the layout may be different)." : null));
            }
        }
        Folders(LmStudioSource, "LM Studio", roots.LmStudioDirectories);
        Folders(JanSource, "Jan", roots.JanDirectories);
        Folders(KoboldCppSource, "KoboldCpp", roots.KoboldCppDirectories);
        return reports;
    }

    /// <summary>Lists the GGUF models in the given stores. Read-only.</summary>
    public static IReadOnlyList<ExternalModelCandidate> Discover(
        StoreRoots roots, string managedDirectory, VisionProjectorManifest? manifest = null)
    {
        manifest ??= VisionProjectorManifest.Default;
        var result = new List<ExternalModelCandidate>();
        if (!string.IsNullOrWhiteSpace(roots.OllamaModelsDirectory))
            result.AddRange(DiscoverOllama(roots.OllamaModelsDirectory, managedDirectory));
        foreach (string lmStudioRoot in roots.LmStudioDirectories)
            result.AddRange(DiscoverGgufFolder(lmStudioRoot, LmStudioSource, managedDirectory, manifest));
        foreach (string janRoot in roots.JanDirectories ?? Array.Empty<string>())
            result.AddRange(DiscoverGgufFolder(janRoot, JanSource, managedDirectory, manifest));
        foreach (string koboldRoot in roots.KoboldCppDirectories ?? Array.Empty<string>())
            result.AddRange(DiscoverGgufFolder(koboldRoot, KoboldCppSource, managedDirectory, manifest));
        return result;
    }

    /// <summary>
    /// Imports one discovered model into the managed library. <paramref name="sourcePath"/> must be the
    /// SourcePath of a candidate that discovery returns now. Any other path is refused.
    /// </summary>
    public static ManagedModelInfo Import(
        string sourcePath,
        bool copyConsent,
        StoreRoots roots,
        string managedDirectory,
        VisionProjectorManifest? manifest = null,
        Func<string, string, bool>? hardLink = null,
        Func<string, long>? freeSpace = null,
        CancellationToken cancellationToken = default,
        IProgress<long>? copyProgress = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string wanted = Path.GetFullPath(sourcePath);
        ExternalModelCandidate candidate = Discover(roots, managedDirectory, manifest)
            .FirstOrDefault(c => string.Equals(Path.GetFullPath(c.SourcePath), wanted, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException("That file is not a model found in Ollama or LM Studio. Refresh the list and choose again.");
        if (candidate.AlreadyImported)
            throw new InvalidOperationException("That model is already in the managed library.");

        Directory.CreateDirectory(managedDirectory);
        string modelDestination = Path.Combine(managedDirectory, candidate.SuggestedFileName);
        string? projectorDestination = candidate.ProjectorSourcePath == null
            ? null
            : Path.Combine(managedDirectory, Path.GetFileName(candidate.ProjectorSourcePath));
        if (File.Exists(modelDestination) || (projectorDestination != null && File.Exists(projectorDestination)))
            throw new InvalidOperationException("A managed model with that file name already exists. Rename or remove it first.");

        var created = new List<string>();
        try
        {
            Place(candidate.SourcePath, modelDestination, managedDirectory, copyConsent, hardLink, freeSpace, created,
                cancellationToken, copyProgress);
            if (candidate.ProjectorSourcePath != null && projectorDestination != null)
                Place(candidate.ProjectorSourcePath, projectorDestination, managedDirectory, copyConsent, hardLink, freeSpace,
                    created, cancellationToken, copyProgress);
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch
        {
            foreach (string path in created)
            {
                try { File.Delete(path); } catch { /* best-effort rollback of files this call created */ }
            }
            throw;
        }

        FileInfo info = new(modelDestination);
        return new ManagedModelInfo
        {
            FileName = info.Name,
            FilePath = info.FullName,
            SizeBytes = info.Length,
            LastWriteTimeUtc = info.LastWriteTimeUtc,
            Quantization = GgufFileNaming.QuantizationOrUnknown(info.Name),
            ProjectorFileName = projectorDestination == null ? null : Path.GetFileName(projectorDestination)
        };
    }

    private static void Place(
        string source, string destination, string managedDirectory, bool copyConsent,
        Func<string, string, bool>? hardLink, Func<string, long>? freeSpace, List<string> created,
        CancellationToken cancellationToken, IProgress<long>? copyProgress)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // A hard link adds a name for the same bytes, so the library holds no second copy.
        bool linked = hardLink?.Invoke(destination, source) ?? TryCreateHardLink(destination, source);
        if (linked)
        {
            created.Add(destination);
            return;
        }

        if (!copyConsent)
            throw new InvalidOperationException(
                "The model cannot be hard-linked into the managed library (it is on another volume, or the file system has no hard links). "
                + "Copying uses extra disk space equal to the model size, so it needs explicit consent.");

        long size = new FileInfo(source).Length;
        long free = (freeSpace ?? DefaultFreeSpace)(managedDirectory);
        if (free < size + CopyReserveBytes)
            throw new InvalidOperationException(
                "Not enough free space to copy this model: it needs " + FormatGiB(size) + " plus " + FormatGiB(CopyReserveBytes)
                + " kept free, and " + FormatGiB(free) + " is available.");

        CopyWithCancellation(source, destination, created, cancellationToken, copyProgress);
    }

    private const int CopyBufferBytes = 1 << 20;

    private static void CopyWithCancellation(
        string source, string destination, List<string> created,
        CancellationToken cancellationToken, IProgress<long>? copyProgress)
    {
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read,
            CopyBufferBytes, FileOptions.SequentialScan);
        using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            CopyBufferBytes, FileOptions.SequentialScan);
        created.Add(destination);

        byte[] buffer = new byte[CopyBufferBytes];
        long copied = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int read = input.Read(buffer, 0, buffer.Length);
            if (read == 0) break;

            output.Write(buffer, 0, read);
            copied += read;
            copyProgress?.Report(copied);
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    private static IEnumerable<ExternalModelCandidate> DiscoverOllama(string root, string managedDirectory)
    {
        string manifestsRoot = Path.Combine(root, "manifests");
        string blobsRoot = Path.Combine(root, "blobs");
        if (!Directory.Exists(manifestsRoot)) yield break;

        var seenBlobs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string manifestPath in EnumerateFiles(manifestsRoot, "*", MaximumScanDepth))
        {
            (string displayName, string blobPath)? model = ReadOllamaModelLayer(manifestPath, manifestsRoot, blobsRoot);
            if (model is not { } found || !seenBlobs.Add(found.blobPath)) continue;
            if (!IsGgufFile(found.blobPath)) continue;

            string suggested = SuggestedFileName(found.displayName);
            long size = new FileInfo(found.blobPath).Length;
            yield return new ExternalModelCandidate
            {
                Source = OllamaSource,
                DisplayName = found.displayName,
                SourcePath = found.blobPath,
                SizeBytes = size,
                CopySizeBytes = size,
                SuggestedFileName = suggested,
                AlreadyImported = IsAlreadyImported(managedDirectory, suggested, size),
                SameVolumeAsLibrary = SameVolume(found.blobPath, managedDirectory),
                TextOnlyNote = OllamaTextOnlyNote
            };
        }
    }

    private static IEnumerable<ExternalModelCandidate> DiscoverGgufFolder(
        string root, string source, string managedDirectory, VisionProjectorManifest manifest)
    {
        if (!Directory.Exists(root)) yield break;
        List<string> files = EnumerateFiles(root, "*.gguf", MaximumScanDepth).ToList();
        foreach (string modelPath in files.Where(p => !GgufFileNaming.IsProjector(p)))
        {
            if (!IsGgufFile(modelPath)) continue;
            string folder = Path.GetDirectoryName(modelPath) ?? root;
            string fileName = Path.GetFileName(modelPath);
            long size = new FileInfo(modelPath).Length;

            // The projector pairs by family and only in the same folder, the way LM Studio lays out a vision model.
            // The family comes from the path inside the store, never from where the store lives on this machine.
            VisionFamily? family = manifest.FamilyFor(Path.GetRelativePath(root, modelPath));
            string? projector = family == null ? null : manifest.ChooseProjector(files.Where(p =>
                GgufFileNaming.IsProjector(p)
                && string.Equals(Path.GetDirectoryName(p), folder, StringComparison.OrdinalIgnoreCase)
                && manifest.FamilyFor(Path.GetRelativePath(root, p))?.Id == family.Id));

            yield return new ExternalModelCandidate
            {
                Source = source,
                DisplayName = Path.GetRelativePath(root, modelPath).Replace('\\', '/'),
                SourcePath = modelPath,
                SizeBytes = size,
                CopySizeBytes = size + (projector == null ? 0 : new FileInfo(projector).Length),
                SuggestedFileName = fileName,
                AlreadyImported = IsAlreadyImported(managedDirectory, fileName, size),
                SameVolumeAsLibrary = SameVolume(modelPath, managedDirectory),
                ProjectorSourcePath = projector,
                TextOnlyNote = family is { VisionCapable: true } && projector == null
                    ? "No vision projector sits beside this model in its folder, so it runs text-only."
                    : null
            };
        }
    }

    /// <summary>The model layer of one Ollama manifest, with a display name built from its path.</summary>
    private static (string DisplayName, string BlobPath)? ReadOllamaModelLayer(string manifestPath, string manifestsRoot, string blobsRoot)
    {
        try
        {
            if (new FileInfo(manifestPath).Length > MaximumManifestBytes) return null;
            string[] parts = Path.GetRelativePath(manifestsRoot, manifestPath)
                .Replace('\\', '/')
                .Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) return null;
            string tag = parts[^1];
            string name = parts[^2];
            string ns = parts.Length >= 4 ? parts[^3] : "library";
            string display = (ns == "library" ? string.Empty : ns + "/") + name + ":" + tag;

            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(manifestPath));
            if (!document.RootElement.TryGetProperty("layers", out JsonElement layers) || layers.ValueKind != JsonValueKind.Array)
                return null;
            foreach (JsonElement layer in layers.EnumerateArray())
            {
                if (!layer.TryGetProperty("mediaType", out JsonElement media) || media.GetString() != OllamaModelMediaType) continue;
                if (!layer.TryGetProperty("digest", out JsonElement digestElement)) return null;
                string digest = digestElement.GetString() ?? string.Empty;
                if (!digest.StartsWith("sha256:", StringComparison.Ordinal)) return null;
                string hex = digest["sha256:".Length..];
                if (hex.Length != 64 || !hex.All(Uri.IsHexDigit)) return null;
                return (display, Path.Combine(blobsRoot, "sha256-" + hex.ToLowerInvariant()));
            }
            return null;
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Files under <paramref name="root"/> matching <paramref name="pattern"/>, to a bounded depth.
    /// Reparse points (symbolic links and junctions) are never followed, so a loop cannot run forever.</summary>
    private static IEnumerable<string> EnumerateFiles(string root, string pattern, int maximumDepth)
    {
        var pending = new Queue<(string Directory, int Depth)>();
        pending.Enqueue((root, 0));
        while (pending.Count > 0)
        {
            (string directory, int depth) = pending.Dequeue();
            IEnumerable<string> entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(directory);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            foreach (string entry in entries)
            {
                FileAttributes attributes;
                try { attributes = File.GetAttributes(entry); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { continue; }
                if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if (depth < maximumDepth) pending.Enqueue((entry, depth + 1));
                    continue;
                }
                if (MatchesPattern(Path.GetFileName(entry), pattern)) yield return entry;
            }
        }
    }

    private static bool MatchesPattern(string fileName, string pattern) =>
        pattern == "*" || fileName.EndsWith(pattern[1..], StringComparison.OrdinalIgnoreCase);

    /// <summary>True when the file starts with the GGUF magic. Reads four bytes and never writes.</summary>
    private static bool IsGgufFile(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            Span<byte> magic = stackalloc byte[4];
            if (stream.Read(magic) != 4 || !magic.SequenceEqual("GGUF"u8)) return false;
            // The header's version field must be a known GGUF version; a truncated or foreign file is not offered.
            return stream.Read(magic) == 4 && System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(magic) is >= 1 and <= 3;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool IsAlreadyImported(string managedDirectory, string fileName, long size)
    {
        string path = Path.Combine(managedDirectory, fileName);
        return File.Exists(path) && new FileInfo(path).Length == size;
    }

    private static bool SameVolume(string source, string managedDirectory)
    {
        try
        {
            return string.Equals(
                Path.GetPathRoot(Path.GetFullPath(source)),
                Path.GetPathRoot(Path.GetFullPath(managedDirectory)),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>A simple .gguf name from a display name: letters, digits, dot, dash and underscore only.</summary>
    private static string SuggestedFileName(string displayName)
    {
        string stem = new(displayName.Select(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '-').ToArray());
        stem = stem.Trim('-', '.');
        return (stem.Length == 0 ? "imported-model" : stem) + ".gguf";
    }

    private static long DefaultFreeSpace(string directory)
    {
        try
        {
            return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(directory)) ?? directory).AvailableFreeSpace;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static string FormatGiB(long bytes) => (bytes / (double)(1L << 30)).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " GiB";

    private static bool TryCreateHardLink(string newLink, string existing)
    {
        if (!OperatingSystem.IsWindows()) return false;
        return CreateHardLinkW(newLink, existing, IntPtr.Zero);
    }

    [SupportedOSPlatform("windows")]
    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string newFileName, string existingFileName, IntPtr securityAttributes);
}
