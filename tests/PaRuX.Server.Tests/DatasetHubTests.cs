using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using PaRuX.Contracts;
using PaRuX.Server;
using Parquet;
using Parquet.Schema;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>Covers Dataset Hub: curation (schema
/// detection, duplicate counting, deterministic split) against real temp files on disk, download
/// request validation, and endpoint auth. Hugging Face search/download itself is not exercised
/// here against the real network - same tier separation this project already applies to
/// ModelDownloadService's own search (no hermetic test hits huggingface.co either).</summary>
public sealed class DatasetHubTests : IClassFixture<ApiSecurityTests.PaRuXFactory>, IDisposable
{
    private readonly ApiSecurityTests.PaRuXFactory _factory;
    private readonly string _testDirectory = Path.Combine(Path.GetTempPath(), "parux-dataset-tests-" + Guid.NewGuid().ToString("N"));

    public DatasetHubTests(ApiSecurityTests.PaRuXFactory factory)
    {
        _factory = factory;
        Directory.CreateDirectory(_testDirectory);
    }

    public void Dispose()
    {
        try { Directory.Delete(_testDirectory, true); } catch { }
    }

    private string WriteTempFile(string extension, string content)
    {
        string path = Path.Combine(_testDirectory, Guid.NewGuid().ToString("N") + extension);
        File.WriteAllText(path, content);
        return path;
    }

    /// <summary>Writes a genuine, real Parquet file via Parquet.Net's own writer - a true
    /// round-trip test (write real Parquet, read it back through DatasetCurationService), not a
    /// synthetic byte fixture that could silently drift from what a real Parquet file actually
    /// looks like. Two columns: a string "text" column (nullable, since real HF datasets routinely
    /// have missing values) and a non-nullable int "label" column - the two families every
    /// realistic curation/training text dataset actually uses.</summary>
    private async Task<string> WriteTempParquetFileAsync(string?[] text, int[] label)
    {
        string path = Path.Combine(_testDirectory, Guid.NewGuid().ToString("N") + ".parquet");
        var textField = new DataField<string?>("text");
        var labelField = new DataField<int>("label");
        var schema = new ParquetSchema(textField, labelField);

        await using Stream stream = File.Create(path);
        await using ParquetWriter writer = await ParquetWriter.CreateAsync(schema, stream);
        using ParquetRowGroupWriter groupWriter = writer.CreateRowGroup();
        await groupWriter.WriteAsync(textField, text.ToList());
        await groupWriter.WriteAsync<int>(labelField, label.AsMemory(), null, null, CancellationToken.None);
        return path;
    }

    [Fact]
    public async Task InspectAsync_detects_csv_schema_and_a_quoted_embedded_comma()
    {
        string path = WriteTempFile(".csv", "name,note\n\"Ada, Lovelace\",\"first, and best\"\nAlan,\"no comma here\"\n");
        var curation = new DatasetCurationService();

        DatasetCurationReport report = await curation.InspectAsync(path, CancellationToken.None);

        Assert.Equal("csv", report.DetectedFormat);
        Assert.Null(report.Error);
        Assert.Equal(2, report.RowCount);
        Assert.Equal(new[] { "name", "note" }, report.Columns);
    }

    [Fact]
    public async Task InspectAsync_detects_a_json_array_of_objects()
    {
        string path = WriteTempFile(".json", "[{\"a\":\"1\",\"b\":\"x\"},{\"a\":\"2\",\"b\":\"\"}]");
        var curation = new DatasetCurationService();

        DatasetCurationReport report = await curation.InspectAsync(path, CancellationToken.None);

        Assert.Equal("json", report.DetectedFormat);
        Assert.Equal(2, report.RowCount);
        Assert.Equal(1, report.EmptyFieldCount);
    }

    [Fact]
    public async Task InspectAsync_detects_jsonl()
    {
        string path = WriteTempFile(".jsonl", "{\"a\":\"1\"}\n{\"a\":\"2\"}\n\n{\"a\":\"3\"}\n");
        var curation = new DatasetCurationService();

        DatasetCurationReport report = await curation.InspectAsync(path, CancellationToken.None);

        Assert.Equal("jsonl", report.DetectedFormat);
        Assert.Equal(3, report.RowCount); // the blank line must not become a phantom row
    }

    [Fact]
    public async Task InspectAsync_counts_exact_duplicate_rows()
    {
        string path = WriteTempFile(".csv", "a,b\n1,x\n1,x\n2,y\n");
        var curation = new DatasetCurationService();

        DatasetCurationReport report = await curation.InspectAsync(path, CancellationToken.None);

        Assert.Equal(3, report.RowCount);
        Assert.Equal(1, report.DuplicateRowCount);
    }

    [Fact]
    public async Task InspectAsync_reports_unsupported_format_instead_of_guessing()
    {
        string path = WriteTempFile(".xlsx", "not a real spreadsheet, doesn't matter");
        var curation = new DatasetCurationService();

        DatasetCurationReport report = await curation.InspectAsync(path, CancellationToken.None);

        Assert.Equal("unsupported", report.DetectedFormat);
        Assert.NotNull(report.Error);
    }

    /// <summary>2026-08-27: .parquet is now a genuinely supported, parsed format (see
    /// DatasetCurationService's own doc comment for the Parquet.Net dependency decision) - a file
    /// merely named .parquet that isn't real Parquet must report a real parse error under
    /// DetectedFormat "parquet", not the honest-but-now-wrong "unsupported" this exact scenario
    /// used to hit before Parquet support existed.</summary>
    [Fact]
    public async Task InspectAsync_reports_a_real_parse_error_for_a_file_merely_named_dot_parquet()
    {
        string path = WriteTempFile(".parquet", "not really parquet, doesn't matter");
        var curation = new DatasetCurationService();

        DatasetCurationReport report = await curation.InspectAsync(path, CancellationToken.None);

        Assert.Equal("parquet", report.DetectedFormat);
        Assert.NotNull(report.Error);
    }

    [Fact]
    public async Task InspectAsync_reports_a_missing_file_as_an_error_not_a_crash()
    {
        var curation = new DatasetCurationService();
        DatasetCurationReport report = await curation.InspectAsync(Path.Combine(_testDirectory, "does-not-exist.csv"), CancellationToken.None);

        Assert.NotNull(report.Error);
    }

    [Fact]
    public async Task InspectAsync_detects_a_real_parquet_files_schema_and_row_count()
    {
        string path = await WriteTempParquetFileAsync(
            text: new[] { "hello world", "second row", null },
            label: new[] { 1, 0, 1 });
        var curation = new DatasetCurationService();

        DatasetCurationReport report = await curation.InspectAsync(path, CancellationToken.None);

        Assert.Equal("parquet", report.DetectedFormat);
        Assert.Null(report.Error);
        Assert.Equal(3, report.RowCount);
        Assert.Equal(new[] { "text", "label" }, report.Columns);
        Assert.Equal(1, report.EmptyFieldCount); // the one null "text" value
    }

    [Fact]
    public async Task SplitAsync_reads_a_real_parquet_source_and_normalizes_output_to_json_lines()
    {
        var rows = Enumerable.Range(0, 20).ToArray();
        string path = await WriteTempParquetFileAsync(
            text: rows.Select(i => (string?)$"row {i}").ToArray(),
            label: rows);
        var curation = new DatasetCurationService();
        var request = new DatasetSplitRequest { SourcePath = path, TrainFraction = 0.7, ValidationFraction = 0.15, Seed = 3 };

        DatasetSplitResult result = await curation.SplitAsync(request, CancellationToken.None);

        Assert.Equal(20, result.TrainCount + result.ValidationCount + result.TestCount);
        Assert.EndsWith(".jsonl", result.TrainPath);
        Assert.EndsWith(".jsonl", result.ValidationPath);
        Assert.EndsWith(".jsonl", result.TestPath);
        string firstTrainLine = (await File.ReadAllLinesAsync(result.TrainPath))[0];
        using JsonDocument doc = JsonDocument.Parse(firstTrainLine);
        Assert.True(doc.RootElement.TryGetProperty("text", out _));
        Assert.True(doc.RootElement.TryGetProperty("label", out _));
    }

    [Fact]
    public async Task SplitAsync_produces_the_requested_proportions_and_is_deterministic_for_the_same_seed()
    {
        var rows = Enumerable.Range(0, 100).Select(i => $"{i},value{i}");
        string path = WriteTempFile(".csv", "id,value\n" + string.Join('\n', rows) + "\n");
        var curation = new DatasetCurationService();
        var request = new DatasetSplitRequest { SourcePath = path, TrainFraction = 0.8, ValidationFraction = 0.1, DeduplicateBeforeSplit = false, Seed = 7 };

        DatasetSplitResult first = await curation.SplitAsync(request, CancellationToken.None);
        string firstTrainContent = await File.ReadAllTextAsync(first.TrainPath);

        Assert.Equal(80, first.TrainCount);
        Assert.Equal(10, first.ValidationCount);
        Assert.Equal(10, first.TestCount);
        Assert.True(File.Exists(first.TrainPath));
        Assert.True(File.Exists(first.ValidationPath));
        Assert.True(File.Exists(first.TestPath));

        DatasetSplitResult second = await curation.SplitAsync(request, CancellationToken.None);
        string secondTrainContent = await File.ReadAllTextAsync(second.TrainPath);
        Assert.Equal(firstTrainContent, secondTrainContent); // same seed -> same shuffle -> same split contents
    }

    [Fact]
    public async Task SplitAsync_removes_duplicates_before_splitting_when_requested()
    {
        var rows = new List<string> { "1,x", "1,x", "2,y", "3,z" }; // one exact duplicate
        string path = WriteTempFile(".csv", "id,value\n" + string.Join('\n', rows) + "\n");
        var curation = new DatasetCurationService();
        var request = new DatasetSplitRequest { SourcePath = path, TrainFraction = 0.5, ValidationFraction = 0.25, DeduplicateBeforeSplit = true, Seed = 1 };

        DatasetSplitResult result = await curation.SplitAsync(request, CancellationToken.None);

        Assert.Equal(1, result.DuplicatesRemoved);
        Assert.Equal(3, result.TrainCount + result.ValidationCount + result.TestCount);
    }

    [Fact]
    public async Task SplitAsync_rejects_fractions_that_do_not_leave_room_for_a_test_split()
    {
        string path = WriteTempFile(".csv", "a\n1\n2\n");
        var curation = new DatasetCurationService();
        var request = new DatasetSplitRequest { SourcePath = path, TrainFraction = 0.9, ValidationFraction = 0.2 };

        await Assert.ThrowsAsync<InvalidDataException>(() => curation.SplitAsync(request, CancellationToken.None));
    }

    [Fact]
    public void SanitizeRepoId_replaces_path_separators()
    {
        Assert.Equal("some-org_some-dataset", DatasetDownloadService.SanitizeRepoId("some-org/some-dataset"));
    }

    [Fact]
    public void SanitizeRepoId_rejects_path_traversal_attempts()
    {
        Assert.Throws<InvalidDataException>(() => DatasetDownloadService.SanitizeRepoId("../../etc/passwd"));
    }

    [Fact]
    public async Task CreateDownloadAsync_rejects_a_request_missing_required_fields()
    {
        var datasets = new DatasetDownloadService();
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            datasets.CreateDownloadAsync(new DatasetDownloadRequest { RepoId = "x" }, CancellationToken.None));
    }

    [Fact]
    public async Task GetBudgetAsync_reports_the_shared_700gb_limit()
    {
        var datasets = new DatasetDownloadService();
        AiStorageBudgetStatus budget = await datasets.GetBudgetAsync();
        Assert.Equal(700L * 1024 * 1024 * 1024, budget.LimitBytes);
    }

    [Fact]
    public async Task Datasets_search_endpoint_rejects_missing_token()
    {
        using HttpClient client = _factory.CreateClient();
        HttpResponseMessage response = await client.GetAsync("/api/v1/datasets/search?query=squad");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Datasets_inspect_endpoint_accepts_the_desktop_session_token_and_a_real_file()
    {
        string path = WriteTempFile(".csv", "a,b\n1,2\n");
        using HttpClient client = _factory.CreateClient();
        ServerState state = _factory.Services.GetRequiredService<ServerState>();
        client.DefaultRequestHeaders.Add("X-PaRuX-Token", state.Token);

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/datasets/inspect", new DatasetInspectRequest { SourcePath = path });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ---- "Use for Eval" (2026-08-27, scope-mapping row #9) --------------------------------

    [Fact]
    public async Task MapToEvalCasesAsync_maps_caller_named_columns_and_ignores_others()
    {
        string path = WriteTempFile(".csv", "question,answer,topic\nWhat is 2+2?,4,math\nCapital of Italy?,Rome,geography\n");
        var curation = new DatasetCurationService();

        (List<CreateEvalCaseRequest> cases, int skipped) = await curation.MapToEvalCasesAsync(
            path, "question", "answer", null, CancellationToken.None);

        Assert.Equal(0, skipped);
        Assert.Equal(2, cases.Count);
        Assert.Equal("What is 2+2?", cases[0].Prompt);
        Assert.Equal("4", cases[0].ExpectedBehavior);
        Assert.Null(cases[0].SystemPrompt);
    }

    [Fact]
    public async Task MapToEvalCasesAsync_reads_an_optional_system_prompt_column()
    {
        string path = WriteTempFile(".csv", "prompt,expected,system\nHi,Hello!,Be friendly\n");
        var curation = new DatasetCurationService();

        (List<CreateEvalCaseRequest> cases, _) = await curation.MapToEvalCasesAsync(
            path, "prompt", "expected", "system", CancellationToken.None);

        Assert.Single(cases);
        Assert.Equal("Be friendly", cases[0].SystemPrompt);
    }

    [Fact]
    public async Task MapToEvalCasesAsync_skips_rows_with_an_empty_prompt_or_expected_cell_and_counts_them()
    {
        string path = WriteTempFile(".csv", "prompt,expected\nreal question,real answer\n,missing prompt\nreal question 2,\n");
        var curation = new DatasetCurationService();

        (List<CreateEvalCaseRequest> cases, int skipped) = await curation.MapToEvalCasesAsync(
            path, "prompt", "expected", null, CancellationToken.None);

        Assert.Single(cases);
        Assert.Equal(2, skipped);
    }

    [Fact]
    public async Task MapToEvalCasesAsync_never_guesses_a_missing_column_and_lists_what_is_actually_there()
    {
        string path = WriteTempFile(".csv", "instruction,output\nDo X,Result X\n");
        var curation = new DatasetCurationService();

        InvalidDataException ex = await Assert.ThrowsAsync<InvalidDataException>(() =>
            curation.MapToEvalCasesAsync(path, "prompt", "output", null, CancellationToken.None));
        Assert.Contains("instruction", ex.Message);
        Assert.Contains("output", ex.Message);
    }

    [Fact]
    public async Task MapToEvalCasesAsync_rejects_a_blank_column_name_instead_of_guessing()
    {
        string path = WriteTempFile(".csv", "a,b\n1,2\n");
        var curation = new DatasetCurationService();

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            curation.MapToEvalCasesAsync(path, "", "b", null, CancellationToken.None));
    }

    [Fact]
    public async Task MapToEvalCasesAsync_works_from_a_real_jsonl_source()
    {
        string path = WriteTempFile(".jsonl", "{\"q\":\"a1\",\"a\":\"b1\"}\n{\"q\":\"a2\",\"a\":\"b2\"}\n");
        var curation = new DatasetCurationService();

        (List<CreateEvalCaseRequest> cases, int skipped) = await curation.MapToEvalCasesAsync(
            path, "q", "a", null, CancellationToken.None);

        Assert.Equal(0, skipped);
        Assert.Equal(2, cases.Count);
    }

    [Fact]
    public async Task Import_eval_endpoint_rejects_missing_token()
    {
        using HttpClient client = _factory.CreateClient();
        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/datasets/import-eval", new ImportDatasetAsEvalCasesRequest());
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Import_eval_endpoint_creates_real_cases_in_a_real_eval_dataset()
    {
        string path = WriteTempFile(".csv", "prompt,expected\nWhat color is the sky?,Blue\nWhat is water made of?,Hydrogen and oxygen\n");
        using HttpClient client = _factory.CreateClient();
        ServerState state = _factory.Services.GetRequiredService<ServerState>();
        client.DefaultRequestHeaders.Add("X-PaRuX-Token", state.Token);

        HttpResponseMessage createDataset = await client.PostAsJsonAsync("/api/v1/eval/datasets",
            new CreateEvalDatasetRequest { Name = "Use-for-Eval import test dataset " + Guid.NewGuid().ToString("N") });
        EvalDatasetDto? dataset = await createDataset.Content.ReadFromJsonAsync<EvalDatasetDto>();
        Assert.NotNull(dataset);

        HttpResponseMessage importResponse = await client.PostAsJsonAsync("/api/v1/datasets/import-eval", new ImportDatasetAsEvalCasesRequest
        {
            SourcePath = path,
            TargetEvalDatasetId = dataset!.Id,
            PromptColumn = "prompt",
            ExpectedBehaviorColumn = "expected",
        });
        Assert.Equal(HttpStatusCode.OK, importResponse.StatusCode);
        ImportDatasetAsEvalCasesResult? result = await importResponse.Content.ReadFromJsonAsync<ImportDatasetAsEvalCasesResult>();
        Assert.NotNull(result);
        Assert.Equal(2, result!.CasesCreated);
        Assert.Equal(0, result.RowsSkipped);

        HttpResponseMessage listResponse = await client.GetAsync("/api/v1/eval/datasets/" + dataset.Id + "/cases");
        EvalCaseListResponse? realCases = await listResponse.Content.ReadFromJsonAsync<EvalCaseListResponse>();
        Assert.NotNull(realCases);
        Assert.Equal(2, realCases!.Cases.Count);
        Assert.Contains(realCases.Cases, c => c.Prompt == "What color is the sky?" && c.ExpectedBehavior == "Blue");
    }

    [Fact]
    public async Task Import_eval_endpoint_rejects_an_unknown_target_dataset_without_creating_orphan_cases()
    {
        string path = WriteTempFile(".csv", "prompt,expected\nHi,Hello\n");
        using HttpClient client = _factory.CreateClient();
        ServerState state = _factory.Services.GetRequiredService<ServerState>();
        client.DefaultRequestHeaders.Add("X-PaRuX-Token", state.Token);

        HttpResponseMessage importResponse = await client.PostAsJsonAsync("/api/v1/datasets/import-eval", new ImportDatasetAsEvalCasesRequest
        {
            SourcePath = path,
            TargetEvalDatasetId = Guid.NewGuid().ToString("N"),
            PromptColumn = "prompt",
            ExpectedBehaviorColumn = "expected",
        });
        Assert.Equal(HttpStatusCode.BadRequest, importResponse.StatusCode);
    }
}
