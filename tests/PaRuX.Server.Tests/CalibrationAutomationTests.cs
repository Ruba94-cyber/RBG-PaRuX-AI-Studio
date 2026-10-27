using PaRuX.Contracts;
using PaRuX.Intelligence;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Idle-time calibration trigger, KV-cache precision auto-choice and the speculative fall-through rule.
/// Hermetic: fake clock, fake optimiser, fake measurements.
/// </summary>
public sealed class CalibrationAutomationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "parux-calib-" + Guid.NewGuid().ToString("N"));

    public CalibrationAutomationTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { }
    }

    // ---------- speculative: first candidate that passes every gate wins ----------

    private static RuntimeOptimizationServerResult Spec(string spec, double decode, string digest, double accept = 80, string kv = "f16") => new()
    {
        Status = "completed",
        Profile = new LlamaPerformanceProfileDto { Id = spec + kv, SpeculativeType = spec, CacheTypeK = kv, CacheTypeV = kv },
        DecodeTokensPerSecond = decode,
        PromptTokensPerSecond = 1000,
        TimeToFirstTokenMilliseconds = 300,
        SustainedDecodeTokensPerSecond = decode,
        DraftTokens = spec == "none" ? 0 : 100,
        DraftTokensAccepted = spec == "none" ? 0 : (int)accept,
        DraftAcceptancePercent = spec == "none" ? null : accept,
        OutputDigest = digest
    };

    [Fact]
    public void A_faster_chain_that_changes_the_output_does_not_hide_a_slower_identical_mtp()
    {
        RuntimeOptimizationServerResult baseline = Spec("none", 53, "same");
        RuntimeOptimizationServerResult mtp = Spec("draft-mtp", 86, "same", 55);
        RuntimeOptimizationServerResult chain = Spec("draft-mtp,ngram-mod", 180, "different", 89);
        var all = new List<RuntimeOptimizationServerResult> { baseline, mtp, chain };
        RuntimeOptimizationService.Score(all, "balanced");
        Assert.Same(mtp, RuntimeOptimizationService.ChooseWinner(all, baseline));

        chain.OutputDigest = "same"; // on a code workload the chain is identical, so the fastest identical mode wins
        RuntimeOptimizationService.Score(all, "balanced");
        Assert.Same(chain, RuntimeOptimizationService.ChooseWinner(all, baseline));
    }

    // ---------- KV precision ----------

    private static RuntimeOptimizationServerResult Kv(string type, double decode, string? sample = "the quick brown fox jumps over the lazy dog", string digest = "d0", double prompt = 2000, double ttft = 200)
    {
        return new RuntimeOptimizationServerResult
        {
            Status = "completed",
            Profile = new LlamaPerformanceProfileDto { Id = "kv-" + type, CacheTypeK = type, CacheTypeV = type, SpeculativeType = "none" },
            DecodeTokensPerSecond = decode,
            PromptTokensPerSecond = prompt,
            TimeToFirstTokenMilliseconds = ttft,
            OutputSample = sample,
            OutputDigest = digest,
            Score = decode
        };
    }

    [Fact]
    public void Full_precision_kv_is_kept_when_quantised_is_not_clearly_faster()
    {
        RuntimeOptimizationServerResult f16 = Kv("f16", 53.3);
        RuntimeOptimizationServerResult q8 = Kv("q8_0", 53.0, digest: "d0");
        KvChoice choice = KvCachePolicy.Choose(new[] { f16, q8 }, q8, "chat", contextNeedsQuantisedKv: false, fullPrecisionFailed: false);
        Assert.Same(f16, choice.Winner);
    }

    [Fact]
    public void Quantised_kv_is_kept_when_it_measures_faster_and_the_output_stays_within_tolerance()
    {
        RuntimeOptimizationServerResult f16 = Kv("f16", 50);
        RuntimeOptimizationServerResult q8 = Kv("q8_0", 54, sample: "the quick brown fox jumps over the lazy cat", digest: "d1"); // 91% identical prefix
        KvChoice choice = KvCachePolicy.Choose(new[] { f16, q8 }, f16, "chat", false, false);
        Assert.Same(q8, choice.Winner);
        Assert.Contains("faster", choice.Reason);
    }

    [Fact]
    public void Quantised_kv_that_diverges_beyond_the_tolerance_is_rejected_even_if_faster()
    {
        RuntimeOptimizationServerResult f16 = Kv("f16", 50);
        RuntimeOptimizationServerResult q4 = Kv("q4_0", 70, sample: "the quick red dragon", digest: "d2");
        KvChoice choice = KvCachePolicy.Choose(new[] { f16, q4 }, f16, "chat", false, false);
        Assert.Same(f16, choice.Winner);
        Assert.Contains("rejected", choice.Reason);
    }

    [Fact]
    public void Quantised_kv_is_kept_without_a_speed_gain_when_the_context_needs_the_vram()
    {
        RuntimeOptimizationServerResult f16 = Kv("f16", 50);
        RuntimeOptimizationServerResult q8 = Kv("q8_0", 50, digest: "d0");
        KvChoice choice = KvCachePolicy.Choose(new[] { f16, q8 }, f16, "chat", contextNeedsQuantisedKv: true, fullPrecisionFailed: false);
        Assert.Same(q8, choice.Winner);
        Assert.Contains("does not fit", choice.Reason);
    }

    [Fact]
    public void When_full_precision_cannot_launch_the_least_aggressive_quantised_type_is_the_reference()
    {
        RuntimeOptimizationServerResult failed = Kv("f16", 0);
        failed.Status = "failed";
        RuntimeOptimizationServerResult q8 = Kv("q8_0", 40, digest: "d0");
        RuntimeOptimizationServerResult q4 = Kv("q4_0", 41, digest: "d0"); // +2.5%: not worth the extra loss
        KvChoice choice = KvCachePolicy.Choose(new[] { failed, q8, q4 }, q8, "chat", false, fullPrecisionFailed: true);
        Assert.Same(q8, choice.Winner);
    }

    [Fact]
    public void Prefill_or_ttft_regression_blocks_a_quantised_choice()
    {
        RuntimeOptimizationServerResult f16 = Kv("f16", 50, prompt: 2000, ttft: 200);
        RuntimeOptimizationServerResult q8 = Kv("q8_0", 60, prompt: 1500, ttft: 200, digest: "d0");
        Assert.Same(f16, KvCachePolicy.Choose(new[] { f16, q8 }, f16, "chat", false, false).Winner);
        RuntimeOptimizationServerResult slowStart = Kv("q8_0", 60, prompt: 2000, ttft: 260, digest: "d0");
        Assert.Same(f16, KvCachePolicy.Choose(new[] { f16, slowStart }, f16, "chat", false, false).Winner);
    }

    [Fact]
    public void Kv_candidates_are_f16_q8_and_q4_without_the_baseline_precision()
    {
        var baseline = new LlamaPerformanceProfileDto { Id = "b", CacheTypeK = "q8_0", CacheTypeV = "q8_0" };
        List<string> types = RuntimeOptimizationService.BuildKvCandidates(baseline, "chat").Select(c => c.CacheTypeK).ToList();
        Assert.Equal(new[] { "f16", "q4_0" }, types);
    }

    [Theory]
    [InlineData("abcdef", "abcdef", 1.0)]
    [InlineData("abcdef", "abcxyz", 0.5)]
    [InlineData("abcdef", "", 0.0)]
    [InlineData(null, "abc", 0.0)]
    public void Prefix_ratio_is_the_identical_leading_share(string? reference, string? candidate, double expected) =>
        Assert.Equal(expected, KvQualityGuard.PrefixRatio(reference, candidate), 3);

    [Fact]
    public void Context_vram_estimate_uses_the_attention_layers_only()
    {
        // Qwen3.8-27B-like geometry: 64 blocks, every 4th full attention, 4 KV heads, 256+256 head dims.
        var model = new GgufModelInfo { BlockCount = 64, KeyValueHeadCount = 4, KeyLength = 256, ValueLength = 256, FullAttentionInterval = 4 };
        const long gib = 1L << 30;
        Assert.False(KvCachePolicy.ContextNeedsQuantisedKv(model, 8192, 13 * gib, 16 * gib));        // small context fits
        Assert.True(KvCachePolicy.ContextNeedsQuantisedKv(model, 262144, 13 * gib, 16 * gib));       // 256k f16 KV (~32 GiB) cannot
        Assert.False(KvCachePolicy.ContextNeedsQuantisedKv(null, 262144, 13 * gib, 16 * gib));       // unknown geometry: keep full precision
    }

    // ---------- registry ----------

    [Fact]
    public void Registry_is_once_per_key_and_a_new_runtime_build_makes_the_model_due_again()
    {
        var registry = new IdleCalibrationRegistry(_directory);
        DateTime t0 = new(2026, 10, 25, 3, 0, 0, DateTimeKind.Utc);
        string k1 = IdleCalibrationRegistry.KeyOf("m", "hw", "b11871", "chat");
        string k2 = IdleCalibrationRegistry.KeyOf("m", "hw", "b11900", "chat");
        Assert.True(registry.IsDue(k1, t0, TimeSpan.FromHours(24)));
        registry.MarkCalibrated(k1, "model", "chat", "idle", t0);
        Assert.False(registry.IsDue(k1, t0.AddDays(30), TimeSpan.FromHours(24)));
        Assert.True(registry.IsDue(k2, t0.AddDays(30), TimeSpan.FromHours(24))); // runtime build changed
        Assert.False(new IdleCalibrationRegistry(_directory).IsDue(k1, t0.AddDays(30), TimeSpan.FromHours(24))); // durable
    }

    [Fact]
    public void Registry_failures_back_off_by_the_interval_and_stop_after_three_attempts()
    {
        var registry = new IdleCalibrationRegistry(_directory);
        DateTime t = new(2026, 10, 25, 3, 0, 0, DateTimeKind.Utc);
        string key = "k";
        registry.MarkFailed(key, "m", "chat", "boom", t);
        Assert.False(registry.IsDue(key, t.AddHours(1), TimeSpan.FromHours(24)));
        Assert.True(registry.IsDue(key, t.AddHours(25), TimeSpan.FromHours(24)));
        registry.MarkFailed(key, "m", "chat", "boom", t.AddHours(25));
        registry.MarkFailed(key, "m", "chat", "boom", t.AddHours(50));
        Assert.False(registry.IsDue(key, t.AddDays(30), TimeSpan.FromHours(24)));
        registry.MarkCancelled("c", "m", "chat", "user_activity", t);
        Assert.False(registry.IsDue("c", t.AddMinutes(10), TimeSpan.FromHours(24)));
        Assert.True(registry.IsDue("c", t.AddMinutes(31), TimeSpan.FromHours(24)));
    }

    // ---------- activity tracker ----------

    [Theory]
    [InlineData("POST", "/api/v1/chat/completions", true)]
    [InlineData("POST", "/v1/chat/completions", true)]
    [InlineData("POST", "/v1/messages", true)]
    [InlineData("POST", "/v1/messages/count_tokens", false)]
    [InlineData("GET", "/v1/chat/completions", false)]
    [InlineData("POST", "/api/v1/maintenance/run-now", false)]
    public void Only_generation_requests_count_as_activity(string method, string path, bool expected) =>
        Assert.Equal(expected, GenerationActivityTracker.IsGenerationRequest(method, path));

    // ---------- idle service ----------

    private sealed class Rig
    {
        public DateTime Now = new(2026, 10, 25, 3, 0, 0, DateTimeKind.Utc);
        public bool Enabled = true, CalibrateWhenIdle = true, Gaming;
        public bool? OnBattery;
        public int? Gpu = 2;
        public string RuntimeTag = "b11871";
        public readonly List<RuntimeOptimizationRequest> Started = new();
        public readonly List<string> Cancelled = new();
        public RuntimeOptimizationRun? Run;
        public GenerationActivityTracker Activity = null!;
        public IdleCalibrationService Service = null!;
        public IdleCalibrationRegistry Registry = null!;
        public List<CalibrationTarget> Targets = new();
        public bool FreshModel;
    }

    private Rig NewRig(params string[] workloads)
    {
        string model = Path.Combine(_directory, "model.gguf");
        File.WriteAllBytes(model, new byte[4096]);
        var rig = new Rig();
        rig.Targets = workloads.Select(w => new CalibrationTarget(model, w)).ToList();
        rig.Activity = new GenerationActivityTracker(() => rig.Now);
        rig.Registry = new IdleCalibrationRegistry(Path.Combine(_directory, "reg"));
        var deps = new IdleCalibrationDependencies
        {
            UtcNow = () => rig.Now,
            Settings = () => new AutoMaintenanceSettings { Enabled = rig.Enabled, CalibrateWhenIdle = rig.CalibrateWhenIdle, IntervalHours = 24 },
            IsGamingActive = () => rig.Gaming,
            IsOnBattery = () => rig.OnBattery,
            GpuUtilizationPercent = _ => Task.FromResult(rig.Gpu),
            Targets = () => rig.Targets,
            HasFreshModel = () => rig.FreshModel,
            Environment = _ => Task.FromResult<CalibrationEnvironment?>(new CalibrationEnvironment("text-cuda", rig.RuntimeTag, "rtx5080")),
            Start = request =>
            {
                rig.Started.Add(request);
                rig.Run = new RuntimeOptimizationRun { Id = "run" + rig.Started.Count, Status = "running", Trigger = request.Trigger, Workload = request.Workload, ModelPath = request.ModelPath };
                return rig.Run;
            },
            Get = id => rig.Run,
            Cancel = id => { rig.Cancelled.Add(id); if (rig.Run != null) rig.Run.Status = "cancelled"; return true; }
        };
        rig.Service = new IdleCalibrationService(deps, rig.Activity, rig.Registry, TimeSpan.FromMinutes(10));
        return rig;
    }

    private static void MakeIdle(Rig rig) => rig.Now = rig.Now.AddMinutes(11);

    [Fact]
    public async Task A_freshly_downloaded_model_is_tuned_after_a_short_pause_not_the_full_idle_threshold()
    {
        Rig rig = NewRig("chat");
        rig.FreshModel = true;
        rig.Now = rig.Now.AddMinutes(3); // past the 2-minute fresh-model pause, well short of the 10-minute default

        Assert.StartsWith("started:", await rig.Service.TickAsync(default));
    }

    [Fact]
    public async Task Without_a_fresh_model_three_idle_minutes_are_not_enough()
    {
        Rig rig = NewRig("chat");
        rig.Now = rig.Now.AddMinutes(3);

        Assert.Equal("not_idle", await rig.Service.TickAsync(default));
    }

    [Fact]
    public async Task Nothing_runs_while_the_user_was_active_recently_or_the_setting_is_off()
    {
        Rig rig = NewRig("chat");
        Assert.Equal("not_idle", await rig.Service.TickAsync(default));
        MakeIdle(rig);
        rig.CalibrateWhenIdle = false;
        Assert.Equal("disabled", await rig.Service.TickAsync(default));
        rig.CalibrateWhenIdle = true; rig.Enabled = false;
        Assert.Equal("disabled", await rig.Service.TickAsync(default));
        Assert.Empty(rig.Started);
    }

    [Fact]
    public async Task Gaming_battery_and_a_busy_gpu_each_hold_the_calibration_back()
    {
        Rig rig = NewRig("chat");
        MakeIdle(rig);
        rig.Gaming = true;
        Assert.Equal("gaming", await rig.Service.TickAsync(default));
        rig.Gaming = false; rig.OnBattery = true;
        Assert.Equal("on_battery", await rig.Service.TickAsync(default));
        rig.OnBattery = false; rig.Gpu = 60;
        Assert.Equal("gpu_busy", await rig.Service.TickAsync(default));
        // A busy GPU is itself activity: the idle clock restarted, so the next idle tick is still "not_idle".
        rig.Gpu = 1;
        Assert.Equal("not_idle", await rig.Service.TickAsync(default));
        Assert.Empty(rig.Started);
        MakeIdle(rig);
        Assert.StartsWith("started:", await rig.Service.TickAsync(default));
    }

    [Fact]
    public async Task An_idle_machine_starts_exactly_one_idle_triggered_calibration_without_downloads()
    {
        Rig rig = NewRig("chat", "coding");
        MakeIdle(rig);
        Assert.Equal("started:model:chat", await rig.Service.TickAsync(default));
        RuntimeOptimizationRequest request = Assert.Single(rig.Started);
        Assert.Equal("idle", request.Trigger);
        Assert.True(request.IncludeSpeculative);
        Assert.Equal("running", await rig.Service.TickAsync(default)); // no second run while one is active
        Assert.Single(rig.Started);
    }

    [Fact]
    public async Task A_prompt_cancels_the_running_calibration_at_once_and_it_is_retried_after_the_backoff()
    {
        Rig rig = NewRig("chat");
        MakeIdle(rig);
        await rig.Service.TickAsync(default);
        using (rig.Activity.Begin()) { }
        Assert.Equal(new[] { "run1" }, rig.Cancelled);
        Assert.Equal("finished", await rig.Service.TickAsync(default));
        string key = IdleCalibrationRegistry.KeyOf(LocalBenchmarkIdentity.ModelId(rig.Targets[0].ModelPath)!, "rtx5080", "b11871", "chat");
        Assert.Equal("cancelled", rig.Registry.Find(key)!.State);
        Assert.Equal("user_activity", rig.Registry.Find(key)!.Note);

        rig.Now = rig.Now.AddMinutes(15);
        Assert.Equal("nothing_due", await rig.Service.TickAsync(default)); // still inside the 30 minute cancel backoff
        rig.Now = rig.Now.AddMinutes(20);
        Assert.StartsWith("started:", await rig.Service.TickAsync(default));
    }

    [Fact]
    public async Task Gaming_starting_mid_run_cancels_it()
    {
        Rig rig = NewRig("chat");
        MakeIdle(rig);
        await rig.Service.TickAsync(default);
        rig.Gaming = true;
        Assert.Equal("finished", await rig.Service.TickAsync(default));
        Assert.Single(rig.Cancelled);
    }

    [Fact]
    public async Task A_calibrated_model_is_not_repeated_until_the_runtime_build_changes()
    {
        Rig rig = NewRig("chat");
        MakeIdle(rig);
        await rig.Service.TickAsync(default);
        rig.Run!.Status = "completed";
        Assert.Equal("finished", await rig.Service.TickAsync(default));
        MakeIdle(rig);
        Assert.Equal("nothing_due", await rig.Service.TickAsync(default));

        rig.RuntimeTag = "b11900"; // a managed runtime update invalidates the calibration
        MakeIdle(rig);
        Assert.StartsWith("started:", await rig.Service.TickAsync(default));
        Assert.Equal(2, rig.Started.Count);
    }

    [Fact]
    public async Task A_manual_run_marks_the_same_ledger_and_writes_measured_locally_history()
    {
        Rig rig = NewRig("chat");
        var history = new LocalBenchmarkHistoryStore(Path.Combine(_directory, "hist"));
        var winner = new RuntimeOptimizationServerResult
        {
            Status = "completed",
            Profile = new LlamaPerformanceProfileDto { Id = "w", SpeculativeType = "draft-mtp", ContextSize = 8192 },
            DecodeTokensPerSecond = 86, PromptTokensPerSecond = 2000, TimeToFirstTokenMilliseconds = 210, PeakGpuMemoryUsedMib = 14000
        };
        var plain = new RuntimeOptimizationServerResult
        {
            Status = "completed",
            Profile = new LlamaPerformanceProfileDto { Id = "p", SpeculativeType = "none", ContextSize = 8192 },
            DecodeTokensPerSecond = 53, PromptTokensPerSecond = 2200, TimeToFirstTokenMilliseconds = 175, Score = 1
        };
        var run = new RuntimeOptimizationRun
        {
            Id = "manual1", Status = "completed", Trigger = "manual", Workload = "chat", ModelPath = rig.Targets[0].ModelPath,
            Winner = winner.Profile, ServerCandidates = { plain, winner }
        };
        rig.Service.OnOptimizationCompleted(run, new RuntimeOptimizationCompletion(rig.Targets[0].ModelPath, "chat", "text-cuda", "b11871", "rtx5080"), history, "hwdigest");

        MakeIdle(rig);
        Assert.Equal("nothing_due", await rig.Service.TickAsync(default)); // the idle trigger sees the manual calibration
        IReadOnlyList<LocalBenchmarkEntry> rows = history.List();
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal(LocalBenchmarkHistoryStore.MeasuredLocally, r.Provenance));
        Assert.Contains(rows, r => r.Workload == "calibration:chat:manual" && r.DecodeTokensPerSecond == 86 && r.LaunchPlan.StartsWith("applied"));
        Assert.Contains(rows, r => r.DecodeTokensPerSecond == 53 && r.LaunchPlan.StartsWith("plain-baseline"));
    }
}
