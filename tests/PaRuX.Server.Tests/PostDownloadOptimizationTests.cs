using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>A newly downloaded chat model is queued for automatic per-PC tuning right away, not only once a role points at it.</summary>
public sealed class PostDownloadOptimizationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "parux-postdl-" + Guid.NewGuid().ToString("N"));
    private readonly IDisposable _state;

    public PostDownloadOptimizationTests()
    {
        Directory.CreateDirectory(_dir);
        _state = LocalAiPaths.UseStateDirForTests(Path.Combine(_dir, "state"));
    }

    public void Dispose()
    {
        _state.Dispose();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private string Model(string name)
    {
        string path = Path.Combine(_dir, name);
        File.WriteAllText(path, "gguf");
        return path;
    }

    [Fact]
    public void A_downloaded_chat_model_is_queued_once_and_listed_as_a_chat_calibration_target()
    {
        string path = Model("Qwen3.8-27B-Q4_K_M.gguf");
        var queue = new PostDownloadOptimizationQueue();

        queue.Enqueue(path);
        queue.Enqueue(path);

        Assert.Equal(new[] { path }, queue.Pending());
        CalibrationTarget target = Assert.Single(IdleCalibrationComposition.QueuedTargets(queue));
        Assert.Equal(path, target.ModelPath);
        Assert.Equal("chat", target.Workload);
    }

    [Theory]
    [InlineData("mmproj-F16.gguf")]
    [InlineData("mtp-Qwen3.8-27B-Q4_0.gguf")]
    [InlineData("notes.txt")]
    public void Projectors_speculative_heads_and_non_models_are_never_queued(string name)
    {
        var queue = new PostDownloadOptimizationQueue();

        queue.Enqueue(Model(name));

        Assert.Empty(queue.Pending());
    }

    [Fact]
    public void A_model_that_was_calibrated_leaves_the_queue_and_a_missing_file_is_dropped()
    {
        string kept = Model("a-model.gguf");
        string gone = Model("b-model.gguf");
        var queue = new PostDownloadOptimizationQueue();
        queue.Enqueue(kept);
        queue.Enqueue(gone);

        File.Delete(gone);
        Assert.Equal(new[] { kept }, queue.Pending());

        queue.MarkDone(kept);
        Assert.Empty(queue.Pending());
    }

    [Fact]
    public void The_queue_survives_a_server_restart()
    {
        string path = Model("persisted.gguf");
        new PostDownloadOptimizationQueue().Enqueue(path);

        Assert.Equal(new[] { path }, new PostDownloadOptimizationQueue().Pending());
    }

    [Fact]
    public void The_completion_handler_runs_the_benchmark_handler_and_queues_the_model()
    {
        string path = Model("handler-model.gguf");
        var inner = new RecordingHandler();
        var queue = new PostDownloadOptimizationQueue();

        new PostDownloadOptimizationHandler(inner, queue).OnGgufDownloaded(path, "label");

        Assert.Equal(new[] { path }, inner.Seen);
        Assert.Equal(new[] { path }, queue.Pending());
    }

    private sealed class RecordingHandler : IModelDownloadCompletionHandler
    {
        public List<string> Seen { get; } = new();
        public void OnGgufDownloaded(string modelPath, string label) => Seen.Add(modelPath);
    }
}
