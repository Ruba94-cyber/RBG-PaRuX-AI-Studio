using PaRuX.Contracts;
using PaRuX.Intelligence;
using Xunit;
using static PaRuX.Server.Tests.LaunchPlannerFixtures;

namespace PaRuX.Server.Tests;

/// <summary>
/// The launch planner over fake hardware and fake GGUF headers: {RTX 5080 16 GB eGPU + 32 GB, 780M iGPU 32 GB, CPU-only 16 GB,
/// 8 GB VRAM + 64 GB} x {dense 8B Q4, dense 32B Q4, MoE 30B-A3B Q4, MoE 40B-A2.5B Q3}. Every figure is a memory-fit estimate,
/// so these tests pin structure and invariants, never a speed.
/// </summary>
public sealed class LaunchPlannerTests
{
    public enum Hw { Rtx5080, Igpu780M, CpuOnly16, Vram8 }
    public enum Mdl { Dense8B, Dense32B, Moe30B, Moe40B }

    private static LaunchHardware Hardware(Hw hw) => hw switch
    {
        Hw.Rtx5080 => Rtx5080Egpu32Gb(),
        Hw.Igpu780M => Igpu780M32Gb(),
        Hw.CpuOnly16 => CpuOnly16Gb(),
        _ => Vram8Gb64GbRam()
    };

    private static GgufModelInfo Model(Mdl m) => m switch
    {
        Mdl.Dense8B => Dense8BQ4(),
        Mdl.Dense32B => Dense32BQ4(),
        Mdl.Moe30B => Moe30BA3BQ4(),
        _ => Moe40BA25BQ3()
    };

    public static TheoryData<Hw, Mdl> Matrix()
    {
        var data = new TheoryData<Hw, Mdl>();
        foreach (Hw hw in Enum.GetValues<Hw>())
            foreach (Mdl m in Enum.GetValues<Mdl>())
                data.Add(hw, m);
        return data;
    }

    private static List<string> LaunchArguments(LaunchPlan plan, Hw hw) =>
        RuntimeLaunchOptimizer.BuildLaunchArguments(plan.Profile, hasCuda: hw is Hw.Rtx5080 or Hw.Vram8, cpuOnly: hw == Hw.CpuOnly16);

    // ---------------------------------------------------------------- the matrix

