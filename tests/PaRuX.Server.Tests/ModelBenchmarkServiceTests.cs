using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class ModelBenchmarkServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "parux-benchmark-service-" + Guid.NewGuid().ToString("N"));
    private readonly string _modelPath;

    public ModelBenchmarkServiceTests()
    {
        Directory.CreateDirectory(ModelDownloadService.ModelsDirectory);
        _modelPath = Path.Combine(ModelDownloadService.ModelsDirectory, "parux-test-benchmark-" + Guid.NewGuid().ToString("N") + ".gguf");
        File.WriteAllBytes(_modelPath, new byte[] { 1, 2, 3 });
    }

    public void Dispose()
    {
        try { if (File.Exists(_modelPath)) File.Delete(_modelPath); } catch { }
        try { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); } catch { }
    }

    [Fact]
    public async Task Manual_run_records_native_measurement_and_exposes_model_average()
    {
        var runner = new FakeRunner(new ModelBenchmarkMeasurement(55.5, 128, 3, "CUDA", "CUDA | RTX 5080"));
        var store = new ModelBenchmarkStore(_directory);
        var service = new ModelBenchmarkService(store, runner);

        ModelBenchmarkRun created = service.Start(new ModelBenchmarkRequest { ModelPath = _modelPath, Label = "Test" });
        ModelBenchmarkRun completed = await WaitAsync(service, created.Id);

        Assert.Equal("completed", completed.Status);
        Assert.Equal(55.5, completed.TokensPerSecond);
        ModelBenchmarkSummary summary = service.ListModels().Single(m => Path.GetFullPath(m.ModelPath) == Path.GetFullPath(_modelPath));
        Assert.Equal(55.5, summary.AverageTokensPerSecond);
        Assert.Equal(1, summary.CompletedRunCount);
    }

    [Fact]
    public async Task Automatic_post_download_run_is_identified_as_automatic()
    {
        var service = new ModelBenchmarkService(new ModelBenchmarkStore(_directory),
            new FakeRunner(new ModelBenchmarkMeasurement(12, 128, 3, "Vulkan", "Vulkan | Radeon 780M")));

        service.OnGgufDownloaded(_modelPath, "Downloaded model");
        ModelBenchmarkRun created = service.ListRuns().Single();
        ModelBenchmarkRun completed = await WaitAsync(service, created.Id);

        Assert.True(completed.Automatic);
        Assert.Equal("completed", completed.Status);
    }

    [Fact]
    public async Task Runner_failure_is_recorded_as_failed_never_as_zero_throughput()
    {
        var service = new ModelBenchmarkService(new ModelBenchmarkStore(_directory), new FakeRunner(new InvalidDataException("missing avg_ts")));
        ModelBenchmarkRun created = service.Start(new ModelBenchmarkRequest { ModelPath = _modelPath });

        ModelBenchmarkRun failed = await WaitAsync(service, created.Id);
        Assert.Equal("failed", failed.Status);
        Assert.Null(failed.TokensPerSecond);
        Assert.Contains("avg_ts", failed.Error);
    }

    [Fact]
    public void Manual_run_rejects_non_gguf_and_paths_outside_model_directories()
    {
        var service = new ModelBenchmarkService(new ModelBenchmarkStore(_directory),
            new FakeRunner(new ModelBenchmarkMeasurement(1, 128, 3, "CPU", "CPU")));
        string outside = Path.Combine(_directory, "outside.gguf");
        Directory.CreateDirectory(_directory);
        File.WriteAllBytes(outside, new byte[] { 1 });

        Assert.Throws<InvalidDataException>(() => service.Start(new ModelBenchmarkRequest { ModelPath = outside }));
        Assert.Throws<InvalidDataException>(() => service.Start(new ModelBenchmarkRequest { ModelPath = Path.ChangeExtension(_modelPath, ".bin") }));
    }

    [Fact]
    public void Benchmark_usage_keeps_flagship_standard_ahead_of_fallback_models()
    {
        Assert.Equal("standard", ModelBenchmarkService.DescribeUsage(LocalAiPaths.StandardModel));
        Assert.Equal("uncensored", ModelBenchmarkService.DescribeUsage(LocalAiPaths.UncensoredModel));
        Assert.Equal("fallback", ModelBenchmarkService.DescribeUsage(LocalAiPaths.SmallModel));
        Assert.Equal("fallback", ModelBenchmarkService.DescribeUsage(LocalAiPaths.SmallUncensoredModel));
    }

    private static async Task<ModelBenchmarkRun> WaitAsync(ModelBenchmarkService service, string id)
    {
        DateTime deadline = DateTime.UtcNow + TestTimeouts.JobCompletion;
        while (DateTime.UtcNow < deadline)
        {
            ModelBenchmarkRun run = service.Get(id)!;
            if (run.Status is "completed" or "failed" or "cancelled") return run;
            await Task.Delay(20);
        }
        throw new TimeoutException();
    }

    private sealed class FakeRunner : IModelBenchmarkRunner
    {
        private readonly ModelBenchmarkMeasurement? _measurement;
        private readonly Exception? _error;
        public FakeRunner(ModelBenchmarkMeasurement measurement) => _measurement = measurement;
        public FakeRunner(Exception error) => _error = error;
        public Task<ModelBenchmarkMeasurement> RunAsync(string modelPath, CancellationToken cancellationToken) =>
            _error == null ? Task.FromResult(_measurement!) : Task.FromException<ModelBenchmarkMeasurement>(_error);
    }
}
