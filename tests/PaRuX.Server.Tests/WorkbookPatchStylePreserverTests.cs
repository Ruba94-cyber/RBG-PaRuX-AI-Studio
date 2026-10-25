using DocumentFormat.OpenXml.Packaging;
using PaRuX.Contracts;
using PaRuX.Server.DesktopIntegration;
using X = DocumentFormat.OpenXml.Spreadsheet;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class WorkbookPatchStylePreserverTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parux-workbook-style-edit-" + Guid.NewGuid().ToString("N"));
    private readonly string _state;
    private readonly string _output;

    public WorkbookPatchStylePreserverTests()
    {
        _state = Path.Combine(_root, "state");
        _output = Path.Combine(_root, "out");
        Directory.CreateDirectory(_state);
        Directory.CreateDirectory(_output);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void Editing_a_styled_cell_without_a_new_style_preserves_its_presentation()
    {
        DocumentWriteService service = Service();
        DocumentWriteResult source = service.WriteWorkbook("Styled", WorkbookWithStyledA1("before"));
        uint styleBefore = StyleIndex(source.Path, "S", "A1");
        Assert.True(styleBefore > 0);

        DocumentWriteResult edited = service.EditWorkbook(source.FileName, "Edited", new WorkbookContent
        {
            Sheets = new List<WorkbookSheet>
            {
                new()
                {
                    Name = "S",
                    StructuredCells = new List<WorkbookCell>
                    {
                        new() { Reference = "A1", Value = "after" }
                    }
                }
            }
        });

        Assert.Equal(styleBefore, StyleIndex(edited.Path, "S", "A1"));
        Assert.Equal("after", InlineValue(edited.Path, "S", "A1"));
        Assert.True(edited.OpenXmlValidated, string.Join(" | ", edited.ValidationWarnings));
    }

    [Fact]
    public void Explicit_new_style_is_not_replaced_by_the_source_style()
    {
        DocumentWriteService service = Service();
        DocumentWriteResult source = service.WriteWorkbook("Styled", WorkbookWithStyledA1("before"));
        uint styleBefore = StyleIndex(source.Path, "S", "A1");

        DocumentWriteResult edited = service.EditWorkbook(source.FileName, "Restyled", new WorkbookContent
        {
            Sheets = new List<WorkbookSheet>
            {
                new()
                {
                    Name = "S",
                    StructuredCells = new List<WorkbookCell>
                    {
                        new()
                        {
                            Reference = "A1",
                            Value = "after",
                            Style = new WorkbookCellStyle { Bold = false, FillColor = "D9EAD3", HorizontalAlignment = "right" }
                        }
                    }
                }
            }
        });

        Assert.NotEqual(styleBefore, StyleIndex(edited.Path, "S", "A1"));
        Assert.Equal("after", InlineValue(edited.Path, "S", "A1"));
    }

    [Fact]
    public void Legacy_row_edit_also_preserves_an_existing_target_cell_style()
    {
        DocumentWriteService service = Service();
        DocumentWriteResult source = service.WriteWorkbook("Styled", WorkbookWithStyledA1("before"));
        uint styleBefore = StyleIndex(source.Path, "S", "A1");

        DocumentWriteResult edited = service.EditWorkbook(source.FileName, "LegacyEdit", new WorkbookContent
        {
            Sheets = new List<WorkbookSheet>
            {
                new()
                {
                    Name = "S",
                    Rows = new List<WorkbookRow>
                    {
                        new() { Cells = new List<string> { "after" } }
                    }
                }
            }
        });

        Assert.Equal(styleBefore, StyleIndex(edited.Path, "S", "A1"));
        Assert.Equal("after", InlineValue(edited.Path, "S", "A1"));
    }

    private DocumentWriteService Service()
    {
        var service = new DocumentWriteService(_state);
        service.SetOutputFolder(_output);
        return service;
    }

    private static WorkbookContent WorkbookWithStyledA1(string value) => new()
    {
        Sheets = new List<WorkbookSheet>
        {
            new()
            {
                Name = "S",
                StructuredCells = new List<WorkbookCell>
                {
                    new()
                    {
                        Reference = "A1",
                        Value = value,
                        Style = new WorkbookCellStyle
                        {
                            Bold = true,
                            FillColor = "FFF2CC",
                            FontColor = "9C0006",
                            HorizontalAlignment = "center"
                        }
                    }
                }
            }
        }
    };

    private static uint StyleIndex(string path, string sheetName, string reference)
    {
        using SpreadsheetDocument document = SpreadsheetDocument.Open(path, false);
        return Cell(document, sheetName, reference).StyleIndex?.Value ?? 0U;
    }

    private static string InlineValue(string path, string sheetName, string reference)
    {
        using SpreadsheetDocument document = SpreadsheetDocument.Open(path, false);
        X.Cell cell = Cell(document, sheetName, reference);
        return cell.InlineString?.Text?.Text ?? cell.CellValue?.Text ?? string.Empty;
    }

    private static X.Cell Cell(SpreadsheetDocument document, string sheetName, string reference)
    {
        X.Sheet sheet = document.WorkbookPart!.Workbook.Sheets!.Elements<X.Sheet>()
            .Single(item => string.Equals(item.Name?.Value, sheetName, StringComparison.Ordinal));
        WorksheetPart worksheet = (WorksheetPart)document.WorkbookPart.GetPartById(sheet.Id!.Value!);
        return worksheet.Worksheet.Descendants<X.Cell>()
            .Single(cell => string.Equals(cell.CellReference?.Value, reference, StringComparison.OrdinalIgnoreCase));
    }
}