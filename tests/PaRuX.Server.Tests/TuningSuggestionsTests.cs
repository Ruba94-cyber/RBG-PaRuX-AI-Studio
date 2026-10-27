using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>Hardware-aware search space, "already optimal" outcome text and the concrete suggestions. Pure: no process, no network.</summary>
public sealed class TuningSuggestionsTests
{
    private const long GiB = 1L << 30;

    private static TextTuningFacts Text(long model = 8 * GiB, long vram = 24 * GiB, string backend = "text-cuda", bool nvidia = true,
        int ctx = 8192, long? kv = 1 * GiB, bool draft = false, string file = "Qwen3-8B-Q8_0.gguf", long ram = 32 * GiB) =>
        new(file, model, backend, nvidia, vram, ram, 51.2, ctx, kv, draft, "chat");

    private static readonly IReadOnlyList<(string FileName, long SizeBytes)> Repo = new List<(string, long)>
    {
        ("Qwen3-8B-Q8_0.gguf", 8 * GiB), ("Qwen3-8B-Q6_K.gguf", 6 * GiB + GiB / 2), ("Qwen3-8B-Q4_K_M.gguf", 5 * GiB),
        ("Qwen3-8B-Q2_K.gguf", 3 * GiB), ("Qwen3-8B-Q4_K_M-00001-of-00002.gguf", 3 * GiB), ("Qwen3-4B-Q4_K_M.gguf", 3 * GiB),
        ("Qwen3-8B-draft-Q8_0.gguf", 300L << 20)
    };

    // ---- outcome text -------------------------------------------------------------------------------------------------

    [Fact]
    public void The_optimal_text_states_the_measured_speed_or_honestly_says_none_was_measured()
    {
        Assert.Equal("Already optimal for this PC (measured 51.2 tok/s)", TuningSuggestions.OptimalText(51.23));
        Assert.Contains("no faster setting", TuningSuggestions.OptimalText(null));
        Assert.Equal("Already optimal for this PC (measured 3.4 s per image)", TuningSuggestions.MediaOptimalText(3.44));
    }

    // ---- text suggestions ---------------------------------------------------------------------------------------------

    [Fact]
    public void A_smaller_quantization_of_the_same_model_is_suggested_with_file_size_estimated_gain_and_download_action()
    {
        var list = TuningSuggestions.ForText(Text(), Repo, "org/Qwen3-8B-GGUF");
        ModelTuningSuggestionDto q = Assert.Single(list, s => s.Kind == "smaller_quant");
        Assert.Equal("Qwen3-8B-Q6_K.gguf", q.FileName); // the best-quality file that is meaningfully smaller
        Assert.Equal("org/Qwen3-8B-GGUF", q.RepoId);
        Assert.Equal("download_file", q.ActionKind);
        Assert.True(q.SizeBytes > 0);
        Assert.Contains("estimate", q.ExpectedGain);
    }

    [Fact]
    public void Two_bit_split_and_other_size_files_are_never_suggested_as_a_faster_version()
    {
        var onlyBad = new List<(string, long)> { ("Qwen3-8B-Q2_K.gguf", 3 * GiB), ("Qwen3-8B-Q4_K_M-00001-of-00002.gguf", 3 * GiB), ("Qwen3-4B-Q4_K_M.gguf", 3 * GiB) };
        Assert.DoesNotContain(TuningSuggestions.ForText(Text(), onlyBad, "org/x"), s => s.Kind == "smaller_quant");
    }

    [Fact]
    public void When_the_model_does_not_fit_the_gpu_the_quantization_that_fits_is_preferred()
    {
        var list = TuningSuggestions.ForText(Text(vram: 8 * GiB, kv: 512L << 20), Repo, "org/x");
        ModelTuningSuggestionDto q = Assert.Single(list, s => s.Kind == "smaller_quant");
        Assert.Equal("Qwen3-8B-Q4_K_M.gguf", q.FileName); // Q6_K (6.5 GiB) would still spill, Q4_K_M (5 GiB) fits
        Assert.Contains("fits fully in GPU memory", q.ExpectedGain);
    }

    [Fact]
    public void A_draft_head_from_the_repository_is_a_concrete_download_suggestion_unless_one_is_installed()
    {
        Assert.Contains(TuningSuggestions.ForText(Text(), Repo, "org/x"), s => s.Kind == "draft_model" && s.FileName == "Qwen3-8B-draft-Q8_0.gguf" && s.SizeBytes == 300L << 20);
        Assert.DoesNotContain(TuningSuggestions.ForText(Text(draft: true), Repo, "org/x"), s => s.Kind == "draft_model");
    }

    [Fact]
    public void Lowering_the_context_is_suggested_only_when_memory_is_tight()
    {
        var tight = TuningSuggestions.ForText(Text(model: 20 * GiB, vram: 24 * GiB, ctx: 65536, kv: 6 * GiB), null, null);
        ModelTuningSuggestionDto c = Assert.Single(tight, s => s.Kind == "lower_context");
        Assert.Contains("32,768", c.Title.Replace(".", ","));
        Assert.DoesNotContain(TuningSuggestions.ForText(Text(model: 4 * GiB, vram: 24 * GiB, ctx: 65536, kv: 2 * GiB), null, null), s => s.Kind == "lower_context");
    }

