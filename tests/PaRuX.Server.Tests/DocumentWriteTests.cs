using PaRuX.Contracts;
using PaRuX.Server;
using PaRuX.Server.DesktopIntegration;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Generating a file is easy; generating one Word and Excel will actually open is not, and the
/// difference is invisible until somebody double-clicks it. So the documents written here are read
/// back through PaRuX's own extractor - the same code that reads the user's real documents - which
/// only succeeds on a structurally valid OpenXML package.
///
/// The other half is the safety model. An assistant that can create files is one mistake away from
/// being an assistant that destroyed one, so never-overwrite and name containment are pinned as
/// hard as the format.
/// </summary>
public sealed class DocumentWriteTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "parux-docwrite-" + Guid.NewGuid().ToString("N"));
    private readonly string _state;
    private readonly string _output;

    public DocumentWriteTests()
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

    private DocumentWriteService NewService()
    {
        var service = new DocumentWriteService(_state);
        service.SetOutputFolder(_output);
        return service;
    }

    // ---- the files really are Word and Excel documents ----

    [Fact]
    public void A_written_word_document_reads_back_through_the_normal_document_reader()
    {
        DocumentWriteResult result = NewService().WriteWord("Quarterly report", new WordDocumentContent
        {
            Title = "Quarterly report",
            Blocks = new List<WordBlock>
            {
                new() { Kind = "heading", Text = "Revenue", Level = 2 },
                new() { Kind = "paragraph", Text = "Revenue grew by a third." },
                new() { Kind = "bullet", Text = "Italy up 12%" },
                new()
                {
                    Kind = "table",
                    Rows = new List<WordTableRow>
                    {
                        new() { Cells = new List<string> { "Region", "Total" } },
                        new() { Cells = new List<string> { "Italy", "1200" } }
                    }
                }
            }
        });

        Assert.True(File.Exists(result.Path));
        Assert.EndsWith(".docx", result.Path, StringComparison.Ordinal);

        string text = DocumentTextExtractor.Extract(result.Path, CancellationToken.None);
        Assert.Contains("Quarterly report", text, StringComparison.Ordinal);
        Assert.Contains("Revenue grew by a third.", text, StringComparison.Ordinal);
        Assert.Contains("Italy up 12%", text, StringComparison.Ordinal);
        Assert.Contains("1200", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Word_headings_are_real_styles_and_a_repeated_title_is_written_once()
    {
        byte[] bytes = OfficeDocumentWriter.BuildWord(new WordDocumentContent
        {
            Title = "Meeting Notes",
            Blocks = new()
            {
                new WordBlock { Kind = "heading", Text = "meeting notes", Level = 1 },   // model repeated the title
                new WordBlock { Kind = "bullet", Text = "Budget approved" },
                new WordBlock { Kind = "heading", Text = "Next steps", Level = 2 }
            }
        });
        using var stream = new MemoryStream(bytes);
        using var document = DocumentFormat.OpenXml.Packaging.WordprocessingDocument.Open(stream, false);
        var styles = document.MainDocumentPart!.StyleDefinitionsPart!.Styles!
            .Elements<DocumentFormat.OpenXml.Wordprocessing.Style>().Select(s => s.StyleId!.Value).ToList();
        Assert.Contains("Heading1", styles);                                    // defined, so Word renders it as a heading
        Assert.Contains("Heading2", styles);
        string text = document.MainDocumentPart.Document.Body!.InnerText;
        Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(text, "(?i)meeting notes").Count);
        Assert.Contains("Next steps", text);
        var errors = new DocumentFormat.OpenXml.Validation.OpenXmlValidator().Validate(document).ToList();
        Assert.True(errors.Count == 0, string.Join("; ", errors.Select(e => e.Description)));
    }

    [Fact]
    public void A_written_workbook_reads_back_with_its_sheet_and_values()
    {
        DocumentWriteResult result = NewService().WriteWorkbook("Costs", new WorkbookContent
        {
            Sheets = new List<WorkbookSheet>
            {
                new()
                {
                    Name = "2026",
                    Rows = new List<WorkbookRow>
                    {
                        new() { Cells = new List<string> { "Item", "Cost" } },
                        new() { Cells = new List<string> { "Hosting", "1200.50" } }
                    }
                }
            }
        });

        Assert.True(File.Exists(result.Path));
        Assert.EndsWith(".xlsx", result.Path, StringComparison.Ordinal);

        string text = DocumentTextExtractor.Extract(result.Path, CancellationToken.None);
        Assert.Contains("Hosting", text, StringComparison.Ordinal);
        Assert.Contains("1200", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_workbook_with_no_sheets_still_opens()
    {
        // Excel refuses a workbook with zero sheets, and a model asked for "an empty template"
        // will send exactly that.
        DocumentWriteResult result = NewService().WriteWorkbook("Empty", new WorkbookContent());

        Assert.True(File.Exists(result.Path));
        DocumentTextExtractor.Extract(result.Path, CancellationToken.None);
    }

    [Theory]
    [InlineData(0, "A")]
    [InlineData(25, "Z")]
    [InlineData(26, "AA")]
    [InlineData(27, "AB")]
    [InlineData(51, "AZ")]
    [InlineData(52, "BA")]
    [InlineData(701, "ZZ")]
    [InlineData(702, "AAA")]
    public void Excel_columns_are_named_the_way_excel_names_them(int index, string expected)
    {
        Assert.Equal(expected, OfficeDocumentWriter.ColumnName(index));
    }

    [Theory]
    [InlineData("2026", "2026")]
    [InlineData("", "Sheet1")]
    [InlineData("   ", "Sheet1")]
    // Excel rejects these characters outright; the workbook would not open at all.
    [InlineData("Q1/Q2", "Q1Q2")]
    [InlineData("Costs[2026]", "Costs2026")]
    [InlineData("A very long sheet name that exceeds the Excel limit", "A very long sheet name that exc")]
    public void A_sheet_name_excel_would_reject_is_cleaned_rather_than_passed_through(string name, string expected)
    {
        Assert.Equal(expected, OfficeDocumentWriter.SafeSheetName(name, 1));
    }

    // ---- the safety model ----

    [Fact]
    public void An_existing_file_is_never_overwritten()
    {
        DocumentWriteService service = NewService();
        var content = new WordDocumentContent { Title = "First" };

        DocumentWriteResult first = service.WriteWord("Report", content);
        DocumentWriteResult second = service.WriteWord("Report", new WordDocumentContent { Title = "Second" });

        Assert.False(first.RenamedToAvoidOverwrite);
        Assert.True(second.RenamedToAvoidOverwrite);
        Assert.NotEqual(first.Path, second.Path);
        Assert.EndsWith("Report (2).docx", second.Path, StringComparison.Ordinal);

        // The first document is still the first document.
        Assert.Contains("First", DocumentTextExtractor.Extract(first.Path, CancellationToken.None), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("sub/report")]
    [InlineData("sub\\report")]
    [InlineData("C:\\Windows\\report")]
    [InlineData("")]
    [InlineData("   ")]
    public void A_name_that_is_not_a_plain_file_name_is_refused(string fileName)
    {
        DocumentWriteService service = NewService();

        Assert.Throws<InvalidDataException>(
            () => service.WriteWord(fileName, new WordDocumentContent { Title = "x" }));
    }

    [Fact]
    public void Documents_land_only_in_the_chosen_folder()
    {
        DocumentWriteResult result = NewService().WriteWord("Report", new WordDocumentContent { Title = "x" });

        Assert.Equal(Path.GetFullPath(_output), Path.GetFullPath(Path.GetDirectoryName(result.Path)!));
    }

    [Fact]
    public void The_default_folder_is_paruxs_own_not_the_users_documents()
    {
        // Writing into somebody's Documents folder without being asked is the presumption this
        // whole path exists to avoid.
        var service = new DocumentWriteService(_state);

        Assert.StartsWith(PaRuXDataRoot.Path, service.OutputFolder, StringComparison.Ordinal);
    }

    [Fact]
    public void A_chosen_folder_survives_a_restart_and_can_be_reset()
    {
        new DocumentWriteService(_state).SetOutputFolder(_output);
        Assert.Equal(Path.GetFullPath(_output), new DocumentWriteService(_state).OutputFolder);

        new DocumentWriteService(_state).SetOutputFolder(null);
        Assert.StartsWith(PaRuXDataRoot.Path, new DocumentWriteService(_state).OutputFolder, StringComparison.Ordinal);
    }

    [Fact]
    public void A_folder_that_does_not_exist_is_refused()
    {
        Assert.Throws<InvalidDataException>(
            () => new DocumentWriteService(_state).SetOutputFolder(Path.Combine(_root, "nope")));
    }

    // ---- what the model is given ----

    [Fact]
    public void The_write_tools_are_offered_without_anything_needing_to_be_connected()
    {
        var names = new DocumentWriteTools(NewService()).GetToolSchemas()
            .Select(s => s?["function"]?["name"]?.GetValue<string>())
            .ToList();

        Assert.Contains(DocumentWriteTools.WriteWordToolName, names);
        Assert.Contains(DocumentWriteTools.WriteWorkbookToolName, names);
    }

    [Fact]
    public async Task The_tool_writes_a_real_document_and_reports_the_path_back()
    {
        var tools = new DocumentWriteTools(NewService());

        string result = await tools.CallAsync(
            DocumentWriteTools.WriteWordToolName,
            """{"fileName":"Notes","title":"Notes","blocks":[{"kind":"paragraph","text":"hello"}]}""",
            CancellationToken.None);

        string expected = Path.Combine(_output, "Notes.docx");
        Assert.Contains(expected, result, StringComparison.Ordinal);
        Assert.True(File.Exists(expected));
        Assert.Contains("hello", DocumentTextExtractor.Extract(expected, CancellationToken.None), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_model_that_sends_numbers_where_strings_were_asked_for_still_gets_a_workbook()
    {
        // Models emit 42 rather than "42" often enough that refusing it would mean failing on a
        // call that is otherwise entirely correct.
        var tools = new DocumentWriteTools(NewService());

        string result = await tools.CallAsync(
            DocumentWriteTools.WriteWorkbookToolName,
            """{"fileName":"Data","sheets":[{"name":"S","rows":[{"cells":["Item",42,true]}]}]}""",
            CancellationToken.None);

        Assert.DoesNotContain("failed", result, StringComparison.OrdinalIgnoreCase);
        string text = DocumentTextExtractor.Extract(Path.Combine(_output, "Data.xlsx"), CancellationToken.None);
        Assert.Contains("42", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{not json")]
    [InlineData("{}")]
    [InlineData("""{"fileName":"../escape","blocks":[]}""")]
    public async Task A_bad_call_fails_visibly_rather_than_writing_something_unexpected(string arguments)
    {
        string result = await new DocumentWriteTools(NewService())
            .CallAsync(DocumentWriteTools.WriteWordToolName, arguments, CancellationToken.None);

        Assert.StartsWith("Document tool failed:", result, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(_output));
    }
}
