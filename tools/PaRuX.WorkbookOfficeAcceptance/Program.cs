using System.Security.Cryptography;
using System.Text.Json;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using PaRuX.Contracts;
using PaRuX.Server.DesktopIntegration;

if (args.Length == 3 && args[0] == "--edit-office-source")
{
    EmitImportedWorkbookEdit(Path.GetFullPath(args[1]), args[2]);
    return;
}
if (args.Length != 1) throw new ArgumentException("Pass the isolated Office acceptance artifact directory.");
string artifactDirectory = Path.GetFullPath(args[0]);
string outputDirectory = Path.Combine(artifactDirectory, "workbooks");
string stateDirectory = Path.Combine(artifactDirectory, "state");
Directory.CreateDirectory(outputDirectory);

var service = new DocumentWriteService(stateDirectory);
service.SetOutputFolder(outputDirectory);
var content = new WorkbookContent
{
    Sheets = new List<WorkbookSheet>
    {
        new()
        {
            Name = "Inputs",
            StructuredCells = new List<WorkbookCell>
            {
                new() { Reference = "A1", Value = "Month", Style = new WorkbookCellStyle { Bold = true, FillColor = "D9EAF7" } },
                new() { Reference = "B1", Value = "Amount", Style = new WorkbookCellStyle { Bold = true, FillColor = "D9EAF7" } },
                new() { Reference = "A2", Value = "January" },
                new() { Reference = "B2", Value = "10", DataType = "number", Style = new WorkbookCellStyle { NumberFormat = "#,##0.00" } },
                new() { Reference = "A3", Value = "February" },
                new() { Reference = "B3", Value = "3", DataType = "number" },
                new() { Reference = "A4", Value = "March" },
                new() { Reference = "B4", Value = "-7", DataType = "number" },
                new() { Reference = "A5", Value = "Note" },
                new() { Reference = "B5", Value = "original note" }
            },
            Charts = new List<WorkbookChart>
            {
                new()
                {
                    Type = "column",
                    Title = "Monthly amounts",
                    CategoryRange = "A2:A4",
                    Series = new List<WorkbookChartSeries> { new() { Name = "Amount", ValueRange = "B2:B4" } },
                    FromCell = "D2",
                    ToCell = "K16"
                }
            }
        },
        new()
        {
            Name = "Rates 2026",
            StructuredCells = new List<WorkbookCell>
            {
                new() { Reference = "A1", Value = "Rate" },
                new() { Reference = "B1", Value = "1.2", DataType = "number", Style = new WorkbookCellStyle { NumberFormat = "0.0%" } }
            }
        },
        new()
        {
            Name = "Summary",
            StructuredCells = new List<WorkbookCell>
            {
                new() { Reference = "A1", Value = "PaRuX check", Style = new WorkbookCellStyle { Bold = true } },
                new() { Reference = "A2", Value = "Pow^" },
                new() { Reference = "B2", Formula = "=2^3^2" },
                new() { Reference = "A3", Value = "-2^2" },
                new() { Reference = "B3", Formula = "=-2^2" },
                new() { Reference = "A4", Value = "Mod neg" },
                new() { Reference = "B4", Formula = "=MOD(-7,3)" },
                new() { Reference = "A5", Value = "Round" },
                new() { Reference = "B5", Formula = "=ROUND(-2.5,0)" },
                new() { Reference = "A6", Value = "Count" },
                new() { Reference = "B6", Formula = "=COUNT(Inputs!B2:B4)" },
                new() { Reference = "A7", Value = "Product" },
                new() { Reference = "B7", Formula = "=PRODUCT(Inputs!B2:B4)" },
                new() { Reference = "A8", Value = "Sum" },
                new() { Reference = "B8", Formula = "=SUM(Inputs!B2:B4)" },
                new() { Reference = "A9", Value = "XRef" },
                new() { Reference = "B9", Formula = "='Rates 2026'!B1*100" },
                new() { Reference = "A10", Value = "Subtotal" },
                new() { Reference = "B10", Formula = "=SUBTOTAL(9,Inputs!B2:B4)" },
                new() { Reference = "A11", Value = "RoundUp" },
                new() { Reference = "B11", Formula = "=ROUNDUP(-1.234,2)" },
                new() { Reference = "A12", Value = "RoundDn" },
                new() { Reference = "B12", Formula = "=ROUNDDOWN(-1.234,2)" },
                new() { Reference = "A13", Value = "Int" },
                new() { Reference = "B13", Formula = "=INT(-2.1)" },
                new() { Reference = "A14", Value = "Sign" },
                new() { Reference = "B14", Formula = "=SIGN(Inputs!B4)" },
                new() { Reference = "A15", Value = "Power" },
                new() { Reference = "B15", Formula = "=POWER(2,3)" },
                new() { Reference = "A16", Value = "Sum refs" },
                new() { Reference = "B16", Formula = "=Inputs!B2+Inputs!B3" },
                new() { Reference = "A17", Value = "Mixed" }
            }
        }
    }
};

