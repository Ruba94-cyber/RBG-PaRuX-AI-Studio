using System.Text.Json;
using PaRuX.Contracts;
using PaRuX.Server;
using PaRuX.Server.EvalLab;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Deterministic non-GPU acceptance journey for the front half of Lifecycle Lab:
/// source dataset -> inspect -> split -> explicit semantic mapping -> durable Eval cases ->
/// SFT chat JSONL + provenance. Training/runtime execution begins only after this handoff and is
/// intentionally a separate GPU/live gate.
/// </summary>
public sealed class DatasetLifecycleAcceptanceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "parux-dataset-lifecycle-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _generatedTrainingFiles = new();

    public DatasetLifecycleAcceptanceTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        foreach (string path in _generatedTrainingFiles)
        {
            try { File.Delete(path); } catch { }
            try { File.Delete(path + ".meta.json"); } catch { }
            try { File.Delete(path + ".tmp"); } catch { }
            try { File.Delete(path + ".meta.json.tmp"); } catch { }
        }
        try { Directory.Delete(_directory, true); } catch { }
    }

    [Fact]
    public async Task Dataset_can_reach_durable_eval_and_training_ready_sft_without_semantic_guessing()
    {
        string source = Path.Combine(_directory, "source.csv");
        await File.WriteAllTextAsync(source,
            "question,answer,system,noise\n" +
            "What is 2+2?,4,Answer concisely,unused-a\n" +
            "Capital of Italy?,Rome,Answer concisely,unused-b\n" +
            "Capital of Italy?,Rome,Answer concisely,unused-b\n" +
            ",missing prompt,Answer concisely,unused-c\n");

        var curation = new DatasetCurationService();
        DatasetCurationReport report = await curation.InspectAsync(source, CancellationToken.None);
        Assert.Equal("csv", report.DetectedFormat);
        Assert.Equal(4, report.RowCount);
        Assert.Equal(1, report.DuplicateRowCount);
        Assert.Equal(new[] { "question", "answer", "system", "noise" }, report.Columns);

        DatasetSplitResult split = await curation.SplitAsync(new DatasetSplitRequest
        {
            SourcePath = source,
            TrainFraction = 0.5,
            ValidationFraction = 0.25,
            DeduplicateBeforeSplit = true,
            Seed = 42
        }, CancellationToken.None);
        Assert.Equal(1, split.DuplicatesRemoved);
        Assert.Equal(3, split.TrainCount + split.ValidationCount + split.TestCount);
        Assert.True(File.Exists(split.TrainPath));
        Assert.True(File.Exists(split.ValidationPath));
        Assert.True(File.Exists(split.TestPath));

        // Mapping names are supplied explicitly by the caller. The unrelated "noise" column is
        // ignored, and the blank-prompt row is counted rather than converted into a fake case.
        (List<CreateEvalCaseRequest> mapped, int skipped) = await curation.MapToEvalCasesAsync(
            source,
            promptColumn: "question",
            expectedBehaviorColumn: "answer",
            systemPromptColumn: "system",
            CancellationToken.None);
        Assert.Equal(1, skipped);
        Assert.Equal(3, mapped.Count);
        Assert.All(mapped, c => Assert.Equal("Answer concisely", c.SystemPrompt));

        var store = new EvalStore(_directory);
        EvalDatasetDto dataset = store.CreateDataset(new CreateEvalDatasetRequest
        {
            Name = "Acceptance dataset",
            Description = "Explicitly mapped from source.csv"
        });
        foreach (CreateEvalCaseRequest request in mapped)
            store.CreateCase(dataset.Id, request);

        List<EvalCaseDto> persisted = store.ListCases(dataset.Id);
        Assert.Equal(3, persisted.Count);
        Assert.Equal(mapped.Select(c => c.Prompt), persisted.Select(c => c.Prompt));

        // Re-open the store to model an app/Core restart before Training handoff.
        var reopened = new EvalStore(_directory);
        List<EvalCaseDto> afterRestart = reopened.ListCases(dataset.Id);
        Assert.Equal(persisted.Select(c => c.Id), afterRestart.Select(c => c.Id));

        string sftPath = await EvalTrainingDatasetExporter.ExportSftAsync(dataset, afterRestart, CancellationToken.None);
        _generatedTrainingFiles.Add(sftPath);
        Assert.True(File.Exists(sftPath));
        Assert.True(File.Exists(sftPath + ".meta.json"));

        string[] lines = await File.ReadAllLinesAsync(sftPath);
        Assert.Equal(afterRestart.Count, lines.Length);
        foreach (string line in lines)
        {
            using JsonDocument doc = JsonDocument.Parse(line);
            JsonElement messages = doc.RootElement.GetProperty("messages");
            Assert.Equal(3, messages.GetArrayLength());
            Assert.Equal("system", messages[0].GetProperty("role").GetString());
            Assert.Equal("user", messages[1].GetProperty("role").GetString());
            Assert.Equal("assistant", messages[2].GetProperty("role").GetString());
        }

        using JsonDocument provenance = JsonDocument.Parse(await File.ReadAllTextAsync(sftPath + ".meta.json"));
        Assert.Equal(dataset.Id, provenance.RootElement.GetProperty("sourceEvalDatasetId").GetString());
        string[] provenanceCaseIds = provenance.RootElement.GetProperty("caseIds")
            .EnumerateArray().Select(x => x.GetString()!).ToArray();
        Assert.Equal(afterRestart.Select(c => c.Id), provenanceCaseIds);
        Assert.Equal(TrainingMethod.Sft, provenance.RootElement.GetProperty("method").GetString());
    }
}
