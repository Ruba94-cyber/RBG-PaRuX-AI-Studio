using PaRuX.Contracts;
using Xunit;
using static PaRuX.Server.Tests.LaunchPlannerFixtures;

namespace PaRuX.Server.Tests;

/// <summary>How the launch plan joins the existing optimizer, argument builder and recovery ladder.</summary>
public sealed class LaunchPlanIntegrationTests
{
    private static string MissingModel() =>
        Path.Combine(Path.GetTempPath(), "parux-plan-missing-" + Guid.NewGuid().ToString("N") + ".gguf");

    private static GpuInventoryStatus NoGpu() => new() { Available = true, Source = "fake" };

    // ---------------------------------------------------------------- regression: no metadata, no change

    [Fact]
    public void The_default_profile_builds_exactly_the_arguments_it_always_did()
    {
        List<string> args = RuntimeLaunchOptimizer.BuildRuntimeArguments(Baseline(context: 8192), hasCuda: true);

        Assert.Equal(
        [
            "--ctx-size", "8192", "--batch-size", "512", "--ubatch-size", "128", "--cache-type-k", "q8_0", "--cache-type-v", "q8_0",
            "--flash-attn", "on", "--gpu-layers", "all", "--fit", "on", "--fit-target", "1024", "--cont-batching",
            "--slot-prompt-similarity", "0.10", "--reasoning-effort", "default", "--reasoning-budget", "-1", "--load-mode", "auto",
            "--prio", "0", "--poll", "50", "--parallel", "1", "--split-mode", "none", "--main-gpu", "0"
        ], args);
    }

    [Fact]
    public async Task A_launch_without_readable_GGUF_metadata_keeps_its_arguments_and_records_no_plan()
    {
        string path = MissingModel();
        RuntimeLaunchOptimization planned = await RuntimeLaunchOptimizer.ResolveAsync(
            path, "chat", hasCuda: false, 512, 128, "q8_0", CancellationToken.None, NoGpu());
        RuntimeLaunchOptimization unplanned = await RuntimeLaunchOptimizer.ResolveAsync(
            path, "chat", hasCuda: false, 512, 128, "q8_0", CancellationToken.None, NoGpu(), planLaunch: false);

        Assert.Equal(
            RuntimeLaunchOptimizer.BuildRuntimeArguments(unplanned.Profile, hasCuda: false),
            RuntimeLaunchOptimizer.BuildRuntimeArguments(planned.Profile, hasCuda: false));
        Assert.Null(planned.Plan);
        Assert.Null(planned.BaselineProfile);
        Assert.Equal(unplanned.Source, planned.Source);
        Assert.DoesNotContain("--n-cpu-moe", RuntimeLaunchOptimizer.BuildRuntimeArguments(planned.Profile, hasCuda: false));
    }

    [Fact]
    public async Task A_GGUF_file_that_is_not_a_GGUF_is_not_planned()
    {
        string path = Path.Combine(Path.GetTempPath(), "parux-plan-notgguf-" + Guid.NewGuid().ToString("N") + ".gguf");
        await File.WriteAllTextAsync(path, "this is not a gguf header");
        try
        {
            RuntimeLaunchOptimization result = await RuntimeLaunchOptimizer.ResolveAsync(
                path, "chat", hasCuda: false, 512, 128, "q8_0", CancellationToken.None, NoGpu());

            Assert.Null(result.Plan);
        }
        finally { File.Delete(path); }
    }

    // ---------------------------------------------------------------- the optimizer applies a plan

    [Fact]
    public async Task With_metadata_the_optimizer_returns_the_planned_profile_the_plan_and_the_pre_plan_baseline()
    {
        var context = new LaunchPlanContext(Rtx5080Egpu32Gb() with { Backend = "text-vulkan" }, B10549, Moe30BA3BQ4());

        RuntimeLaunchOptimization result = await RuntimeLaunchOptimizer.ResolveAsync(
            MissingModel(), "chat", hasCuda: false, 512, 128, "q8_0", CancellationToken.None, NoGpu(), planContext: context);

        Assert.NotNull(result.Plan);
        Assert.True(result.Plan!.Applied);
        Assert.EndsWith("+launch-plan", result.Source);
        Assert.True(result.Profile.CpuMoeLayers > 0);
        Assert.NotNull(result.BaselineProfile);
        Assert.Equal(0, result.BaselineProfile!.CpuMoeLayers);
        Assert.Contains("--n-cpu-moe", RuntimeLaunchOptimizer.BuildRuntimeArguments(result.Profile, hasCuda: false));
        Assert.DoesNotContain("--n-cpu-moe", RuntimeLaunchOptimizer.BuildRuntimeArguments(result.BaselineProfile, hasCuda: false));
    }