    [Fact]
    public void An_nvidia_gpu_on_the_vulkan_build_is_told_to_switch_to_cuda_in_runtime_manager()
    {
        ModelTuningSuggestionDto s = Assert.Single(TuningSuggestions.ForText(Text(backend: "text-vulkan"), null, null), x => x.Kind == "switch_backend");
        Assert.Equal("open_runtime_manager", s.ActionKind);
        Assert.Contains("CUDA", s.Title);
    }

    [Fact]
    public void The_result_is_never_empty_and_a_not_final_pass_adds_no_placeholder()
    {
        var done = TuningSuggestions.ForText(Text(), null, null);
        ModelTuningSuggestionDto info = Assert.Single(done);
        Assert.Equal("info", info.Kind);
        Assert.Empty(TuningSuggestions.ForText(Text(), null, null, final: false));
    }

    // ---- media suggestions --------------------------------------------------------------------------------------------

    private static MediaTuningFacts Media(string label = "z_image_bf16", bool nvidia = true, bool cuda = true, long vram = 12 * GiB,
        long weights = 10 * GiB, long? peak = null) => new(label, "GPU", nvidia, cuda, vram, weights, peak, 3.4, 1024, 1024);

    [Fact]
    public void A_large_base_image_model_gets_a_distilled_variant_and_an_fp8_suggestion_with_the_saving()
    {
        var list = TuningSuggestions.ForMedia(Media());
        ModelTuningSuggestionDto variant = Assert.Single(list, s => s.Kind == "media_variant");
        Assert.Contains("Z-Image Turbo", variant.Detail);
        Assert.Contains("estimate", variant.ExpectedGain);
        ModelTuningSuggestionDto fp8 = Assert.Single(list, s => s.Kind == "media_precision");
        Assert.Contains("5.0 GiB", fp8.ExpectedGain);
    }

    [Fact]
    public void An_already_distilled_fp8_model_gets_neither_suggestion_but_still_a_non_empty_answer()
    {
        var list = TuningSuggestions.ForMedia(Media(label: "flux_schnell_fp8"));
        Assert.DoesNotContain(list, s => s.Kind is "media_variant" or "media_precision");
        Assert.NotEmpty(list);
    }

    [Fact]
    public void Memory_pressure_at_1024_px_suggests_a_smaller_base_size_with_the_compute_saving()
    {
        ModelTuningSuggestionDto p = Assert.Single(TuningSuggestions.ForMedia(Media(peak: 11900L << 20)), s => s.Kind == "media_preset");
        Assert.Contains("768x768", p.Title);
        Assert.Contains("44%", p.ExpectedGain);
    }

    [Fact]
    public void A_non_nvidia_gpu_and_a_cpu_only_pytorch_each_get_an_actionable_explanation()
    {
        Assert.Contains(TuningSuggestions.ForMedia(Media(nvidia: false)), s => s.Title.Contains("NVIDIA"));
        ModelTuningSuggestionDto repair = Assert.Single(TuningSuggestions.ForMedia(Media(cuda: false)), s => s.Kind == "switch_backend");
        Assert.Equal("open_runtime_manager", repair.ActionKind);
    }

    // ---- hardware search space ----------------------------------------------------------------------------------------

    private static LlamaPerformanceProfileDto Baseline(int ctx = 8192) => new() { Id = "base", DisplayName = "Base", ContextSize = ctx, CacheTypeK = "q8_0", CacheTypeV = "q8_0", FlashAttention = "on" };

    private static HardwareSearchFacts Facts(long model, long vram, bool nvidia = true, int physical = 8, long? blocks = 40) =>
        new(model, vram, 32 * GiB, physical * 2, physical, blocks, nvidia);

    [Fact]
    public void A_model_that_fits_an_nvidia_gpu_adds_no_server_run_at_all()
    {
        HardwareSearchFacts f = Facts(8 * GiB, 24 * GiB);
        Assert.Empty(RuntimeHardwareSearchSpace.BuildOffloadCandidates(Baseline(), f, "chat"));
        Assert.Empty(RuntimeHardwareSearchSpace.BuildThreadCandidates(Baseline(), f));
    }

    [Fact]
    public void A_model_larger_than_vram_races_explicit_partial_offload_with_fewer_layers_than_the_model_has()
    {
        var list = RuntimeHardwareSearchSpace.BuildOffloadCandidates(Baseline(), Facts(14 * GiB, 8 * GiB), "chat");
        var layers = list.Where(c => c.GpuLayers > 0).ToList();
        Assert.NotEmpty(layers);
        Assert.All(layers, c => Assert.InRange(c.GpuLayers, 1, 39));
        Assert.True(list.Count <= RuntimeHardwareSearchSpace.MaximumOffloadCandidates);
        Assert.Equal(list.Count, list.Select(c => c.Id).Distinct().Count());
    }

