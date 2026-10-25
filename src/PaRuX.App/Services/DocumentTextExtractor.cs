using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using ExcelDataReader;

namespace PaRuX.Services
{
    public static class DocumentTextExtractor
    {
        public const int MaximumCharacters = 120000;

        public static bool IsTextDocument(string path)
        {
            string extension = Path.GetExtension(path).ToLowerInvariant();
            return extension == ".txt" || extension == ".md" || extension == ".csv" || extension == ".tsv"
                || extension == ".json" || extension == ".xml" || extension == ".yaml" || extension == ".yml"
                || extension == ".log" || extension == ".cs" || extension == ".js" || extension == ".ts"
                || extension == ".tsx" || extension == ".jsx" || extension == ".py" || extension == ".ps1"
                || extension == ".sql" || extension == ".html" || extension == ".css";
        }

        public static bool CanExtract(string path)
        {
            string extension = Path.GetExtension(path).ToLowerInvariant();
            return IsTextDocument(path) || extension == ".docx" || extension == ".xlsx"
                || extension == ".xls" || extension == ".xlsb" || extension == ".pptx";
        }

        public static Task<string> ExtractAsync(string path, CancellationToken ct) => Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            string extension = Path.GetExtension(path).ToLowerInvariant();
            string text;
            if (IsTextDocument(path)) text = ReadTextCapped(path);
            else if (extension == ".docx") text = ExtractWord(path);
            else if (extension == ".pptx") text = ExtractPresentation(path);
            else if (extension == ".xls" || extension == ".xlsx" || extension == ".xlsb") text = ExtractWorkbook(path, ct);
            else throw new InvalidDataException("This document format is not supported.");
            if (text.Length > MaximumCharacters)
                text = text.Substring(0, MaximumCharacters) + "\n[Document truncated at 120,000 characters]";
            return text;
        }, ct);

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
}
