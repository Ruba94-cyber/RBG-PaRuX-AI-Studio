using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PaRuX.Server;

internal sealed record ComfyBuildIdentity(string Tag, string Variant);

/// <summary>
/// Keeps LocalAiPaths.ComfyRoot as the stable active path expected by the existing process manager,
/// while inactive managed builds live beside it under runtime/comfyui-builds. Activation is a
/// same-volume directory swap: the working build is moved aside before the incoming build replaces
/// it, and is restored if the replacement move fails. A marker travels with every managed build,
/// so rollback does not depend on a separate pointer file that can drift from the filesystem.
///
/// Large/user-owned ComfyUI data is deliberately NOT duplicated into every inactive build. Models,
/// input/output history, user settings and custom nodes are moved out of the active tree during a
/// swap and attached to the incoming runtime. That keeps multi-gigabyte checkpoints in one place
/// and means update/rollback changes runtime code, not the user's data.
/// </summary>
internal sealed class ComfyRuntimeStore
{
    private const string MarkerName = ".parux-runtime.json";
    private static readonly string[] PersistentDirectories =
    {
        Path.Combine("ComfyUI", "models"),
        Path.Combine("ComfyUI", "input"),
        Path.Combine("ComfyUI", "output"),
        Path.Combine("ComfyUI", "user"),
        Path.Combine("ComfyUI", "custom_nodes")
    };
    private static readonly string[] PersistentFiles =
    {
        Path.Combine("ComfyUI", "extra_model_paths.yaml")
    };

    private readonly string _activeRoot;
    private readonly string _storeRoot;

    public ComfyRuntimeStore(string? activeRoot = null, string? storeRoot = null)
    {
        _activeRoot = Path.GetFullPath(activeRoot ?? LocalAiPaths.ComfyRoot);
        _storeRoot = Path.GetFullPath(storeRoot ?? Path.Combine(LocalAiPaths.RuntimeRoot, "comfyui-builds"));
    }

    internal string ActiveRoot => _activeRoot;

    public ComfyBuildIdentity? GetActiveIdentity()
    {
        if (!IsPortableRoot(_activeRoot)) return null;
        return ReadMarker(_activeRoot) ?? new ComfyBuildIdentity("legacy", "legacy");
    }

    public IReadOnlyList<ComfyBuildIdentity> ListInstalled()
    {
        var result = new List<ComfyBuildIdentity>();
        ComfyBuildIdentity? active = GetActiveIdentity();
        if (active != null) result.Add(active);

        if (Directory.Exists(_storeRoot))
        {
            foreach (string directory in Directory.EnumerateDirectories(_storeRoot))
            {
                if (!IsPortableRoot(directory)) continue;
                ComfyBuildIdentity? identity = ReadMarker(directory);
                if (identity != null && !result.Contains(identity)) result.Add(identity);
            }
        }

        return result;
    }