    [Theory]
    [MemberData(nameof(Matrix))]
    public void Every_cell_is_planned_with_a_reason_for_each_decision_and_no_speed_claim(Hw hw, Mdl m)
    {
        LaunchPlan plan = Plan(Model(m), Hardware(hw));

        Assert.True(plan.Applied, plan.NotAppliedReason);
        Assert.NotEmpty(plan.Decisions);
        Assert.All(plan.Decisions, d => Assert.False(string.IsNullOrWhiteSpace(d.Reason), d.Flag));
        Assert.Contains("unmeasured", plan.Explain());
        Assert.Contains("unmeasured", LaunchPlan.PerformanceClaim);
        Assert.DoesNotContain("faster", plan.Explain(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("tok/s", plan.Explain(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(m is Mdl.Moe30B or Mdl.Moe40B ? LaunchModelClass.MixtureOfExperts : LaunchModelClass.Dense, plan.ModelClass);
    }

    [Theory]
    [MemberData(nameof(Matrix))]
    public void Planning_is_deterministic(Hw hw, Mdl m)
    {
        LaunchPlan first = Plan(Model(m), Hardware(hw));
        LaunchPlan second = Plan(Model(m), Hardware(hw));

        Assert.Equal(first.Explain(), second.Explain());
        Assert.Equal(LaunchArguments(first, hw), LaunchArguments(second, hw));
    }

    [Theory]
    [MemberData(nameof(Matrix))]
    public void No_plan_ever_emits_mlock_or_no_mmap_flags_and_only_verified_moe_flags(Hw hw, Mdl m)
    {
        List<string> args = LaunchArguments(Plan(Model(m), Hardware(hw)), hw);

        Assert.DoesNotContain("--mlock", args);
        Assert.DoesNotContain("--no-mmap", args);
        // Everything else is the existing builder's vocabulary plus the two flags verified against b10549.
        Assert.All(args.Where(a => a.StartsWith("--", StringComparison.Ordinal)), a => Assert.Contains(a, KnownFlags));
    }

    private static readonly string[] KnownFlags =
    [
        "--ctx-size", "--batch-size", "--ubatch-size", "--cache-type-k", "--cache-type-v", "--flash-attn", "--gpu-layers", "--fit",
        "--fit-target", "--cont-batching", "--slot-prompt-similarity", "--reasoning-effort", "--reasoning-budget", "--load-mode", "--prio",
        "--poll", "--parallel", "--threads", "--threads-batch", "--split-mode", "--main-gpu", "--device", "--n-cpu-moe", "--override-tensor"
    ];

    [Theory]
    [InlineData(Mdl.Dense8B)]
    public void Dense_8B_fits_every_accelerator_and_keeps_every_layer_on_it(Mdl m)
    {
        foreach (Hw hw in new[] { Hw.Rtx5080, Hw.Igpu780M, Hw.Vram8 })
        {
            LaunchPlan plan = Plan(Model(m), Hardware(hw));
            Assert.True(plan.Fits, hw.ToString());
            Assert.Equal(-1, plan.Profile.GpuLayers);
            Assert.Equal(0, plan.Profile.CpuMoeLayers);
            Assert.Equal("all", LaunchPlannerFixtures.ValueOf(LaunchArguments(plan, hw), "--gpu-layers"));
        }
    }

    [Fact]
    public void RTX_5080_with_a_dense_8B_goes_fully_to_VRAM_without_pinning_threads_or_memory()
    {
        LaunchPlan plan = Plan(Dense8BQ4(), Rtx5080Egpu32Gb());

        Assert.Equal(0, plan.Profile.Threads);
        Assert.Equal("none", plan.Profile.LoadMode); // measured load-mode finding for a fully offloaded model
        Assert.Equal("q8_0", plan.Profile.CacheTypeK);
        Assert.Equal(32768, plan.Profile.ContextSize);
    }

    [Fact]
    public void RTX_5080_with_a_dense_32B_offloads_a_proper_subset_of_layers_and_uses_physical_cores()
    {
        LaunchPlan plan = Plan(Dense32BQ4(), Rtx5080Egpu32Gb());

        Assert.InRange(plan.Profile.GpuLayers, 1, 63);
        Assert.Equal(0, plan.Profile.CpuMoeLayers);
        Assert.Equal(6, plan.Profile.Threads);       // 8 physical cores minus 2, NOT 16 logical
        Assert.Equal(8, plan.Profile.ThreadsBatch);
        Assert.False(plan.Profile.CpuStrict);
        Assert.Equal("mmap+mlock", plan.Profile.LoadMode); // RAM headroom proven in this fixture
    }

    [Theory]
    [InlineData(Hw.Rtx5080, Mdl.Moe30B)]
    [InlineData(Hw.Rtx5080, Mdl.Moe40B)]
    [InlineData(Hw.Vram8, Mdl.Moe30B)]
    [InlineData(Hw.Vram8, Mdl.Moe40B)]
    public void A_moe_that_does_not_fit_VRAM_keeps_attention_on_the_GPU_and_moves_experts_to_the_CPU(Hw hw, Mdl m)
    {
        LaunchPlan plan = Plan(Model(m), Hardware(hw));
        List<string> args = LaunchArguments(plan, hw);
        long blocks = Model(m).BlockCount!.Value;

        Assert.True(plan.Fits);
        Assert.Equal(-1, plan.Profile.GpuLayers);
        Assert.InRange(plan.Profile.CpuMoeLayers, 1, (int)blocks);
        Assert.Equal(plan.Profile.CpuMoeLayers.ToString(), ValueOf(args, "--n-cpu-moe"));
        Assert.Equal("all", ValueOf(args, "--gpu-layers"));
        Assert.DoesNotContain("--override-tensor", args);
        Assert.Contains(plan.Decisions, d => d.Flag == "--n-cpu-moe" && d.Reason.Contains("verified-help-b10549"));
    }

    [Fact]
    public void A_smaller_card_pushes_more_experts_to_the_CPU_than_a_bigger_one()
    {
        int big = Plan(Moe30BA3BQ4(), Rtx5080Egpu32Gb()).Profile.CpuMoeLayers;
        int small = Plan(Moe30BA3BQ4(), Vram8Gb64GbRam()).Profile.CpuMoeLayers;

        Assert.True(small > big, $"8 GB card N={small} should exceed 16 GB card N={big}");
    }

    [Fact]
    public void A_moe_that_fits_VRAM_gets_no_expert_offload()
    {
        LaunchHardware roomy = Rtx5080Egpu32Gb() with { GpuMemoryBytes = 40L * 1024 * 1024 * 1024 };

        LaunchPlan plan = Plan(Moe30BA3BQ4(), roomy);

        Assert.Equal(0, plan.Profile.CpuMoeLayers);
        Assert.Equal(-1, plan.Profile.GpuLayers);
        Assert.Equal("none", plan.Profile.LoadMode);
    }

    [Theory]
    [InlineData(Mdl.Dense32B)]
    [InlineData(Mdl.Moe30B)]
    [InlineData(Mdl.Moe40B)]
    public void The_780M_shared_memory_never_gets_expert_offload_and_never_counts_RAM_twice(Mdl m)
    {
        LaunchHardware igpu = Igpu780M32Gb();
        LaunchPlan plan = Plan(Model(m), igpu);

        Assert.Equal(0, plan.Profile.CpuMoeLayers);
        Assert.Equal(string.Empty, plan.Profile.OverrideTensor);
        Assert.InRange(plan.Profile.GpuLayers, 1, (int)Model(m).BlockCount!.Value - 1);
        // 32 GB total, 24 GiB usable after the shared reserve, the aperture capped at 16 GiB: the pool is one number.
        Assert.True(igpu.SharedMemoryPrimary);
        Assert.Equal(16L * 1024 * 1024 * 1024, igpu.GpuMemoryBytes);
        Assert.Equal("auto", plan.Profile.LoadMode); // pinning pages in shared memory would only shrink the system
    }

    [Fact]
    public void The_780M_dense_8B_fits_the_aperture_in_one_pass()
    {
        LaunchPlan plan = Plan(Dense8BQ4(), Igpu780M32Gb());

        Assert.True(plan.Fits);
        Assert.Contains(plan.Decisions, d => d.Flag == "--gpu-layers" && d.Reason.Contains("counted once"));
    }

    [Theory]
    [InlineData(Mdl.Dense8B, true)]
    [InlineData(Mdl.Dense32B, false)]
    [InlineData(Mdl.Moe30B, false)]
    [InlineData(Mdl.Moe40B, false)]
    public void CPU_only_16_GB_runs_on_the_CPU_and_reports_honestly_whether_the_model_fits(Mdl m, bool fits)
    {
        LaunchPlan plan = Plan(Model(m), CpuOnly16Gb());
        List<string> args = LaunchArguments(plan, Hw.CpuOnly16);

        Assert.Equal(fits, plan.Fits);
        Assert.Equal("0", ValueOf(args, "--gpu-layers"));
        Assert.DoesNotContain("--device", args);
        Assert.Equal("3", ValueOf(args, "--threads"));        // 4 physical - 1, not 8 logical
        Assert.Equal("4", ValueOf(args, "--threads-batch"));
        Assert.Equal(fits, plan.Warnings.Count == 0);
        if (!fits) Assert.Contains("does not report this model as supported", plan.Warnings[0]);
    }

    [Fact]
    public void CPU_only_does_not_lock_memory_when_headroom_is_not_proven()
    {
        LaunchPlan plan = Plan(Dense8BQ4(), CpuOnly16Gb());

        Assert.Equal("auto", plan.Profile.LoadMode);
        Assert.Contains(plan.Decisions, d => d.Flag == "--load-mode" && d.Reason.Contains("not proven"));
    }

    // ---------------------------------------------------------------- context / KV cache

    [Fact]
    public void KV_cache_is_never_quantized_when_the_default_fits()
    {
        LaunchPlan plan = Plan(Dense8BQ4(), Rtx5080Egpu32Gb(), baseline: Baseline(cache: "f16"));

        Assert.Equal("f16", plan.Profile.CacheTypeK);
        Assert.Equal("f16", plan.Profile.CacheTypeV);
        Assert.Equal(32768, plan.Profile.ContextSize);
    }

    [Fact]
    public void KV_cache_steps_down_to_q8_0_on_CUDA_only_when_f16_does_not_fit_and_the_reason_says_so()
    {
        // 8 GB card, dense 8B, f16 KV at 32k: 5.0 GB weights + 4.8 GB KV does not fit; q8_0 (2.6 GB) does not either at
        // 32k, so the ladder reaches a smaller window at q8_0 before the lossiest cache.
        LaunchPlan plan = Plan(Dense8BQ4(), Vram8Gb64GbRam(), baseline: Baseline(cache: "f16"));

        Assert.True(plan.Fits);
        Assert.Equal("q8_0", plan.Profile.CacheTypeK);
        Assert.Contains(plan.Decisions, d => d.Flag.Contains("cache-type") && d.Reason.Contains("from f16"));
    }

    [Fact]
    public void KV_quantization_is_not_attempted_on_Vulkan()
    {
        LaunchPlan plan = Plan(Dense8BQ4(), Igpu780M32Gb(), baseline: Baseline(cache: "f16", context: 262144));

        Assert.Equal("f16", plan.Profile.CacheTypeK);
    }

    [Fact]
    public void An_explicit_user_context_and_cache_are_never_changed_even_when_they_do_not_fit()
    {
        LlamaPerformanceProfileDto pinned = Baseline(context: 131072, contextPinned: true, cache: "f16");

        LaunchPlan plan = Plan(Dense8BQ4(), Vram8Gb64GbRam(), baseline: pinned);

        Assert.Equal(131072, plan.Profile.ContextSize);
        Assert.Equal("f16", plan.Profile.CacheTypeK);
        Assert.True(plan.Profile.ContextIsPerRequest);
        // The memory pressure is absorbed by placement instead.
        Assert.InRange(plan.Profile.GpuLayers, 0, 35);
    }

    [Fact]
    public void KV_cache_that_lives_in_system_RAM_does_not_consume_the_VRAM_budget()
    {
        LlamaPerformanceProfileDto kvInRam = Baseline(context: 131072, contextPinned: true);
        kvInRam.DisableKvOffload = true;

        LaunchPlan plan = Plan(Dense8BQ4(), Vram8Gb64GbRam(), baseline: kvInRam);

        Assert.Equal(-1, plan.Profile.GpuLayers);
        Assert.True(plan.Profile.DisableKvOffload);
    }

    // ---------------------------------------------------------------- batch sizes / load mode

    [Fact]
    public void Batch_sizes_shrink_only_when_the_accelerator_has_almost_no_room_left()
    {
        LlamaPerformanceProfileDto big = Baseline();
        big.BatchSize = 2048;
        big.UBatchSize = 512;

        LaunchPlan tight = Plan(Dense32BQ4(), Rtx5080Egpu32Gb(), baseline: big);
        LaunchPlan roomy = Plan(Dense8BQ4(), Rtx5080Egpu32Gb(), baseline: big);

        Assert.Equal(1024, tight.Profile.BatchSize);
        Assert.Equal(256, tight.Profile.UBatchSize);
        Assert.Equal(2048, roomy.Profile.BatchSize);
        Assert.Equal(512, roomy.Profile.UBatchSize);
    }

    [Fact]
    public void Memory_is_pinned_only_with_proven_RAM_headroom()
    {
        LaunchHardware tightRam = Rtx5080Egpu32Gb() with { SystemRamAvailableBytes = 12L * 1024 * 1024 * 1024 };

        LaunchPlan pinned = Plan(Dense32BQ4(), Rtx5080Egpu32Gb());
        LaunchPlan notPinned = Plan(Dense32BQ4(), tightRam);

        Assert.Equal("mmap+mlock", pinned.Profile.LoadMode);
        Assert.Equal("auto", notPinned.Profile.LoadMode);
    }

    [Fact]
    public void A_measured_or_explicit_load_mode_is_not_overridden()
    {
        LlamaPerformanceProfileDto explicitMode = Baseline();
        explicitMode.LoadMode = "mmap";

        LaunchPlan plan = Plan(Dense32BQ4(), Rtx5080Egpu32Gb(), baseline: explicitMode);

        Assert.Equal("mmap", plan.Profile.LoadMode);
    }

    // ---------------------------------------------------------------- flag verification

    [Fact]
    public void An_unverified_runtime_build_gets_layer_offload_and_no_moe_flags()
    {
        LaunchPlan plan = Plan(Moe30BA3BQ4(), Rtx5080Egpu32Gb(), LlamaServerCapabilities.Unverified);
        List<string> args = LaunchArguments(plan, Hw.Rtx5080);

        Assert.DoesNotContain("--n-cpu-moe", args);
        Assert.DoesNotContain("--override-tensor", args);
        Assert.InRange(plan.Profile.GpuLayers, 1, 47);
        Assert.Contains(plan.Decisions, d => d.Flag == "--gpu-layers" && d.Reason.Contains("not verified"));
    }

    [Fact]
    public void Override_tensor_is_the_fallback_only_when_n_cpu_moe_is_missing_but_override_is_listed()
    {
        var caps = new LlamaServerCapabilities(NCpuMoe: false, OverrideTensor: true, LoadMode: true, Source: "probed-help");

        LaunchPlan plan = Plan(Moe30BA3BQ4(), Rtx5080Egpu32Gb(), caps);
        List<string> args = LaunchArguments(plan, Hw.Rtx5080);

        Assert.DoesNotContain("--n-cpu-moe", args);
        string pattern = ValueOf(args, "--override-tensor");
        Assert.EndsWith(@"\.ffn_.*_exps\.=CPU", pattern);
        Assert.StartsWith(@"blk\.(0|1|2|", pattern);
        Assert.Equal(-1, plan.Profile.GpuLayers);
    }

    [Fact]
    public void The_override_pattern_covers_exactly_the_first_N_layers()
    {
        Assert.Equal(@"blk\.(0|1|2)\.ffn_.*_exps\.=CPU", LaunchPlanner.ExpertOverridePattern(3, 48));
        Assert.Equal(@"ffn_.*_exps\.=CPU", LaunchPlanner.ExpertOverridePattern(48, 48));
    }

    [Fact]
    public void Capabilities_are_read_from_help_text_and_only_the_hand_verified_build_is_trusted_without_it()
    {
        const string help = """
            -ot,   --override-tensor <tensor name pattern>=<buffer type>,...
            -cmoe, --cpu-moe                        keep all Mixture of Experts (MoE) weights in the CPU
            -ncmoe, --n-cpu-moe N                   keep the Mixture of Experts (MoE) weights of the first N layers in the
            -lm,   --load-mode MODE                 model loading mode (default: auto)
            """;
        LlamaServerCapabilities probed = LlamaServerCapabilities.FromHelpText(help);
        LlamaServerCapabilities noMoe = LlamaServerCapabilities.FromHelpText("--threads N\n--load-mode MODE\n");

        Assert.True(probed is { NCpuMoe: true, OverrideTensor: true, LoadMode: true });
        Assert.True(noMoe is { NCpuMoe: false, OverrideTensor: false, LoadMode: true });
        Assert.True(LlamaServerCapabilities.FromBuild(10549).NCpuMoe);
        Assert.False(LlamaServerCapabilities.FromBuild(10548).NCpuMoe);
        Assert.False(LlamaServerCapabilities.FromBuild(null).NCpuMoe);
        Assert.False(LlamaServerCapabilities.FromHelpText("   ").NCpuMoe);
        Assert.Equal(10549, LlamaServerCapabilities.ParseBuildNumber("llama.cpp-b10549-cuda12.4"));
        Assert.Equal(10549, LlamaServerCapabilities.ParseBuildNumber("b10549"));
        Assert.Null(LlamaServerCapabilities.ParseBuildNumber("latest"));
    }

    [Fact]
    public async Task The_probe_prefers_the_real_help_text_over_the_build_table_and_is_cached()
    {
        string exe = Path.Combine(Path.GetTempPath(), "parux-fake-llama-" + Guid.NewGuid().ToString("N") + ".exe");
        await File.WriteAllTextAsync(exe, "x");
        try
        {
            LlamaServerCapabilityProbe.InvalidateCache();
            int runs = 0;
            Task<string?> Runner(string _, CancellationToken __) { runs++; return Task.FromResult<string?>("--load-mode MODE"); }

            LlamaServerCapabilities first = await LlamaServerCapabilityProbe.GetAsync(exe, "b10549", CancellationToken.None, Runner);
            LlamaServerCapabilities second = await LlamaServerCapabilityProbe.GetAsync(exe, "b10549", CancellationToken.None, Runner);

            Assert.False(first.NCpuMoe); // the probe saw no --n-cpu-moe, whatever the tag claims
            Assert.True(first.LoadMode);
            Assert.Equal(1, runs);
            Assert.Same(first, second);
        }
        finally
        {
            File.Delete(exe);
            LlamaServerCapabilityProbe.InvalidateCache();
        }
    }

    [Fact]
    public async Task An_unprobeable_executable_falls_back_to_the_build_table()
    {
        string exe = Path.Combine(Path.GetTempPath(), "parux-fake-llama-" + Guid.NewGuid().ToString("N") + ".exe");
        await File.WriteAllTextAsync(exe, "x");
        try
        {
            LlamaServerCapabilityProbe.InvalidateCache();
            LlamaServerCapabilities caps = await LlamaServerCapabilityProbe.GetAsync(
                exe, "llama.cpp-b10549-vulkan", CancellationToken.None, (_, _) => throw new InvalidOperationException("cannot run"));

            Assert.True(caps.NCpuMoe);
            Assert.Equal("verified-help-b10549", caps.Source);
        }
        finally
        {
            File.Delete(exe);
            LlamaServerCapabilityProbe.InvalidateCache();
        }
    }

    // ---------------------------------------------------------------- not applicable

    [Fact]
    public void Missing_or_unreadable_metadata_leaves_the_baseline_untouched()
    {
        LlamaPerformanceProfileDto baseline = Baseline();
        var unparsed = new GgufModelInfo { ParsedSuccessfully = false, SizeBytes = 1000 };

        LaunchPlan plan = LaunchPlanner.Plan(new LaunchPlanInput(unparsed, Rtx5080Egpu32Gb(), B10549, baseline));

        Assert.False(plan.Applied);
        Assert.Same(baseline, plan.Profile);
        Assert.Contains("default arguments", plan.NotAppliedReason);
    }

    [Fact]
    public void A_header_without_attention_geometry_is_not_planned_rather_than_guessed()
    {
        GgufModelInfo model = Dense8BQ4();
        model.EmbeddingLength = null;
        model.KeyLength = null;

        LaunchPlan plan = Plan(model, Rtx5080Egpu32Gb());

        Assert.False(plan.Applied);
        Assert.Contains("KV cache", plan.NotAppliedReason);
    }

    [Fact]
    public void A_moe_with_unknown_expert_size_is_laid_out_like_a_dense_model_and_says_so()
    {
        GgufModelInfo model = Moe30BA3BQ4();
        model.ExpertTensorBytes = null;
        model.ExpertFeedForwardLength = null;
        model.ParameterCount = null;

        LaunchPlan plan = Plan(model, Rtx5080Egpu32Gb());

        Assert.Equal(0, plan.Profile.CpuMoeLayers);
        Assert.InRange(plan.Profile.GpuLayers, 1, 47);
        Assert.Contains(plan.Decisions, d => d.Flag == "model-class" && d.Reason.Contains("expert size unknown"));
    }

    [Fact]
    public void The_expert_size_is_estimated_from_parameter_share_when_the_directory_was_not_read()
    {
        GgufModelInfo model = Moe30BA3BQ4();
        model.ExpertTensorBytes = null;

        LaunchPlan plan = Plan(model, Rtx5080Egpu32Gb());

        Assert.InRange(plan.Profile.CpuMoeLayers, 1, 48);
        Assert.Contains(plan.Decisions, d => d.Flag == "model-class" && d.Reason.Contains("estimated from expert parameter share"));
    }
}
