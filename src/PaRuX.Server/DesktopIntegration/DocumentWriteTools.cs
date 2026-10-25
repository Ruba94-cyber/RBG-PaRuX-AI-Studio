using System.Text.Json;
using System.Text.Json.Nodes;
using PaRuX.Contracts;

namespace PaRuX.Server.DesktopIntegration;

/// <summary>
/// Native local document tools. Workbook creation/editing is structured rather than script-based:
/// formulas, types, styles and chart ranges become OpenXML semantics. Edit authority is deliberately
/// narrower than read authority: the source must already be an .xlsx in PaRuX's configured output
/// folder and the original is never overwritten.
/// </summary>
public sealed class DocumentWriteTools
{
    public const string WriteWordToolName = "parux_write_word_document";
    public const string WriteWorkbookToolName = "parux_write_excel_workbook";
    public const string EditWorkbookToolName = "parux_edit_excel_workbook";

    private readonly DocumentWriteService _documents;

    public DocumentWriteTools(DocumentWriteService documents) => _documents = documents;

    public static bool IsBuiltInTool(string? name) =>
        string.Equals(name, WriteWordToolName, StringComparison.Ordinal)
        || string.Equals(name, WriteWorkbookToolName, StringComparison.Ordinal)
        || string.Equals(name, EditWorkbookToolName, StringComparison.Ordinal);

    public JsonArray GetToolSchemas(IReadOnlyCollection<string>? allowedTools = null)
    {
        var schemas = new JsonArray();
        if (ContextEngineService.ToolNameAllowed(WriteWordToolName, allowedTools))
            schemas.Add(WordSchema());
        if (ContextEngineService.ToolNameAllowed(WriteWorkbookToolName, allowedTools))
            schemas.Add(WorkbookSchema(edit: false));
        if (ContextEngineService.ToolNameAllowed(EditWorkbookToolName, allowedTools))
            schemas.Add(WorkbookSchema(edit: true));
        return schemas;
    }

    public Task<string> CallAsync(string toolName, string argumentsJson, CancellationToken ct)
    {
        if (!IsBuiltInTool(toolName)) throw new InvalidOperationException("Unknown PaRuX document tool.");
        ct.ThrowIfCancellationRequested();

        JsonNode? root;
        try { root = JsonNode.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson); }
        catch (JsonException)
        {
            return Task.FromResult("Document tool failed: the arguments were not valid JSON.");
        }

