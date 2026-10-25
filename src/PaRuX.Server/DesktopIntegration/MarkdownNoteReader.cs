using System.Text;
using System.Text.RegularExpressions;

namespace PaRuX.Server.DesktopIntegration;

/// <summary>
/// Reads a folder of Markdown notes - an Obsidian vault, a Logseq graph, a Zettelkasten, or just a
/// directory someone keeps notes in. One reader covers all of them because none of those apps owns
/// the format: they own an editor over plain files, which is exactly why they are readable at all.
///
/// The title comes from a YAML front-matter <c>title</c>, else the first heading, else the file
/// name - the order a person would guess. Front matter is stripped from the body rather than fed to
/// a model as if it were prose.
/// </summary>
internal static class MarkdownNoteReader
{
    private static readonly string[] Extensions = { ".md", ".markdown", ".txt" };

    /// <summary>
    /// Folders that are the app's own bookkeeping rather than the user's notes. Reading them back
    /// as if they were notes would be noise at best - and in Logseq's case actively misleading,
    /// since bak holds superseded copies of pages that still exist.
    /// </summary>
    private static readonly string[] SkippedDirectoryNames =
    {
        ".obsidian", ".trash", ".git", ".logseq", "node_modules", ".stfolder"
    };

    /// <summary>
    /// Skipped only under a specific parent, given as "parent/child". A bare "bak" would be too
    /// greedy - somebody's notes folder may legitimately be called that - while "logseq/bak" names
    /// the one directory the app itself owns.
    /// </summary>
    private static readonly string[] SkippedDirectoryPairs = { "logseq/bak" };

    private static readonly Regex FrontMatter = new(
        @"\A---\r?\n(?<yaml>.*?)\r?\n---\r?\n", RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex FrontMatterTitle = new(
        @"^title\s*:\s*(?<value>.+)$", RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex FirstHeading = new(
        @"^\#{1,6}\s+(?<value>.+)$", RegexOptions.Multiline | RegexOptions.Compiled);

    internal static IEnumerable<DesktopItem> Read(string sourceId, string root, DesktopReadLimits limits)
    {
        var items = new List<DesktopItem>();
        if (!Directory.Exists(root)) return items;

        int scanned = 0;
        foreach (string path in EnumerateNoteFiles(root))
        {
            if (++scanned > limits.MaxFilesScanned) break;
            if (items.Count >= limits.MaxItems) break;

            DesktopItem? item = ReadFile(sourceId, path, limits);
            if (item != null) items.Add(item);
        }
        return items;
    }

    /// <summary>
    /// Prunes skipped directories while walking rather than filtering after the fact. An Obsidian
    /// vault's .obsidian folder and a Logseq graph's bak folder can hold more files than the notes
    /// do, and visiting them to discard them is the slow way to get the same answer.
    /// </summary>
    private static IEnumerable<string> EnumerateNoteFiles(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            string directory = pending.Pop();

            string[] subdirectories;
            try { subdirectories = Directory.GetDirectories(directory); }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }

            foreach (string subdirectory in subdirectories)
            {
                if (IsSkipped(subdirectory)) continue;
                pending.Push(subdirectory);
            }

            string[] files;
            try { files = Directory.GetFiles(directory); }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }

            foreach (string file in files)
            {
                if (Extensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
                    yield return file;
            }
        }
    }

    internal static bool IsSkipped(string directory)
    {
        string name = Path.GetFileName(directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (name.StartsWith('.')) return true;
        if (SkippedDirectoryNames.Any(s => string.Equals(s, name, StringComparison.OrdinalIgnoreCase))) return true;

        string? parent = Path.GetFileName(Path.GetDirectoryName(directory) ?? string.Empty);
        return !string.IsNullOrEmpty(parent)
            && SkippedDirectoryPairs.Any(pair => string.Equals(
                pair, parent + "/" + name, StringComparison.OrdinalIgnoreCase));
    }

    internal static DesktopItem? ReadFile(string sourceId, string path, DesktopReadLimits limits)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length == 0 || info.Length > limits.MaxFileBytes) return null;

            using FileStream stream = new(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            string raw = reader.ReadToEnd();
            if (string.IsNullOrWhiteSpace(raw)) return null;

            string title = Path.GetFileNameWithoutExtension(path);
            string body = raw;

            Match matter = FrontMatter.Match(raw);
            if (matter.Success)
            {
                body = raw[matter.Length..];
                Match fromMatter = FrontMatterTitle.Match(matter.Groups["yaml"].Value);
                if (fromMatter.Success) title = fromMatter.Groups["value"].Value.Trim().Trim('"', '\'');
            }

            if (string.Equals(title, Path.GetFileNameWithoutExtension(path), StringComparison.Ordinal))
            {
                Match heading = FirstHeading.Match(body);
                if (heading.Success) title = heading.Groups["value"].Value.Trim();
            }

            string relative = Path.GetRelativePath(Path.GetDirectoryName(path) ?? ".", path);
            return new DesktopItem(
                sourceId,
                DesktopItem.KindNote,
                path,
                string.IsNullOrWhiteSpace(title) ? relative : title,
                Path.GetFileName(path),
                new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero),
                body.Length <= limits.MaxBodyCharacters ? body.Trim() : body[..limits.MaxBodyCharacters].Trim() + "…",
                path);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }
}
