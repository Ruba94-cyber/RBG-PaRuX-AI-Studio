using System.Diagnostics;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// The sanity gate on a real launch. PaRuX.RuntimeStub runs as a real child process named llama-server, health is answered
/// as ok, and the loopback reply decides whether the launch is accepted. A live but garbage runtime must be stopped and refused.
/// </summary>
[Collection("OverheadBenchmarkSerial")]
public sealed class RuntimeOutputSanityLaunchTests : IDisposable
{
    private readonly string _work = Path.Combine(Path.GetTempPath(), "parux-sanity-launch-" + Guid.NewGuid().ToString("N"));
    private readonly IDisposable _root;
    private readonly IDisposable _state;
    private readonly IDisposable _models;

    public RuntimeOutputSanityLaunchTests()
    {
        _root = LocalAiPaths.UseRootForTests(Path.Combine(_work, "localai"));
        _state = LocalAiPaths.UseStateDirForTests(Path.Combine(_work, "state"));
        _models = ModelDownloadService.UseModelsDirectoryForTests(Path.Combine(_work, "models"));
    }

    public void Dispose()
    {
        _models.Dispose();
        _state.Dispose();
        _root.Dispose();
        try { Directory.Delete(_work, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>Copies the staged stub into a runtime folder as llama-server, so the launch is a real process.</summary>
    private string StageStubRuntime()
    {
        string staged = Path.Combine(AppContext.BaseDirectory, "runtime-stub");
        Assert.True(File.Exists(Path.Combine(staged, LocalAiPaths.LlamaServerExecutable)),
            "PaRuX.RuntimeStub was not staged; this test refuses to pass without a real child process.");
        string folder = Path.Combine(_work, "runtime");
        Directory.CreateDirectory(folder);
        foreach (string source in Directory.EnumerateFiles(staged, "llama-server*"))
            File.Copy(source, Path.Combine(folder, Path.GetFileName(source)), overwrite: true);
        return Path.Combine(folder, LocalAiPaths.LlamaServerExecutable);
    }

    [Fact]
    public async Task A_healthy_child_that_answers_garbage_is_stopped_and_not_accepted()
    {
        string exe = StageStubRuntime();
        using var responder = LoopbackChatResponder.Start(200, "{\"choices\":[{\"message\":{\"content\":\"\\uFFFD\\uFFFD!!!!!!!!\"}}]}");
        using var http = new HttpClient();
        var manager = new LocalAiProcessManager(0);

        LocalAiProcessManager.RuntimeResult result = await manager.LaunchAndWaitAsync(
            exe, ["--port", "0"], Path.GetDirectoryName(exe)!, "sanity probe profile", LocalAiPaths.TextPidFile, null,
            () => Task.FromResult(true), () => manager.StopText(), TimeSpan.FromSeconds(60), CancellationToken.None,
            ct => RuntimeOutputSanity.ProbeAsync(responder.Endpoint, http, ct));

        Assert.False(result.Success);
        Assert.Contains("output sanity check failed", result.Message);
        Assert.Null(manager.GetTextProcessId());
    }

    [Fact]
    public async Task A_healthy_child_that_answers_plausibly_is_accepted_and_left_running()
    {
        string exe = StageStubRuntime();
        using var responder = LoopbackChatResponder.Start(200, "{\"choices\":[{\"message\":{\"content\":\"4\"}}]}");
        using var http = new HttpClient();
        var manager = new LocalAiProcessManager(0);
        try
        {
            LocalAiProcessManager.RuntimeResult result = await manager.LaunchAndWaitAsync(
                exe, ["--port", "0"], Path.GetDirectoryName(exe)!, "sanity probe profile", LocalAiPaths.TextPidFile, null,
                () => Task.FromResult(true), () => manager.StopText(), TimeSpan.FromSeconds(60), CancellationToken.None,
                ct => RuntimeOutputSanity.ProbeAsync(responder.Endpoint, http, ct));

            Assert.True(result.Success, result.Message);
            Assert.NotNull(manager.GetTextProcessId());
        }
        finally
        {
            manager.StopText();
        }
    }

    [Fact]
    public Task Vulkan_direct_text_start_stops_a_child_with_implausible_output() =>
        AssertTextStartPathAsync(TextStartPath.VulkanAssistant, plausible: false);

    [Fact]
    public Task Vulkan_direct_text_start_accepts_a_child_with_plausible_output() =>
        AssertTextStartPathAsync(TextStartPath.VulkanAssistant, plausible: true);

    [Fact]
    public Task Arbitrary_single_model_start_stops_a_child_with_implausible_output() =>
        AssertTextStartPathAsync(TextStartPath.ArbitrarySingleModel, plausible: false);

    [Fact]
    public Task Arbitrary_single_model_start_accepts_a_child_with_plausible_output() =>
        AssertTextStartPathAsync(TextStartPath.ArbitrarySingleModel, plausible: true);

    [Fact]
    public Task Hot_model_pool_router_stops_a_child_with_implausible_output() =>
        AssertTextStartPathAsync(TextStartPath.HotModelPoolRouter, plausible: false);

    [Fact]
    public Task Hot_model_pool_router_accepts_a_child_with_plausible_output() =>
        AssertTextStartPathAsync(TextStartPath.HotModelPoolRouter, plausible: true);

    [Fact]
    public async Task Gaming_started_during_health_check_skips_sanity_probe_and_stops_the_child()
    {
        StageStubRuntime(LocalAiPaths.RuntimeVulkanPath);
        WriteTestModel(LocalAiPaths.SmallModel);
        var manager = new LocalAiProcessManager(0);
        Process? child = null;
        int probeCalls = 0;
        manager.TextHealthProbeForTests = _ =>
        {
            int? processId = manager.GetTextProcessId();
            Assert.True(processId.HasValue);
            child = Process.GetProcessById(processId!.Value);
            Directory.CreateDirectory(Path.GetDirectoryName(LocalAiPaths.GameMarkerFile)!);
            File.WriteAllText(LocalAiPaths.GameMarkerFile, "{}");
            return Task.FromResult(true);
        };
        manager.TextOutputSanityProbeForTests = _ =>
        {
            probeCalls++;
            return Task.FromResult(new RuntimeOutputSanity.Verdict(true, "test reply was plausible"));
        };

        try
        {
            LocalAiProcessManager.RuntimeResult result = await manager.StartMediaAssistantAsync(CancellationToken.None);

            Assert.False(result.Success, result.Message);
            Assert.Contains("game started", result.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, probeCalls);
            Assert.Null(manager.GetTextProcessId());
            Assert.NotNull(child);
            Assert.True(child!.WaitForExit(5000));
        }
        finally
        {
            manager.StopText();
            child?.Dispose();
        }
    }

    [Fact]
    public async Task Cancellation_during_output_sanity_probe_still_propagates_to_the_caller()
    {
        StageStubRuntime(LocalAiPaths.RuntimeVulkanPath);
        WriteTestModel(LocalAiPaths.SmallModel);
        using var cancellation = new CancellationTokenSource();
        var manager = new LocalAiProcessManager(0)
        {
            TextHealthProbeForTests = _ => Task.FromResult(true)
        };
        CancellationToken probeToken = default;
        int? childId = null;
        manager.TextOutputSanityProbeForTests = token =>
        {
            probeToken = token;
            childId = manager.GetTextProcessId();
            cancellation.Cancel();
            return Task.FromCanceled<RuntimeOutputSanity.Verdict>(token);
        };

        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => manager.StartMediaAssistantAsync(cancellation.Token));

            Assert.Equal(cancellation.Token, probeToken);
            Assert.True(childId.HasValue);
            Assert.Equal(childId, manager.GetTextProcessId());
        }
        finally
        {
            manager.StopText();
        }
    }

    private async Task AssertTextStartPathAsync(TextStartPath path, bool plausible)
    {
        bool isHotPool = path == TextStartPath.HotModelPoolRouter;
        string modelPath = path == TextStartPath.VulkanAssistant
            ? LocalAiPaths.SmallModel
            : Path.Combine(ModelDownloadService.ModelsDirectory, isHotPool ? "target.gguf" : "arbitrary.gguf");
        WriteTestModel(modelPath);
        if (isHotPool)
            WriteTestModel(Path.Combine(ModelDownloadService.ModelsDirectory, "candidate.gguf"));

        string runtime = isHotPool ? LocalAiPaths.RuntimeCudaPath : LocalAiPaths.RuntimeVulkanPath;
        StageStubRuntime(runtime);

        int vramMib = isHotPool ? 16 * 1024 : 0;
        var manager = new LocalAiProcessManager(vramMib)
        {
            InventoryProbe = () => isHotPool
                ? GpuInventoryFixtures.Inventory(GpuInventoryFixtures.Nvidia())
                : GpuInventoryFixtures.Inventory(GpuInventoryFixtures.SoftwareRenderer()),
            TextHealthProbeForTests = _ => Task.FromResult(true)
        };
        int routeSupportCalls = 0;
        int routeValidationCalls = 0;
        if (isHotPool)
        {
            manager.HotModelRouterSupportProbeForTests = (_, _, _) =>
            {
                routeSupportCalls++;
                return Task.FromResult(true);
            };
            manager.HotModelRouteValidationProbeForTests = (_, _) =>
            {
                routeValidationCalls++;
                return Task.FromResult(true);
            };
        }

        var childrenAtProbe = new List<Process>();
        manager.TextOutputSanityProbeForTests = _ =>
        {
            int? processId = manager.GetTextProcessId();
            Assert.True(processId.HasValue, "The output probe must run after a child process is started and recorded.");
            childrenAtProbe.Add(Process.GetProcessById(processId!.Value));
            return Task.FromResult(new RuntimeOutputSanity.Verdict(plausible,
                plausible ? "test reply was plausible" : "test reply was garbage"));
        };

        try
        {
            LocalAiProcessManager.RuntimeResult result = path switch
            {
                TextStartPath.VulkanAssistant => await manager.StartMediaAssistantAsync(CancellationToken.None),
                TextStartPath.ArbitrarySingleModel => await manager.StartArbitraryTextModelAsync(
                    modelPath, "output probe test", "Compare: ", CancellationToken.None),
                TextStartPath.HotModelPoolRouter => await manager.StartArbitraryTextModelAsync(
                    modelPath, "output probe pool test", "Compare: ", CancellationToken.None),
                _ => throw new ArgumentOutOfRangeException(nameof(path))
            };

            int expectedProbeCount = isHotPool && !plausible ? 2 : 1;
            Assert.Equal(expectedProbeCount, childrenAtProbe.Count);
            if (isHotPool)
            {
                Assert.Equal(1, routeSupportCalls);
                Assert.Equal(plausible ? 1 : 0, routeValidationCalls);
            }

            if (plausible)
            {
                Assert.True(result.Success, result.Message);
                Assert.Equal(childrenAtProbe[^1].Id, manager.GetTextProcessId());
                Assert.False(childrenAtProbe[^1].HasExited);
                if (isHotPool) Assert.Contains("hot model pool", result.Message, StringComparison.OrdinalIgnoreCase);
            }
            else
            {
                Assert.False(result.Success, result.Message);
                Assert.Contains("output sanity check failed", result.Message, StringComparison.OrdinalIgnoreCase);
                Assert.Null(manager.GetTextProcessId());
                Assert.All(childrenAtProbe, child => Assert.True(child.WaitForExit(5000),
                    "A child that failed output sanity must be stopped."));
            }
        }
        finally
        {
            manager.StopText();
            foreach (Process child in childrenAtProbe) child.Dispose();
        }
    }

    private void StageStubRuntime(string runtimeFolder)
    {
        string staged = Path.Combine(AppContext.BaseDirectory, "runtime-stub");
        Assert.True(File.Exists(Path.Combine(staged, LocalAiPaths.LlamaServerExecutable)),
            "PaRuX.RuntimeStub was not staged; this test refuses to pass without a real child process.");
        Directory.CreateDirectory(runtimeFolder);
        foreach (string source in Directory.EnumerateFiles(staged, "llama-server*"))
            File.Copy(source, Path.Combine(runtimeFolder, Path.GetFileName(source)), overwrite: true);
    }

    private static void WriteTestModel(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[16]);
    }

    private enum TextStartPath
    {
        VulkanAssistant,
        ArbitrarySingleModel,
        HotModelPoolRouter
    }
}
