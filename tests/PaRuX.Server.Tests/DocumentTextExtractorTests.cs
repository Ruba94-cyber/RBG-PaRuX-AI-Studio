using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using DocumentFormat.OpenXml.Wordprocessing;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class DocumentTextExtractorTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "PaRuXDocumentTests-" + Guid.NewGuid().ToString("N"));

    public DocumentTextExtractorTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public async Task ExtractsDocxText()
    {
        string path = Path.Combine(_directory, "sample.docx");
        using (WordprocessingDocument document = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document))
        {
            MainDocumentPart main = document.AddMainDocumentPart();
            main.Document = new Document(new Body(new Paragraph(
                new DocumentFormat.OpenXml.Wordprocessing.Run(
                    new DocumentFormat.OpenXml.Wordprocessing.Text("PaRuX DOCX evidence")))));
            main.Document.Save();
        }

        string result = await DocumentTextExtractor.ExtractAsync(path, CancellationToken.None);

        Assert.Contains("PaRuX DOCX evidence", result);
    }

    [Fact]
    public async Task ExtractsXlsxCellsAndSheetName()
    {
        string path = Path.Combine(_directory, "sample.xlsx");
        using (SpreadsheetDocument document = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook))
        {
            WorkbookPart workbook = document.AddWorkbookPart();
            workbook.Workbook = new Workbook();
            WorksheetPart worksheet = workbook.AddNewPart<WorksheetPart>();
            worksheet.Worksheet = new Worksheet(new SheetData(
                new Row(new Cell { DataType = CellValues.String, CellValue = new CellValue("PaRuX XLSX evidence") })));
            var sheets = workbook.Workbook.AppendChild(new Sheets());
            sheets.Append(new Sheet { Id = workbook.GetIdOfPart(worksheet), SheetId = 1, Name = "Quality" });
            workbook.Workbook.Save();
        }

        string result = await DocumentTextExtractor.ExtractAsync(path, CancellationToken.None);

        Assert.Contains("[Sheet: Quality]", result);
        Assert.Contains("PaRuX XLSX evidence", result);
    }

    [Fact]
    public async Task CapsLargeTextDocuments()
    {
        string path = Path.Combine(_directory, "large.txt");
        await File.WriteAllTextAsync(path, new string('x', DocumentTextExtractor.MaximumCharacters + 500));

        string result = await DocumentTextExtractor.ExtractAsync(path, CancellationToken.None);

        Assert.Contains("[Document truncated at 120,000 characters]", result);
        Assert.True(result.Length < DocumentTextExtractor.MaximumCharacters + 100);
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, true); } catch { }
    }
}
