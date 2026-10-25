using System.Text.Json;
using PaRuX.Contracts;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class EvalTrainingDatasetExporterTests : IDisposable
{
    private readonly string _datasetId = "test-" + Guid.NewGuid().ToString("N");
    private string? _output;

    public void Dispose()
    {
        try { if (_output != null && File.Exists(_output)) File.Delete(_output); } catch { }
        try { if (_output != null && File.Exists(_output + ".meta.json")) File.Delete(_output + ".meta.json"); } catch { }
    }

    [Fact]
    public async Task ExportSftAsync_writes_ordered_chat_jsonl_and_provenance()
    {
        var dataset = new EvalDatasetDto
        {
            Id = _datasetId,
            Name = "Mapped / Eval : Dataset",
            CreatedUtc = DateTime.UtcNow
        };
        DateTime t0 = DateTime.UtcNow.AddMinutes(-2);
        var cases = new List<EvalCaseDto>
        {
            new()
            {
                Id = "b",
                DatasetId = _datasetId,
                Prompt = "second prompt",
                ExpectedBehavior = "second answer",
                CreatedUtc = t0.AddMinutes(1)
            },
            new()
            {
                Id = "a",
                DatasetId = _datasetId,
                Prompt = " first prompt ",
                ExpectedBehavior = " first answer ",
                SystemPrompt = " system ",
                CreatedUtc = t0
            }
        };

        _output = await EvalTrainingDatasetExporter.ExportSftAsync(dataset, cases, CancellationToken.None);

        Assert.True(File.Exists(_output));
        string[] lines = await File.ReadAllLinesAsync(_output);
        Assert.Equal(2, lines.Length);

        using JsonDocument first = JsonDocument.Parse(lines[0]);
        JsonElement firstMessages = first.RootElement.GetProperty("messages");
        Assert.Equal(3, firstMessages.GetArrayLength());
        Assert.Equal("system", firstMessages[0].GetProperty("role").GetString());
        Assert.Equal("system", firstMessages[0].GetProperty("content").GetString());
        Assert.Equal("user", firstMessages[1].GetProperty("role").GetString());
        Assert.Equal("first prompt", firstMessages[1].GetProperty("content").GetString());
        Assert.Equal("assistant", firstMessages[2].GetProperty("role").GetString());
        Assert.Equal("first answer", firstMessages[2].GetProperty("content").GetString());

        using JsonDocument second = JsonDocument.Parse(lines[1]);
        JsonElement secondMessages = second.RootElement.GetProperty("messages");
        Assert.Equal(2, secondMessages.GetArrayLength());
        Assert.Equal("second prompt", secondMessages[0].GetProperty("content").GetString());
        Assert.Equal("second answer", secondMessages[1].GetProperty("content").GetString());

        string metadataPath = _output + ".meta.json";
        Assert.True(File.Exists(metadataPath));
        using JsonDocument metadata = JsonDocument.Parse(await File.ReadAllTextAsync(metadataPath));
        Assert.Equal(_datasetId, metadata.RootElement.GetProperty("sourceEvalDatasetId").GetString());
        Assert.Equal("sft", metadata.RootElement.GetProperty("method").GetString());
        Assert.Equal(new[] { "b", "a" }.OrderBy(_ => 0).Count(), metadata.RootElement.GetProperty("caseIds").GetArrayLength());
    }

    [Fact]
    public async Task ExportSftAsync_rejects_empty_eval_dataset()
    {
        var dataset = new EvalDatasetDto
        {
            Id = _datasetId,
            Name = "Empty",
            CreatedUtc = DateTime.UtcNow
        };

        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            EvalTrainingDatasetExporter.ExportSftAsync(dataset, Array.Empty<EvalCaseDto>(), CancellationToken.None));

        Assert.Contains("no cases", error.Message, StringComparison.OrdinalIgnoreCase);
    }
}
