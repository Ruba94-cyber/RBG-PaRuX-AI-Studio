using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// The media start overlaps ComfyUI's cold start with the media assistant's model load, a prewarm starts only ComfyUI, and every
/// launch is intercepted before a process exists, so what is asserted is the ordering, the cleanup and the refusals, not a stub's timing.
/// </summary>
[Collection("OverheadBenchmarkSerial")]
public sealed class MediaSwapOverlapTests : IDisposable
{
    private readonly string _work = Path.Combine(Path.GetTempPath(), "parux-media-overlap-" + Guid.NewGuid().ToString("N"));
    private readonly IDisposable _root;
    private readonly IDisposable _state;

    public MediaSwapOverlapTests()
    {
        _root = LocalAiPaths.UseRootForTests(Path.Combine(_work, "localai"));
        _state = LocalAiPaths.UseStateDirForTests(Path.Combine(_work, "state"));
        WriteFile(Path.Combine(LocalAiPaths.RuntimeVulkanPath, LocalAiPaths.LlamaServerExecutable));
        WriteFile(LocalAiPaths.SmallModel);
        WriteFile(LocalAiPaths.ComfyPython);
        WriteFile(LocalAiPaths.ComfyMain);
    }

    public void Dispose()
    {
        _state.Dispose();
        _root.Dispose();
        try { Directory.Delete(_work, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static void WriteFile(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[16]);
    }

    private static bool IsComfy(string exe) => Path.GetFileNameWithoutExtension(exe).Equals("python", StringComparison.OrdinalIgnoreCase);

    [Fact]
    public async Task Comfy_starts_while_the_media_assistant_is_still_loading()
    {
        using var comfyStarted = new ManualResetEventSlim();
        var order = new List<string>();
        bool comfySeenDuringAssistantLoad = false;
        var manager = new LocalAiProcessManager(16384)
        {
            TextHealthProbeForTests = _ => Task.FromResult(false),
            MediaHealthProbeForTests = _ => Task.FromResult(false),
            LaunchInterceptForTests = (exe, args) =>
            {
                if (IsComfy(exe))
                {
                    lock (order) order.Add("comfy");
                    comfyStarted.Set();
                    return new LocalAiProcessManager.RuntimeResult(true, "comfy ready");
                }
                lock (order) order.Add("assistant-begin");
                // The assistant's "model load" does not finish until ComfyUI has begun on another thread.
                comfySeenDuringAssistantLoad = comfyStarted.Wait(TimeSpan.FromSeconds(15));
                lock (order) order.Add("assistant-end");
                return new LocalAiProcessManager.RuntimeResult(true, "assistant ready");
            }
        };
        var stages = new List<(string Stage, string? Detail)>();
        using IDisposable capture = MediaSwapTimings.CaptureForTests((stage, _, detail) => { lock (stages) stages.Add((stage, detail)); });

        LocalAiProcessManager.RuntimeResult result = await manager.StartMediaAsync(CancellationToken.None);

        Assert.True(result.Success, result.Message);
        Assert.True(comfySeenDuringAssistantLoad, "ComfyUI must be launched while the assistant launch is still in progress.");
        Assert.Equal(["assistant-begin", "comfy", "assistant-end"], order);
        Assert.Contains(stages, s => s.Stage == "media-runtime-start" && s.Detail!.Contains("overlapped", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_failed_assistant_reports_its_failure_and_cleans_up_the_comfy_that_started_beside_it()
    {
        using var comfyStarted = new ManualResetEventSlim();
        int comfyLaunches = 0;
        var manager = new LocalAiProcessManager(16384)
        {
            TextHealthProbeForTests = _ => Task.FromResult(false),
            MediaHealthProbeForTests = _ => Task.FromResult(false),
            LaunchInterceptForTests = (exe, args) =>
            {
                if (IsComfy(exe))
                {
                    Interlocked.Increment(ref comfyLaunches);
                    comfyStarted.Set();
                    return new LocalAiProcessManager.RuntimeResult(true, "comfy ready");
                }
                comfyStarted.Wait(TimeSpan.FromSeconds(15));
                return new LocalAiProcessManager.RuntimeResult(false, "assistant exploded");
            }
        };

        LocalAiProcessManager.RuntimeResult result = await manager.StartMediaAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("assistant exploded", result.Message, StringComparison.Ordinal);
        Assert.Equal(1, comfyLaunches);
    }

    [Fact]
    public async Task A_cancelled_media_start_does_not_leave_the_overlapped_comfy_attempt_running()
    {
        using var cancellation = new CancellationTokenSource();
        using var comfyStarted = new ManualResetEventSlim();
        var manager = new LocalAiProcessManager(16384)
        {
            TextHealthProbeForTests = _ => Task.FromResult(false),
            MediaHealthProbeForTests = _ => Task.FromResult(false),
            LaunchInterceptForTests = (exe, args) =>
            {
                if (IsComfy(exe)) { comfyStarted.Set(); return new LocalAiProcessManager.RuntimeResult(true, "comfy ready"); }
                comfyStarted.Wait(TimeSpan.FromSeconds(15));
                cancellation.Cancel();
                cancellation.Token.ThrowIfCancellationRequested();
                return new LocalAiProcessManager.RuntimeResult(true, "unreachable");
            }
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => manager.StartMediaAsync(cancellation.Token));

        // The start gate is free again: a following operation is not blocked behind an orphaned overlapped launch.
        await manager.WaitForIdleAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);
    }

    [Fact]
    public async Task A_healthy_comfy_is_reused_and_never_relaunched()
    {
        int comfyLaunches = 0;
        var manager = new LocalAiProcessManager(16384)
        {
            TextHealthProbeForTests = _ => Task.FromResult(false),
            MediaHealthProbeForTests = _ => Task.FromResult(true),
            LaunchInterceptForTests = (exe, args) =>
            {
                if (IsComfy(exe)) Interlocked.Increment(ref comfyLaunches);
                return new LocalAiProcessManager.RuntimeResult(true, "ok");
            }
        };

        LocalAiProcessManager.RuntimeResult result = await manager.StartMediaAsync(CancellationToken.None);

        Assert.True(result.Success, result.Message);
        Assert.Equal(0, comfyLaunches);
    }

    [Fact]
    public async Task Gaming_mode_starts_nothing()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(LocalAiPaths.GameMarkerFile)!);
        File.WriteAllText(LocalAiPaths.GameMarkerFile, "{}");
        int launches = 0;
        var manager = new LocalAiProcessManager(16384)
        {
            LaunchInterceptForTests = (exe, args) => { Interlocked.Increment(ref launches); return new LocalAiProcessManager.RuntimeResult(true, "ok"); }
        };

        Assert.False((await manager.StartMediaAsync(CancellationToken.None)).Success);
        Assert.False((await manager.PrewarmMediaRuntimeAsync(_ => Task.FromResult<string?>(null), CancellationToken.None)).Success);
        Assert.Equal(0, launches);
    }

    [Fact]
    public async Task Prewarm_launches_only_comfy_and_never_touches_the_text_runtime()
    {
        var launched = new List<string>();
        var manager = new LocalAiProcessManager(16384)
        {
            MediaHealthProbeForTests = _ => Task.FromResult(false),
            LaunchInterceptForTests = (exe, args) => { lock (launched) launched.Add(Path.GetFileNameWithoutExtension(exe)); return new LocalAiProcessManager.RuntimeResult(true, "ok"); }
        };

        LocalAiProcessManager.RuntimeResult result = await manager.PrewarmMediaRuntimeAsync(_ => Task.FromResult<string?>(null), CancellationToken.None);

        Assert.True(result.Success, result.Message);
        Assert.Equal(["python"], launched);
    }

    [Fact]
    public async Task Prewarm_honours_an_admission_refusal_and_reports_it_unchanged()
    {
        int launches = 0;
        var manager = new LocalAiProcessManager(16384)
        {
            MediaHealthProbeForTests = _ => Task.FromResult(false),
            LaunchInterceptForTests = (exe, args) => { Interlocked.Increment(ref launches); return new LocalAiProcessManager.RuntimeResult(true, "ok"); }
        };

        LocalAiProcessManager.RuntimeResult result = await manager.PrewarmMediaRuntimeAsync(
            _ => Task.FromResult<string?>("Not prewarmed: only 900 MiB of VRAM is free."), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("Not prewarmed: only 900 MiB of VRAM is free.", result.Message);
        Assert.Equal(0, launches);
    }

    [Fact]
    public async Task Prewarm_of_a_runtime_that_is_already_up_launches_nothing()
    {
        int launches = 0;
        var manager = new LocalAiProcessManager(16384)
        {
            MediaHealthProbeForTests = _ => Task.FromResult(true),
            LaunchInterceptForTests = (exe, args) => { Interlocked.Increment(ref launches); return new LocalAiProcessManager.RuntimeResult(true, "ok"); }
        };

        LocalAiProcessManager.RuntimeResult result = await manager.PrewarmMediaRuntimeAsync(
            _ => throw new InvalidOperationException("admission must not run for a runtime that is already warm"), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(0, launches);
    }

    private static GpuAdapterStatus Discrete(uint vendorId, long dedicatedBytes) => new()
    {
        Index = 0,
        Name = "Discrete test adapter",
        VendorId = vendorId,
        DeviceId = 0x1,
        DedicatedVideoMemoryBytes = dedicatedBytes,
        SharedSystemMemoryBytes = 16L * 1024 * 1024 * 1024
    };

    private static void WriteRestoreFiles()
    {
        WriteFile(LocalAiPaths.StandardModel);
        WriteFile(LocalAiPaths.StandardVision!);
        WriteFile(Path.Combine(LocalAiPaths.RuntimeCudaPath, LocalAiPaths.LlamaServerExecutable));
        WriteFile(Path.Combine(LocalAiPaths.RuntimeVulkanPath, LocalAiPaths.LlamaServerExecutable));
    }

    [Fact]
    public async Task A_restored_text_model_gets_one_bounded_warmup_after_it_is_healthy()
    {
        WriteRestoreFiles();
        int warmups = 0;
        var manager = new LocalAiProcessManager(16384)
        {
            InventoryProbe = () => GpuInventoryFixtures.Inventory(Discrete(0x10de, 16L * 1024 * 1024 * 1024)),
            TextHealthProbeForTests = _ => Task.FromResult(false),
            LaunchInterceptForTests = (exe, args) => new LocalAiProcessManager.RuntimeResult(true, "ok"),
            TextWarmupForTests = _ => { Interlocked.Increment(ref warmups); return Task.CompletedTask; }
        };

        LocalAiProcessManager.RuntimeResult result = await manager.RestoreTextProfileAsync(CancellationToken.None);

        Assert.True(result.Success, result.Message);
        Assert.Equal(1, warmups);
    }

    [Fact]
    public async Task A_failed_warmup_never_fails_the_restore()
    {
        WriteRestoreFiles();
        var manager = new LocalAiProcessManager(16384)
        {
            InventoryProbe = () => GpuInventoryFixtures.Inventory(Discrete(0x10de, 16L * 1024 * 1024 * 1024)),
            TextHealthProbeForTests = _ => Task.FromResult(false),
            LaunchInterceptForTests = (exe, args) => new LocalAiProcessManager.RuntimeResult(true, "ok"),
            TextWarmupForTests = _ => throw new HttpRequestException("connection refused")
        };

        LocalAiProcessManager.RuntimeResult result = await manager.RestoreTextProfileAsync(CancellationToken.None);

        Assert.True(result.Success, result.Message);
    }
}
