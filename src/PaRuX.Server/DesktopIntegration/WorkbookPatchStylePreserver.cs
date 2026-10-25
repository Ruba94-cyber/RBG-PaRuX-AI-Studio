using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using PaRuX.Contracts;
using X = DocumentFormat.OpenXml.Spreadsheet;

namespace PaRuX.Server.DesktopIntegration;

/// <summary>
/// A workbook edit that changes a value/formula without specifying a new style means "keep the
/// existing presentation". StructuredWorkbookWriter replaces cell nodes, so this pass copies the
/// source cell's style index back onto exactly those requested edits and revalidates the final
/// package. It never broadens the edit scope or copies executable/external content.
/// </summary>
internal static class WorkbookPatchStylePreserver
{
    private const int MaximumValidationWarnings = 50;

    internal static StructuredWorkbookResult Apply(
        byte[] sourceBytes,
        StructuredWorkbookResult patched,
        WorkbookContent patch)
    {
        ArgumentNullException.ThrowIfNull(sourceBytes);
        ArgumentNullException.ThrowIfNull(patch);

        using var sourceStream = new MemoryStream(sourceBytes, writable: false);
        using SpreadsheetDocument source = SpreadsheetDocument.Open(sourceStream, false);
        using var outputStream = new MemoryStream();
        outputStream.Write(patched.Bytes, 0, patched.Bytes.Length);
        outputStream.Position = 0;

        using (SpreadsheetDocument output = SpreadsheetDocument.Open(outputStream, true))
        {
            WorkbookPart? sourceWorkbook = source.WorkbookPart;
            WorkbookPart? outputWorkbook = output.WorkbookPart;
            if (sourceWorkbook == null || outputWorkbook == null)
                throw new InvalidDataException("Workbook edit cannot preserve styles because the workbook part is missing.");

            uint sheetNumber = 1;
            foreach (WorkbookSheet requested in patch.Sheets ?? new List<WorkbookSheet>())
            {
                string requestedName = OfficeDocumentWriter.SafeSheetName(requested.Name, sheetNumber++);
                WorksheetPart? sourceSheet = FindSheet(sourceWorkbook, requestedName);
                WorksheetPart? outputSheet = FindSheet(outputWorkbook, requestedName);
                if (sourceSheet == null || outputSheet == null) continue;

                uint rowIndex = 1;
                foreach (WorkbookRow row in requested.Rows ?? new List<WorkbookRow>())
                {
                    int columnIndex = 0;
                    foreach (string _ in row.Cells ?? new List<string>())
                        PreserveCellStyle(sourceSheet, outputSheet, OfficeDocumentWriter.ColumnName(columnIndex++) + rowIndex);
                    rowIndex++;
                }

                foreach (WorkbookCell cell in requested.StructuredCells ?? new List<WorkbookCell>())
                {
                    if (cell.Style != null) continue;
                    string reference = WorkbookFormulaEvaluator.NormalizeCellReference(cell.Reference);
                    PreserveCellStyle(sourceSheet, outputSheet, reference);
                }

                outputSheet.Worksheet.Save();
            }
        }

        byte[] finalBytes = outputStream.ToArray();
        var warnings = new List<string>();
        using (var validateStream = new MemoryStream(finalBytes, writable: false))
        using (SpreadsheetDocument document = SpreadsheetDocument.Open(validateStream, false))
        {
            var validator = new OpenXmlValidator();
            foreach (ValidationErrorInfo error in validator.Validate(document).Take(MaximumValidationWarnings))
                warnings.Add((error.Path?.XPath ?? "package") + ": " + error.Description);
        }

        return patched with
        {
            Bytes = finalBytes,
            Validated = warnings.Count == 0,
            ValidationWarnings = warnings
        };
    }

    private static WorksheetPart? FindSheet(WorkbookPart workbook, string name)
    {
        X.Sheet? sheet = workbook.Workbook.Sheets?.Elements<X.Sheet>().FirstOrDefault(item =>
            string.Equals(item.Name?.Value, name, StringComparison.OrdinalIgnoreCase));
        if (sheet?.Id?.Value is not string relationshipId) return null;
        return workbook.GetPartById(relationshipId) as WorksheetPart;
    }

    private static void PreserveCellStyle(WorksheetPart source, WorksheetPart output, string reference)
    {
        X.Cell? sourceCell = source.Worksheet.Descendants<X.Cell>().FirstOrDefault(cell =>
            string.Equals(cell.CellReference?.Value, reference, StringComparison.OrdinalIgnoreCase));
        if (sourceCell?.StyleIndex?.Value is not uint styleIndex) return;

        X.Cell? outputCell = output.Worksheet.Descendants<X.Cell>().FirstOrDefault(cell =>
            string.Equals(cell.CellReference?.Value, reference, StringComparison.OrdinalIgnoreCase));
        if (outputCell == null) return;
        outputCell.StyleIndex = styleIndex;
    }
}