        string fileName = root?["fileName"]?.GetValue<string>()?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(fileName))
            return Task.FromResult("Document tool failed: a fileName is required.");

        try
        {
            DocumentWriteResult result;
            if (toolName == WriteWordToolName)
            {
                result = _documents.WriteWord(fileName, ReadWordContent(root));
            }
            else if (toolName == EditWorkbookToolName)
            {
                string source = root?["sourceFileName"]?.GetValue<string>()?.Trim() ?? string.Empty;
                if (source.Length == 0)
                    return Task.FromResult("Document tool failed: sourceFileName is required for workbook editing.");
                result = _documents.EditWorkbook(source, fileName, ReadWorkbookContent(root));
            }
            else
            {
                result = _documents.WriteWorkbook(fileName, ReadWorkbookContent(root));
            }

            string workbookEvidence = result.CalculationStatus == "not-applicable"
                ? string.Empty
                : " Calculation status: " + result.CalculationStatus
                  + ". OpenXML validation: " + (result.OpenXmlValidated ? "passed" : "warnings")
                  + (result.ValidationWarnings.Count == 0
                      ? "."
                      : "; " + string.Join(" | ", result.ValidationWarnings.Take(3)) + ".");
            return Task.FromResult(
                "Saved " + result.FileName + " (" + result.SizeBytes + " bytes) to " + result.Path
                + (result.RenamedToAvoidOverwrite
                    ? ". A file with the requested name already existed, so this one was numbered rather than replacing it."
                    : ".")
                + (result.SourceWorkbookPreserved ? " The source workbook was preserved unchanged." : string.Empty)
                + workbookEvidence
                + " Tell the user this exact path and do not describe an uncomputed formula as already recalculated.");
        }
        catch (InvalidDataException ex) { return Task.FromResult("Document tool failed: " + ex.Message); }
        catch (FileNotFoundException ex) { return Task.FromResult("Document tool failed: " + ex.Message); }
        catch (IOException ex) { return Task.FromResult("Document tool failed: " + ex.Message); }
        catch (UnauthorizedAccessException ex)
        {
            return Task.FromResult("Document tool failed: PaRuX cannot write to that folder (" + ex.Message + ").");
        }
    }

    private static JsonObject WordSchema() => new()
    {
        ["type"] = "function",
        ["function"] = new JsonObject
        {
            ["name"] = WriteWordToolName,
            ["description"] =
                "Create a Word (.docx) file locally in PaRuX's configured documents folder. "
                + "An existing file is never overwritten and the full output path is returned.",
            ["parameters"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["fileName"] = new JsonObject { ["type"] = "string" },
                    ["title"] = new JsonObject { ["type"] = "string" },
                    ["blocks"] = new JsonObject
                    {
                        ["type"] = "array",
                        ["items"] = new JsonObject
                        {
                            ["type"] = "object",
                            ["properties"] = new JsonObject
                            {
                                ["kind"] = new JsonObject { ["type"] = "string", ["description"] = "paragraph | heading | bullet | table" },
                                ["text"] = new JsonObject { ["type"] = "string" },
                                ["level"] = new JsonObject { ["type"] = "integer" },
                                ["rows"] = new JsonObject
                                {
                                    ["type"] = "array",
                                    ["items"] = new JsonObject
                                    {
                                        ["type"] = "object",
                                        ["properties"] = new JsonObject
                                        {
                                            ["cells"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } }
                                        }
                                    }
                                }
                            },
                            ["required"] = new JsonArray("kind")
                        }
                    }
                },
                ["required"] = new JsonArray("fileName", "blocks")
            }
        }
    };

    private static JsonObject WorkbookSchema(bool edit)
    {
        JsonObject properties = WorkbookProperties();
        if (edit)
        {
            properties["sourceFileName"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "Existing .xlsx filename already in PaRuX's configured output folder. No folders/paths."
            };
        }
        return new JsonObject
        {
            ["type"] = "function",
            ["function"] = new JsonObject
            {
                ["name"] = edit ? EditWorkbookToolName : WriteWorkbookToolName,
                ["description"] = edit
                    ? "Edit selected cells/charts in an existing PaRuX-output XLSX and save a new copy. Unmentioned workbook content/styles are preserved; the source is never overwritten. Formula results are reported as locally cached or uncomputed/recalculate-on-open."
                    : "Create a native Excel XLSX with typed cells, formulas, formatting and charts. Use structuredCells for formula semantics; legacy rows remain available for simple tables. Supported formulas receive independently calculated cached results; unsupported formulas remain uncomputed and are recalculated by the spreadsheet app on open.",
                ["parameters"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = properties,
                    ["required"] = edit
                        ? new JsonArray("sourceFileName", "fileName", "sheets")
                        : new JsonArray("fileName", "sheets")
                }
            }
        };
    }

    private static JsonObject WorkbookProperties() => new()
    {
        ["fileName"] = new JsonObject { ["type"] = "string", ["description"] = "Output filename without folders." },
        ["recalculateOnOpen"] = new JsonObject { ["type"] = "boolean", ["description"] = "Default true. Ask Excel/LibreOffice to fully recalculate formulas when opened." },
        ["sheets"] = new JsonObject
        {
            ["type"] = "array",
            ["items"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["name"] = new JsonObject { ["type"] = "string" },
                    ["rows"] = LegacyRowsSchema(),
                    ["structuredCells"] = new JsonObject
                    {
                        ["type"] = "array",
                        ["items"] = new JsonObject
                        {
                            ["type"] = "object",
                            ["properties"] = new JsonObject
                            {
                                ["reference"] = new JsonObject { ["type"] = "string", ["description"] = "A1 reference such as B4." },
                                ["value"] = new JsonObject { ["description"] = "Literal value; number/boolean/string accepted." },
                                ["formula"] = new JsonObject { ["type"] = "string", ["description"] = "Real Excel formula. Do not put formula text in legacy rows." },
                                ["dataType"] = new JsonObject { ["type"] = "string", ["description"] = "auto | string | number | boolean | date" },
                                ["style"] = StyleSchema()
                            },
                            ["required"] = new JsonArray("reference")
                        }
                    },
                    ["charts"] = new JsonObject
                    {
                        ["type"] = "array",
                        ["items"] = new JsonObject
                        {
                            ["type"] = "object",
                            ["properties"] = new JsonObject
                            {
                                ["type"] = new JsonObject { ["type"] = "string", ["description"] = "column | bar | line | pie" },
                                ["title"] = new JsonObject { ["type"] = "string" },
                                ["categoryRange"] = new JsonObject { ["type"] = "string", ["description"] = "A1 range, optionally sheet-qualified." },
                                ["series"] = new JsonObject
                                {
                                    ["type"] = "array",
                                    ["items"] = new JsonObject
                                    {
                                        ["type"] = "object",
                                        ["properties"] = new JsonObject
                                        {
                                            ["name"] = new JsonObject { ["type"] = "string" },
                                            ["valueRange"] = new JsonObject { ["type"] = "string" }
                                        },
                                        ["required"] = new JsonArray("valueRange")
                                    }
                                },
                                ["fromCell"] = new JsonObject { ["type"] = "string" },
                                ["toCell"] = new JsonObject { ["type"] = "string" }
                            },
                            ["required"] = new JsonArray("categoryRange", "series")
                        }
                    }
                }
            }
        }
    };

    private static JsonObject LegacyRowsSchema() => new()
    {
        ["type"] = "array",
        ["description"] = "Simple backward-compatible rows. Formula-looking strings remain literal text; use structuredCells.formula for formulas.",
        ["items"] = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["cells"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject() }
            }
        }
    };

    private static JsonObject StyleSchema() => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["bold"] = new JsonObject { ["type"] = "boolean" },
            ["fillColor"] = new JsonObject { ["type"] = "string", ["description"] = "RGB/ARGB hex." },
            ["fontColor"] = new JsonObject { ["type"] = "string", ["description"] = "RGB/ARGB hex." },
            ["numberFormat"] = new JsonObject { ["type"] = "string" },
            ["horizontalAlignment"] = new JsonObject { ["type"] = "string", ["description"] = "left | center | right" }
        }
    };

    private static WordDocumentContent ReadWordContent(JsonNode? root)
    {
        var content = new WordDocumentContent { Title = root?["title"]?.GetValue<string>() ?? string.Empty };
        if (root?["blocks"] is not JsonArray blocks) return content;
        foreach (JsonNode? node in blocks)
        {
            if (node is not JsonObject block) continue;
            var parsed = new WordBlock
            {
                Kind = Str(block, "kind", "paragraph"),
                Text = Str(block, "text", string.Empty),
                Level = Int(block, "level")
            };
            if (block["rows"] is JsonArray rows)
            {
                foreach (JsonNode? rowNode in rows)
                    if (rowNode is JsonObject row)
                        parsed.Rows.Add(new WordTableRow { Cells = Strings(row["cells"]) });
            }
            content.Blocks.Add(parsed);
        }
        return content;
    }

    private static WorkbookContent ReadWorkbookContent(JsonNode? root)
    {
        var content = new WorkbookContent { RecalculateOnOpen = Bool(root as JsonObject, "recalculateOnOpen", true) };
        if (root?["sheets"] is not JsonArray sheets) return content;

        foreach (JsonNode? node in sheets)
        {
            if (node is not JsonObject sheet) continue;
            var parsed = new WorkbookSheet { Name = Str(sheet, "name", string.Empty) };
            if (sheet["rows"] is JsonArray rows)
            {
                foreach (JsonNode? rowNode in rows)
                    if (rowNode is JsonObject row)
                        parsed.Rows.Add(new WorkbookRow { Cells = Strings(row["cells"]) });
            }
            if (sheet["structuredCells"] is JsonArray cells)
            {
                foreach (JsonNode? cellNode in cells)
                {
                    if (cellNode is not JsonObject cell) continue;
                    parsed.StructuredCells.Add(new WorkbookCell
                    {
                        Reference = Str(cell, "reference", string.Empty),
                        Value = ScalarText(cell["value"]),
                        Formula = OptionalStr(cell, "formula"),
                        DataType = Str(cell, "dataType", "auto"),
                        Style = ReadStyle(cell["style"] as JsonObject)
                    });
                }
            }
            if (sheet["charts"] is JsonArray charts)
            {
                foreach (JsonNode? chartNode in charts)
                {
                    if (chartNode is not JsonObject chart) continue;
                    var parsedChart = new WorkbookChart
                    {
                        Type = Str(chart, "type", "column"),
                        Title = Str(chart, "title", string.Empty),
                        CategoryRange = Str(chart, "categoryRange", string.Empty),
                        FromCell = Str(chart, "fromCell", "H2"),
                        ToCell = Str(chart, "toCell", "P18")
                    };
                    if (chart["series"] is JsonArray series)
                    {
                        foreach (JsonNode? seriesNode in series)
                            if (seriesNode is JsonObject item)
                                parsedChart.Series.Add(new WorkbookChartSeries
                                {
                                    Name = Str(item, "name", string.Empty),
                                    ValueRange = Str(item, "valueRange", string.Empty)
                                });
                    }
                    parsed.Charts.Add(parsedChart);
                }
            }
            content.Sheets.Add(parsed);
        }
        return content;
    }

    private static WorkbookCellStyle? ReadStyle(JsonObject? node)
    {
        if (node == null) return null;
        return new WorkbookCellStyle
        {
            Bold = Bool(node, "bold", false),
            FillColor = OptionalStr(node, "fillColor"),
            FontColor = OptionalStr(node, "fontColor"),
            NumberFormat = OptionalStr(node, "numberFormat"),
            HorizontalAlignment = OptionalStr(node, "horizontalAlignment")
        };
    }

    private static List<string> Strings(JsonNode? node)
    {
        var values = new List<string>();
        if (node is not JsonArray array) return values;
        foreach (JsonNode? item in array)
        {
            if (item == null) { values.Add(string.Empty); continue; }
            values.Add(ScalarText(item) ?? string.Empty);
        }
        return values;
    }

    private static string? ScalarText(JsonNode? item)
    {
        if (item == null) return null;
        if (item is JsonValue value)
        {
            if (value.TryGetValue(out string? text)) return text;
            if (value.TryGetValue(out double number)) return number.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (value.TryGetValue(out bool boolean)) return boolean ? "true" : "false";
            if (value.TryGetValue(out long integer)) return integer.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        return item.ToJsonString().Trim('"');
    }

    private static string Str(JsonObject node, string name, string fallback) =>
        node[name] is JsonValue value && value.TryGetValue(out string? text) && !string.IsNullOrWhiteSpace(text)
            ? text
            : fallback;

    private static string? OptionalStr(JsonObject node, string name) =>
        node[name] is JsonValue value && value.TryGetValue(out string? text) && !string.IsNullOrWhiteSpace(text)
            ? text
            : null;

    private static int Int(JsonObject node, string name) =>
        node[name] is JsonValue value && value.TryGetValue(out int number) ? number : 0;

    private static bool Bool(JsonObject? node, string name, bool fallback) =>
        node?[name] is JsonValue value && value.TryGetValue(out bool result) ? result : fallback;
}
