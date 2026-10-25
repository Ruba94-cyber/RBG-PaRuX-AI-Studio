using DocumentFormat.OpenXml.Packaging;
using PaRuX.Contracts;
using PaRuX.Server.DesktopIntegration;
using X = DocumentFormat.OpenXml.Spreadsheet;
using C = DocumentFormat.OpenXml.Drawing.Charts;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class WorkbookNativeV2Tests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parux-workbook-v2-" + Guid.NewGuid().ToString("N"));
    private readonly string _state;
    private readonly string _output;

    public WorkbookNativeV2Tests()
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

    private DocumentWriteService Service()
    {
        var service = new DocumentWriteService(_state);
        service.SetOutputFolder(_output);
        return service;
    }

    [Fact]
    public void Cross_sheet_formula_is_real_and_cached_from_independent_local_evaluation()
    {
        DocumentWriteResult result = Service().WriteWorkbook("Budget", new WorkbookContent
        {
            Sheets = new List<WorkbookSheet>
            {
                new()
                {
                    Name = "Inputs",
                    StructuredCells = new List<WorkbookCell>
                    {
                        new() { Reference = "B2", Value = "100", DataType = "number" },
                        new() { Reference = "B3", Value = "50", DataType = "number" }
                    }
                },
                new()
                {
                    Name = "Summary",
                    StructuredCells = new List<WorkbookCell>
                    {
                        new() { Reference = "C2", Formula = "=SUM(Inputs!B2:B3)*1.2" }
                    }
                }
            }
        });

        Assert.Equal("cached-local-and-recalc-on-open", result.CalculationStatus);
        Assert.True(result.OpenXmlValidated, string.Join(" | ", result.ValidationWarnings));
        using SpreadsheetDocument document = SpreadsheetDocument.Open(result.Path, false);
        X.Cell cell = Cell(document, "Summary", "C2");
        Assert.Equal("SUM(Inputs!B2:B3)*1.2", cell.CellFormula?.Text);
        Assert.Equal(180d, double.Parse(cell.CellValue!.Text, System.Globalization.CultureInfo.InvariantCulture), 8);
        Assert.True(document.WorkbookPart!.Workbook.CalculationProperties?.FullCalculationOnLoad?.Value);
    }

    [Fact]
    public void Editing_workbook_formula_can_use_existing_cross_sheet_numeric_cells()
    {
        DocumentWriteService service = Service();
        DocumentWriteResult source = service.WriteWorkbook("FormulaSource", new WorkbookContent
        {
            Sheets = new List<WorkbookSheet>
            {
                new()
                {
                    Name = "Inputs",
                    StructuredCells = new List<WorkbookCell>
                    {
                        new() { Reference = "A1", Value = "21", DataType = "number" },
                        new() { Reference = "A2", Formula = "=A1+1" }
                    }
                },
                new()
                {
                    Name = "Summary",
                    StructuredCells = new List<WorkbookCell>
                    {
                        new() { Reference = "B1", Value = "keep this note" }
                    }
                }
            }
        });
        byte[] originalBytes = File.ReadAllBytes(source.Path);

        DocumentWriteResult edited = service.EditWorkbook(source.FileName, "FormulaEdit", new WorkbookContent
        {
            Sheets = new List<WorkbookSheet>
            {
                new()
                {
                    Name = "Summary",
                    StructuredCells = new List<WorkbookCell>
                    {
                        new() { Reference = "A1", Formula = "=Inputs!A2*2" }
                    }
                }
            }
        });

        Assert.True(edited.SourceWorkbookPreserved);
        Assert.Equal(originalBytes, File.ReadAllBytes(source.Path));
        Assert.Equal("cached-local-and-recalc-on-open", edited.CalculationStatus);
        Assert.True(edited.OpenXmlValidated, string.Join(" | ", edited.ValidationWarnings));
        using SpreadsheetDocument document = SpreadsheetDocument.Open(edited.Path, false);
        X.Cell formulaCell = Cell(document, "Summary", "A1");
        Assert.Equal("Inputs!A2*2", formulaCell.CellFormula?.Text);
        Assert.Equal(44d, double.Parse(formulaCell.CellValue!.Text,
            System.Globalization.CultureInfo.InvariantCulture), 8);
        Assert.Equal("keep this note", Inline(Cell(document, "Summary", "B1")));
    }

    [Fact]
    public void Editing_an_unrelated_cell_preserves_opaque_table_formulas()
    {
        DocumentWriteService service = Service();
        DocumentWriteResult source = service.WriteWorkbook("TableFormulaSource", new WorkbookContent
        {
            Sheets = new List<WorkbookSheet>
            {
                new()
                {
                    Name = "Sales",
                    StructuredCells = new List<WorkbookCell>
                    {
                        new() { Reference = "A1", Value = "1", DataType = "number" },
                        new() { Reference = "B1", Value = "before" }
                    }
                }
            }
        });
        using (SpreadsheetDocument document = SpreadsheetDocument.Open(source.Path, true))
        {
            X.Cell formula = Cell(document, "Sales", "A1");
            formula.CellFormula = new X.CellFormula("SUM(Table1[Amount])");
            formula.CellValue = new X.CellValue("12");
            SheetPart(document, "Sales").Worksheet.Save();
        }

        DocumentWriteResult edited = service.EditWorkbook(source.FileName, "TableFormulaEdit", new WorkbookContent
        {
            Sheets = new List<WorkbookSheet>
            {
                new()
                {
                    Name = "Sales",
                    StructuredCells = new List<WorkbookCell> { new() { Reference = "B1", Value = "after" } }
                }
            }
        });

        Assert.True(edited.OpenXmlValidated, string.Join(" | ", edited.ValidationWarnings));
        Assert.Equal("contains-uncomputed-formulas-recalc-on-open", edited.CalculationStatus);
        using SpreadsheetDocument editedDocument = SpreadsheetDocument.Open(edited.Path, false);
        Assert.Equal("SUM(Table1[Amount])", Cell(editedDocument, "Sales", "A1").CellFormula?.Text);
        Assert.Equal("after", Inline(Cell(editedDocument, "Sales", "B1")));
        Assert.True(editedDocument.WorkbookPart!.Workbook.CalculationProperties?.FullCalculationOnLoad?.Value);
    }

    [Fact]
    public void Shared_formula_follower_cache_is_not_used_as_a_new_formula_input()
    {
        DocumentWriteService service = Service();
        DocumentWriteResult source = service.WriteWorkbook("SharedFormulaSource", new WorkbookContent
        {
            Sheets = new List<WorkbookSheet>
            {
                new()
                {
                    Name = "S",
                    StructuredCells = new List<WorkbookCell>
                    {
                        new() { Reference = "A1", Value = "2", DataType = "number" },
                        new() { Reference = "A2", Value = "3", DataType = "number" },
                        new() { Reference = "B1", Value = "4", DataType = "number" },
                        new() { Reference = "B2", Value = "6", DataType = "number" },
                        new() { Reference = "C1", Value = "old" }
                    }
                }
            }
        });
        using (SpreadsheetDocument document = SpreadsheetDocument.Open(source.Path, true))
        {
            X.Cell master = Cell(document, "S", "B1");
            master.CellFormula = new X.CellFormula("A1*2")
            {
                FormulaType = X.CellFormulaValues.Shared,
                SharedIndex = 1U,
                Reference = "B1:B2"
            };
            master.CellValue = new X.CellValue("4");

            X.Cell follower = Cell(document, "S", "B2");
            follower.CellFormula = new X.CellFormula
            {
                FormulaType = X.CellFormulaValues.Shared,
                SharedIndex = 1U
            };
            follower.CellValue = new X.CellValue("999"); // Deliberately stale; never use as an input.
            SheetPart(document, "S").Worksheet.Save();
        }

        DocumentWriteResult edited = service.EditWorkbook(source.FileName, "SharedFormulaEdit", new WorkbookContent
        {
            Sheets = new List<WorkbookSheet>
            {
                new()
                {
                    Name = "S",
                    StructuredCells = new List<WorkbookCell>
                    {
                        new() { Reference = "A1", Value = "10", DataType = "number" },
                        new() { Reference = "C1", Formula = "=B2+1" }
                    }
                }
            }
        });

        using SpreadsheetDocument editedDocument = SpreadsheetDocument.Open(edited.Path, false);
        X.Cell newFormula = Cell(editedDocument, "S", "C1");
        Assert.Equal("B2+1", newFormula.CellFormula?.Text);
        Assert.Null(newFormula.CellValue);
        Assert.Equal("contains-uncomputed-formulas-recalc-on-open", edited.CalculationStatus);
        Assert.True(editedDocument.WorkbookPart!.Workbook.CalculationProperties?.FullCalculationOnLoad?.Value);
    }

    [Fact]
    public void Formula_evaluator_preserves_whitespace_in_existing_sheet_names()
    {
        var evaluator = new WorkbookFormulaEvaluator(
            new WorkbookContent
            {
                Sheets = new List<WorkbookSheet>
                {
                    new()
                    {
                        Name = "Summary",
                        StructuredCells = new List<WorkbookCell> { new() { Reference = "A1", Formula = "=' Sales '!A1+1" } }
                    }
                }
            },
            new WorkbookContent
            {
                Sheets = new List<WorkbookSheet>
                {
                    new()
                    {
                        Name = " Sales ",
                        StructuredCells = new List<WorkbookCell> { new() { Reference = "A1", Value = "7", DataType = "number" } }
                    }
                }
            });

        WorkbookFormulaResult formula = Assert.Single(evaluator.EvaluateAll());
        Assert.True(formula.Evaluated, formula.Error);
        Assert.Equal(8d, formula.NumericValue);
    }

    [Fact]
    public void Unsupported_formula_is_preserved_but_never_given_a_fabricated_cached_result()
    {
        DocumentWriteResult result = Service().WriteWorkbook("Formula", new WorkbookContent
        {
            Sheets = new List<WorkbookSheet>
            {
                new()
                {
                    Name = "S",
                    StructuredCells = new List<WorkbookCell>
                    {
                        new() { Reference = "A1", Formula = "=XLOOKUP(1,B1:B2,C1:C2)" }
                    }
                }
            }
        });

        Assert.Equal("contains-uncomputed-formulas-recalc-on-open", result.CalculationStatus);
        using SpreadsheetDocument document = SpreadsheetDocument.Open(result.Path, false);
        X.Cell cell = Cell(document, "S", "A1");
        Assert.Equal("XLOOKUP(1,B1:B2,C1:C2)", cell.CellFormula?.Text);
        Assert.Null(cell.CellValue);
    }

    [Fact]
    public void Formula_external_workbook_links_are_rejected_instead_of_creating_hidden_egress_dependencies()
    {
        WorkbookContent content = new()
        {
            Sheets = new List<WorkbookSheet>
            {
                new()
                {
                    Name = "S",
                    StructuredCells = new List<WorkbookCell>
                    {
                        new() { Reference = "A1", Formula = "='[Other.xlsx]Data'!A1" }
                    }
                }
            }
        };

        Assert.Throws<InvalidDataException>(() => Service().WriteWorkbook("Unsafe", content));
    }

    [Fact]
    public void Edit_refuses_a_real_vba_project_part_without_changing_source()
    {
        DocumentWriteResult source = Service().WriteWorkbook("VbaPartSource", new WorkbookContent
        {
            Sheets = new List<WorkbookSheet> { new() { Name = "Keep", StructuredCells = new List<WorkbookCell> { new() { Reference = "A1", Value = "source" } } } }
        });
        VbaProjectPart vbaPart;
        using (SpreadsheetDocument document = SpreadsheetDocument.Open(source.Path, true))
        {
            vbaPart = document.WorkbookPart!.AddNewPart<VbaProjectPart>();
            using var payload = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("synthetic non-executable VBA project marker"));
            vbaPart.FeedData(payload);
        }
        byte[] originalBytes = File.ReadAllBytes(source.Path);

        using (SpreadsheetDocument document = SpreadsheetDocument.Open(source.Path, false))
            Assert.NotNull(document.WorkbookPart!.VbaProjectPart);
        InvalidDataException error = Assert.Throws<InvalidDataException>(() => Service().EditWorkbook(
            source.FileName,
            "VbaPartEdited",
            new WorkbookContent { Sheets = new List<WorkbookSheet> { new() { Name = "Keep", StructuredCells = new List<WorkbookCell> { new() { Reference = "A1", Value = "edited" } } } } }));

        Assert.Contains("macro-enabled", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(originalBytes, File.ReadAllBytes(source.Path));
    }

    [Fact]
    public void Edit_refuses_a_real_external_workbook_part_without_changing_source()
    {
        DocumentWriteResult source = Service().WriteWorkbook("ExternalPartSource", new WorkbookContent
        {
            Sheets = new List<WorkbookSheet> { new() { Name = "Keep", StructuredCells = new List<WorkbookCell> { new() { Reference = "A1", Value = "source" } } } }
        });
        using (SpreadsheetDocument document = SpreadsheetDocument.Open(source.Path, true))
        {
            WorkbookPart workbookPart = document.WorkbookPart!;
            ExternalWorkbookPart externalPart = workbookPart.AddNewPart<ExternalWorkbookPart>();
            externalPart.AddExternalRelationship(
                "http://schemas.openxmlformats.org/officeDocument/2006/relationships/externalLinkPath",
                new Uri("file:///synthetic-unavailable-workbook.xlsx"),
                "rId1");
            externalPart.ExternalLink = new X.ExternalLink(new X.ExternalBook { Id = "rId1" });
            externalPart.ExternalLink.Save(externalPart);
        }
        byte[] originalBytes = File.ReadAllBytes(source.Path);

        using (SpreadsheetDocument document = SpreadsheetDocument.Open(source.Path, false))
            Assert.Single(document.WorkbookPart!.GetPartsOfType<ExternalWorkbookPart>());
        InvalidDataException error = Assert.Throws<InvalidDataException>(() => Service().EditWorkbook(
            source.FileName,
            "ExternalPartEdited",
            new WorkbookContent { Sheets = new List<WorkbookSheet> { new() { Name = "Keep", StructuredCells = new List<WorkbookCell> { new() { Reference = "A1", Value = "edited" } } } } }));

        Assert.Contains("external workbook links", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(originalBytes, File.ReadAllBytes(source.Path));
    }

    [Fact]
    public void Styled_cell_and_chart_are_native_openxml_parts_with_requested_ranges()
    {
        DocumentWriteResult result = Service().WriteWorkbook("Chart", new WorkbookContent
        {
            Sheets = new List<WorkbookSheet>
            {
                new()
                {
                    Name = "Sales",
                    StructuredCells = new List<WorkbookCell>
                    {
                        new() { Reference = "A1", Value = "Month", Style = new WorkbookCellStyle { Bold = true, FillColor = "D9EAF7" } },
                        new() { Reference = "B1", Value = "Revenue", Style = new WorkbookCellStyle { Bold = true } },
                        new() { Reference = "A2", Value = "Jan" },
                        new() { Reference = "B2", Value = "10", DataType = "number", Style = new WorkbookCellStyle { NumberFormat = "#,##0.00" } },
                        new() { Reference = "A3", Value = "Feb" },
                        new() { Reference = "B3", Value = "20", DataType = "number" },
                        new() { Reference = "A4", Value = "Mar" },
                        new() { Reference = "B4", Value = "-5", DataType = "number" }
                    },
                    Charts = new List<WorkbookChart>
                    {
                        new()
                        {
                            Type = "column",
                            Title = "Revenue",
                            CategoryRange = "A2:A4",
                            Series = new List<WorkbookChartSeries>
                            {
                                new() { Name = "Revenue", ValueRange = "B2:B4" }
                            },
                            FromCell = "D2",
                            ToCell = "K16"
                        }
                    }
                }
            }
        });

        Assert.True(result.OpenXmlValidated, string.Join(" | ", result.ValidationWarnings));
        using SpreadsheetDocument document = SpreadsheetDocument.Open(result.Path, false);
        WorksheetPart worksheet = SheetPart(document, "Sales");
        Assert.NotNull(worksheet.DrawingsPart);
        ChartPart chart = Assert.Single(worksheet.DrawingsPart!.ChartParts);
        string chartXml = chart.ChartSpace.OuterXml;
        Assert.Contains("'Sales'!$A$2:$A$4", chartXml, StringComparison.Ordinal);
        Assert.Contains("'Sales'!$B$2:$B$4", chartXml, StringComparison.Ordinal);
        C.BarChart barChart = Assert.Single(chart.ChartSpace.Descendants<C.BarChart>());
        C.BarChartSeries series = Assert.Single(barChart.Elements<C.BarChartSeries>());
        Assert.False(series.InvertIfNegative?.Val?.Value ?? true);
        C.CategoryAxis categoryAxis = Assert.Single(chart.ChartSpace.Descendants<C.CategoryAxis>());
        Assert.Equal(C.TickLabelPositionValues.Low, categoryAxis.TickLabelPosition?.Val?.Value);
        Assert.True(Cell(document, "Sales", "A1").StyleIndex?.Value > 0);
        Assert.True(Cell(document, "Sales", "B2").StyleIndex?.Value > 0);
    }

    [Fact]
    public void Horizontal_bar_chart_keeps_category_labels_next_to_axis_and_explicitly_preserves_negative_fill()
    {
        DocumentWriteResult result = Service().WriteWorkbook("HorizontalChart", new WorkbookContent
        {
            Sheets = new List<WorkbookSheet>
            {
                new()
                {
                    Name = "Sales",
                    StructuredCells = new List<WorkbookCell>
                    {
                        new() { Reference = "A1", Value = "Month" },
                        new() { Reference = "B1", Value = "Value" },
                        new() { Reference = "A2", Value = "Jan" },
                        new() { Reference = "B2", Value = "10", DataType = "number" },
                        new() { Reference = "A3", Value = "Feb" },
                        new() { Reference = "B3", Value = "-7", DataType = "number" }
                    },
                    Charts = new List<WorkbookChart>
                    {
                        new()
                        {
                            Type = "bar",
                            Title = "Horizontal amounts",
                            CategoryRange = "A2:A3",
                            Series = new List<WorkbookChartSeries>
                            {
                                new() { Name = "Value", ValueRange = "B2:B3" }
                            }
                        }
                    }
                }
            }
        });

        using SpreadsheetDocument document = SpreadsheetDocument.Open(result.Path, false);
        ChartPart chart = Assert.Single(SheetPart(document, "Sales").DrawingsPart!.ChartParts);
        C.BarChartSeries series = Assert.Single(chart.ChartSpace.Descendants<C.BarChartSeries>());
        Assert.False(series.InvertIfNegative?.Val?.Value ?? true);
        C.CategoryAxis categoryAxis = Assert.Single(chart.ChartSpace.Descendants<C.CategoryAxis>());
        Assert.Equal(C.TickLabelPositionValues.NextTo, categoryAxis.TickLabelPosition?.Val?.Value);
    }

    [Fact]
    public void Editing_workbook_preserves_unrelated_sheet_cell_style_and_original_file()
    {
        DocumentWriteService service = Service();
        DocumentWriteResult source = service.WriteWorkbook("Original", new WorkbookContent
        {
            Sheets = new List<WorkbookSheet>
            {
                new()
                {
                    Name = "Keep",
                    StructuredCells = new List<WorkbookCell>
                    {
                        new() { Reference = "A1", Value = "untouched", Style = new WorkbookCellStyle { Bold = true, FillColor = "FFF2CC" } }
                    }
                },
                new()
                {
                    Name = "Edit",
                    StructuredCells = new List<WorkbookCell>
                    {
                        new() { Reference = "A1", Value = "before" },
                        new() { Reference = "B1", Value = "preserve-me" }
                    }
                }
            }
        });
        byte[] originalBytes = File.ReadAllBytes(source.Path);
        uint originalStyle;
        using (SpreadsheetDocument original = SpreadsheetDocument.Open(source.Path, false))
            originalStyle = Cell(original, "Keep", "A1").StyleIndex?.Value ?? 0U;

        DocumentWriteResult edited = service.EditWorkbook(
            source.FileName,
            "Edited",
            new WorkbookContent
            {
                Sheets = new List<WorkbookSheet>
                {
                    new()
                    {
                        Name = "Edit",
                        StructuredCells = new List<WorkbookCell>
                        {
                            new() { Reference = "A1", Value = "after" }
                        }
                    }
                }
            });

        Assert.True(edited.SourceWorkbookPreserved);
        Assert.Equal(originalBytes, File.ReadAllBytes(source.Path));
        Assert.NotEqual(source.Path, edited.Path);
        using SpreadsheetDocument document = SpreadsheetDocument.Open(edited.Path, false);
        Assert.Equal("untouched", Inline(Cell(document, "Keep", "A1")));
        Assert.Equal(originalStyle, Cell(document, "Keep", "A1").StyleIndex?.Value ?? 0U);
        Assert.Equal("after", Inline(Cell(document, "Edit", "A1")));
        Assert.Equal("preserve-me", Inline(Cell(document, "Edit", "B1")));
    }

    [Fact]
    public void Edit_source_cannot_escape_output_folder()
    {
        DocumentWriteService service = Service();
        Assert.Throws<InvalidDataException>(() => service.EditWorkbook(
            "../outside.xlsx", "Edited", new WorkbookContent()));
    }

    [Fact]
    public void Formula_evaluator_detects_cycles_instead_of_caching_a_bogus_number()
    {
        var evaluator = new WorkbookFormulaEvaluator(new WorkbookContent
        {
            Sheets = new List<WorkbookSheet>
            {
                new()
                {
                    Name = "S",
                    StructuredCells = new List<WorkbookCell>
                    {
                        new() { Reference = "A1", Formula = "=B1+1" },
                        new() { Reference = "B1", Formula = "=A1+1" }
                    }
                }
            }
        });

        IReadOnlyList<WorkbookFormulaResult> result = evaluator.EvaluateAll();
        Assert.Equal(2, result.Count);
        Assert.All(result, item => Assert.False(item.Evaluated));
        Assert.Contains(result, item => item.Error?.Contains("Circular", StringComparison.OrdinalIgnoreCase) == true);
    }

    [Fact]
    public void Expanded_formula_functions_and_power_operator_evaluate_accurately_with_caching()
    {
        DocumentWriteService service = Service();
        DocumentWriteResult result = service.WriteWorkbook("FormulasAdvanced", new WorkbookContent
        {
            Sheets = new List<WorkbookSheet>
            {
                new()
                {
                    Name = "Data",
                    StructuredCells = new List<WorkbookCell>
                    {
                        new() { Reference = "A1", Value = "10", DataType = "number" },
                        new() { Reference = "A2", Value = "20", DataType = "number" },
                        new() { Reference = "A3", Value = "30", DataType = "number" },
                        new() { Reference = "A4", Value = "-5.25", DataType = "number" },
                        new() { Reference = "A5", Value = "0", DataType = "number" },
                        new() { Reference = "A6", Value = "16", DataType = "number" },

                        new() { Reference = "B1", Formula = "=COUNT(A1:A3)" },
                        new() { Reference = "B2", Formula = "=PRODUCT(A1:A3)" },
                        new() { Reference = "B3", Formula = "=ABS(A4)" },
                        new() { Reference = "B4", Formula = "=SQRT(A6)" },
                        new() { Reference = "B5", Formula = "=INT(A4)" },
                        new() { Reference = "B6", Formula = "=SIGN(A4)" },
                        new() { Reference = "B7", Formula = "=POWER(2, 3)" },
                        new() { Reference = "B8", Formula = "=2^3^2" },
                        new() { Reference = "B9", Formula = "=2 + 3 * 4 ^ 2" },
                        new() { Reference = "B10", Formula = "=MOD(10, 3)" },
                        new() { Reference = "B11", Formula = "=MOD(-3, 2)" },
                        new() { Reference = "B12", Formula = "=ROUND(3.14159, 2)" },
                        new() { Reference = "B13", Formula = "=ROUND(1250, -2)" },
                        new() { Reference = "B14", Formula = "=ROUNDUP(3.141, 2)" },
                        new() { Reference = "B15", Formula = "=ROUNDDOWN(3.149, 2)" },
                        new() { Reference = "B16", Formula = "=TRUNC(4.89, 1)" },
                        new() { Reference = "B17", Formula = "=EXP(0)" },
                        new() { Reference = "B18", Formula = "=LN(EXP(1))" },
                        new() { Reference = "B19", Formula = "=PI()" }
                    }
                }
            }
        });

        Assert.Equal("cached-local-and-recalc-on-open", result.CalculationStatus);
        Assert.True(result.OpenXmlValidated, string.Join(" | ", result.ValidationWarnings));
        using SpreadsheetDocument document = SpreadsheetDocument.Open(result.Path, false);

        Assert.Equal(3d, double.Parse(Cell(document, "Data", "B1").CellValue!.Text, System.Globalization.CultureInfo.InvariantCulture), 6);
        Assert.Equal(6000d, double.Parse(Cell(document, "Data", "B2").CellValue!.Text, System.Globalization.CultureInfo.InvariantCulture), 6);
        Assert.Equal(5.25d, double.Parse(Cell(document, "Data", "B3").CellValue!.Text, System.Globalization.CultureInfo.InvariantCulture), 6);
        Assert.Equal(4d, double.Parse(Cell(document, "Data", "B4").CellValue!.Text, System.Globalization.CultureInfo.InvariantCulture), 6);
        Assert.Equal(-6d, double.Parse(Cell(document, "Data", "B5").CellValue!.Text, System.Globalization.CultureInfo.InvariantCulture), 6);
        Assert.Equal(-1d, double.Parse(Cell(document, "Data", "B6").CellValue!.Text, System.Globalization.CultureInfo.InvariantCulture), 6);
        Assert.Equal(8d, double.Parse(Cell(document, "Data", "B7").CellValue!.Text, System.Globalization.CultureInfo.InvariantCulture), 6);
        Assert.Equal(64d, double.Parse(Cell(document, "Data", "B8").CellValue!.Text, System.Globalization.CultureInfo.InvariantCulture), 6);
        Assert.Equal(50d, double.Parse(Cell(document, "Data", "B9").CellValue!.Text, System.Globalization.CultureInfo.InvariantCulture), 6);
        Assert.Equal(1d, double.Parse(Cell(document, "Data", "B10").CellValue!.Text, System.Globalization.CultureInfo.InvariantCulture), 6);
        Assert.Equal(1d, double.Parse(Cell(document, "Data", "B11").CellValue!.Text, System.Globalization.CultureInfo.InvariantCulture), 6);
        Assert.Equal(3.14d, double.Parse(Cell(document, "Data", "B12").CellValue!.Text, System.Globalization.CultureInfo.InvariantCulture), 6);
        Assert.Equal(1300d, double.Parse(Cell(document, "Data", "B13").CellValue!.Text, System.Globalization.CultureInfo.InvariantCulture), 6);
        Assert.Equal(3.15d, double.Parse(Cell(document, "Data", "B14").CellValue!.Text, System.Globalization.CultureInfo.InvariantCulture), 6);
        Assert.Equal(3.14d, double.Parse(Cell(document, "Data", "B15").CellValue!.Text, System.Globalization.CultureInfo.InvariantCulture), 6);
        Assert.Equal(4.8d, double.Parse(Cell(document, "Data", "B16").CellValue!.Text, System.Globalization.CultureInfo.InvariantCulture), 6);
        Assert.Equal(1d, double.Parse(Cell(document, "Data", "B17").CellValue!.Text, System.Globalization.CultureInfo.InvariantCulture), 6);
        Assert.Equal(1d, double.Parse(Cell(document, "Data", "B18").CellValue!.Text, System.Globalization.CultureInfo.InvariantCulture), 6);
        Assert.Equal(Math.PI, double.Parse(Cell(document, "Data", "B19").CellValue!.Text, System.Globalization.CultureInfo.InvariantCulture), 6);
    }

    [Fact]
    public void Invalid_formula_domain_or_shape_fails_gracefully_without_corrupting_openxml()
    {
        DocumentWriteService service = Service();
        DocumentWriteResult result = service.WriteWorkbook("FormulasFaults", new WorkbookContent
        {
            Sheets = new List<WorkbookSheet>
            {
                new()
                {
                    Name = "Faults",
                    StructuredCells = new List<WorkbookCell>
                    {
                        new() { Reference = "A1", Value = "1", DataType = "number" },
                        new() { Reference = "A2", Value = "2", DataType = "number" },
                        new() { Reference = "B1", Formula = "=SQRT(-4)" },
                        new() { Reference = "B2", Formula = "=MOD(5, 0)" },
                        new() { Reference = "B3", Formula = "=ABS(A1:A2)" },
                        new() { Reference = "B4", Formula = "=PI(42)" }
                    }
                }
            }
        });

        Assert.Equal("contains-uncomputed-formulas-recalc-on-open", result.CalculationStatus);
        Assert.True(result.OpenXmlValidated, string.Join(" | ", result.ValidationWarnings));
        using SpreadsheetDocument document = SpreadsheetDocument.Open(result.Path, false);

        Assert.Null(Cell(document, "Faults", "B1").CellValue);
        Assert.Null(Cell(document, "Faults", "B2").CellValue);
        Assert.Null(Cell(document, "Faults", "B3").CellValue);
        Assert.Null(Cell(document, "Faults", "B4").CellValue);
    }

    private static WorksheetPart SheetPart(SpreadsheetDocument document, string name)
    {
        X.Sheet sheet = document.WorkbookPart!.Workbook.Sheets!.Elements<X.Sheet>()
            .Single(item => string.Equals(item.Name?.Value, name, StringComparison.Ordinal));
        return (WorksheetPart)document.WorkbookPart.GetPartById(sheet.Id!.Value!);
    }

    private static X.Cell Cell(SpreadsheetDocument document, string sheet, string reference) =>
        SheetPart(document, sheet).Worksheet.Descendants<X.Cell>()
            .Single(cell => string.Equals(cell.CellReference?.Value, reference, StringComparison.OrdinalIgnoreCase));

    private static string Inline(X.Cell cell) => cell.InlineString?.Text?.Text ?? cell.CellValue?.Text ?? string.Empty;
}