DocumentWriteResult created = service.WriteWorkbook("Office Acceptance Source", content);
string sourceHashBeforeEdit = Hash(created.Path);
uint sourceHeaderStyle = GetStyleIndex(created.Path, "Inputs", "A1");
uint sourceAmountStyle = GetStyleIndex(created.Path, "Inputs", "B2");
DocumentWriteResult edited = service.EditWorkbook(created.FileName, "Office Acceptance Edited", new WorkbookContent
{
    Sheets = new List<WorkbookSheet>
    {
        new()
        {
            Name = "Inputs",
            StructuredCells = new List<WorkbookCell> { new() { Reference = "B5", Value = "edited note" } }
        },
        new()
        {
            Name = "Summary",
            StructuredCells = new List<WorkbookCell>
            {
                new() { Reference = "A1", Value = "Edited check", Style = new WorkbookCellStyle { Bold = true } },
                new() { Reference = "A17", Value = "Mixed" },
                new() { Reference = "B17", Formula = "=SUM(Inputs!B2:B4)*'Rates 2026'!B1" }
            }
        }
    }
});
string sourceHashAfterEdit = Hash(created.Path);

using SpreadsheetDocument document = SpreadsheetDocument.Open(edited.Path, false);
WorkbookPart workbookPart = document.WorkbookPart!;
List<FormulaCache> formulas = new();
foreach (Sheet sheet in workbookPart.Workbook.Sheets!.Elements<Sheet>())
{
    WorksheetPart part = (WorksheetPart)workbookPart.GetPartById(sheet.Id!);
    foreach (Cell cell in part.Worksheet.Descendants<Cell>().Where(c => c.CellFormula != null))
    {
        formulas.Add(new FormulaCache(
            sheet.Name!.Value + "!" + cell.CellReference!.Value,
            cell.CellFormula!.Text ?? string.Empty,
            cell.CellValue?.Text));
    }
}

var summary = new
{
    created = new { created.Path, created.CalculationStatus, created.OpenXmlValidated, created.SizeBytes },
    edited = new { edited.Path, edited.CalculationStatus, edited.OpenXmlValidated, edited.SourceWorkbookPreserved, edited.SizeBytes },
    sourceHashBeforeEdit,
    sourceHashAfterEdit,
    sourceUntouched = sourceHashBeforeEdit == sourceHashAfterEdit,
    styleIndexes = new
    {
        sourceHeader = sourceHeaderStyle,
        editedHeader = GetStyleIndex(edited.Path, "Inputs", "A1"),
        sourceAmount = sourceAmountStyle,
        editedAmount = GetStyleIndex(edited.Path, "Inputs", "B2")
    },
    formulaCount = formulas.Count,
    formulas,
    sheetNames = workbookPart.Workbook.Sheets.Elements<Sheet>().Select(s => s.Name!.Value).ToArray(),
    nonDefaultStyleCellCount = workbookPart.WorksheetParts.SelectMany(p => p.Worksheet.Descendants<Cell>())
        .Count(c => c.StyleIndex?.Value > 0),
    chartPartCount = workbookPart.WorksheetParts.SelectMany(p => p.DrawingsPart?.ChartParts ?? Enumerable.Empty<ChartPart>()).Count(),
    chartXml = workbookPart.WorksheetParts.SelectMany(p => p.DrawingsPart?.ChartParts ?? Enumerable.Empty<ChartPart>())
        .Select(c => c.ChartSpace.OuterXml).ToArray(),
    macroProjectPresent = workbookPart.VbaProjectPart != null,
    externalWorkbookLinkCount = workbookPart.GetPartsOfType<ExternalWorkbookPart>().Count()
};
Console.WriteLine("PARUX_OFFICE_FIXTURE=" + JsonSerializer.Serialize(summary));

