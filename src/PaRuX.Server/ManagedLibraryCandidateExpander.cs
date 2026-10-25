using PaRuX.Intelligence;

namespace PaRuX.Server;

/// <summary>
/// Expands an ordinary role candidate list with every usable top-level GGUF in PaRuX's Managed
/// Model Library for interactive AUTO routing. Studio/expert candidate pools stay explicit; this
/// helper is invoked only when the caller deliberately opts into the wider Chat AUTO scope.
/// </summary>
public static class ManagedLibraryCandidateExpander
{
    public static List<string> Expand(
        IReadOnlyList<string> registeredCandidates,
        string managedModelsDirectory)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddIfUsable(string? path, bool managedLibraryEntry)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
            if (!string.Equals(Path.GetExtension(path), ".gguf", StringComparison.OrdinalIgnoreCase)) return;
            if (managedLibraryEntry && !IsLikelyBaseTextModelFileName(Path.GetFileName(path))) return;

            string full;
            try { full = Path.GetFullPath(path); }
            catch { return; }
            if (seen.Add(full)) result.Add(full);
        }

        foreach (string path in registeredCandidates) AddIfUsable(path, managedLibraryEntry: false);

        if (!Directory.Exists(managedModelsDirectory)) return result;
        try
        {
            foreach (string path in Directory.EnumerateFiles(
                managedModelsDirectory, "*.gguf", SearchOption.TopDirectoryOnly))
                AddIfUsable(path, managedLibraryEntry: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        return result;
    }

    /// <summary>
    /// Projector GGUFs are not standalone language models and must never enter automatic text
    /// ranking merely because a user imported one into the flat managed directory. This is a
    /// conservative filename guard until PaRuX has a first-class GGUF metadata inspector.
    /// </summary>
    public static bool IsLikelyBaseTextModelFileName(string? fileName)
    {
        string name = (fileName ?? string.Empty).Trim();
        if (name.Length == 0) return false;
        return !GgufFileNaming.IsProjector(name)
            && !name.Contains("projector", StringComparison.OrdinalIgnoreCase);
    }
}
