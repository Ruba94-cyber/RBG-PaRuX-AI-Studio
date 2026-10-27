using System.Runtime.InteropServices;

using Microsoft.Extensions.Logging;

using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>Why a managed model cannot be deleted right now. Kind is "assigned-role", "assigned-media",
/// "loaded", "external-link" or "not-managed".</summary>
public sealed class ManagedModelDeleteBlocker
{
    public string Kind { get; init; } = string.Empty;
    /// <summary>The role (text) or workflow type (media) the model is assigned to, when Kind is an assignment.</summary>
    public string? Role { get; init; }
    public string Message { get; init; } = string.Empty;
}

public enum ManagedModelDeleteStatus { Deleted, NotFound, Refused, Invalid }

public sealed class ManagedModelDeleteResult
{
    public ManagedModelDeleteStatus Status { get; init; }
    public string Message { get; init; } = string.Empty;
    public List<string> DeletedFiles { get; init; } = new();
    public long BytesFreed { get; init; }
    public List<ManagedModelDeleteBlocker> Blockers { get; init; } = new();
}

/// <summary>A text role ("text") or media workflow type ("media") and the file that currently backs it.</summary>
public sealed record ModelAssignment(string Kind, string Role, string FilePathOrName);

/// <summary>
/// Guarded deletion of PaRuX-managed models. Refuses (with the reason) when the model backs a role or media
/// override or is loaded, never touches files outside the managed roots, and never deletes a file that is
/// hard-linked from another application (Ollama / LM Studio / Jan) or is a symbolic link.
/// </summary>
public sealed class ManagedModelDeletionService
{
    private static readonly string[] MediaExtensions = { ".safetensors", ".ckpt", ".sft", ".pt", ".gguf" };

    private readonly ModelDownloadService _models;
    private readonly Func<CancellationToken, Task<string?>> _loadedTextModelPath;
    private readonly Func<IReadOnlyList<ModelAssignment>> _assignments;
    private readonly Func<IReadOnlyList<string>> _mediaRoots;
    private readonly Func<string, int> _linkCount;
    private readonly ILogger? _log;

    public ManagedModelDeletionService(
        ModelDownloadService models,
        Func<CancellationToken, Task<string?>> loadedTextModelPath,
        Func<IReadOnlyList<ModelAssignment>>? assignments = null,
        Func<IReadOnlyList<string>>? mediaRoots = null,
        Func<string, int>? linkCount = null,
        ILogger? log = null)
    {
        _models = models;
        _loadedTextModelPath = loadedTextModelPath;
        _assignments = assignments ?? DefaultAssignments;
        _mediaRoots = mediaRoots ?? (() => new[] { LocalAiPaths.DiffusionModelsPath, LocalAiPaths.CheckpointsPath });
        _linkCount = linkCount ?? HardLinkCount;
        _log = log;
    }

    public async Task<ManagedModelDeleteResult> DeleteAsync(string? idOrFileName, CancellationToken ct)
    {
        if (!PaRuXPathSafety.IsSimpleFileName(idOrFileName))
            return Fail(ManagedModelDeleteStatus.Invalid, "A model is addressed by a simple file name, never a path.");
        string name = idOrFileName!;

        ManagedModelInfo? text = _models.ListManagedLibrary().FirstOrDefault(m =>
            string.Equals(m.FileName, name, StringComparison.OrdinalIgnoreCase)
            || (m.SplitSet?.PartFileNames.Any(p => string.Equals(p, name, StringComparison.OrdinalIgnoreCase)) ?? false));
        if (text != null) return await DeleteTextAsync(text, ct);
        return DeleteMedia(name);
    }

    private async Task<ManagedModelDeleteResult> DeleteTextAsync(ManagedModelInfo model, CancellationToken ct)
    {
        string root = ModelDownloadService.ModelsDirectory;
        List<string> names = model.SplitSet is { } set && set.PartFileNames.Count > 0
            ? new List<string>(set.PartFileNames) : new List<string> { model.FileName };
        var paths = new List<string>();
        foreach (string n in names)
        {
            string full = Path.GetFullPath(Path.Combine(root, n));
            if (!PaRuXPathSafety.IsWithin(root, full) || SamePath(root, full))
                return Fail(ManagedModelDeleteStatus.Invalid, "The model path is outside PaRuX's managed library.");
            paths.Add(full);
        }

        var blockers = new List<ManagedModelDeleteBlocker>();
        foreach (ModelAssignment a in _assignments().Where(a => a.Kind == "text"))
            if (!string.IsNullOrWhiteSpace(a.FilePathOrName) && paths.Any(p => SamePath(p, a.FilePathOrName)))
                blockers.Add(new ManagedModelDeleteBlocker
                {
                    Kind = "assigned-role", Role = a.Role,
                    Message = "It is assigned to the \"" + a.Role + "\" role. Switch that role to Automatic first."
                });
        string? loaded = await _loadedTextModelPath(ct);
        if (!string.IsNullOrWhiteSpace(loaded) && paths.Any(p => SamePath(p, loaded)))
            blockers.Add(new ManagedModelDeleteBlocker { Kind = "loaded", Message = "It is currently loaded. Unload it (or switch the role to Automatic) first." });
        AddLinkBlockers(paths, blockers);
        if (blockers.Count > 0) return Refuse(model.FileName, blockers);

        long bytes = paths.Where(File.Exists).Sum(p => new FileInfo(p).Length);
        if (!_models.DeleteManagedModel(model.FileName))
            return Fail(ManagedModelDeleteStatus.NotFound, "That model is not in the Managed Model Library.");
        _log?.LogInformation("Deleted managed model {Files} ({Bytes} bytes)", string.Join(", ", names), bytes);
        return new ManagedModelDeleteResult
        {
            Status = ManagedModelDeleteStatus.Deleted, DeletedFiles = names, BytesFreed = bytes,
            Message = "Deleted " + string.Join(", ", names) + "."
        };
    }

