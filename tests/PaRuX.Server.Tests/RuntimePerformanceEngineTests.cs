using System.Diagnostics;
using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class RuntimePerformanceEngineTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parux-performance-engine-" + Guid.NewGuid().ToString("N"));

    public RuntimePerformanceEngineTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void Full_profile_copy_preserves_topology_and_speculation()
    {
        var source = new LlamaPerformanceProfileDto
        {
            Id = "winner",
            DisplayName = "Winner",
            BatchSize = 2048,
            UBatchSize = 512,
            CacheTypeK = "f16",
            CacheTypeV = "f16",
            FlashAttention = "on",
            GpuLayers = -1,
            SplitMode = "layer",
            MainGpu = 1,
            Device = "CUDA1,CUDA0",
            TensorSplit = "3,1",
            EnableCudaP2P = true,
            ContextSize = 32768,
            FitTargetMib = 1536,
            Threads = 8,
            ThreadsBatch = 12,
            SpeculativeType = "ngram-simple",
            SpeculativeDraftMax = 64,
            SpeculativeDraftModelPath = "draft.gguf",
            SpeculativeDraftDevice = "CUDA0",
            BackendSampling = true
        };

        LlamaPerformanceProfileDto copy = RuntimePerformanceProfileCopy.Copy(source);

        Assert.Equal(source.Id, copy.Id);
        Assert.Equal(source.Device, copy.Device);
        Assert.Equal(source.TensorSplit, copy.TensorSplit);
        Assert.Equal(source.EnableCudaP2P, copy.EnableCudaP2P);
        Assert.Equal(source.SpeculativeType, copy.SpeculativeType);
        Assert.Equal(source.SpeculativeDraftMax, copy.SpeculativeDraftMax);
        Assert.Equal(source.SpeculativeDraftModelPath, copy.SpeculativeDraftModelPath);
        Assert.Equal(source.SpeculativeDraftDevice, copy.SpeculativeDraftDevice);
        Assert.Equal(source.BackendSampling, copy.BackendSampling);
        Assert.Equal(RuntimePerformanceProfileCopy.Signature(source), RuntimePerformanceProfileCopy.Signature(copy));
    }

    [Fact]
    public void Server_arguments_emit_measured_single_gpu_and_speculative_controls()
    {
        var profile = new LlamaPerformanceProfileDto
        {
            BatchSize = 1024,
            UBatchSize = 256,
            CacheTypeK = "q8_0",
            CacheTypeV = "q8_0",
            FlashAttention = "on",
            GpuLayers = -1,
            SplitMode = "none",
            MainGpu = 1,
            Device = "CUDA1",
            ContextSize = 16384,
            FitTargetMib = 1024,
            SpeculativeType = "ngram-simple",
            SpeculativeDraftMax = 64,
            BackendSampling = true,
            EnableCudaP2P = true
        };

        List<string> args = RuntimeLaunchOptimizer.BuildRuntimeArguments(profile, hasCuda: true);

        AssertFlagValue(args, "--ctx-size", "16384");
        AssertFlagValue(args, "--batch-size", "1024");
        AssertFlagValue(args, "--ubatch-size", "256");
        AssertFlagValue(args, "--main-gpu", "1");
        AssertFlagValue(args, "--device", "CUDA1");
        AssertFlagValue(args, "--spec-type", "ngram-simple");
        AssertFlagValue(args, "--spec-draft-n-max", "64");
        Assert.Contains("--backend-sampling", args);

        var psi = new ProcessStartInfo();
        RuntimeLaunchOptimizer.ApplyEnvironment(psi, profile);
        Assert.Equal("1", psi.Environment["GGML_CUDA_P2P"]);
    }

    [Fact]
    public void Layer_split_emits_explicit_proportions_but_tensor_is_fail_closed_until_validated()
    {
        var layer = new LlamaPerformanceProfileDto
        {
            BatchSize = 512,
            UBatchSize = 128,
            SplitMode = "layer",
            Device = "CUDA0,CUDA1",
            TensorSplit = "16,8",
            ContextSize = 8192
        };
        List<string> args = RuntimeLaunchOptimizer.BuildRuntimeArguments(layer, true);
        AssertFlagValue(args, "--split-mode", "layer");
        AssertFlagValue(args, "--tensor-split", "16,8");

        layer.SplitMode = "tensor";
        InvalidDataException exception = Assert.Throws<InvalidDataException>(() =>
            RuntimeLaunchOptimizer.BuildRuntimeArguments(layer, true));
        Assert.Contains("dedicated validated topology", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Runtime_command_keeps_only_the_last_profile_specific_reasoning_value()
    {
        var args = new List<string>
        {
            "--ctx-size", "8192",
            "--reasoning-effort", "low",
            "--reasoning-budget", "768",
            "--reasoning-effort", "medium",
            "--reasoning-preserve"
        };

        LocalAiProcessManager.KeepLastValueFor(args, "--reasoning-effort");
        LocalAiProcessManager.KeepLastValueFor(args, "--reasoning-budget");

        Assert.Equal(1, args.Count(value => value == "--reasoning-effort"));
        AssertFlagValue(args, "--reasoning-effort", "medium");
        Assert.Equal(1, args.Count(value => value == "--reasoning-budget"));
    }

    [Theory]
    [InlineData("balanced")]
    [InlineData("turbo")]
    [InlineData("eco")]
    [InlineData("performance")]
    [InlineData("efficiency")]
    public void Performance_policy_persists_supported_modes(string requested)
    {
        string path = Path.Combine(_root, "policy.json");
        var store = new RuntimePerformancePolicyStore(path);
        string normalized = store.SetMode(requested);
        var reopened = new RuntimePerformancePolicyStore(path);
        Assert.Equal(normalized, reopened.GetMode());
    }

    [Fact]
    public void Performance_policy_rejects_unknown_mode()
    {
        var store = new RuntimePerformancePolicyStore(Path.Combine(_root, "bad-policy.json"));
        Assert.Throws<InvalidDataException>(() => store.SetMode("overclock-everything"));
    }

    [Fact]
    public void Speculative_winner_needs_real_decode_gain_and_bounded_ttft_regression()
    {
        RuntimeOptimizationServerResult baseline = Candidate("none", decode: 50, prompt: 1000, ttft: 300, watts: 200);
        RuntimeOptimizationServerResult weakSpec = Candidate("ngram-simple", decode: 50.5, prompt: 1000, ttft: 280, watts: 200);
        RuntimeOptimizationService.Score(new[] { baseline, weakSpec }, "turbo");
        RuntimeOptimizationServerResult winner = RuntimeOptimizationService.ChooseWinner(
            new List<RuntimeOptimizationServerResult> { baseline, weakSpec }, baseline);
        Assert.Same(baseline, winner);

        RuntimeOptimizationServerResult goodSpec = Candidate("ngram-simple", decode: 58, prompt: 1000, ttft: 310, watts: 205);
        RuntimeOptimizationService.Score(new[] { baseline, goodSpec }, "turbo");
        winner = RuntimeOptimizationService.ChooseWinner(
            new List<RuntimeOptimizationServerResult> { baseline, goodSpec }, baseline);
        Assert.Same(goodSpec, winner);
    }

    [Fact]
    public void Multi_gpu_needs_material_measured_gain_before_promotion()
    {
        RuntimeOptimizationServerResult single = Candidate("none", decode: 50, prompt: 1000, ttft: 300, watts: 190, device: "CUDA0");
        RuntimeOptimizationServerResult noisyMulti = Candidate("none", decode: 50.8, prompt: 1010, ttft: 298, watts: 285, device: "CUDA0,CUDA1", splitMode: "layer");
        RuntimeOptimizationService.Score(new[] { single, noisyMulti }, "turbo");

        RuntimeOptimizationServerResult winner = RuntimeOptimizationPolicy.ChooseOrdinaryTopologyWinner(
            new[] { single, noisyMulti }, single);

        Assert.Same(single, winner);
        Assert.Equal(RuntimeOptimizationPolicy.RejectedTopologyStatus, noisyMulti.Status);
    }

    [Fact]
    public void Rejected_multi_gpu_cannot_reenter_final_winner_selection()
    {
        RuntimeOptimizationServerResult single = Candidate("none", decode: 50, prompt: 1000, ttft: 300, watts: 190, device: "CUDA0");
        RuntimeOptimizationServerResult noisyMulti = Candidate("none", decode: 50.8, prompt: 1010, ttft: 298, watts: 285, device: "CUDA0,CUDA1", splitMode: "layer");
        RuntimeOptimizationService.Score(new[] { single, noisyMulti }, "turbo");
        RuntimeOptimizationServerResult baseline = RuntimeOptimizationPolicy.ChooseOrdinaryTopologyWinner(
            new[] { single, noisyMulti }, single);

        var finalEligible = new List<RuntimeOptimizationServerResult> { single, noisyMulti }
            .Where(candidate => candidate.Status == "completed")
            .ToList();
        RuntimeOptimizationService.Score(finalEligible, "turbo");
        RuntimeOptimizationServerResult winner = RuntimeOptimizationService.ChooseWinner(finalEligible, baseline);

        Assert.Same(single, winner);
    }

    [Fact]
    public void Multi_gpu_can_win_after_material_gain_with_bounded_latency()
    {
        RuntimeOptimizationServerResult single = Candidate("none", decode: 50, prompt: 1000, ttft: 300, watts: 190, device: "CUDA0");
        RuntimeOptimizationServerResult multi = Candidate("none", decode: 54, prompt: 1040, ttft: 315, watts: 280, device: "CUDA0,CUDA1", splitMode: "layer");
        RuntimeOptimizationService.Score(new[] { single, multi }, "turbo");

        RuntimeOptimizationServerResult winner = RuntimeOptimizationPolicy.ChooseOrdinaryTopologyWinner(
            new[] { single, multi }, single);

        Assert.Same(multi, winner);
        Assert.Equal("completed", multi.Status);
    }

    [Theory]
    [InlineData(50, 44, 83, true)]
    [InlineData(50, 44, 72, false)]
    [InlineData(50, 48, 84, false)]
    public void Thermal_regression_requires_both_sustained_drop_and_high_temperature(
        double initial, double sustained, double temperature, bool expected)
    {
        double? regression = RuntimeOptimizationPolicy.ComputeSustainedRegressionPercent(initial, sustained);
        Assert.Equal(expected, RuntimeOptimizationPolicy.IsThermalRegressionSuspected(regression, temperature));
    }

    [Fact]
    public void Thermal_suspect_speculative_candidate_does_not_replace_stable_baseline()
    {
        RuntimeOptimizationServerResult baseline = Candidate("none", decode: 50, prompt: 1000, ttft: 300, watts: 200);
        RuntimeOptimizationServerResult hotSpec = Candidate("ngram-simple", decode: 60, prompt: 1020, ttft: 295, watts: 260,
            sustained: 52, temperature: 84);
        hotSpec.SustainedDecodeRegressionPercent = RuntimeOptimizationPolicy.ComputeSustainedRegressionPercent(
            hotSpec.DecodeTokensPerSecond, hotSpec.SustainedDecodeTokensPerSecond);
        hotSpec.ThermalRegressionSuspected = RuntimeOptimizationPolicy.IsThermalRegressionSuspected(
            hotSpec.SustainedDecodeRegressionPercent, hotSpec.MaxGpuTemperatureC);

        RuntimeOptimizationService.Score(new[] { baseline, hotSpec }, "turbo");
        RuntimeOptimizationServerResult winner = RuntimeOptimizationService.ChooseWinner(
            new List<RuntimeOptimizationServerResult> { baseline, hotSpec }, baseline);

        Assert.Same(baseline, winner);
    }

    [Fact]
    public void Eco_mode_can_reward_tokens_per_watt_without_ignoring_speed()
    {
        RuntimeOptimizationServerResult fastHot = Candidate("none", decode: 60, prompt: 1000, ttft: 250, watts: 300);
        RuntimeOptimizationServerResult efficient = Candidate("none", decode: 57, prompt: 980, ttft: 260, watts: 170);

        RuntimeOptimizationService.Score(new[] { fastHot, efficient }, "eco");

        Assert.True(efficient.Score > fastHot.Score,
            $"Expected efficient profile ({efficient.Score}) to beat hot profile ({fastHot.Score}) in Eco mode.");
    }

    [Fact]
    public void Runtime_race_score_is_monotonic_for_prompt_and_decode_speed()
    {
        double baseline = RuntimeBuildRaceService.CombinedScore(1000, 40);
        Assert.True(RuntimeBuildRaceService.CombinedScore(1200, 40) > baseline);
        Assert.True(RuntimeBuildRaceService.CombinedScore(1000, 50) > baseline);
    }

    private static RuntimeOptimizationServerResult Candidate(
        string speculative,
        double decode,
        double prompt,
        double ttft,
        double watts,
        double? sustained = null,
        double? temperature = null,
        string device = "CUDA0",
        string splitMode = "none") => new()
    {
        Status = "completed",
        Profile = new LlamaPerformanceProfileDto
        {
            SpeculativeType = speculative,
            Device = device,
            SplitMode = splitMode
        },
        DecodeTokensPerSecond = decode,
        PromptTokensPerSecond = prompt,
        TimeToFirstTokenMilliseconds = ttft,
        SustainedDecodeTokensPerSecond = sustained ?? decode,
        AverageGpuPowerWatts = watts,
        MaxGpuTemperatureC = temperature
    };

    private static void AssertFlagValue(IReadOnlyList<string> args, string flag, string value)
    {
        int index = args.ToList().IndexOf(flag);
        Assert.True(index >= 0 && index + 1 < args.Count, "Missing flag " + flag);
        Assert.Equal(value, args[index + 1]);
    }
}
