using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using PaRuX.Contracts;
using W = DocumentFormat.OpenXml.Wordprocessing;
using X = DocumentFormat.OpenXml.Spreadsheet;

namespace PaRuX.Server.DesktopIntegration;

/// <summary>
/// Builds real .docx and .xlsx files with the OpenXML SDK already used elsewhere in this codebase,
/// so a document PaRuX produces opens in Word and Excel rather than being a text file with the
/// wrong extension.
///
/// Both namespaces are aliased rather than imported: OpenXML defines <c>BottomBorder</c>,
/// <c>Text</c> and a dozen more names in <em>both</em> the Wordprocessing and Spreadsheet
/// namespaces, so an unqualified import compiles only until the second format is added to the file.
///
/// The vocabulary is deliberately small - headings, paragraphs, bullets, tables; sheets of rows -
/// rather than a document model. A model asked for "a report" reliably produces those; anything
/// richer invites it to emit layout it cannot see the result of.
/// </summary>
internal static class OfficeDocumentWriter
{
    internal static byte[] BuildWord(WordDocumentContent content)
    {
        using var stream = new MemoryStream();
        using (WordprocessingDocument document =
               WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document))
        {
            MainDocumentPart mainPart = document.AddMainDocumentPart();
            mainPart.Document = new W.Document();
            // Paragraphs reference Heading1..9; without a styles part Word has no definition for
            // them and every heading renders as plain body text (found opening a generated file).
            StyleDefinitionsPart stylesPart = mainPart.AddNewPart<StyleDefinitionsPart>();
            stylesPart.Styles = BuildStyles();
            stylesPart.Styles.Save();
            var body = new W.Body();

            List<WordBlock> blocks = content.Blocks ?? new List<WordBlock>();
            if (!string.IsNullOrWhiteSpace(content.Title))
            {
                body.Append(Heading(content.Title, level: 1));
                // Models commonly repeat the title as their first heading; one title is enough.
                if (blocks.Count > 0
                    && string.Equals((blocks[0].Kind ?? string.Empty).Trim(), "heading", StringComparison.OrdinalIgnoreCase)
                    && string.Equals((blocks[0].Text ?? string.Empty).Trim(), content.Title.Trim(), StringComparison.OrdinalIgnoreCase))
                    blocks = blocks.Skip(1).ToList();
            }

            foreach (WordBlock block in blocks)
            {
                switch ((block.Kind ?? "paragraph").Trim().ToLowerInvariant())
                {
                    case "heading":
                        // Clamped rather than trusted: Word has styles Heading1..Heading9, and an
                        // out-of-range level silently produces an unstyled paragraph.
                        body.Append(Heading(block.Text ?? string.Empty,
                            Math.Clamp(block.Level <= 0 ? 2 : block.Level, 1, 9)));
                        break;

                    case "bullet":
                        body.Append(Bullet(block.Text ?? string.Empty));
                        break;

                    case "table":
                        if (block.Rows is { Count: > 0 }) body.Append(Table(block.Rows));
                        break;

                    default:
                        body.Append(Paragraph(block.Text ?? string.Empty));
                        break;
                }
            }

            // Word treats a body with no trailing section properties as damaged in some versions.
            body.Append(new W.SectionProperties(new W.PageSize { Width = 11906, Height = 16838 }));

            mainPart.Document.Append(body);
            mainPart.Document.Save();
        }
        return stream.ToArray();
    }

    private static W.Styles BuildStyles()
    {
        var styles = new W.Styles(
            new W.DocDefaults(
                new W.RunPropertiesDefault(new W.RunPropertiesBaseStyle(
                    new W.RunFonts { Ascii = "Calibri", HighAnsi = "Calibri", ComplexScript = "Calibri" },
                    new W.FontSize { Val = "22" })),
                new W.ParagraphPropertiesDefault(new W.ParagraphPropertiesBaseStyle(
                    new W.SpacingBetweenLines { After = "120", Line = "264", LineRule = W.LineSpacingRuleValues.Auto }))),
            new W.Style(new W.StyleName { Val = "Normal" }, new W.PrimaryStyle())
            { Type = W.StyleValues.Paragraph, StyleId = "Normal", Default = true });
        for (int level = 1; level <= 9; level++)
        {
            styles.Append(new W.Style(
                new W.StyleName { Val = "heading " + level },
                new W.BasedOn { Val = "Normal" },
                new W.NextParagraphStyle { Val = "Normal" },
                new W.PrimaryStyle(),
                new W.StyleParagraphProperties(
                    new W.KeepNext(),
                    new W.SpacingBetweenLines { Before = level == 1 ? "240" : "200", After = "80" },
                    new W.OutlineLevel { Val = level - 1 }),
                new W.StyleRunProperties(
                    new W.Bold(),
                    new W.FontSize { Val = Math.Max(22, 36 - (level - 1) * 4).ToString(System.Globalization.CultureInfo.InvariantCulture) }))
            { Type = W.StyleValues.Paragraph, StyleId = "Heading" + level });
        }
        return styles;
    }

    private static W.Paragraph Heading(string text, int level) =>
        new(new W.ParagraphProperties(new W.ParagraphStyleId { Val = "Heading" + level }),
            new W.Run(new W.Text(text) { Space = SpaceProcessingModeValues.Preserve }));

    private static W.Paragraph Paragraph(string text) =>
        new(new W.Run(new W.Text(text) { Space = SpaceProcessingModeValues.Preserve }));

    /// <summary>
    /// A bullet by indentation and a literal marker rather than a numbering definition. A real
    /// numbering part is the correct way to do this and is also the part most likely to produce a
    /// document Word refuses to open; the visible result is the same and it always opens.
    /// </summary>
    private static W.Paragraph Bullet(string text) =>
        new(new W.ParagraphProperties(new W.Indentation { Left = "720", Hanging = "360" }),
            new W.Run(new W.Text("• " + text) { Space = SpaceProcessingModeValues.Preserve }));

    private static W.Table Table(List<WordTableRow> rows)
    {
        var table = new W.Table(
            new W.TableProperties(
                new W.TableBorders(
                    new W.TopBorder { Val = W.BorderValues.Single, Size = 4 },
                    new W.BottomBorder { Val = W.BorderValues.Single, Size = 4 },
                    new W.LeftBorder { Val = W.BorderValues.Single, Size = 4 },
                    new W.RightBorder { Val = W.BorderValues.Single, Size = 4 },
                    new W.InsideHorizontalBorder { Val = W.BorderValues.Single, Size = 4 },
                    new W.InsideVerticalBorder { Val = W.BorderValues.Single, Size = 4 })));

        foreach (WordTableRow row in rows)
        {
            var tableRow = new W.TableRow();
            foreach (string cell in row.Cells ?? new List<string>())
            {
                tableRow.Append(new W.TableCell(
                    new W.Paragraph(new W.Run(new W.Text(cell ?? string.Empty)
                    {
                        Space = SpaceProcessingModeValues.Preserve
                    }))));
            }
            table.Append(tableRow);
        }
        return table;
    }

    internal static byte[] BuildWorkbook(WorkbookContent content)
    {
        using var stream = new MemoryStream();
        using (SpreadsheetDocument document =
               SpreadsheetDocument.Create(stream, SpreadsheetDocumentType.Workbook))
        {
            WorkbookPart workbookPart = document.AddWorkbookPart();
            workbookPart.Workbook = new X.Workbook();
            X.Sheets sheets = workbookPart.Workbook.AppendChild(new X.Sheets());

            uint sheetId = 1;
            List<WorkbookSheet> input = content.Sheets is { Count: > 0 }
                ? content.Sheets
                : new List<WorkbookSheet> { new() { Name = "Sheet1" } };

            foreach (WorkbookSheet sheet in input)
            {
                WorksheetPart worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
                var sheetData = new X.SheetData();

                uint rowIndex = 1;
                foreach (WorkbookRow row in sheet.Rows ?? new List<WorkbookRow>())
                {
                    var xlRow = new X.Row { RowIndex = rowIndex };
                    int columnIndex = 0;
                    foreach (string value in row.Cells ?? new List<string>())
                    {
                        xlRow.Append(BuildCell(ColumnName(columnIndex++) + rowIndex, value));
                    }
                    sheetData.Append(xlRow);
                    rowIndex++;
                }

                worksheetPart.Worksheet = new X.Worksheet(sheetData);
                worksheetPart.Worksheet.Save();

                sheets.Append(new X.Sheet
                {
                    Id = workbookPart.GetIdOfPart(worksheetPart),
                    SheetId = sheetId,
                    Name = SafeSheetName(sheet.Name, sheetId)
                });
                sheetId++;
            }

            workbookPart.Workbook.Save();
        }
        return stream.ToArray();
    }

    /// <summary>
    /// A value that parses as a number is written as a number, so it sums and sorts. Writing
    /// everything as text produces a spreadsheet that looks right and cannot be used as one, which
    /// is the most annoying way a generated workbook fails.
    /// </summary>
    private static X.Cell BuildCell(string reference, string? value)
    {
        value ??= string.Empty;
        if (double.TryParse(value, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double number))
        {
            return new X.Cell
            {
                CellReference = reference,
                DataType = X.CellValues.Number,
                CellValue = new X.CellValue(number.ToString(System.Globalization.CultureInfo.InvariantCulture))
            };
        }

        // Inline strings rather than the shared-string table: one fewer part to keep consistent,
        // and for a generated sheet the deduplication the shared table buys is not worth the risk
        // of an index that does not match.
        return new X.Cell
        {
            CellReference = reference,
            DataType = X.CellValues.InlineString,
            InlineString = new X.InlineString(new X.Text(value))
        };
    }

    /// <summary>A, B, ... Z, AA, AB - Excel's column naming, which is base-26 with no zero.</summary>
    internal static string ColumnName(int zeroBasedIndex)
    {
        if (zeroBasedIndex < 0) zeroBasedIndex = 0;
        string name = string.Empty;
        int index = zeroBasedIndex;
        while (true)
        {
            name = (char)('A' + index % 26) + name;
            index = index / 26 - 1;
            if (index < 0) break;
        }
        return name;
    }

    /// <summary>
    /// Excel rejects a workbook whose sheet name is empty, longer than 31 characters, or contains
    /// any of : \ / ? * [ ] - so a model-supplied name is cleaned rather than passed through and
    /// discovered at open time.
    /// </summary>
    internal static string SafeSheetName(string? name, uint fallbackNumber)
    {
        string cleaned = new((name ?? string.Empty)
            .Where(c => !char.IsControl(c) && c is not (':' or '\\' or '/' or '?' or '*' or '[' or ']'))
            .ToArray());
        cleaned = cleaned.Trim();
        if (cleaned.Length == 0) return "Sheet" + fallbackNumber;
        return cleaned.Length <= 31 ? cleaned : cleaned[..31];
    }
}
