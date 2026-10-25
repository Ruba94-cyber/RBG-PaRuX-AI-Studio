using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// The text launch ladder. The planner and argument transforms are pure. The start-path tests drive StartTextAsync through the
/// launch seam, so the rungs it attempts, their arguments and the switch-back to a previously working runtime are the production code.
/// </summary>
public sealed class TextLaunchLadderTests : IDisposable
{
    private static readonly string[] CudaArguments =
        ["--ctx-size", "4096", "--flash-attn", "on", "--gpu-layers", "all", "--device", "CUDA0"];

    private readonly string _work = Path.Combine(Path.GetTempPath(), "parux-ladder-" + Guid.NewGuid().ToString("N"));
    private readonly IDisposable _root;
    private readonly IDisposable _state;

    public TextLaunchLadderTests()
    {
        _root = LocalAiPaths.UseRootForTests(Path.Combine(_work, "localai"));
        _state = LocalAiPaths.UseStateDirForTests(Path.Combine(_work, "state"));
    }

    public void Dispose()
    {
        _state.Dispose();
        _root.Dispose();
        try { Directory.Delete(_work, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void A_cuda_launch_with_flash_attention_and_offload_climbs_the_full_ladder_in_order()
    {
        IReadOnlyList<TextLaunchLadder.Rung> rungs = TextLaunchLadder.Plan(TextLaunchLadder.Engine.Cuda, false, CudaArguments, vulkanAvailable: true);

        Assert.Equal(
            new[]
            {
                TextLaunchLadder.Step.Primary, TextLaunchLadder.Step.DropFlashAttention, TextLaunchLadder.Step.ReduceOffload,
                TextLaunchLadder.Step.VulkanEngine, TextLaunchLadder.Step.CpuOnly
            },
            rungs.Select(rung => rung.Step).ToArray());
        Assert.Equal(TextLaunchLadder.Engine.Vulkan, rungs[3].Engine);
    }

    [Fact]
    public void Without_an_installed_vulkan_build_the_cuda_ladder_goes_from_offload_straight_to_cpu()
    {
        IReadOnlyList<TextLaunchLadder.Rung> rungs = TextLaunchLadder.Plan(TextLaunchLadder.Engine.Cuda, false, CudaArguments, vulkanAvailable: false);

        Assert.Equal(
            new[] { TextLaunchLadder.Step.Primary, TextLaunchLadder.Step.DropFlashAttention, TextLaunchLadder.Step.ReduceOffload, TextLaunchLadder.Step.CpuOnly },
            rungs.Select(rung => rung.Step).ToArray());
    }

    [Fact]
    public void A_profile_already_without_flash_attention_or_offload_skips_those_rungs()
    {
        string[] arguments = ["--flash-attn", "off", "--gpu-layers", "0"];

        IReadOnlyList<TextLaunchLadder.Rung> rungs = TextLaunchLadder.Plan(TextLaunchLadder.Engine.Cuda, false, arguments, vulkanAvailable: true);

        Assert.Equal(
            new[] { TextLaunchLadder.Step.Primary, TextLaunchLadder.Step.VulkanEngine, TextLaunchLadder.Step.CpuOnly },
            rungs.Select(rung => rung.Step).ToArray());
    }

    [Fact]
    public void A_vulkan_primary_never_climbs_to_a_second_vulkan_engine_rung()
    {
        IReadOnlyList<TextLaunchLadder.Rung> rungs = TextLaunchLadder.Plan(TextLaunchLadder.Engine.Vulkan, false, CudaArguments, vulkanAvailable: true);

        Assert.Equal(
            new[] { TextLaunchLadder.Step.Primary, TextLaunchLadder.Step.DropFlashAttention, TextLaunchLadder.Step.ReduceOffload, TextLaunchLadder.Step.CpuOnly },
            rungs.Select(rung => rung.Step).ToArray());
    }

    [Fact]
    public void A_cpu_only_launch_is_its_own_single_attempt()
    {
        IReadOnlyList<TextLaunchLadder.Rung> rungs = TextLaunchLadder.Plan(TextLaunchLadder.Engine.Vulkan, true, CudaArguments, vulkanAvailable: true);

        TextLaunchLadder.Rung only = Assert.Single(rungs);
        Assert.Equal(TextLaunchLadder.Step.Primary, only.Step);
        Assert.Equal(TextLaunchLadder.Engine.Cpu, only.Engine);
    }

    [Theory]
    [InlineData(0, true, true, false)]
    [InlineData(0, true, false, false)]
    [InlineData(0, false, true, false)]
    [InlineData(0, false, false, false)]
    [InlineData(1, true, true, false)]
    [InlineData(1, false, false, false)]
    [InlineData(1, true, true, true)]
    [InlineData(0, true, true, true)]
    public void Every_plan_is_bounded_and_ends_on_cpu(int primaryEngine, bool flashOn, bool vulkanAvailable, bool cpuOnly)
    {
        // 0 = CUDA primary, 1 = Vulkan primary. The enum is internal, so the theory carries the index.
        var engine = (TextLaunchLadder.Engine)(primaryEngine == 0 ? TextLaunchLadder.Engine.Cuda : TextLaunchLadder.Engine.Vulkan);
        string[] arguments = flashOn ? CudaArguments : ["--flash-attn", "off", "--gpu-layers", "0"];

        IReadOnlyList<TextLaunchLadder.Rung> rungs = TextLaunchLadder.Plan(engine, cpuOnly, arguments, vulkanAvailable);

        Assert.InRange(rungs.Count, 1, TextLaunchLadder.MaxRungs);
        Assert.Equal(TextLaunchLadder.Engine.Cpu, rungs[^1].Engine);
    }

    [Fact]
    public void The_drop_flash_attention_rung_turns_flash_off_and_changes_nothing_else()
    {
        var primary = CudaArguments.ToList();

        List<string> result = TextLaunchLadder.ArgumentsFor(Rung(TextLaunchLadder.Step.DropFlashAttention, TextLaunchLadder.Engine.Cuda), primary);

        Assert.Equal("off", ValueOf(result, "--flash-attn"));
        Assert.Equal(primary.Count, result.Count);
        Assert.Equal("4096", ValueOf(result, "--ctx-size"));
        Assert.Equal("on", ValueOf(primary, "--flash-attn"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Rungs_that_turn_flash_attention_off_also_unquantize_the_v_cache(int stepIndex)
    {
        TextLaunchLadder.Step step = stepIndex switch
        {
            0 => TextLaunchLadder.Step.DropFlashAttention,
            1 => TextLaunchLadder.Step.ReduceOffload,
            _ => TextLaunchLadder.Step.VulkanEngine
        };
        // llama.cpp refuses "quantized V cache requires flash_attn to be enabled", so a rung that only
        // flipped --flash-attn off could never start with the default q8_0 cache.
        var primary = new List<string> { "--flash-attn", "on", "--cache-type-k", "q8_0", "--cache-type-v", "q8_0", "--gpu-layers", "all" };

        List<string> result = TextLaunchLadder.ArgumentsFor(Rung(step, TextLaunchLadder.Engine.Cuda), primary);

        Assert.Equal("off", ValueOf(result, "--flash-attn"));
        Assert.Equal("f16", ValueOf(result, "--cache-type-v"));
        Assert.Equal("q8_0", ValueOf(result, "--cache-type-k"));
        Assert.Equal("q8_0", ValueOf(primary, "--cache-type-v"));
    }

    [Fact]
    public void A_cuda_primary_without_an_installed_cuda_runtime_skips_straight_to_vulkan_then_cpu()
    {
        IReadOnlyList<TextLaunchLadder.Rung> rungs = TextLaunchLadder.Plan(
            TextLaunchLadder.Engine.Cuda, false, CudaArguments, vulkanAvailable: true, primaryAvailable: false);

        Assert.Equal(
            new[] { TextLaunchLadder.Step.VulkanKeepFlash, TextLaunchLadder.Step.VulkanEngine, TextLaunchLadder.Step.CpuOnly },
            rungs.Select(rung => rung.Step).ToArray());
        List<string> first = TextLaunchLadder.ArgumentsFor(rungs[0], CudaArguments);
        Assert.Equal("on", ValueOf(first, "--flash-attn"));
        Assert.Equal("Vulkan0", ValueOf(first, "--device"));
    }

    [Fact]
    public void A_cuda_primary_with_nothing_installed_but_cpu_still_ends_on_cpu()
    {
        IReadOnlyList<TextLaunchLadder.Rung> rungs = TextLaunchLadder.Plan(
            TextLaunchLadder.Engine.Cuda, false, CudaArguments, vulkanAvailable: false, primaryAvailable: false);

        TextLaunchLadder.Rung only = Assert.Single(rungs);
        Assert.Equal(TextLaunchLadder.Engine.Cpu, only.Engine);
    }

    [Theory]
    [InlineData("all", "24")]
    [InlineData("40", "20")]
    [InlineData("1", "1")]
    [InlineData("0", "0")]
    public void The_reduced_offload_rung_halves_an_explicit_layer_count_and_caps_all_at_twenty_four(string before, string after)
    {
        var primary = new List<string> { "--flash-attn", "on", "--gpu-layers", before };

        List<string> result = TextLaunchLadder.ArgumentsFor(Rung(TextLaunchLadder.Step.ReduceOffload, TextLaunchLadder.Engine.Cuda), primary);

        Assert.Equal(after, ValueOf(result, "--gpu-layers"));
        Assert.Equal("off", ValueOf(result, "--flash-attn"));
    }

    [Fact]
    public void The_vulkan_rung_pins_device_zero_and_turns_flash_attention_off()
    {
        List<string> result = TextLaunchLadder.ArgumentsFor(Rung(TextLaunchLadder.Step.VulkanEngine, TextLaunchLadder.Engine.Vulkan), CudaArguments.ToList());

        Assert.Equal("Vulkan0", ValueOf(result, "--device"));
        Assert.Equal("off", ValueOf(result, "--flash-attn"));
        Assert.Single(result.Where(argument => argument == "--device"));
    }

    [Fact]
    public void The_cpu_rung_drops_accelerator_placement_and_keeps_every_layer_on_the_cpu()
    {
        List<string> result = TextLaunchLadder.ArgumentsFor(Rung(TextLaunchLadder.Step.CpuOnly, TextLaunchLadder.Engine.Cpu), CudaArguments.ToList());

        Assert.DoesNotContain("--device", result);
        Assert.Equal("0", ValueOf(result, "--gpu-layers"));
    }

    [Fact]
    public void Planning_never_mutates_the_primary_argument_list()
    {
        var primary = CudaArguments.ToList();
        var before = primary.ToArray();

        IReadOnlyList<TextLaunchLadder.Rung> rungs = TextLaunchLadder.Plan(TextLaunchLadder.Engine.Cuda, false, primary, vulkanAvailable: true);
        foreach (TextLaunchLadder.Rung rung in rungs) TextLaunchLadder.ArgumentsFor(rung, primary);

        Assert.Equal(before, primary);
    }

    [Fact]
    public async Task A_failing_cuda_start_walks_the_bounded_ladder_and_records_every_rung()
    {
        WriteFile(Path.Combine(LocalAiPaths.RuntimeCudaPath, LocalAiPaths.LlamaServerExecutable));
        WriteFile(Path.Combine(LocalAiPaths.RuntimeVulkanPath, LocalAiPaths.LlamaServerExecutable));
        WriteFile(LocalAiPaths.StandardModel);
        WriteFile(LocalAiPaths.StandardVision!);

        var launches = new List<(string Exe, IReadOnlyList<string> Args)>();
        var manager = new LocalAiProcessManager(16000)
        {
            InventoryProbe = () => GpuInventoryFixtures.Inventory(),
            LaunchInterceptForTests = (exe, arguments) =>
            {
                launches.Add((exe, arguments));
                return new LocalAiProcessManager.RuntimeResult(false, "intercepted failure");
            }
        };

        LocalAiProcessManager.RuntimeResult result = await manager.StartTextAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(5, launches.Count);
        Assert.Equal(5, manager.LastTextLaunchTrace.Count);
        Assert.StartsWith(LocalAiPaths.RuntimeCudaPath, launches[0].Exe, StringComparison.Ordinal);
        Assert.Equal("off", ValueOf(launches[1].Args, "--flash-attn"));
        Assert.Equal(TextLaunchLadder.ReducedGpuLayers(ValueOf(launches[0].Args, "--gpu-layers")), ValueOf(launches[2].Args, "--gpu-layers"));
        Assert.StartsWith(LocalAiPaths.RuntimeVulkanPath, launches[3].Exe, StringComparison.Ordinal);
        Assert.Equal("Vulkan0", ValueOf(launches[3].Args, "--device"));
        Assert.Equal("0", ValueOf(launches[4].Args, "--gpu-layers"));
        Assert.StartsWith("CUDA: failed", manager.LastTextLaunchTrace[0], StringComparison.Ordinal);
        Assert.StartsWith("CPU: failed", manager.LastTextLaunchTrace[4], StringComparison.Ordinal);
        Assert.Contains("launch ladder", result.Message);
    }

    [Fact]
    public async Task A_game_that_starts_during_a_failed_rung_ends_the_ladder_before_another_runtime_is_launched()
    {
        WriteFile(Path.Combine(LocalAiPaths.RuntimeCudaPath, LocalAiPaths.LlamaServerExecutable));
        WriteFile(Path.Combine(LocalAiPaths.RuntimeVulkanPath, LocalAiPaths.LlamaServerExecutable));
        WriteFile(LocalAiPaths.StandardModel);
        WriteFile(LocalAiPaths.StandardVision!);

        var launches = new List<string>();
        var manager = new LocalAiProcessManager(16000)
        {
            InventoryProbe = () => GpuInventoryFixtures.Inventory(),
            LaunchInterceptForTests = (exe, _) =>
            {
                launches.Add(exe);
                // GamingActive is the game marker file, so this is the state a game start leaves behind during a load.
                WriteFile(LocalAiPaths.GameMarkerFile);
                return new LocalAiProcessManager.RuntimeResult(false, "intercepted failure");
            }
        };

        LocalAiProcessManager.RuntimeResult result = await manager.StartTextAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.Single(launches);
    }

    [Fact]
    public async Task A_switch_whose_ladder_fails_restores_the_runtime_that_was_working()
    {
        WriteFile(Path.Combine(LocalAiPaths.RuntimeVulkanPath, LocalAiPaths.LlamaServerExecutable));
        WriteFile(LocalAiPaths.SmallModel);
        string otherModel = WriteFile(Path.Combine(_work, "other-model.gguf"));
        string workingModel = LocalAiPaths.SmallModel;

        var launches = new List<IReadOnlyList<string>>();
        bool healthy = false;
        var manager = new LocalAiProcessManager(0)
        {
            InventoryProbe = () => GpuInventoryFixtures.Inventory(GpuInventoryFixtures.IntegratedAmd()),
            TextHealthProbeForTests = _ => Task.FromResult(healthy),
            LaunchInterceptForTests = (_, arguments) =>
            {
                launches.Add(arguments);
                return ValueOf(arguments, "--model") == workingModel
                    ? new LocalAiProcessManager.RuntimeResult(true, "Started: working runtime")
                    : new LocalAiProcessManager.RuntimeResult(false, "intercepted failure");
            }
        };

        LocalAiProcessManager.RuntimeResult first = await manager.StartTextAsync(CancellationToken.None);
        Assert.True(first.Success, first.Message);

        healthy = true;
        launches.Clear();
        LocalAiProcessManager.RuntimeResult switched = await manager.StartTextAsync(CancellationToken.None, otherModel);

        Assert.False(switched.Success);
        Assert.Contains("was restored", switched.Message);
        Assert.Equal(workingModel, ValueOf(launches[^1], "--model"));
        Assert.Contains(manager.LastTextLaunchTrace, entry => entry.StartsWith("restore ", StringComparison.Ordinal) && entry.EndsWith(": ready", StringComparison.Ordinal));
    }

    private static TextLaunchLadder.Rung Rung(TextLaunchLadder.Step step, TextLaunchLadder.Engine engine) =>
        new(step, engine, step.ToString(), string.Empty);

    private static string? ValueOf(IReadOnlyList<string> arguments, string flag)
    {
        int index = arguments.ToList().IndexOf(flag);
        return index < 0 || index + 1 >= arguments.Count ? null : arguments[index + 1];
    }

    /// <summary>Creates a small placeholder file (and its folder) so the start path finds the runtime or model it checks for.</summary>
    private static string WriteFile(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[16]);
        return path;
    }
}