static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
static uint GetStyleIndex(string path, string sheetName, string reference)
{
    using SpreadsheetDocument document = SpreadsheetDocument.Open(path, false);
    WorkbookPart workbookPart = document.WorkbookPart!;
    Sheet sheet = workbookPart.Workbook.Sheets!.Elements<Sheet>().Single(s => s.Name!.Value == sheetName);
    WorksheetPart worksheetPart = (WorksheetPart)workbookPart.GetPartById(sheet.Id!);
    Cell cell = worksheetPart.Worksheet.Descendants<Cell>().Single(c => c.CellReference!.Value == reference);
    return cell.StyleIndex?.Value ?? 0;
}
static void EmitImportedWorkbookEdit(string artifactDirectory, string sourceFileName)
{
    string outputDirectory = Path.Combine(artifactDirectory, "workbooks");
    string stateDirectory = Path.Combine(artifactDirectory, "state");
    string sourcePath = Path.Combine(outputDirectory, sourceFileName);
    if (!File.Exists(sourcePath)) throw new FileNotFoundException("Office-originated workbook was not found in the isolated workbooks directory.", sourcePath);
    string sourceHashBefore = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(sourcePath)));
    var service = new DocumentWriteService(stateDirectory);
    service.SetOutputFolder(outputDirectory);
    DocumentWriteResult edited = service.EditWorkbook(sourceFileName, "Office Originated Edited", new WorkbookContent
    {
        Sheets = new List<WorkbookSheet>
        {
            new()
            {
                Name = "Imported Data",
                StructuredCells = new List<WorkbookCell>
                {
                    new() { Reference = "B4", Value = "3", DataType = "number" }
                }
            },
            new()
            {
                Name = "Summary",
                StructuredCells = new List<WorkbookCell>
                {
                    new() { Reference = "A2", Value = "New cross-sheet result" },
                    new() { Reference = "C2", Formula = "='Imported Data'!D4*2" }
                }
            }
        }
    });
    string sourceHashAfter = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(sourcePath)));

    using SpreadsheetDocument document = SpreadsheetDocument.Open(edited.Path, false);
    WorkbookPart workbookPart = document.WorkbookPart!;
    var sheetDetails = workbookPart.Workbook.Sheets!.Elements<Sheet>().Select(sheet =>
    {
        WorksheetPart part = (WorksheetPart)workbookPart.GetPartById(sheet.Id!);
        TableParts? tables = part.Worksheet.Elements<TableParts>().FirstOrDefault();
        return new
        {
            name = sheet.Name!.Value,
            state = sheet.State?.InnerText ?? "visible",
            mergeRanges = part.Worksheet.Elements<MergeCells>().FirstOrDefault()?.Elements<MergeCell>().Select(m => m.Reference!.Value).ToArray() ?? Array.Empty<string>(),
            tableCount = tables?.Elements<TablePart>().Count() ?? 0,
            tableNames = tables?.Elements<TablePart>().Select(t => ((TableDefinitionPart)part.GetPartById(t.Id!)).Table!.DisplayName!.Value).ToArray() ?? Array.Empty<string>(),
            chartCount = part.DrawingsPart?.ChartParts.Count() ?? 0,
            chartReferences = part.DrawingsPart?.ChartParts.SelectMany(c => c.ChartSpace.Descendants<DocumentFormat.OpenXml.Drawing.Charts.CategoryAxisData>().SelectMany(x => x.Descendants<DocumentFormat.OpenXml.Drawing.Charts.Formula>())
                .Concat(c.ChartSpace.Descendants<DocumentFormat.OpenXml.Drawing.Charts.Values>().SelectMany(x => x.Descendants<DocumentFormat.OpenXml.Drawing.Charts.Formula>()))).Select(f => f.Text).ToArray() ?? Array.Empty<string>()
        };
    }).ToArray();
    var definedNames = workbookPart.Workbook.DefinedNames?.Elements<DefinedName>().Select(n => (object)new { name = n.Name!.Value, formula = n.Text }).ToArray() ?? Array.Empty<object>();
    var report = new
    {
        sourceFileName,
        sourceHashBefore,
        sourceHashAfter,
        sourceUntouched = sourceHashBefore == sourceHashAfter,
        sourceValidationWarnings = PackageWarnings(sourcePath, DocumentFormat.OpenXml.FileFormatVersions.Office2007),
        sourceOffice2019ValidationWarnings = PackageWarnings(sourcePath, DocumentFormat.OpenXml.FileFormatVersions.Office2019),
        editedOffice2019ValidationWarnings = PackageWarnings(edited.Path, DocumentFormat.OpenXml.FileFormatVersions.Office2019),
        edited = new { edited.Path, edited.OpenXmlValidated, edited.SourceWorkbookPreserved, edited.SizeBytes, edited.ValidationWarnings },
        sheets = sheetDetails,
        definedNames,
        editedValue = ReadCell(edited.Path, "Imported Data", "B4"),
        unrelatedValue = ReadCell(edited.Path, "Imported Data", "C4"),
        sourceFormula = ReadFormula(edited.Path, "Imported Data", "D4"),
        newFormula = ReadFormula(edited.Path, "Summary", "C2"),
        styleIndexes = new
        {
            mergedHeader = GetStyleIndex(edited.Path, "Imported Data", "A1"),
            editedInput = GetStyleIndex(edited.Path, "Imported Data", "B4"),
            sourceMergedHeader = GetStyleIndex(sourcePath, "Imported Data", "A1"),
            sourceInput = GetStyleIndex(sourcePath, "Imported Data", "B4")
        },
        macroProjectPresent = workbookPart.VbaProjectPart != null,
        externalWorkbookLinkCount = workbookPart.GetPartsOfType<ExternalWorkbookPart>().Count()
    };
    Console.WriteLine("PARUX_OFFICE_IMPORTED_EDIT=" + JsonSerializer.Serialize(report));
}

