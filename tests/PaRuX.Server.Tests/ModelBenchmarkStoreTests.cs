using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class ModelBenchmarkStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "parux-benchmark-store-" + Guid.NewGuid().ToString("N"));

    public void Dispose() { try { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); } catch { } }

    [Fact]
    public void Completed_measurements_persist_across_store_instances()
    {
        var first = new ModelBenchmarkStore(_directory);
        ModelBenchmarkRun run = first.Create(@"C:\models\one.gguf", "One", false);
        first.MarkRunning(run.Id, "Running");
        first.Complete(run.Id, 42.25, 128, 3, "CUDA", "CUDA | RTX 5080");

        ModelBenchmarkRun restored = new ModelBenchmarkStore(_directory).Get(run.Id)!;
        Assert.Equal("completed", restored.Status);
        Assert.Equal(42.25, restored.TokensPerSecond);
        Assert.Equal("CUDA | RTX 5080", restored.HardwareKey);
    }

    [Fact]
    public void Average_never_mixes_different_hardware_profiles_or_failed_runs()
    {
        var store = new ModelBenchmarkStore(_directory);
        const string path = @"C:\models\one.gguf";
        ModelBenchmarkRun cudaA = store.Create(path, "One", false);
        store.Complete(cudaA.Id, 30, 128, 3, "CUDA", "CUDA | RTX 5080");
        ModelBenchmarkRun failed = store.Create(path, "One", false);
        store.Fail(failed.Id, "bad output");
        ModelBenchmarkRun vulkan = store.Create(path, "One", false);
        store.Complete(vulkan.Id, 4, 128, 3, "Vulkan", "Vulkan | Radeon 780M");

        (int count, double? average, _) = store.GetSummary(path);
        Assert.Equal(1, count);
        Assert.Equal(4, average);
    }

    [Fact]
    public void A_run_left_in_progress_is_recovered_as_failed_after_restart()
    {
        var first = new ModelBenchmarkStore(_directory);
        ModelBenchmarkRun run = first.Create(@"C:\models\one.gguf", "One", true);
        first.MarkRunning(run.Id, "Running");

        ModelBenchmarkRun recovered = new ModelBenchmarkStore(_directory).Get(run.Id)!;
        Assert.Equal("failed", recovered.Status);
        Assert.Equal("Interrupted", recovered.Stage);
        Assert.NotNull(recovered.CompletedUtc);
    }
}