    public ComfyBuildIdentity ResolveInstalled(string tag, string? variant)
    {
        ValidateIdentityPart(tag, nameof(tag));
        string? normalizedVariant = string.IsNullOrWhiteSpace(variant) ? null : ComfyBuildCatalog.NormalizeVariant(variant);
        if (!string.IsNullOrWhiteSpace(variant) && normalizedVariant == null && !string.Equals(variant, "legacy", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Unknown ComfyUI variant \"" + variant + "\".");
        if (string.Equals(variant, "legacy", StringComparison.OrdinalIgnoreCase)) normalizedVariant = "legacy";

        List<ComfyBuildIdentity> matches = ListInstalled()
            .Where(i => string.Equals(i.Tag, tag, StringComparison.Ordinal)
                && (normalizedVariant == null || string.Equals(i.Variant, normalizedVariant, StringComparison.Ordinal)))
            .ToList();

        if (matches.Count == 0)
            throw new InvalidDataException("No installed ComfyUI build \"" + tag + "\""
                + (normalizedVariant == null ? string.Empty : " variant \"" + normalizedVariant + "\"") + " was found.");
        if (matches.Count > 1)
            throw new InvalidDataException("More than one ComfyUI variant is installed for tag \"" + tag
                + "\". Specify variant explicitly: " + string.Join(", ", matches.Select(m => m.Variant)) + ".");
        return matches[0];
    }

    public bool TryActivateInstalled(ComfyBuildIdentity identity)
    {
        ValidateIdentity(identity);
        if (GetActiveIdentity() == identity) return true;
        string stored = StoragePath(identity);
        if (!IsPortableRoot(stored) || ReadMarker(stored) != identity) return false;
        SwapIncoming(stored, identity);
        return true;
    }

    public void ActivateExtracted(string portableRoot, ComfyBuildIdentity identity)
    {
        ValidateIdentity(identity);
        string incoming = Path.GetFullPath(portableRoot);
        if (!IsPortableRoot(incoming))
            throw new InvalidDataException("The extracted ComfyUI archive does not contain the expected ComfyUI/main.py and python_embeded/python.exe layout.");
        if (PathsEqual(incoming, _activeRoot))
            throw new InvalidDataException("The incoming ComfyUI build cannot already be the active runtime directory.");

        WriteMarker(incoming, identity);
        SwapIncoming(incoming, identity);
    }

    /// <summary>Moves a freshly extracted portable build into the store WITHOUT activating it: the active runtime, its models and
    /// its user data are not touched. Used by the automatic updater, which smoke-tests and benchmarks the staged build first.</summary>
    public void StageExtracted(string portableRoot, ComfyBuildIdentity identity)
    {
        ValidateIdentity(identity);
        string incoming = Path.GetFullPath(portableRoot);
        if (!IsPortableRoot(incoming))
            throw new InvalidDataException("The extracted ComfyUI archive does not contain the expected ComfyUI/main.py and python_embeded/python.exe layout.");
        if (PathsEqual(incoming, _activeRoot))
            throw new InvalidDataException("The incoming ComfyUI build cannot already be the active runtime directory.");
        if (GetActiveIdentity() == identity)
            throw new InvalidDataException("That ComfyUI build is already the active runtime.");
        Directory.CreateDirectory(_storeRoot);
        string destination = StoragePath(identity);
        if (Directory.Exists(destination)) Directory.Delete(destination, recursive: true);
        WriteMarker(incoming, identity);
        Directory.Move(incoming, destination);
    }

    /// <summary>Folder of an installed build: the stable active path when it is the active one, otherwise its store folder.</summary>
    public string PathOf(ComfyBuildIdentity identity) =>
        GetActiveIdentity() == identity ? _activeRoot : StoragePath(identity);

    /// <summary>Deletes an INACTIVE stored build. Refuses the active build and anything whose marker does not match.</summary>
    public bool RemoveStored(ComfyBuildIdentity identity)
    {
        ValidateIdentity(identity);
        if (GetActiveIdentity() == identity) return false;
        string stored = StoragePath(identity);
        if (!IsPortableRoot(stored) || ReadMarker(stored) != identity) return false;
        try { Directory.Delete(stored, recursive: true); return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    public void Rollback(ComfyBuildIdentity target)
    {
        if (!TryActivateInstalled(target))
            throw new InvalidDataException("The requested ComfyUI rollback target is not installed: " + target.Tag + " / " + target.Variant + ".");
    }

    public void ResetToLegacy()
    {
        ComfyBuildIdentity legacy = new("legacy", "legacy");
        if (GetActiveIdentity() == legacy) return;
        Rollback(legacy);
    }

    private void SwapIncoming(string incomingPath, ComfyBuildIdentity incomingIdentity)
    {
        if (!IsPortableRoot(incomingPath))
            throw new InvalidDataException("Incoming ComfyUI runtime is incomplete: " + incomingPath);

        Directory.CreateDirectory(Path.GetDirectoryName(_activeRoot)!);
        Directory.CreateDirectory(_storeRoot);

        ComfyBuildIdentity? previousIdentity = null;
        string? previousStorage = null;
        bool previousMoved = false;
        bool incomingMoved = false;
        bool dataAttachedToIncoming = false;
        string transferRoot = Path.Combine(_storeRoot, ".data-transfer-" + Guid.NewGuid().ToString("N"));
        var transferred = new List<string>();

        try
        {
            if (Directory.Exists(_activeRoot))
            {
                if (!IsPortableRoot(_activeRoot))
                    throw new InvalidDataException(
                        "The current ComfyUI directory exists but is incomplete. PaRuX will not overwrite it automatically: " + _activeRoot);

                previousIdentity = GetActiveIdentity()
                    ?? throw new InvalidDataException("The active ComfyUI runtime could not be identified safely.");
                WriteMarker(_activeRoot, previousIdentity);
                previousStorage = StoragePath(previousIdentity);

                transferred = DetachPersistentData(_activeRoot, transferRoot);

                if (Directory.Exists(previousStorage))
                    Directory.Delete(previousStorage, recursive: true);
                Directory.Move(_activeRoot, previousStorage);
                previousMoved = true;
            }

            Directory.Move(incomingPath, _activeRoot);
            incomingMoved = true;

            if (transferred.Count > 0)
            {
                AttachPersistentData(transferRoot, _activeRoot, transferred);
                dataAttachedToIncoming = true;
            }

            WriteMarker(_activeRoot, incomingIdentity);
            if (!IsPortableRoot(_activeRoot))
                throw new InvalidDataException("The activated ComfyUI runtime failed final layout validation.");

            TryDeleteDirectory(transferRoot);
        }
        catch (Exception activationError)
        {
            Exception? restoreError = null;
            try
            {
                // If user data already reached the incoming build, pull exactly those paths back
                // out before returning that build to its original location.
                if (dataAttachedToIncoming && incomingMoved && Directory.Exists(_activeRoot))
                    DetachSpecificPersistentData(_activeRoot, transferRoot, transferred);

                if (incomingMoved && Directory.Exists(_activeRoot) && !Directory.Exists(incomingPath))
                    Directory.Move(_activeRoot, incomingPath);

                if (previousMoved && previousStorage != null && Directory.Exists(previousStorage) && !Directory.Exists(_activeRoot))
                    Directory.Move(previousStorage, _activeRoot);

                if (transferred.Count > 0 && Directory.Exists(_activeRoot))
                    AttachPersistentData(transferRoot, _activeRoot, transferred);
            }
            catch (Exception ex)
            {
                restoreError = ex;
            }
            finally
            {
                TryDeleteDirectory(transferRoot);
            }

            if (restoreError != null)
                throw new IOException("ComfyUI activation failed and restoring the previous runtime also failed. Activation: "
                    + activationError.Message + " Restore: " + restoreError.Message, activationError);
            throw;
        }
    }

    private static List<string> DetachPersistentData(string sourceRoot, string transferRoot)
    {
        var moved = new List<string>();
        foreach (string relative in PersistentDirectories)
        {
            string source = Path.Combine(sourceRoot, relative);
            if (!Directory.Exists(source)) continue;
            string destination = Path.Combine(transferRoot, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            Directory.Move(source, destination);
            moved.Add(relative);
        }
        foreach (string relative in PersistentFiles)
        {
            string source = Path.Combine(sourceRoot, relative);
            if (!File.Exists(source)) continue;
            string destination = Path.Combine(transferRoot, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Move(source, destination);
            moved.Add(relative);
        }
        return moved;
    }

    private static void DetachSpecificPersistentData(string sourceRoot, string transferRoot, IReadOnlyList<string> relatives)
    {
        foreach (string relative in relatives)
        {
            string source = Path.Combine(sourceRoot, relative);
            string destination = Path.Combine(transferRoot, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

            if (Directory.Exists(source))
            {
                if (Directory.Exists(destination)) Directory.Delete(destination, recursive: true);
                Directory.Move(source, destination);
            }
            else if (File.Exists(source))
            {
                if (File.Exists(destination)) File.Delete(destination);
                File.Move(source, destination);
            }
        }
    }

    private static void AttachPersistentData(string transferRoot, string destinationRoot, IReadOnlyList<string> relatives)
    {
        foreach (string relative in relatives)
        {
            string source = Path.Combine(transferRoot, relative);
            string destination = Path.Combine(destinationRoot, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

            if (Directory.Exists(source))
            {
                if (Directory.Exists(destination)) Directory.Delete(destination, recursive: true);
                else if (File.Exists(destination)) File.Delete(destination);
                Directory.Move(source, destination);
            }
            else if (File.Exists(source))
            {
                if (Directory.Exists(destination)) Directory.Delete(destination, recursive: true);
                else if (File.Exists(destination)) File.Delete(destination);
                File.Move(source, destination);
            }
        }
    }

    private string StoragePath(ComfyBuildIdentity identity)
    {
        ValidateIdentity(identity);
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(identity.Tag + "\n" + identity.Variant));
        string key = Convert.ToHexString(hash).ToLowerInvariant()[..24];
        return Path.Combine(_storeRoot, key);
    }

    internal static bool IsPortableRoot(string path) =>
        Directory.Exists(path)
        && File.Exists(Path.Combine(path, "ComfyUI", "main.py"))
        && File.Exists(Path.Combine(path, "python_embeded", "python.exe"));

    private static ComfyBuildIdentity? ReadMarker(string root)
    {
        try
        {
            string marker = Path.Combine(root, MarkerName);
            if (!File.Exists(marker)) return null;
            ComfyBuildIdentity? identity = JsonSerializer.Deserialize<ComfyBuildIdentity>(File.ReadAllText(marker));
            if (identity == null) return null;
            ValidateIdentity(identity);
            return identity;
        }
        catch
        {
            return null;
        }
    }

    private static void WriteMarker(string root, ComfyBuildIdentity identity)
    {
        ValidateIdentity(identity);
        File.WriteAllText(Path.Combine(root, MarkerName), JsonSerializer.Serialize(identity));
    }

    private static void ValidateIdentity(ComfyBuildIdentity identity)
    {
        ValidateIdentityPart(identity.Tag, "tag");
        ValidateIdentityPart(identity.Variant, "variant");
    }

    private static void ValidateIdentityPart(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 160 || value.Any(char.IsControl))
            throw new InvalidDataException(name + " is missing or invalid.");
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { }
    }

    private static bool PathsEqual(string a, string b)
    {
        try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
        catch { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
    }
}
