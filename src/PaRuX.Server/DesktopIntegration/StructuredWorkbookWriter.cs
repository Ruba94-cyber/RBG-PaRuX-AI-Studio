using System.Globalization;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using PaRuX.Contracts;
using A = DocumentFormat.OpenXml.Drawing;
using C = DocumentFormat.OpenXml.Drawing.Charts;
using Xdr = DocumentFormat.OpenXml.Drawing.Spreadsheet;
using X = DocumentFormat.OpenXml.Spreadsheet;

namespace PaRuX.Server.DesktopIntegration;

internal sealed record StructuredWorkbookResult(
    byte[] Bytes,
    string CalculationStatus,
    bool Validated,
    IReadOnlyList<string> ValidationWarnings);

/// <summary>
/// Native XLSX writer/editor. It preserves the legacy row contract, adds formula/style/chart
/// semantics, and can patch an existing workbook in memory without deleting unrelated sheets,
/// cells, styles or package parts. Formula caches are written only when the bounded PaRuX evaluator
/// independently produced the value. Unsupported formulas are left without a fabricated cache and
/// marked for recalculation on open.
/// </summary>
internal static class StructuredWorkbookWriter
{
    private const int MaximumValidationWarnings = 50;

    internal static StructuredWorkbookResult Build(WorkbookContent content)
    {
        ArgumentNullException.ThrowIfNull(content);
        using var stream = new MemoryStream();
        using (SpreadsheetDocument document = SpreadsheetDocument.Create(stream, SpreadsheetDocumentType.Workbook, true))
        {
            WorkbookPart workbookPart = document.AddWorkbookPart();
            workbookPart.Workbook = new X.Workbook(new X.Sheets());
            Apply(document, content, patchExisting: false);
        }
        return Validate(stream.ToArray(), content);
    }

