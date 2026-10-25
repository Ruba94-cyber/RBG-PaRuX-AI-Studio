using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class WindowsMlEngineWorkerIntegrationTests : IDisposable
{
    private const string ProviderName = "NvTensorRtRtxExecutionProvider";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parux-winml-engine-" + Guid.NewGuid().ToString("N"));
    private readonly IDisposable? _modelsScope;
    private readonly IDisposable? _pathsScope;
    private readonly LocalInferenceEngineRegistry _registry = new();
    private readonly ModelDownloadService _models = new();
    private readonly string _variantId;
    private readonly string _workerPath;
    private string _modelDir = string.Empty;

    public WindowsMlEngineWorkerIntegrationTests()
    {
        try
        {
            string modelRoot = Path.Combine(_root, "models");
            string localAiRoot = Path.Combine(_root, "local-ai");
            Directory.CreateDirectory(modelRoot);
            _modelsScope = ModelDownloadService.UseModelsDirectoryForTests(modelRoot);
            _pathsScope = LocalAiPaths.UseRootForTests(localAiRoot);

            var catalog = new MultiFormatModelCatalog(
                modelRoot,
                Path.Combine(modelRoot, ".multi-format-library.json"),
                new ManagedModelRevisionStore(Path.Combine(modelRoot, ".revisions.json")));
            string modelPath = Path.Combine(catalog.GetManagedVariantRoot(ManagedModelFormat.Onnx), "test", "rev-a", "fp16");
            Directory.CreateDirectory(modelPath);
            _modelDir = modelPath;
            File.WriteAllBytes(Path.Combine(modelPath, "model.onnx"), new byte[] { 1, 2, 3 });
            File.WriteAllText(Path.Combine(modelPath, "genai_config.json"), "{}");
            File.WriteAllText(Path.Combine(modelPath, "chat_template.jinja"), "{{ messages }}");
            _variantId = catalog.Register(new ManagedModelVariantIdentity(
                "test/model", "rev-a", "onnx-fp16", ManagedModelFormat.Onnx, "fp16", modelPath)).Variant.StableKey;

            string workerName = OperatingSystem.IsWindows() ? "PaRuX.WinMlWorkerStub.exe" : "PaRuX.WinMlWorkerStub";
            _workerPath = Path.Combine(AppContext.BaseDirectory, "winml-worker-stub", workerName);
            Assert.True(File.Exists(_workerPath), "The controlled Windows ML worker stub must be staged beside the test output.");
        }
        catch
        {
            Cleanup();
            throw;
        }
    }

    [Fact]
    public async Task Cancellation_reuses_worker_and_repeated_crashes_restart_without_stale_events()
    {
        WindowsMlProviderInfo provider = new()
        {
            Name = ProviderName,
            ReadyState = "Ready",
            LibraryFileVersion = "test-stub",
            LibrarySha256 = new string('a', 64)
        };
        var status = new WindowsMlPerformancePackStatus { SupportedOs = true, WorkerAvailable = true, Providers = { provider } };
        string identity = WindowsMlGenAiProtocol.BuildRuntimeIdentity(provider);
        await using var engine = new WindowsMlGenAiLocalInferenceEngine(
            ProviderName,
            identity,
            _models,
            _registry,
            new LocalAiProcessManager(),
            _workerPath,
            (_, _) => Task.FromResult(status));

        await CancelAndAssertWarmRecoveryAsync(engine, "cancel-1", "warm-1");
        Assert.Equal(1, engine.WarmCancellationCount);
        Assert.Equal(0, engine.ForcedRestartCount);
        Assert.Equal(1, engine.WorkerStartCount);

        await CancelAndAssertWarmRecoveryAsync(engine, "cancel-2", "warm-2");
        Assert.Equal(2, engine.WarmCancellationCount);
        Assert.Equal(0, engine.ForcedRestartCount);
        Assert.Equal(1, engine.WorkerStartCount);

        await AssertWorkerCrashAsync(engine);
        Assert.Equal(1, engine.ForcedRestartCount);
        Assert.Equal(new[] { "result:after-crash-1" }, await CollectTextAsync(engine, "after-crash-1"));
        Assert.Equal(2, engine.WorkerStartCount);

        await AssertWorkerCrashAsync(engine);
        Assert.Equal(2, engine.ForcedRestartCount);
        Assert.Equal(new[] { "result:after-crash-2" }, await CollectTextAsync(engine, "after-crash-2"));
        Assert.Equal(3, engine.WorkerStartCount);
    }

    [Fact]
    public async Task Structured_output_is_rejected_explicitly_without_starting_a_worker()
    {
        var provider = new WindowsMlProviderInfo
        {
            Name = ProviderName, ReadyState = "Ready", LibraryFileVersion = "test-stub", LibrarySha256 = new string('a', 64)
        };
        var status = new WindowsMlPerformancePackStatus { SupportedOs = true, WorkerAvailable = true, Providers = { provider } };
        await using var engine = new WindowsMlGenAiLocalInferenceEngine(
            ProviderName, WindowsMlGenAiProtocol.BuildRuntimeIdentity(provider), _models, _registry,
            new LocalAiProcessManager(), _workerPath, (_, _) => Task.FromResult(status));

        LocalInferenceRequest request = Request("x") with
        {
            StructuredOutput = new LocalStructuredOutputRequest("s", "{\"type\":\"object\"}")
        };
        await Assert.ThrowsAsync<NotSupportedException>(async () =>
        {
            await foreach (LocalInferenceChunk _ in engine.StreamAsync(request, CancellationToken.None)) { }
        });
        Assert.Equal(0, engine.WorkerStartCount);
    }

    private WindowsMlGenAiLocalInferenceEngine NewEngine(string providerName, string? adapterPin)
    {
        var provider = new WindowsMlProviderInfo
        {
            Name = providerName, ReadyState = "Ready", LibraryFileVersion = "test-stub", LibrarySha256 = new string('a', 64)
        };
        var status = new WindowsMlPerformancePackStatus { SupportedOs = true, WorkerAvailable = true, Providers = { provider } };
        return new WindowsMlGenAiLocalInferenceEngine(
            providerName, WindowsMlGenAiProtocol.BuildRuntimeIdentity(provider, adapterPin), _models, _registry,
            new LocalAiProcessManager(), _workerPath, (_, _) => Task.FromResult(status));
    }

    [Fact]
    public async Task Model_without_thinking_control_serves_thinking_allowed_but_rejects_thinking_false_without_restart()
    {
        File.WriteAllText(Path.Combine(_modelDir, "stub-no-thinking"), "x");
        await using WindowsMlGenAiLocalInferenceEngine engine = NewEngine(ProviderName, null);

        Assert.Equal(new[] { "result:ok" }, await CollectTextAsync(engine, "ok"));
        Assert.False(engine.ThinkingControlExposed);

        LocalInferenceRequest demand = Request("x") with { EnableThinking = false };
        await Assert.ThrowsAsync<NotSupportedException>(async () =>
        {
            await foreach (LocalInferenceChunk _ in engine.StreamAsync(demand, CancellationToken.None)) { }
        });
        Assert.Equal(0, engine.ForcedRestartCount);
        Assert.Equal(1, engine.WorkerStartCount);
        Assert.Equal(new[] { "result:again" }, await CollectTextAsync(engine, "again"));
    }

    [Fact]
    public async Task Model_with_thinking_control_accepts_thinking_false()
    {
        await using WindowsMlGenAiLocalInferenceEngine engine = NewEngine(ProviderName, null);
        LocalInferenceRequest demand = Request("t") with { EnableThinking = false };
        var text = new List<string>();
        await foreach (LocalInferenceChunk chunk in engine.StreamAsync(demand, CancellationToken.None))
            if (!string.IsNullOrEmpty(chunk.Content)) text.Add(chunk.Content);
        Assert.Equal(new[] { "result:t" }, text);
        Assert.True(engine.ThinkingControlExposed);
    }

    [Fact]
    public async Task Pinned_directml_engine_is_admitted_only_with_adapter_evidence_and_unpinned_directml_is_refused()
    {
        await using (WindowsMlGenAiLocalInferenceEngine pinned = NewEngine("DmlExecutionProvider", "10de:2c02"))
            Assert.Equal(new[] { "result:p" }, await CollectTextAsync(pinned, "p"));

        File.WriteAllText(Path.Combine(_modelDir, "stub-no-adapter-evidence"), "x");
        await using (WindowsMlGenAiLocalInferenceEngine noEvidence = NewEngine("DmlExecutionProvider", "10de:2c02"))
            await Assert.ThrowsAsync<InvalidDataException>(async () => await CollectTextAsync(noEvidence, "q"));

        await using WindowsMlGenAiLocalInferenceEngine unpinned = NewEngine("DmlExecutionProvider", null);
        await Assert.ThrowsAsync<InvalidDataException>(async () => await CollectTextAsync(unpinned, "r"));
    }

    [Fact]
    public async Task Descriptor_claims_text_only_no_vision_tool_or_structured_parity()
    {
        await using WindowsMlGenAiLocalInferenceEngine engine = NewEngine(ProviderName, null);
        Assert.Equal(LocalInferenceCapabilities.Text, engine.Descriptor.Capabilities);
    }

    [Fact]
    public async Task Cancelling_one_request_never_cancels_another_and_releases_serialization()
    {
        await using WindowsMlGenAiLocalInferenceEngine engine = NewEngine(ProviderName, null);

        using var cancelA = new CancellationTokenSource();
        await using IAsyncEnumerator<LocalInferenceChunk> a = engine
            .StreamAsync(Request("cancel-a"), cancelA.Token).GetAsyncEnumerator();
        Assert.True(await a.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(15)));

        // B and C queue behind A's single-flight gate.
        using var cancelB = new CancellationTokenSource();
        Task<string[]> b = CollectWithTokenAsync(engine, "b", cancelB.Token);
        Task<string[]> c = CollectWithTokenAsync(engine, "c", CancellationToken.None);
        await Task.Delay(300);
        Assert.False(b.IsCompleted);
        Assert.False(c.IsCompleted);

        // Cancelling queued B must not disturb running A or queued C.
        cancelB.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => b.WaitAsync(TimeSpan.FromSeconds(15)));
        Assert.False(c.IsCompleted);

        // Cancelling A drains its terminal, releases the gate and lets C run with only its own events.
        cancelA.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await a.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(15)));
        Assert.Equal(new[] { "result:c" }, await c.WaitAsync(TimeSpan.FromSeconds(15)));

        Assert.Equal(1, engine.WarmCancellationCount);
        Assert.Equal(0, engine.ForcedRestartCount);
        Assert.Equal(1, engine.WorkerStartCount);
        using var idle = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await engine.WaitForGenerationIdleAsync(idle.Token);
    }

    private async Task<string[]> CollectWithTokenAsync(
        WindowsMlGenAiLocalInferenceEngine engine, string prompt, CancellationToken token)
    {
        var chunks = new List<string>();
        await foreach (LocalInferenceChunk chunk in engine.StreamAsync(Request(prompt), token).WithCancellation(token))
        {
            if (!string.IsNullOrEmpty(chunk.Content)) chunks.Add(chunk.Content);
            if (chunk.Error != null) throw new InvalidOperationException(chunk.Error);
        }
        return chunks.ToArray();
    }

    public void Dispose() => Cleanup();

    private void Cleanup()
    {
        try { _pathsScope?.Dispose(); } catch { }
        try { _modelsScope?.Dispose(); } catch { }
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }

    private async Task CancelAndAssertWarmRecoveryAsync(
        WindowsMlGenAiLocalInferenceEngine engine,
        string cancelPrompt,
        string recoveryPrompt)
    {
        using var cancellation = new CancellationTokenSource();
        await using IAsyncEnumerator<LocalInferenceChunk> enumerator = engine
            .StreamAsync(Request(cancelPrompt), cancellation.Token)
            .GetAsyncEnumerator();
        Assert.True(await enumerator.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(15)));
        Assert.Equal("before-cancel:" + cancelPrompt, enumerator.Current.Content);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await enumerator.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(15)));
        Assert.Equal(new[] { "result:" + recoveryPrompt }, await CollectTextAsync(engine, recoveryPrompt));
    }

    private async Task AssertWorkerCrashAsync(WindowsMlGenAiLocalInferenceEngine engine)
    {
        await Assert.ThrowsAsync<System.Threading.Channels.ChannelClosedException>(async () =>
            await CollectTextAsync(engine, "crash"));
    }

    private async Task<string[]> CollectTextAsync(WindowsMlGenAiLocalInferenceEngine engine, string prompt)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var chunks = new List<string>();
        await foreach (LocalInferenceChunk chunk in engine
            .StreamAsync(Request(prompt), timeout.Token)
            .WithCancellation(timeout.Token))
        {
            if (!string.IsNullOrEmpty(chunk.Content)) chunks.Add(chunk.Content);
            if (chunk.Error != null) throw new InvalidOperationException(chunk.Error);
        }
        return chunks.ToArray();
    }

    private LocalInferenceRequest Request(string prompt) => new(
        _variantId,
        new[] { ("user", prompt) },
        MaxOutputTokens: 16,
        Temperature: 0,
        Stream: true);
}