    private ManagedModelDeleteResult DeleteMedia(string name)
    {
        if (!MediaExtensions.Any(e => name.EndsWith(e, StringComparison.OrdinalIgnoreCase)))
            return Fail(ManagedModelDeleteStatus.NotFound, "That model is not in the Managed Model Library.");
        string? path = null;
        foreach (string folder in _mediaRoots())
        {
            string candidate = Path.GetFullPath(Path.Combine(folder, name));
            if (PaRuXPathSafety.IsWithin(folder, candidate) && File.Exists(candidate)) { path = candidate; break; }
        }
        if (path == null) return Fail(ManagedModelDeleteStatus.NotFound, "That model is not in the Managed Model Library.");

        // Ownership: only a checkpoint PaRuX downloaded itself (it recorded a download record) is PaRuX-managed.
        ModelDownloadProvenance? record = ModelDownloadProvenanceManifestStore.Get(ModelDownloadService.ModelsDirectory, name);
        if (record == null)
            return Refuse(name, new List<ManagedModelDeleteBlocker>
            {
                new() { Kind = "not-managed", Message = "PaRuX did not download this file, so it is user-managed. Remove it from the ComfyUI models folder yourself." }
            });

        var blockers = new List<ManagedModelDeleteBlocker>();
        foreach (ModelAssignment a in _assignments().Where(a => a.Kind == "media"))
            if (string.Equals(Path.GetFileName(a.FilePathOrName), name, StringComparison.OrdinalIgnoreCase))
                blockers.Add(new ManagedModelDeleteBlocker
                {
                    Kind = "assigned-media", Role = a.Role,
                    Message = "It is the " + a.Role + " model. Switch that workflow back to Automatic first."
                });
        AddLinkBlockers(new List<string> { path }, blockers);
        if (blockers.Count > 0) return Refuse(name, blockers);

        long bytes = new FileInfo(path).Length;
        File.Delete(path);
        try { ModelDownloadProvenanceManifestStore.Remove(ModelDownloadService.ModelsDirectory, name); } catch (InvalidDataException) { }
        _log?.LogInformation("Deleted managed media checkpoint {File} ({Bytes} bytes)", name, bytes);
        return new ManagedModelDeleteResult
        {
            Status = ManagedModelDeleteStatus.Deleted, DeletedFiles = new List<string> { name }, BytesFreed = bytes,
            Message = "Deleted " + name + "."
        };
    }

    private void AddLinkBlockers(IEnumerable<string> paths, List<ManagedModelDeleteBlocker> blockers)
    {
        foreach (string p in paths)
        {
            bool linked = false;
            try { linked = (File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0 || _linkCount(p) > 1; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            if (!linked) continue;
            blockers.Add(new ManagedModelDeleteBlocker
            {
                Kind = "external-link",
                Message = Path.GetFileName(p) + " is linked from another application (Ollama, LM Studio or Jan), so PaRuX does not delete it. Remove it in that application."
            });
            return;
        }
    }

    private ManagedModelDeleteResult Refuse(string name, List<ManagedModelDeleteBlocker> blockers)
    {
        _log?.LogWarning("Refused to delete {File}: {Reasons}", name, string.Join(" | ", blockers.Select(b => b.Kind)));
        return new ManagedModelDeleteResult
        {
            Status = ManagedModelDeleteStatus.Refused, Blockers = blockers,
            Message = "Not deleted: " + string.Join(" ", blockers.Select(b => b.Message))
        };
    }

    private static ManagedModelDeleteResult Fail(ManagedModelDeleteStatus status, string message) =>
        new() { Status = status, Message = message };

    private static bool SamePath(string a, string b)
    {
        try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
        catch (ArgumentException) { return false; }
    }

    private static IReadOnlyList<ModelAssignment> DefaultAssignments()
    {
        var list = new List<ModelAssignment>();
        foreach (string role in LocalAiPaths.KnownTextRoles)
        {
            string? path = LocalAiPaths.GetRoleModelPathOrNull(role);
            if (!string.IsNullOrWhiteSpace(path)) list.Add(new ModelAssignment("text", role, path));
        }
        foreach (string type in new[] { "image", "edit", "video" })
        {
            string? file = LocalAiPaths.GetMediaCheckpointOverride(type);
            if (string.IsNullOrWhiteSpace(file))
            {
                try { file = MediaCheckpointResolution.Resolve(type).File; }
                catch (Exception ex) when (ex is IOException or InvalidDataException) { }
            }
            if (!string.IsNullOrWhiteSpace(file)) list.Add(new ModelAssignment("media", type, file));
        }
        return list;
    }

    private static int HardLinkCount(string path)
    {
        if (!OperatingSystem.IsWindows()) return 1;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return GetFileInformationByHandle(stream.SafeFileHandle.DangerousGetHandle(), out ByHandleFileInformation info)
                ? (int)info.NumberOfLinks : 1;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 1; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(IntPtr handle, out ByHandleFileInformation info);
}
