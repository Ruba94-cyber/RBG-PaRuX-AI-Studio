using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>Hermetic: planner is pure; the optimizer runs against a fake ComfyUI harness with scripted timings and images.</summary>
public sealed class ComfyMediaOptimizerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "parux-comfyopt-" + Guid.NewGuid().ToString("N"));
    private DateTime _now = new(2026, 10, 25, 12, 0, 0, DateTimeKind.Utc);
    public ComfyMediaOptimizerTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private const string Help = @"
usage: main.py [--cuda-malloc | --disable-cuda-malloc] [--use-pytorch-cross-attention | --use-sage-attention]
  --gpu-only
  --highvram
  --lowvram
  --novram
  --reserve-vram RESERVE_VRAM
  --disable-dynamic-vram
  --enable-dynamic-vram
  --fast [FAST ...]     Enable some untested and potentially quality
                        deteriorating optimizations. Current valid optimizations:
                        fp16_accumulation fp8_matrix_mult cublas_ops autotune
  --debug-hang          Enable stack trace dumps
";

    private static ComfyCapabilities Caps => ComfyHelpParser.Parse(Help);
    private static ComfyGpuFacts Blackwell16(long free = 15L << 30) =>
        new("NVIDIA GeForce RTX 5080", 16L << 30, free, 12.0, "617.14", true, true);
    private static readonly ComfyModelFootprint Small = new(4L << 30, 2L << 30, 335_000_000, 1024, 1024);
    private static readonly ComfyModelFootprint Huge = new(14L << 30, 8L << 30, 335_000_000, 1024, 1024);
    private static readonly ComfyEnvironmentFacts Cuda = new(true, false, false, false);

    // ---- help parsing / planner ---------------------------------------------------------------------------------

    [Fact]
    public void Help_parser_reads_flags_and_fast_options()
    {
        ComfyCapabilities c = Caps;
        Assert.True(c.Supports("--cuda-malloc"));
        Assert.True(c.Supports("--use-pytorch-cross-attention"));
        Assert.False(c.Supports("--use-flash-attention"));
        Assert.Equal(new[] { "autotune", "cublas_ops", "fp16_accumulation", "fp8_matrix_mult" }, c.FastOptions.OrderBy(x => x).ToArray());
        Assert.Empty(ComfyHelpParser.Parse("").Flags);
    }

    [Fact]
    public void Plan_is_deterministic_and_every_choice_has_a_reason()
    {
        var a = ComfyLaunchPlanner.Plan(Blackwell16(), Small, Cuda, Caps, ComfyPlanTier.Aggressive);
        var b = ComfyLaunchPlanner.Plan(Blackwell16(), Small, Cuda, Caps, ComfyPlanTier.Aggressive);
        Assert.Equal(a.Key, b.Key);
        Assert.All(a.Decisions, d => Assert.False(string.IsNullOrWhiteSpace(d.Reason)));
    }

    [Fact]
    public void Model_that_fits_stays_resident_without_dynamic_streaming()
    {
        var p = ComfyLaunchPlanner.Plan(Blackwell16(), Small, Cuda, Caps, ComfyPlanTier.Safe);
        Assert.Contains("--disable-dynamic-vram", p.Arguments);
        Assert.Contains("--gpu-only", p.Arguments);
        Assert.DoesNotContain("--enable-dynamic-vram", p.Arguments);
        Assert.Equal("default", p.TextEncoderDevice);
    }

    [Fact]
    public void Model_that_does_not_fit_streams_and_offloads_the_text_encoder()
    {
        var p = ComfyLaunchPlanner.Plan(Blackwell16(), Huge, Cuda, Caps, ComfyPlanTier.Safe);
        Assert.Contains("--enable-dynamic-vram", p.Arguments);
        Assert.Contains("--lowvram", p.Arguments);
        Assert.Equal("cpu", p.TextEncoderDevice);
        Assert.DoesNotContain("--gpu-only", p.Arguments);
    }

    [Fact]
    public void Flags_the_installed_comfyui_does_not_list_are_never_emitted()
    {
        ComfyCapabilities old = ComfyHelpParser.Parse("--reserve-vram X\n--highvram\n--lowvram");
        var p = ComfyLaunchPlanner.Plan(Blackwell16(), Small, Cuda, old, ComfyPlanTier.Aggressive);
        Assert.All(p.Arguments.Where(a => a.StartsWith("--", StringComparison.Ordinal)), a => Assert.True(old.Supports(a), a));
        Assert.DoesNotContain("--fast", p.Arguments);
        Assert.DoesNotContain("--cuda-malloc", p.Arguments);
    }

    [Fact]
    public void Fast_is_aggressive_tier_only_and_marked_as_changing_numerics()
    {
        Assert.DoesNotContain("--fast", ComfyLaunchPlanner.Plan(Blackwell16(), Small, Cuda, Caps, ComfyPlanTier.Safe).Arguments);
        var p = ComfyLaunchPlanner.Plan(Blackwell16(), Small, Cuda, Caps, ComfyPlanTier.Aggressive);
        Assert.Contains("--fast", p.Arguments);
        Assert.True(p.ChangesNumerics);
        Assert.False(ComfyLaunchPlanner.Plan(Blackwell16(), Small, Cuda, Caps, ComfyPlanTier.Safe).ChangesNumerics);
    }

    [Fact]
    public void Pre_ada_gpu_gets_no_fast_options_and_sage_only_when_it_works()
    {
        var turing = new ComfyGpuFacts("RTX 2070", 8L << 30, 7L << 30, 7.5, "560", true, true);
        Assert.DoesNotContain("--fast", ComfyLaunchPlanner.Plan(turing, Small, Cuda, Caps, ComfyPlanTier.Aggressive).Arguments);
        var withSage = new ComfyEnvironmentFacts(true, true, false, false);
        Assert.Contains("--use-sage-attention", ComfyLaunchPlanner.Plan(Blackwell16(), Small, withSage, Caps, ComfyPlanTier.Aggressive).Arguments);
        Assert.DoesNotContain("--use-sage-attention", ComfyLaunchPlanner.Plan(Blackwell16(), Small, withSage, Caps, ComfyPlanTier.Safe).Arguments);
    }

    [Fact]
    public void CPU_only_pytorch_yields_no_tuning_and_says_why()
    {
        var p = ComfyLaunchPlanner.Plan(Blackwell16(), Small, new ComfyEnvironmentFacts(false, false, false, false), Caps, ComfyPlanTier.Aggressive);
        Assert.Empty(p.Arguments);
        Assert.Contains("no CUDA", p.Decisions[0].Reason);
    }

    // ---- optimizer ------------------------------------------------------------------------------------------------

    private sealed class FakeHarness : IComfyCalibrationHarness
    {
        public bool Gaming, Busy;
        public ComfyEngineSnapshot Snapshot = new("hw1", "0.37.0", "2.14.1+cu130", "model1", "z-image-turbo",
            Blackwell16(), Small, Cuda, Caps);
        /// <summary>args key -> (cold, warm, peakMiB, ssim vs baseline). Missing key = failure.</summary>
        public Dictionary<string, (double Cold, double Warm, long PeakMib, double Ssim, bool Identical)> Script = new();
        public List<string> Runs = new();
        public Func<IReadOnlyList<string>, Task>? OnRun;

        public bool IsGamingActive() => Gaming;
        public bool IsBusy() => Busy;
        public Task<ComfyEngineSnapshot> InspectAsync(CancellationToken ct) => Task.FromResult(Snapshot);
        public async Task<ComfyCandidateRun> RunAsync(IReadOnlyList<string> args, int warmRuns, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            string key = string.Join(' ', args);
            Runs.Add(key);
            if (OnRun is not null) await OnRun(args);
            if (!Script.TryGetValue(key, out var s)) throw new InvalidOperationException("scripted failure for: " + key);
            var samples = new List<ComfyRunSample> { new(ComfyMediaOptimizer.SeedFor(0), s.Cold, key + "#0") };
            for (int i = 1; i <= warmRuns; i++) samples.Add(new(ComfyMediaOptimizer.SeedFor(i), s.Warm, key + "#" + i));
            return new ComfyCandidateRun(samples, s.PeakMib << 20, 4.0);
        }
        public Task<ComfyImageComparison> CompareAsync(string reference, string candidate, CancellationToken ct)
        {
            string key = candidate[..candidate.LastIndexOf('#')];
            var s = Script[key];
            return Task.FromResult(new ComfyImageComparison(s.Identical, s.Ssim));
        }
    }

    private (ComfyMediaOptimizer Opt, ComfyOptimizationStore Store, LocalBenchmarkHistoryStore History) Make(FakeHarness h)
    {
        var store = new ComfyOptimizationStore(_dir);
        var hist = new LocalBenchmarkHistoryStore(_dir);
        return (new ComfyMediaOptimizer(h, store, hist, () => _now), store, hist);
    }

    private static string Key(ComfyEngineSnapshot s, string id) =>
        ComfyMediaOptimizer.BuildCandidates(s).First(c => c.Id == id).Plan.Key;

    private FakeHarness Scripted(Action<FakeHarness, Func<string, string>>? tweak = null)
    {
        var h = new FakeHarness();
        string K(string id) => Key(h.Snapshot, id);
        h.Script[K("legacy")] = (30, 10.0, 9000, 1, true);
        h.Script[K("planned-safe")] = (30, 9.9, 9000, 1, true);       // inside the 3% noise margin
        h.Script[K("planned-alt-memory")] = (30, 10.1, 9000, 1, true);
        h.Script[K("planned-fp16acc")] = (31, 9.0, 9100, 0.995, false);
        h.Script[K("planned-fp8")] = (31, 8.2, 8000, 0.990, false);
        h.Script[K("planned-fast")] = (31, 8.0, 8000, 0.970, false);   // fastest but the picture changed too much
        tweak?.Invoke(h, K);
        return h;
    }

    [Fact]
    public void Race_includes_the_other_memory_strategy_only_when_it_could_physically_fit()
    {
        var snap = new FakeHarness().Snapshot;
        var fits = ComfyMediaOptimizer.BuildCandidates(snap);
        Assert.Equal("legacy", fits[0].Id);
        string alt = fits.Single(c => c.Id == "planned-alt-memory").Plan.Key;
        Assert.Contains("--enable-dynamic-vram", alt);          // planner chose resident, so the alternative streams
        Assert.DoesNotContain("--gpu-only", alt);

        var tooBig = snap with { Footprint = new ComfyModelFootprint(20L << 30, 8L << 30, 0, 1024, 1024) };
        Assert.DoesNotContain(ComfyMediaOptimizer.BuildCandidates(tooBig), c => c.Id == "planned-alt-memory");
        Assert.True(ComfyMediaOptimizer.BuildCandidates(snap).Count <= ComfyMediaOptimizer.MaximumCandidates);
    }

    [Fact]
    public async Task Fastest_equivalent_candidate_wins_and_non_equivalent_is_refused()
    {
        FakeHarness h = Scripted();
        var (opt, store, hist) = Make(h);
        ComfyOptimizationRecord r = await opt.RunAsync(false, default);

        Assert.Equal("improved", r.Outcome);
        Assert.Equal("planned-fp8", r.ChosenId);
        Assert.Contains("--fast", r.ChosenArguments);
        Assert.Equal("not_equivalent", r.Candidates.Single(c => c.Id == "planned-fast").Status);
        Assert.Contains("measured locally", r.Summary);
        Assert.Equal("measured_locally", r.Provenance);
        Assert.Equal(r.ChosenId, store.Find(h.Snapshot.Fingerprint)!.ChosenId);
        LocalBenchmarkEntry adopted = hist.List().Single(e => e.LaunchPlan.Contains("[adopted]"));
        Assert.Equal(8200, adopted.WarmMs!.Value, 1);
        Assert.Equal(31000, adopted.LoadMs!.Value, 1);
        Assert.Equal("measured_locally", adopted.Provenance);
    }

    [Fact]
    public async Task Gains_inside_the_noise_margin_keep_the_defaults()
    {
        FakeHarness h = Scripted((hh, K) =>
        {
            hh.Script[K("planned-fp16acc")] = (30, 9.8, 9000, 1, true);
            hh.Script[K("planned-fp8")] = (30, 9.9, 9000, 1, true);
            hh.Script[K("planned-fast")] = (30, 9.75, 9000, 1, true);
        });
        var (opt, store, _) = Make(h);
        ComfyOptimizationRecord r = await opt.RunAsync(false, default);
        Assert.Equal("no_improvement", r.Outcome);
        Assert.Equal("legacy", r.ChosenId);
        Assert.Null(store.FindForStack(h.Snapshot.StackKey)); // not "improved": launch keeps the defaults
    }

    [Fact]
    public async Task A_candidate_that_wins_warm_but_costs_a_much_slower_cold_start_is_rejected()
    {
        FakeHarness h = Scripted((hh, K) => hh.Script[K("planned-fp8")] = (45, 8.0, 8000, 0.99, false));
        var (opt, _, _) = Make(h);
        ComfyOptimizationRecord r = await opt.RunAsync(false, default);
        Assert.NotEqual("planned-fp8", r.ChosenId);
        Assert.Contains("cold", r.Candidates.Single(c => c.Id == "planned-fp8").Detail);
    }

    [Fact]
    public async Task A_failing_candidate_is_recorded_and_the_race_continues()
    {
        FakeHarness h = Scripted((hh, K) => hh.Script.Remove(K("planned-fp16acc")));
        var (opt, _, _) = Make(h);
        ComfyOptimizationRecord r = await opt.RunAsync(false, default);
        Assert.Equal("failed", r.Candidates.Single(c => c.Id == "planned-fp16acc").Status);
        Assert.Equal("improved", r.Outcome);
    }

    [Fact]
    public async Task Failing_baseline_changes_nothing_and_stores_no_launch_override()
    {
        FakeHarness h = Scripted((hh, K) => hh.Script.Remove(K("legacy")));
        var (opt, store, _) = Make(h);
        ComfyOptimizationRecord r = await opt.RunAsync(false, default);
        Assert.Equal("failed", r.Outcome);
        Assert.Null(store.FindForStack(h.Snapshot.StackKey));
        Assert.Single(h.Runs); // the rest of the candidates were not even started
    }

    [Fact]
    public async Task Gaming_mode_defers_before_and_during_the_race()
    {
        FakeHarness h = Scripted();
        h.Gaming = true;
        var (opt, store, _) = Make(h);
        Assert.Equal("deferred", (await opt.RunAsync(false, default)).Outcome);
        Assert.Empty(h.Runs);

        h.Gaming = false;
        h.OnRun = _ => { h.Gaming = true; return Task.CompletedTask; };
        ComfyOptimizationRecord r = await opt.RunAsync(false, default);
        Assert.Equal("deferred", r.Outcome);
        Assert.Single(h.Runs);
        Assert.Empty(store.List());
    }

    [Fact]
    public async Task Cancellation_stops_the_race_and_persists_nothing()
    {
        FakeHarness h = Scripted();
        using var cts = new CancellationTokenSource();
        h.OnRun = _ => { cts.Cancel(); return Task.CompletedTask; };
        var (opt, store, _) = Make(h);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => opt.RunAsync(false, cts.Token));
        Assert.Empty(store.List());
        Assert.Equal("cancelled", opt.Status.Phase);
    }

    [Fact]
    public async Task CPU_only_torch_is_reported_not_tuned()
    {
        FakeHarness h = Scripted();
        h.Snapshot = h.Snapshot with { Env = new ComfyEnvironmentFacts(false, false, false, false) };
        var (opt, _, _) = Make(h);
        ComfyOptimizationRecord r = await opt.RunAsync(false, default);
        Assert.Equal("not_applicable", r.Outcome);
        Assert.Contains("CPU", r.Summary);
        Assert.Empty(h.Runs);
    }

    [Fact]
    public async Task Existing_result_for_the_same_fingerprint_is_not_remeasured_unless_forced()
    {
        FakeHarness h = Scripted();
        var (opt, _, _) = Make(h);
        await opt.RunAsync(false, default);
        int runs = h.Runs.Count;
        await opt.RunAsync(false, default);
        Assert.Equal(runs, h.Runs.Count);
        await opt.RunAsync(true, default);
        Assert.True(h.Runs.Count > runs);
    }

    [Fact]
    public async Task A_new_comfyui_version_or_model_invalidates_the_stored_result()
    {
        FakeHarness h = Scripted();
        var (opt, store, _) = Make(h);
        await opt.RunAsync(false, default);
        Assert.NotNull(store.FindForStack(h.Snapshot.StackKey));
        ComfyEngineSnapshot upgraded = h.Snapshot with { ComfyVersion = "0.38.0" };
        Assert.Null(store.FindForStack(upgraded.StackKey));
        Assert.Null(store.Find(h.Snapshot with { ModelId = "other" } is { } m ? m.Fingerprint : ""));
    }

    [Fact]
    public async Task Idle_tick_runs_only_when_enabled_idle_not_gaming_and_unmeasured()
    {
        FakeHarness h = Scripted();
        var (opt, _, _) = Make(h);
        Assert.Null(await opt.TickAsync(false, default));
        h.Busy = true;
        Assert.Null(await opt.TickAsync(true, default));
        h.Busy = false; h.Gaming = true;
        Assert.Null(await opt.TickAsync(true, default));
        Assert.Empty(h.Runs);
        h.Gaming = false;
        Assert.NotNull(await opt.TickAsync(true, default));
        int runs = h.Runs.Count;
        Assert.Null(await opt.TickAsync(true, default)); // already measured for this fingerprint
        Assert.Equal(runs, h.Runs.Count);
    }

    [Fact]
    public async Task Idle_tick_backs_off_for_a_day_after_a_failure()
    {
        FakeHarness h = Scripted((hh, K) => hh.Script.Remove(K("legacy")));
        var (opt, _, _) = Make(h);
        Assert.Equal("failed", (await opt.TickAsync(true, default))!.Outcome);
        int runs = h.Runs.Count;
        Assert.Null(await opt.TickAsync(true, default));
        Assert.Equal(runs, h.Runs.Count);
        _now = _now.AddHours(25);
        Assert.NotNull(await opt.TickAsync(true, default));
    }

    // ---- launch resolution ----------------------------------------------------------------------------------------

    [Fact]
    public async Task Launch_uses_measured_flags_only_while_the_installed_comfyui_still_supports_them()
    {
        FakeHarness h = Scripted();
        var (opt, store, _) = Make(h);
        await opt.RunAsync(false, default);

        var ok = ComfyLaunchArguments.Resolve(store, h.Snapshot.StackKey, Caps);
        Assert.Equal("measured", ok.Source);
        Assert.Contains("--fast", ok.Arguments);

        var downgraded = ComfyLaunchArguments.Resolve(store, h.Snapshot.StackKey, ComfyHelpParser.Parse("--reserve-vram X"));
        Assert.Equal("default", downgraded.Source);
        Assert.Equal(ComfyLaunchArguments.Legacy, downgraded.Arguments);

        Assert.Equal("default", ComfyLaunchArguments.Resolve(store, "other-stack", Caps).Source);
        Assert.Equal("default", ComfyLaunchArguments.Resolve(store, null, null).Source);
    }

    [Fact]
    public async Task A_failed_launch_with_measured_flags_reverts_to_defaults()
    {
        FakeHarness h = Scripted();
        var (opt, store, _) = Make(h);
        await opt.RunAsync(false, default);
        string fp = ComfyLaunchArguments.Resolve(store, h.Snapshot.StackKey, Caps).Fingerprint!;
        store.Invalidate(fp, "exited with code 1");
        Assert.Equal("default", ComfyLaunchArguments.Resolve(store, h.Snapshot.StackKey, Caps).Source);
    }

    // ---- automatic tuning right after a checkpoint download (visible state) -------------------------------------

    private static ModelTuningItemDto Only(ModelTuningTracker t) => Assert.Single(t.Snapshot().Items);

    [Fact]
    public async Task A_downloaded_checkpoint_is_queued_visibly_and_measured_after_the_short_grace()
    {
        FakeHarness h = Scripted();
        var (opt, _, _) = Make(h);
        var tuning = new ModelTuningTracker();
        opt.Tuning = tuning;

        opt.QueueCheckpoint("C:/models/z_image_turbo.safetensors", "z_image_turbo");
        Assert.Equal(ModelTuningStates.Queued, Only(tuning).State);
        Assert.Equal("media", Only(tuning).Kind);

        Assert.Null(await opt.TickAsync(true, default));      // still inside the grace
        Assert.Empty(h.Runs);
        _now = _now.AddSeconds(25);
        Assert.NotNull(await opt.TickAsync(true, default));

        ModelTuningItemDto item = Only(tuning);
        Assert.Equal(ModelTuningStates.Applied, item.State);
        Assert.Equal(22, item.GainPercent!.Value, 1);          // warm 10.0 s -> 8.2 s
        Assert.Equal(0, opt.PendingCount);
        Assert.Equal("z_image_turbo", item.ModelLabel);
    }

    [Fact]
    public async Task A_queued_checkpoint_is_held_back_by_gaming_generation_and_battery_and_never_runs_then()
    {
        FakeHarness h = Scripted();
        var (opt, _, _) = Make(h);
        var tuning = new ModelTuningTracker();
        opt.Tuning = tuning;
        opt.QueueCheckpoint("C:/models/a.safetensors", "a");
        _now = _now.AddMinutes(1);

        h.Gaming = true;
        Assert.Null(await opt.TickAsync(true, default));
        Assert.Contains("Gaming", Only(tuning).Reason);
        h.Gaming = false; h.Busy = true;
        Assert.Null(await opt.TickAsync(true, default));
        Assert.Contains("busy", Only(tuning).Reason);
        h.Busy = false; opt.IsOnBattery = () => true;
        Assert.Null(await opt.TickAsync(true, default));
        Assert.Contains("battery", Only(tuning).Reason);

        Assert.Empty(h.Runs);
        Assert.Equal(ModelTuningStates.Queued, Only(tuning).State);
        Assert.Equal(1, opt.PendingCount);
    }

    [Fact]
    public async Task With_automatic_media_tuning_off_a_queued_checkpoint_is_skipped_not_left_waiting()
    {
        FakeHarness h = Scripted();
        var (opt, _, _) = Make(h);
        var tuning = new ModelTuningTracker();
        opt.Tuning = tuning;
        opt.QueueCheckpoint("C:/models/a.safetensors", "a");

        Assert.Null(await opt.TickAsync(false, default));

        Assert.Equal(ModelTuningStates.Skipped, Only(tuning).State);
        Assert.Contains("Settings", Only(tuning).Reason);
        Assert.Empty(h.Runs);
    }

    [Fact]
    public async Task Cancelling_a_running_media_tuning_stops_it_cleanly_and_reports_it()
    {
        FakeHarness h = Scripted();
        var (opt, _, _) = Make(h);
        var tuning = new ModelTuningTracker();
        opt.Tuning = tuning;
        h.OnRun = _ => { opt.CancelCurrent(); return Task.CompletedTask; };
        opt.QueueCheckpoint("C:/models/a.safetensors", "a");
        _now = _now.AddMinutes(1);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => opt.TickAsync(true, default));

        Assert.Equal(ModelTuningStates.Skipped, Only(tuning).State);
        Assert.Equal("Cancelled by you", Only(tuning).Reason);
        Assert.Equal(0, opt.PendingCount);
    }

    [Fact]
    public async Task A_checkpoint_on_an_already_tuned_stack_is_reported_as_already_tuned()
    {
        FakeHarness h = Scripted();
        var (opt, _, _) = Make(h);
        await opt.RunAsync(false, default);
        var tuning = new ModelTuningTracker();
        opt.Tuning = tuning;
        opt.QueueCheckpoint("C:/models/b.safetensors", "b");
        _now = _now.AddMinutes(1);
        int runs = h.Runs.Count;

        Assert.Null(await opt.TickAsync(true, default));

        Assert.Equal(ModelTuningStates.Skipped, Only(tuning).State);
        Assert.Contains("Already tuned", Only(tuning).Reason);
        Assert.Equal(runs, h.Runs.Count);
    }

    [Fact]
    public async Task A_measurement_without_a_faster_flag_set_is_reported_as_no_faster_profile()
    {
        FakeHarness h = Scripted((hh, K) =>
        {
            hh.Script[K("planned-fp16acc")] = (30, 9.9, 9000, 1, true);
            hh.Script[K("planned-fp8")] = (30, 9.9, 9000, 1, true);
            hh.Script[K("planned-fast")] = (30, 9.9, 9000, 1, true);
        });
        var (opt, _, _) = Make(h);
        var tuning = new ModelTuningTracker();
        opt.Tuning = tuning;
        opt.QueueCheckpoint("C:/models/a.safetensors", "a");
        _now = _now.AddMinutes(1);

        await opt.TickAsync(true, default);

        Assert.Equal(ModelTuningStates.Skipped, Only(tuning).State);
        Assert.Equal("No faster profile found", Only(tuning).Reason);
    }
}
