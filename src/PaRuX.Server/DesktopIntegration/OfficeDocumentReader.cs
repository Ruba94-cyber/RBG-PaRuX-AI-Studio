namespace PaRuX.Server.DesktopIntegration;

/// <summary>
/// Reads Word, Excel and PowerPoint documents out of a folder the user chose, so PaRuX can answer
/// from the reports, spreadsheets and decks they already have.
///
/// It reuses <see cref="DocumentTextExtractor"/> rather than opening the packages again: that code
/// already handles the OpenXML formats, the legacy binary ones, and the character cap, and a second
/// implementation would only differ from it over time.
///
/// Temporary and lock files are skipped by name. Word and Excel leave "~$report.docx" beside an open
/// document - a tiny owner-metadata file, not a document - and reading it back to the user as an
/// empty untitled report is exactly the sort of noise that makes people distrust the whole feature.
/// </summary>
internal static class OfficeDocumentReader
{
    private static readonly string[] Extensions =
        { ".docx", ".xlsx", ".xls", ".xlsb", ".pptx", ".pdf", ".rtf" };

    internal static IEnumerable<DesktopItem> Read(
        string sourceId, string root, DesktopReadLimits limits, CancellationToken ct = default)
    {
        var items = new List<DesktopItem>();
        if (!Directory.Exists(root)) return items;

        int scanned = 0;
        foreach (string path in EnumerateDocuments(root))
        {
            if (++scanned > limits.MaxFilesScanned) break;
            if (items.Count >= limits.MaxItems) break;
            ct.ThrowIfCancellationRequested();

            DesktopItem? item = ReadFile(sourceId, path, limits, ct);
            if (item != null) items.Add(item);
        }
        return items;
    }

    internal static DesktopItem? ReadFile(
        string sourceId, string path, DesktopReadLimits limits, CancellationToken ct = default)
    {
        if (IsTemporary(path)) return null;

        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length == 0 || info.Length > limits.MaxFileBytes) return null;
            if (!DocumentTextExtractor.CanExtract(path)) return null;

            string text = DocumentTextExtractor.Extract(path, ct);
            if (string.IsNullOrWhiteSpace(text)) return null;

            return new DesktopItem(
                sourceId,
                DesktopItem.KindDocument,
                path,
                Path.GetFileNameWithoutExtension(path),
                DescribeKind(path),
                new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero),
                text.Length <= limits.MaxBodyCharacters ? text.Trim() : text[..limits.MaxBodyCharacters].Trim() + "…",
                path);
        }
        catch (OperationCanceledException) { throw; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        // A corrupt or password-protected document is one document PaRuX cannot read, not a reason
        // to abandon the folder.
        catch (InvalidDataException) { return null; }
        catch (Exception ex) when (ex is FormatException or NotSupportedException) { return null; }
    }

    /// <summary>Word and Excel's "~$" owner files, and anything hidden, are bookkeeping rather than
    /// documents.</summary>
    internal static bool IsTemporary(string path)
    {
        string name = Path.GetFileName(path);
        return name.StartsWith("~$", StringComparison.Ordinal)
            || name.StartsWith('.')
            || name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase);
    }

    private static string DescribeKind(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".docx" => "Word document",
        ".xlsx" or ".xls" or ".xlsb" => "Excel workbook",
        ".pptx" => "PowerPoint presentation",
        ".pdf" => "PDF",
        ".rtf" => "Rich text document",
        _ => "Document"
    };

    private static IEnumerable<string> EnumerateDocuments(string root)
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
                string name = Path.GetFileName(subdirectory);
                if (name.StartsWith('.')) continue;
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
}
