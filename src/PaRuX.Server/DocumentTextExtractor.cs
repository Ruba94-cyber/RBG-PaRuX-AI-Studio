using System.Text;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using ExcelDataReader;

namespace PaRuX.Server;

/// <summary>
/// Server-side copy of PaRuX.App's DocumentTextExtractor, kept identical on
/// purpose (same formats, same 120,000-character cap) rather than shared via
/// a project reference, so the already-verified client-side RAG/attachment
/// path can't be destabilized by a change made for the server-side one.
/// </summary>
public static class DocumentTextExtractor
{
    public const int MaximumCharacters = 120000;

    public static bool IsTextDocument(string path)
    {
        string extension = Path.GetExtension(path).ToLowerInvariant();
        return extension is ".txt" or ".md" or ".csv" or ".tsv"
            or ".json" or ".xml" or ".yaml" or ".yml"
            or ".log" or ".cs" or ".js" or ".ts"
            or ".tsx" or ".jsx" or ".py" or ".ps1"
            or ".sql" or ".html" or ".css";
    }

    public static bool CanExtract(string path)
    {
        string extension = Path.GetExtension(path).ToLowerInvariant();
        return IsTextDocument(path) || extension is ".docx" or ".xlsx" or ".xls" or ".xlsb" or ".pptx";
    }

    public static Task<string> ExtractAsync(string path, CancellationToken ct) =>
        Task.Run(() => Extract(path, ct), ct);

    /// <summary>
    /// The synchronous body, exposed because callers that are already on a background thread - the
    /// desktop-integration readers walking a folder of documents - would otherwise wrap a Task.Run
    /// only to block on it immediately.
    /// </summary>
    public static string Extract(string path, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        string extension = Path.GetExtension(path).ToLowerInvariant();
        string text;
        if (IsTextDocument(path)) text = ReadTextCapped(path);
        else if (extension == ".docx") text = ExtractWord(path);
        else if (extension == ".pptx") text = ExtractPresentation(path);
        else if (extension is ".xls" or ".xlsx" or ".xlsb") text = ExtractWorkbook(path, ct);
        else throw new InvalidDataException("This document format is not supported.");
        if (text.Length > MaximumCharacters)
            text = text[..MaximumCharacters] + "\n[Document truncated at 120,000 characters]";
        return text;
    }

    private static string ReadTextCapped(string path)
    {
        using var reader = new StreamReader(path, Encoding.UTF8, true);
        char[] buffer = new char[MaximumCharacters + 1];
        int total = 0;
        while (total < buffer.Length)
        {
            int read = reader.Read(buffer, total, buffer.Length - total);
            if (read == 0) break;
            total += read;
        }
        return new string(buffer, 0, total);
    }

    private static string ExtractWord(string path)
    {
        using WordprocessingDocument document = WordprocessingDocument.Open(path, false);
        var builder = new StringBuilder();
        foreach (Paragraph paragraph in document.MainDocumentPart?.Document?.Body?.Descendants<Paragraph>() ?? Enumerable.Empty<Paragraph>())
        {
            string value = paragraph.InnerText.Trim();
            if (!string.IsNullOrWhiteSpace(value)) builder.AppendLine(value);
        }
        return builder.ToString();
    }

    private static string ExtractPresentation(string path)
    {
        using PresentationDocument document = PresentationDocument.Open(path, false);
        var builder = new StringBuilder();
        int slide = 0;
        foreach (SlidePart part in document.PresentationPart?.SlideParts ?? Enumerable.Empty<SlidePart>())
        {
            builder.AppendLine("[Slide " + (++slide) + "]");
            if (part.Slide == null) continue;
            foreach (DocumentFormat.OpenXml.Drawing.Text text in part.Slide.Descendants<DocumentFormat.OpenXml.Drawing.Text>())
                if (!string.IsNullOrWhiteSpace(text.Text)) builder.AppendLine(text.Text);
        }
        return builder.ToString();
    }

    private static string ExtractWorkbook(string path, CancellationToken ct)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        using FileStream stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using IExcelDataReader reader = ExcelReaderFactory.CreateReader(stream);
        var builder = new StringBuilder();
        do
        {
            builder.AppendLine("[Sheet: " + reader.Name + "]");
            int rows = 0;
            while (rows++ < 1000 && reader.Read())
            {
                ct.ThrowIfCancellationRequested();
                int fields = Math.Min(reader.FieldCount, 100);
                string[] values = new string[fields];
                for (int i = 0; i < fields; i++) values[i] = reader.GetValue(i)?.ToString() ?? string.Empty;
                builder.AppendLine(string.Join("\t", values));
                if (builder.Length >= MaximumCharacters) return builder.ToString();
            }
        } while (reader.NextResult());
        return builder.ToString();
    }
}