    [Fact]
    public void A_tiny_gpu_races_cpu_only_and_a_non_nvidia_gpu_races_flash_attention_off_with_a_full_precision_cache()
    {
        Assert.Contains(RuntimeHardwareSearchSpace.BuildOffloadCandidates(Baseline(), Facts(14 * GiB, 1 * GiB), "chat"), c => c.GpuLayers == 0);
        LlamaPerformanceProfileDto fa = Assert.Single(RuntimeHardwareSearchSpace.BuildOffloadCandidates(Baseline(), Facts(4 * GiB, 12 * GiB, nvidia: false), "chat"));
        Assert.Equal("off", fa.FlashAttention);
        Assert.Equal("f16", fa.CacheTypeK);
        Assert.Equal("f16", fa.CacheTypeV);
    }

    [Fact]
    public void Context_right_sizing_is_chat_only_keeps_the_8k_floor_and_applies_only_when_memory_is_tight()
    {
        HardwareSearchFacts tight = Facts(14 * GiB, 8 * GiB);
        Assert.Contains(RuntimeHardwareSearchSpace.BuildOffloadCandidates(Baseline(32768), tight, "chat"), c => c.ContextSize == 16384);
        Assert.DoesNotContain(RuntimeHardwareSearchSpace.BuildOffloadCandidates(Baseline(32768), tight, "coding"), c => c.ContextSize == 16384);
        Assert.DoesNotContain(RuntimeHardwareSearchSpace.BuildOffloadCandidates(Baseline(8192), tight, "chat"), c => c.ContextSize < 8192);
    }

    [Fact]
    public void Thread_counts_are_raced_only_when_the_cpu_is_on_the_critical_path()
    {
        var spill = RuntimeHardwareSearchSpace.BuildThreadCandidates(Baseline(), Facts(14 * GiB, 8 * GiB, physical: 8));
        Assert.Equal(new[] { 8, 6 }, spill.Select(c => c.Threads).ToArray());
        Assert.All(spill, c => Assert.Equal(16, c.ThreadsBatch));
        Assert.Empty(RuntimeHardwareSearchSpace.BuildThreadCandidates(Baseline(), Facts(4 * GiB, 24 * GiB)));
        Assert.NotEmpty(RuntimeHardwareSearchSpace.BuildThreadCandidates(Baseline(), Facts(4 * GiB, 0)));
    }

    private static RuntimeOptimizationServerResult R(double decode, double ttft = 100, string status = "completed", bool thermal = false) => new()
    {
        Status = status, DecodeTokensPerSecond = decode, TimeToFirstTokenMilliseconds = ttft, ThermalRegressionSuspected = thermal,
        Profile = new LlamaPerformanceProfileDto { Id = "c" + decode }
    };

    [Fact]
    public void A_hardware_candidate_is_adopted_only_on_a_clear_safe_win_otherwise_the_baseline_stays()
    {
        RuntimeOptimizationServerResult baseline = R(20);
        Assert.Same(baseline, RuntimeHardwareSearchSpace.Choose(baseline, new[] { R(20.8) }));                 // below 5%
        Assert.Same(baseline, RuntimeHardwareSearchSpace.Choose(baseline, new[] { R(40, ttft: 200) }));         // first token too slow
        Assert.Same(baseline, RuntimeHardwareSearchSpace.Choose(baseline, new[] { R(40, thermal: true) }));     // new thermal regression
        Assert.Same(baseline, RuntimeHardwareSearchSpace.Choose(baseline, new[] { R(40, status: "failed") })); // OOM is only evidence
        RuntimeOptimizationServerResult win = R(30);
        Assert.Same(win, RuntimeHardwareSearchSpace.Choose(baseline, new[] { R(22), win, R(25) }));
    }

    // ---- tracker --------------------------------------------------------------------------------------------------

    [Fact]
    public void Optimal_is_published_as_skipped_with_suggestions_and_requeueing_clears_them()
    {
        var tuning = new ModelTuningTracker();
        string id = tuning.Running("text", "C:/m/a.gguf", "a", "chat");
        tuning.Optimal(id, "Already optimal for this PC (measured 51.2 tok/s)", TuningSuggestions.ForText(Text(), null, null));

        ModelTuningItemDto item = Assert.Single(tuning.Snapshot().Items);
        Assert.Equal(ModelTuningStates.Skipped, item.State);
        Assert.NotEmpty(item.Suggestions);
        tuning.SetSuggestions(id, Enumerable.Range(0, 20).Select(i => new ModelTuningSuggestionDto { Title = "s" + i }).ToList());
        Assert.Equal(ModelTuningTracker.MaximumSuggestions, tuning.Get(id)!.Suggestions.Count);

        tuning.Queued("text", "C:/m/a.gguf", "a", "chat");
        Assert.Empty(tuning.Get(id)!.Suggestions);
        tuning.SetSuggestions(id, new[] { new ModelTuningSuggestionDto { Title = "late" } }); // a late answer must not attach to a new run
        Assert.Empty(tuning.Get(id)!.Suggestions);
    }
}