static string[] PackageWarnings(string path, DocumentFormat.OpenXml.FileFormatVersions version)
{
    using SpreadsheetDocument package = SpreadsheetDocument.Open(path, false);
    return new DocumentFormat.OpenXml.Validation.OpenXmlValidator(version).Validate(package)
        .Take(50).Select(error => (error.Path?.XPath ?? "package") + ": " + error.Description).ToArray();
}

static string? ReadCell(string path, string sheetName, string reference)
{
    using SpreadsheetDocument document = SpreadsheetDocument.Open(path, false);
    WorkbookPart workbookPart = document.WorkbookPart!;
    Sheet sheet = workbookPart.Workbook.Sheets!.Elements<Sheet>().Single(s => s.Name!.Value == sheetName);
    WorksheetPart part = (WorksheetPart)workbookPart.GetPartById(sheet.Id!);
    Cell? cell = part.Worksheet.Descendants<Cell>().SingleOrDefault(c => c.CellReference!.Value == reference);
    if (cell is null) return null;
    if (cell.DataType?.Value == CellValues.SharedString && int.TryParse(cell.CellValue?.Text, out int sharedIndex))
        return workbookPart.SharedStringTablePart?.SharedStringTable?.Elements<SharedStringItem>().ElementAt(sharedIndex).InnerText;
    return cell.CellValue?.Text ?? cell.InlineString?.InnerText ?? cell.CellFormula?.Text;
}

static string? ReadFormula(string path, string sheetName, string reference)
{
    using SpreadsheetDocument document = SpreadsheetDocument.Open(path, false);
    WorkbookPart workbookPart = document.WorkbookPart!;
    Sheet sheet = workbookPart.Workbook.Sheets!.Elements<Sheet>().Single(s => s.Name!.Value == sheetName);
    WorksheetPart part = (WorksheetPart)workbookPart.GetPartById(sheet.Id!);
    return part.Worksheet.Descendants<Cell>().SingleOrDefault(c => c.CellReference!.Value == reference)?.CellFormula?.Text;
}

sealed record FormulaCache(string Cell, string Formula, string? LocalCache);