    internal static StructuredWorkbookResult Patch(byte[] source, WorkbookContent content)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(content);
        using var stream = new MemoryStream();
        stream.Write(source, 0, source.Length);
        stream.Position = 0;
        WorkbookContent evaluationBase;
        using (SpreadsheetDocument document = SpreadsheetDocument.Open(stream, true))
        {
            WorkbookPart workbookPart = document.WorkbookPart
                ?? throw new InvalidDataException("Existing workbook has no WorkbookPart.");
            if (workbookPart.GetPartsOfType<VbaProjectPart>().Any())
                throw new InvalidDataException("PaRuX will not edit a macro-enabled workbook through the native XLSX tool.");
            if (workbookPart.GetPartsOfType<ExternalWorkbookPart>().Any())
                throw new InvalidDataException("PaRuX will not edit a workbook containing external workbook links through the native tool.");
            workbookPart.Workbook ??= new X.Workbook(new X.Sheets());
            workbookPart.Workbook.Sheets ??= new X.Sheets();
            evaluationBase = ReadFormulaEvaluationContext(workbookPart);
            Apply(document, content, patchExisting: true, evaluationBase);
        }
        return Validate(stream.ToArray(), content, evaluationBase);
    }

    private static WorkbookContent ReadFormulaEvaluationContext(WorkbookPart workbookPart)
    {
        var sheets = new List<WorkbookSheet>();
        foreach (X.Sheet sheet in workbookPart.Workbook.Sheets?.Elements<X.Sheet>() ?? Enumerable.Empty<X.Sheet>())
        {
            string name = sheet.Name?.Value
                ?? throw new InvalidDataException("Existing workbook contains a sheet without a name.");
            if (sheet.Id?.Value is not string relationshipId)
                throw new InvalidDataException("Existing workbook contains a sheet without a worksheet relationship.");

            if (workbookPart.GetPartById(relationshipId) is not WorksheetPart worksheetPart)
            {
                // Preserve non-worksheet sheet kinds in the package. They have no cell values
                // usable by this evaluator, but keeping the name lets it report unresolved refs.
                sheets.Add(new WorkbookSheet { Name = name, StructuredCells = new List<WorkbookCell>() });
                continue;
            }
            var cells = new List<WorkbookCell>();
            foreach (X.Cell cell in worksheetPart.Worksheet?.Descendants<X.Cell>() ?? Enumerable.Empty<X.Cell>())
            {
                string? reference = cell.CellReference?.Value;
                if (string.IsNullOrWhiteSpace(reference)) continue;
                if (cell.CellFormula != null)
                {
                    // Empty shared-formula followers and formulas outside our bounded grammar
                    // remain opaque dependencies. The evaluator reports them as uncomputed and
                    // never trusts their possibly stale cached <v> values.
                    cells.Add(new WorkbookCell { Reference = reference, Formula = cell.CellFormula.Text ?? string.Empty });
                    continue;
                }

                X.CellValues? dataType = cell.DataType?.Value;
                string? numericText = cell.CellValue?.Text;
                if ((dataType.HasValue && dataType.Value != X.CellValues.Number)
                    || !double.TryParse(numericText, NumberStyles.Float | NumberStyles.AllowThousands,
                        CultureInfo.InvariantCulture, out _))
                    continue;

                cells.Add(new WorkbookCell
                {
                    Reference = reference,
                    Value = numericText,
                    DataType = "number"
                });
            }

            sheets.Add(new WorkbookSheet { Name = name, StructuredCells = cells });
        }

        return new WorkbookContent { Sheets = sheets };
    }

    private static void Apply(
        SpreadsheetDocument document,
        WorkbookContent content,
        bool patchExisting,
        WorkbookContent? evaluationBase = null)
    {
        WorkbookPart workbookPart = document.WorkbookPart!;
        X.Sheets sheets = workbookPart.Workbook.Sheets as X.Sheets
            ?? workbookPart.Workbook.AppendChild(new X.Sheets());
        var styles = new WorkbookStyleRegistry(workbookPart);
        var evaluator = new WorkbookFormulaEvaluator(content, evaluationBase);
        IReadOnlyList<WorkbookFormulaResult> formulaResults = evaluator.EvaluateAll();

        uint nextSheetId = sheets.Elements<X.Sheet>().Select(sheet => sheet.SheetId?.Value ?? 0U).DefaultIfEmpty(0U).Max() + 1U;
        var occupiedNames = sheets.Elements<X.Sheet>()
            .Select(sheet => sheet.Name?.Value ?? string.Empty)
            .Where(name => name.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        uint inputSheetNumber = 1;

        List<WorkbookSheet> input = content.Sheets is { Count: > 0 }
            ? content.Sheets
            : patchExisting ? new List<WorkbookSheet>() : new List<WorkbookSheet> { new() { Name = "Sheet1" } };

        foreach (WorkbookSheet requested in input)
        {
            string requestedName = OfficeDocumentWriter.SafeSheetName(requested.Name, inputSheetNumber++);
            X.Sheet? sheet = sheets.Elements<X.Sheet>().FirstOrDefault(item =>
                string.Equals(item.Name?.Value, requestedName, StringComparison.OrdinalIgnoreCase));
            WorksheetPart worksheetPart;
            string actualName;
            if (sheet == null)
            {
                actualName = UniqueSheetName(requestedName, occupiedNames);
                worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
                worksheetPart.Worksheet = new X.Worksheet(new X.SheetData());
                sheet = new X.Sheet
                {
                    Id = workbookPart.GetIdOfPart(worksheetPart),
                    SheetId = nextSheetId++,
                    Name = actualName
                };
                sheets.Append(sheet);
                occupiedNames.Add(actualName);
            }
            else
            {
                actualName = sheet.Name?.Value ?? requestedName;
                worksheetPart = (WorksheetPart)workbookPart.GetPartById(sheet.Id!);
                worksheetPart.Worksheet ??= new X.Worksheet(new X.SheetData());
                if (worksheetPart.Worksheet.GetFirstChild<X.SheetData>() == null)
                    worksheetPart.Worksheet.Append(new X.SheetData());
            }

            ApplyLegacyRows(worksheetPart, requested, actualName, evaluator, styles);
            ApplyStructuredCells(worksheetPart, requested, actualName, evaluator, styles);
            ApplyCharts(worksheetPart, requested, actualName);
            worksheetPart.Worksheet.Save();
        }

        if (content.RecalculateOnOpen || formulaResults.Count > 0)
        {
            X.CalculationProperties properties = workbookPart.Workbook.CalculationProperties
                ?? workbookPart.Workbook.AppendChild(new X.CalculationProperties());
            properties.CalculationMode = X.CalculateModeValues.Auto;
            properties.FullCalculationOnLoad = true;
            properties.ForceFullCalculation = true;
        }

        styles.Save();
        workbookPart.Workbook.Save();
    }

    private static void ApplyLegacyRows(
        WorksheetPart worksheetPart,
        WorkbookSheet sheet,
        string sheetName,
        WorkbookFormulaEvaluator evaluator,
        WorkbookStyleRegistry styles)
    {
        uint rowIndex = 1;
        foreach (WorkbookRow row in sheet.Rows ?? new List<WorkbookRow>())
        {
            int columnIndex = 0;
            foreach (string value in row.Cells ?? new List<string>())
            {
                string reference = OfficeDocumentWriter.ColumnName(columnIndex++) + rowIndex;
                SetCell(worksheetPart, BuildLiteralCell(reference, value, "auto", null, styles));
            }
            rowIndex++;
        }
    }

    private static void ApplyStructuredCells(
        WorksheetPart worksheetPart,
        WorkbookSheet sheet,
        string sheetName,
        WorkbookFormulaEvaluator evaluator,
        WorkbookStyleRegistry styles)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (WorkbookCell requested in sheet.StructuredCells ?? new List<WorkbookCell>())
        {
            string reference = WorkbookFormulaEvaluator.NormalizeCellReference(requested.Reference);
            if (!seen.Add(reference))
                throw new InvalidDataException("Workbook sheet '" + sheetName + "' specifies cell " + reference + " more than once.");

            X.Cell cell;
            if (!string.IsNullOrWhiteSpace(requested.Formula))
            {
                string formula = WorkbookFormulaEvaluator.NormalizeFormula(requested.Formula!);
                cell = new X.Cell
                {
                    CellReference = reference,
                    CellFormula = new X.CellFormula(formula)
                };
                if (evaluator.TryGetCachedValue(sheetName, reference, out double cached))
                {
                    cell.DataType = X.CellValues.Number;
                    cell.CellValue = new X.CellValue(cached.ToString("R", CultureInfo.InvariantCulture));
                }
            }
            else
            {
                cell = BuildLiteralCell(reference, requested.Value, requested.DataType, requested.Style, styles);
            }

            if (requested.Style != null)
                cell.StyleIndex = styles.Resolve(requested.Style, requested.DataType);
            SetCell(worksheetPart, cell);
        }
    }

    private static X.Cell BuildLiteralCell(
        string reference,
        string? value,
        string? dataType,
        WorkbookCellStyle? style,
        WorkbookStyleRegistry styles)
    {
        value ??= string.Empty;
        string kind = (dataType ?? "auto").Trim().ToLowerInvariant();
        if (kind == "number" || (kind == "auto" && double.TryParse(value,
                NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out _)))
        {
            if (!double.TryParse(value, NumberStyles.Float | NumberStyles.AllowThousands,
                    CultureInfo.InvariantCulture, out double number))
                throw new InvalidDataException("Cell " + reference + " declares number but value is not invariant numeric text.");
            return new X.Cell
            {
                CellReference = reference,
                DataType = X.CellValues.Number,
                CellValue = new X.CellValue(number.ToString("R", CultureInfo.InvariantCulture)),
                StyleIndex = style == null ? null : styles.Resolve(style, kind)
            };
        }

        if (kind == "boolean" || (kind == "auto" && bool.TryParse(value, out _)))
        {
            if (!bool.TryParse(value, out bool boolean))
                throw new InvalidDataException("Cell " + reference + " declares boolean but value is not true/false.");
            return new X.Cell
            {
                CellReference = reference,
                DataType = X.CellValues.Boolean,
                CellValue = new X.CellValue(boolean ? "1" : "0"),
                StyleIndex = style == null ? null : styles.Resolve(style, kind)
            };
        }

        if (kind == "date")
        {
            if (!DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out DateTime date))
                throw new InvalidDataException("Cell " + reference + " declares date but value is not an invariant date.");
            WorkbookCellStyle effective = style ?? new WorkbookCellStyle { NumberFormat = "yyyy-mm-dd" };
            return new X.Cell
            {
                CellReference = reference,
                DataType = X.CellValues.Number,
                CellValue = new X.CellValue(date.ToOADate().ToString("R", CultureInfo.InvariantCulture)),
                StyleIndex = styles.Resolve(effective, kind)
            };
        }

        if (kind is not ("auto" or "string"))
            throw new InvalidDataException("Unsupported workbook cell dataType '" + dataType + "'.");
        return new X.Cell
        {
            CellReference = reference,
            DataType = X.CellValues.InlineString,
            InlineString = new X.InlineString(new X.Text(value) { Space = SpaceProcessingModeValues.Preserve }),
            StyleIndex = style == null ? null : styles.Resolve(style, kind)
        };
    }

    private static void SetCell(WorksheetPart worksheetPart, X.Cell newCell)
    {
        string reference = newCell.CellReference?.Value
            ?? throw new InvalidDataException("Workbook cell has no reference.");
        (int column, uint rowIndex) = ParseReference(reference);
        X.SheetData sheetData = worksheetPart.Worksheet.GetFirstChild<X.SheetData>()!;
        X.Row? row = sheetData.Elements<X.Row>().FirstOrDefault(item => item.RowIndex?.Value == rowIndex);
        if (row == null)
        {
            row = new X.Row { RowIndex = rowIndex };
            X.Row? nextRow = sheetData.Elements<X.Row>().FirstOrDefault(item => (item.RowIndex?.Value ?? uint.MaxValue) > rowIndex);
            if (nextRow == null) sheetData.Append(row); else sheetData.InsertBefore(row, nextRow);
        }

        X.Cell? existing = row.Elements<X.Cell>().FirstOrDefault(cell =>
            string.Equals(cell.CellReference?.Value, reference, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            existing.InsertAfterSelf(newCell);
            existing.Remove();
            return;
        }

        X.Cell? nextCell = row.Elements<X.Cell>().FirstOrDefault(cell =>
        {
            if (cell.CellReference?.Value == null) return false;
            return ParseReference(cell.CellReference.Value).Column > column;
        });
        if (nextCell == null) row.Append(newCell); else row.InsertBefore(newCell, nextCell);
    }

    private static void ApplyCharts(WorksheetPart worksheetPart, WorkbookSheet sheet, string sheetName)
    {
        if (sheet.Charts == null || sheet.Charts.Count == 0) return;
        DrawingsPart? drawingsPart = worksheetPart.GetPartsOfType<DrawingsPart>().FirstOrDefault();
        if (drawingsPart == null)
        {
            drawingsPart = worksheetPart.AddNewPart<DrawingsPart>();
            drawingsPart.WorksheetDrawing = new Xdr.WorksheetDrawing();
            worksheetPart.Worksheet.Append(new X.Drawing { Id = worksheetPart.GetIdOfPart(drawingsPart) });
        }
        drawingsPart.WorksheetDrawing ??= new Xdr.WorksheetDrawing();

        uint drawingId = drawingsPart.WorksheetDrawing.Descendants<Xdr.NonVisualDrawingProperties>()
            .Select(item => item.Id?.Value ?? 0U).DefaultIfEmpty(1U).Max() + 1U;
        uint chartIndex = 0;
        foreach (WorkbookChart definition in sheet.Charts)
        {
            string category = NormalizeChartRange(definition.CategoryRange, sheetName);
            if (definition.Series == null || definition.Series.Count == 0)
                throw new InvalidDataException("Chart '" + definition.Title + "' requires at least one series.");
            List<(string Name, string Range)> series = definition.Series.Select(item =>
                (string.IsNullOrWhiteSpace(item.Name) ? "Series " + (definition.Series.IndexOf(item) + 1) : item.Name.Trim(),
                 NormalizeChartRange(item.ValueRange, sheetName))).ToList();

            ChartPart chartPart = drawingsPart.AddNewPart<ChartPart>();
            BuildChartPart(chartPart, definition, category, series, chartIndex++);
            AppendChartAnchor(drawingsPart, chartPart, definition, drawingId++);
        }
        drawingsPart.WorksheetDrawing.Save();
    }

    private static void BuildChartPart(
        ChartPart chartPart,
        WorkbookChart definition,
        string categoryRange,
        IReadOnlyList<(string Name, string Range)> series,
        uint chartIndex)
    {
        var chartSpace = new C.ChartSpace();
        chartSpace.Append(new C.EditingLanguage { Val = "en-US" });
        C.Chart chart = chartSpace.AppendChild(new C.Chart());
        if (!string.IsNullOrWhiteSpace(definition.Title)) chart.Append(BuildChartTitle(definition.Title.Trim()));
        C.PlotArea plotArea = chart.AppendChild(new C.PlotArea());
        plotArea.Append(new C.Layout());
        string type = (definition.Type ?? "column").Trim().ToLowerInvariant();
        uint categoryAxisId = 48_650_112U + chartIndex * 10U;
        uint valueAxisId = categoryAxisId + 1U;

        if (type == "pie")
        {
            var pie = plotArea.AppendChild(new C.PieChart(new C.VaryColors { Val = true }));
            for (uint i = 0; i < series.Count; i++)
            {
                var s = pie.AppendChild(new C.PieChartSeries(
                    new C.Index { Val = i },
                    new C.Order { Val = i },
                    new C.SeriesText(new C.NumericValue(series[(int)i].Name))));
                AddSeriesReferences(s, categoryRange, series[(int)i].Range);
            }
        }
        else if (type == "line")
        {
            var line = plotArea.AppendChild(new C.LineChart(
                new C.Grouping { Val = C.GroupingValues.Standard },
                new C.VaryColors { Val = false }));
            for (uint i = 0; i < series.Count; i++)
            {
                var s = line.AppendChild(new C.LineChartSeries(
                    new C.Index { Val = i },
                    new C.Order { Val = i },
                    new C.SeriesText(new C.NumericValue(series[(int)i].Name))));
                AddSeriesReferences(s, categoryRange, series[(int)i].Range);
            }
            line.Append(new C.AxisId { Val = categoryAxisId }, new C.AxisId { Val = valueAxisId });
            AppendAxes(plotArea, categoryAxisId, valueAxisId, C.TickLabelPositionValues.NextTo);
        }
        else if (type is "column" or "bar")
        {
            var bar = plotArea.AppendChild(new C.BarChart(
                new C.BarDirection { Val = type == "bar" ? C.BarDirectionValues.Bar : C.BarDirectionValues.Column },
                new C.BarGrouping { Val = C.BarGroupingValues.Clustered },
                new C.VaryColors { Val = false }));
            for (uint i = 0; i < series.Count; i++)
            {
                var s = bar.AppendChild(new C.BarChartSeries(
                    new C.Index { Val = i },
                    new C.Order { Val = i },
                    new C.SeriesText(new C.NumericValue(series[(int)i].Name)),
                    new C.InvertIfNegative { Val = false }));
                AddSeriesReferences(s, categoryRange, series[(int)i].Range);
            }
            bar.Append(new C.AxisId { Val = categoryAxisId }, new C.AxisId { Val = valueAxisId });
            AppendAxes(
                plotArea,
                categoryAxisId,
                valueAxisId,
                categoryTickLabelPosition: type == "column"
                    ? C.TickLabelPositionValues.Low
                    : C.TickLabelPositionValues.NextTo);
        }
        else
        {
            throw new InvalidDataException("Unsupported chart type '" + definition.Type + "'. Use column, bar, line or pie.");
        }

        chart.Append(new C.Legend(new C.LegendPosition { Val = C.LegendPositionValues.Right }, new C.Layout()));
        chart.Append(new C.PlotVisibleOnly { Val = true });
        chartPart.ChartSpace = chartSpace;
        chartPart.ChartSpace.Save();
    }

    private static void AddSeriesReferences(OpenXmlCompositeElement series, string categoryRange, string valueRange)
    {
        series.Append(new C.CategoryAxisData(
            new C.StringReference(new C.Formula(categoryRange))));
        series.Append(new C.Values(
            new C.NumberReference(new C.Formula(valueRange))));
    }

    private static C.Title BuildChartTitle(string title) => new(
        new C.ChartText(
            new C.RichText(
                new A.BodyProperties(),
                new A.ListStyle(),
                new A.Paragraph(
                    new A.Run(
                        new A.RunProperties { Language = "en-US" },
                        new A.Text(title)),
                    new A.EndParagraphRunProperties { Language = "en-US" }))),
        new C.Layout(),
        new C.Overlay { Val = false });

    private static void AppendAxes(
        C.PlotArea plotArea,
        uint categoryAxisId,
        uint valueAxisId,
        C.TickLabelPositionValues categoryTickLabelPosition)
    {
        plotArea.Append(new C.CategoryAxis(
            new C.AxisId { Val = categoryAxisId },
            new C.Scaling(new C.Orientation { Val = C.OrientationValues.MinMax }),
            new C.Delete { Val = false },
            new C.AxisPosition { Val = C.AxisPositionValues.Bottom },
            new C.TickLabelPosition { Val = categoryTickLabelPosition },
            new C.CrossingAxis { Val = valueAxisId },
            new C.Crosses { Val = C.CrossesValues.AutoZero },
            new C.AutoLabeled { Val = true },
            new C.LabelAlignment { Val = C.LabelAlignmentValues.Center },
            new C.LabelOffset { Val = (ushort)100 }));

        plotArea.Append(new C.ValueAxis(
            new C.AxisId { Val = valueAxisId },
            new C.Scaling(new C.Orientation { Val = C.OrientationValues.MinMax }),
            new C.Delete { Val = false },
            new C.AxisPosition { Val = C.AxisPositionValues.Left },
            new C.MajorGridlines(),
            new C.NumberingFormat { FormatCode = "General", SourceLinked = true },
            new C.TickLabelPosition { Val = C.TickLabelPositionValues.NextTo },
            new C.CrossingAxis { Val = categoryAxisId },
            new C.Crosses { Val = C.CrossesValues.AutoZero },
            new C.CrossBetween { Val = C.CrossBetweenValues.Between }));
    }

    private static void AppendChartAnchor(
        DrawingsPart drawingsPart,
        ChartPart chartPart,
        WorkbookChart definition,
        uint drawingId)
    {
        (int fromColumn, uint fromRow) = ParseReference(
            WorkbookFormulaEvaluator.NormalizeCellReference(string.IsNullOrWhiteSpace(definition.FromCell) ? "H2" : definition.FromCell));
        (int toColumn, uint toRow) = ParseReference(
            WorkbookFormulaEvaluator.NormalizeCellReference(string.IsNullOrWhiteSpace(definition.ToCell) ? "P18" : definition.ToCell));
        if (toColumn <= fromColumn || toRow <= fromRow)
            throw new InvalidDataException("Chart ToCell must be below and to the right of FromCell.");

        var anchor = new Xdr.TwoCellAnchor(
            new Xdr.FromMarker(
                new Xdr.ColumnId(fromColumn.ToString(CultureInfo.InvariantCulture)),
                new Xdr.ColumnOffset("0"),
                new Xdr.RowId(((long)fromRow - 1).ToString(CultureInfo.InvariantCulture)),
                new Xdr.RowOffset("0")),
            new Xdr.ToMarker(
                new Xdr.ColumnId(toColumn.ToString(CultureInfo.InvariantCulture)),
                new Xdr.ColumnOffset("0"),
                new Xdr.RowId(((long)toRow - 1).ToString(CultureInfo.InvariantCulture)),
                new Xdr.RowOffset("0")));

        var frame = new Xdr.GraphicFrame { Macro = string.Empty };
        frame.Append(new Xdr.NonVisualGraphicFrameProperties(
            new Xdr.NonVisualDrawingProperties { Id = drawingId, Name = "PaRuX Chart " + drawingId },
            new Xdr.NonVisualGraphicFrameDrawingProperties()));
        frame.Append(new Xdr.Transform(
            new A.Offset { X = 0L, Y = 0L },
            new A.Extents { Cx = 0L, Cy = 0L }));
        frame.Append(new A.Graphic(
            new A.GraphicData(
                new C.ChartReference { Id = drawingsPart.GetIdOfPart(chartPart) })
            { Uri = "http://schemas.openxmlformats.org/drawingml/2006/chart" }));
        anchor.Append(frame, new Xdr.ClientData());
        drawingsPart.WorksheetDrawing!.Append(anchor);
    }

    private static string NormalizeChartRange(string range, string currentSheet)
    {
        string input = (range ?? string.Empty).Trim();
        if (!WorkbookFormulaEvaluator.TryParseRangeToken(input, currentSheet,
                out string sheet, out string from, out string? to, out int consumed)
            || consumed != input.Length || to == null)
            throw new InvalidDataException("Chart ranges must be rectangular A1 ranges such as A2:A8 or 'Sales'!B2:B8.");
        string escaped = sheet.Replace("'", "''", StringComparison.Ordinal);
        return "'" + escaped + "'!" + Absolute(from) + ":" + Absolute(to);
    }

    private static string Absolute(string reference)
    {
        (int column, uint row) = ParseReference(reference);
        return "$" + OfficeDocumentWriter.ColumnName(column) + "$" + row.ToString(CultureInfo.InvariantCulture);
    }

    private static (int Column, uint Row) ParseReference(string reference)
    {
        string normalized = WorkbookFormulaEvaluator.NormalizeCellReference(reference);
        int split = 0;
        while (split < normalized.Length && char.IsLetter(normalized[split])) split++;
        string letters = normalized[..split];
        uint row = uint.Parse(normalized[split..], CultureInfo.InvariantCulture);
        int column = 0;
        foreach (char c in letters)
            column = checked(column * 26 + (c - 'A' + 1));
        return (column - 1, row);
    }

    private static string UniqueSheetName(string requested, HashSet<string> occupied)
    {
        if (!occupied.Contains(requested)) return requested;
        for (int suffix = 2; suffix <= 999; suffix++)
        {
            string marker = " (" + suffix + ")";
            string prefix = requested.Length + marker.Length <= 31
                ? requested
                : requested[..Math.Max(1, 31 - marker.Length)];
            string candidate = prefix + marker;
            if (!occupied.Contains(candidate)) return candidate;
        }
        throw new InvalidDataException("Could not allocate a unique Excel sheet name for '" + requested + "'.");
    }

    private static StructuredWorkbookResult Validate(
        byte[] bytes,
        WorkbookContent content,
        WorkbookContent? evaluationBase = null)
    {
        var warnings = new List<string>();
        using (var stream = new MemoryStream(bytes, writable: false))
        using (SpreadsheetDocument document = SpreadsheetDocument.Open(stream, false))
        {
            var validator = new OpenXmlValidator();
            foreach (ValidationErrorInfo error in validator.Validate(document).Take(MaximumValidationWarnings))
                warnings.Add((error.Path?.XPath ?? "package") + ": " + error.Description);
        }

        var evaluator = new WorkbookFormulaEvaluator(content, evaluationBase);
        IReadOnlyList<WorkbookFormulaResult> formulas = evaluator.EvaluateAll();
        string status = formulas.Count == 0
            ? "no-formulas"
            : formulas.Any(result => !result.Evaluated)
                ? "contains-uncomputed-formulas-recalc-on-open"
                : "cached-local-and-recalc-on-open";
        return new StructuredWorkbookResult(bytes, status, warnings.Count == 0, warnings);
    }

    private sealed class WorkbookStyleRegistry
    {
        private readonly WorkbookStylesPart _part;
        private readonly X.Stylesheet _stylesheet;
        private readonly Dictionary<string, uint> _cache = new(StringComparer.Ordinal);
        private uint _nextNumberFormatId;

        internal WorkbookStyleRegistry(WorkbookPart workbookPart)
        {
            _part = workbookPart.WorkbookStylesPart ?? workbookPart.AddNewPart<WorkbookStylesPart>();
            if (_part.Stylesheet is X.Stylesheet existingStylesheet)
                _stylesheet = existingStylesheet;
            else
            {
                _stylesheet = CreateBaseStylesheet();
                _part.Stylesheet = _stylesheet;
            }
            EnsureCollections(_stylesheet);
            _nextNumberFormatId = Math.Max(164U,
                _stylesheet.NumberingFormats?.Elements<X.NumberingFormat>()
                    .Select(item => item.NumberFormatId?.Value ?? 163U).DefaultIfEmpty(163U).Max() + 1U ?? 164U);
        }

        internal uint Resolve(WorkbookCellStyle style, string? dataType)
        {
            string fill = NormalizeColor(style.FillColor);
            string font = NormalizeColor(style.FontColor);
            string numberFormat = (style.NumberFormat ?? string.Empty).Trim();
            string alignment = (style.HorizontalAlignment ?? string.Empty).Trim().ToLowerInvariant();
            if (alignment.Length > 0 && alignment is not ("left" or "center" or "right"))
                throw new InvalidDataException("Workbook alignment must be left, center or right.");
            string key = string.Join('|', style.Bold, fill, font, numberFormat, alignment);
            if (_cache.TryGetValue(key, out uint cached)) return cached;

            uint fontId = AddFont(style.Bold, font);
            uint fillId = AddFill(fill);
            uint numberFormatId = 0;
            if (numberFormat.Length > 0)
            {
                X.NumberingFormat? existing = _stylesheet.NumberingFormats!.Elements<X.NumberingFormat>()
                    .FirstOrDefault(item => string.Equals(item.FormatCode?.Value, numberFormat, StringComparison.Ordinal));
                if (existing?.NumberFormatId?.Value is uint existingId) numberFormatId = existingId;
                else
                {
                    numberFormatId = _nextNumberFormatId++;
                    _stylesheet.NumberingFormats!.Append(new X.NumberingFormat
                    {
                        NumberFormatId = numberFormatId,
                        FormatCode = numberFormat
                    });
                }
            }

            var format = new X.CellFormat
            {
                FontId = fontId,
                FillId = fillId,
                BorderId = 0,
                FormatId = 0,
                NumberFormatId = numberFormatId,
                ApplyFont = fontId != 0,
                ApplyFill = fillId > 1,
                ApplyNumberFormat = numberFormatId != 0
            };
            if (alignment.Length > 0)
            {
                format.Alignment = new X.Alignment
                {
                    Horizontal = alignment switch
                    {
                        "left" => X.HorizontalAlignmentValues.Left,
                        "center" => X.HorizontalAlignmentValues.Center,
                        "right" => X.HorizontalAlignmentValues.Right,
                        _ => X.HorizontalAlignmentValues.General
                    }
                };
                format.ApplyAlignment = true;
            }
            _stylesheet.CellFormats!.Append(format);
            uint styleIndex = (uint)_stylesheet.CellFormats.ChildElements.Count - 1U;
            _cache[key] = styleIndex;
            return styleIndex;
        }

        internal void Save()
        {
            _stylesheet.Fonts!.Count = (uint)_stylesheet.Fonts.ChildElements.Count;
            _stylesheet.Fills!.Count = (uint)_stylesheet.Fills.ChildElements.Count;
            _stylesheet.Borders!.Count = (uint)_stylesheet.Borders.ChildElements.Count;
            _stylesheet.CellStyleFormats!.Count = (uint)_stylesheet.CellStyleFormats.ChildElements.Count;
            _stylesheet.CellFormats!.Count = (uint)_stylesheet.CellFormats.ChildElements.Count;
            if (_stylesheet.NumberingFormats != null)
                _stylesheet.NumberingFormats.Count = (uint)_stylesheet.NumberingFormats.ChildElements.Count;
            _part.Stylesheet.Save();
        }

        private uint AddFont(bool bold, string color)
        {
            if (!bold && color.Length == 0) return 0;
            var font = new X.Font();
            if (bold) font.Append(new X.Bold());
            if (color.Length > 0) font.Append(new X.Color { Rgb = color });
            _stylesheet.Fonts!.Append(font);
            return (uint)_stylesheet.Fonts.ChildElements.Count - 1U;
        }

        private uint AddFill(string color)
        {
            if (color.Length == 0) return 0;
            _stylesheet.Fills!.Append(new X.Fill(
                new X.PatternFill(
                    new X.ForegroundColor { Rgb = color },
                    new X.BackgroundColor { Indexed = 64U })
                { PatternType = X.PatternValues.Solid }));
            return (uint)_stylesheet.Fills.ChildElements.Count - 1U;
        }

        private static string NormalizeColor(string? value)
        {
            string color = (value ?? string.Empty).Trim().TrimStart('#').ToUpperInvariant();
            if (color.Length == 0) return string.Empty;
            if (color.Length == 6) color = "FF" + color;
            if (color.Length != 8 || color.Any(c => !Uri.IsHexDigit(c)))
                throw new InvalidDataException("Workbook colors must be RGB or ARGB hex values.");
            return color;
        }

        private static X.Stylesheet CreateBaseStylesheet() => new(
            new X.Fonts(new X.Font()) { Count = 1U },
            new X.Fills(
                new X.Fill(new X.PatternFill { PatternType = X.PatternValues.None }),
                new X.Fill(new X.PatternFill { PatternType = X.PatternValues.Gray125 })) { Count = 2U },
            new X.Borders(new X.Border()) { Count = 1U },
            new X.CellStyleFormats(new X.CellFormat()) { Count = 1U },
            new X.CellFormats(new X.CellFormat()) { Count = 1U },
            new X.CellStyles(new X.CellStyle { Name = "Normal", FormatId = 0U, BuiltinId = 0U }) { Count = 1U },
            new X.DifferentialFormats { Count = 0U },
            new X.TableStyles { Count = 0U, DefaultTableStyle = "TableStyleMedium2", DefaultPivotStyle = "PivotStyleLight16" });

        private static void EnsureCollections(X.Stylesheet stylesheet)
        {
            stylesheet.Fonts ??= new X.Fonts(new X.Font()) { Count = 1U };
            stylesheet.Fills ??= new X.Fills(
                new X.Fill(new X.PatternFill { PatternType = X.PatternValues.None }),
                new X.Fill(new X.PatternFill { PatternType = X.PatternValues.Gray125 })) { Count = 2U };
            stylesheet.Borders ??= new X.Borders(new X.Border()) { Count = 1U };
            stylesheet.CellStyleFormats ??= new X.CellStyleFormats(new X.CellFormat()) { Count = 1U };
            stylesheet.CellFormats ??= new X.CellFormats(new X.CellFormat()) { Count = 1U };
            stylesheet.NumberingFormats ??= new X.NumberingFormats();
        }
    }
}