    [Fact]
    public async Task The_hot_pool_opt_out_never_plans()
    {
        var context = new LaunchPlanContext(Rtx5080Egpu32Gb(), B10549, Moe30BA3BQ4());

        RuntimeLaunchOptimization result = await RuntimeLaunchOptimizer.ResolveAsync(
            MissingModel(), "chat", hasCuda: false, 512, 128, "q8_0", CancellationToken.None, NoGpu(), planLaunch: false, planContext: context);

        Assert.Null(result.Plan);
        Assert.Equal(0, result.Profile.CpuMoeLayers);
    }

    // ---------------------------------------------------------------- CPU-only fallback strips MoE placement

    [Fact]
    public void The_CPU_fallback_drops_expert_placement_flags()
    {
        string[] args = ["--gpu-layers", "all", "--n-cpu-moe", "12", "--override-tensor", "x=CPU", "--threads", "6"];

        List<string> cpu = RuntimeLaunchOptimizer.ToCpuFallbackArguments(args);

        Assert.DoesNotContain("--n-cpu-moe", cpu);
        Assert.DoesNotContain("--override-tensor", cpu);
        Assert.Equal("6", ValueOf(cpu, "--threads"));
    }

    // ---------------------------------------------------------------- ladder: known-good baseline rung

    [Fact]
    public void An_unplanned_launch_has_exactly_the_rungs_it_had_before()
    {
        string[] primary = ["--flash-attn", "on", "--gpu-layers", "all"];

        IReadOnlyList<TextLaunchLadder.Rung> rungs = TextLaunchLadder.Plan(TextLaunchLadder.Engine.Cuda, false, primary, vulkanAvailable: true);

        Assert.Equal(
            [TextLaunchLadder.Step.Primary, TextLaunchLadder.Step.DropFlashAttention, TextLaunchLadder.Step.ReduceOffload,
             TextLaunchLadder.Step.VulkanEngine, TextLaunchLadder.Step.CpuOnly],
            rungs.Select(r => r.Step));
    }

    [Fact]
    public void A_planned_launch_retries_the_pre_plan_arguments_right_after_the_plan_fails()
    {
        string[] planned = ["--flash-attn", "on", "--gpu-layers", "all", "--n-cpu-moe", "16"];
        string[] baseline = ["--flash-attn", "on", "--gpu-layers", "all"];

        IReadOnlyList<TextLaunchLadder.Rung> rungs = TextLaunchLadder.Plan(
            TextLaunchLadder.Engine.Cuda, false, planned, vulkanAvailable: true, hasPlannedLaunch: true);

        Assert.Equal(TextLaunchLadder.Step.Primary, rungs[0].Step);
        Assert.Equal(TextLaunchLadder.Step.UnplannedBaseline, rungs[1].Step);
        Assert.InRange(rungs.Count, 2, TextLaunchLadder.MaxRungs);
        Assert.Contains("--n-cpu-moe", TextLaunchLadder.ArgumentsFor(rungs[0], planned, baseline));
        Assert.Equal(baseline, TextLaunchLadder.ArgumentsFor(rungs[1], planned, baseline));
        // Every later rung derives from the known-good arguments, never from the failed plan.
        foreach (TextLaunchLadder.Rung rung in rungs.Skip(2))
            Assert.DoesNotContain("--n-cpu-moe", TextLaunchLadder.ArgumentsFor(rung, planned, baseline));
    }

    [Fact]
    public void A_planned_CPU_only_launch_also_gets_the_baseline_retry_and_stays_bounded()
    {
        IReadOnlyList<TextLaunchLadder.Rung> rungs = TextLaunchLadder.Plan(
            TextLaunchLadder.Engine.Vulkan, cpuOnly: true, ["--threads", "3"], vulkanAvailable: false, hasPlannedLaunch: true);

        Assert.Equal(2, rungs.Count);
        Assert.Equal(TextLaunchLadder.Step.UnplannedBaseline, rungs[1].Step);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void The_ladder_never_exceeds_its_bound(bool cuda, bool vulkan)
    {
        IReadOnlyList<TextLaunchLadder.Rung> rungs = TextLaunchLadder.Plan(
            cuda ? TextLaunchLadder.Engine.Cuda : TextLaunchLadder.Engine.Vulkan, false,
            ["--flash-attn", "on", "--gpu-layers", "all"], vulkan, hasPlannedLaunch: true);

        Assert.InRange(rungs.Count, 2, TextLaunchLadder.MaxRungs);
    }
}
