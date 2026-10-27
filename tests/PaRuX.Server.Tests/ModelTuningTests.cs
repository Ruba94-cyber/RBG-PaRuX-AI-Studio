using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Automatic "tuning for your PC": download completion -> queued -> started within the short grace when idle, held back by
/// generating/Gaming/battery, cancelled on activity, result recorded; projectors/heads/assistant never queued. Hermetic: fake clock and runner.
/// </summary>
public sealed class ModelTuningTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "parux-tuning-" + Guid.NewGuid().ToString("N"));
    private readonly IDisposable _state;

    public ModelTuningTests()
    {
        Directory.CreateDirectory(_dir);
        _state = LocalAiPaths.UseStateDirForTests(Path.Combine(_dir, "state"));
    }

    public void Dispose()
    {
        _state.Dispose();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private string Model(string name)
    {
        string path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, System.Text.Encoding.UTF8.GetBytes(name + new string((char)120, 4096)));
        return path;
    }

    private sealed class NoopInner : IModelDownloadCompletionHandler
    {
        public readonly List<string> Seen = new();
        public void OnGgufDownloaded(string modelPath, string label) => Seen.Add(modelPath);
    }

    // ---------- download completion -> queued ----------

    [Theory]
    [InlineData("Llama-3.1-8B-Instruct-Q4_K_M.gguf")]          // dense
    [InlineData("Qwen3-30B-A3B-Instruct-Q4_K_M.gguf")]         // MoE
    [InlineData("Qwen3.8-27B-IQ3_XXS.gguf")]                    // embedded MTP
    [InlineData("Qwen2.5-VL-7B-Instruct-Q4_K_M.gguf")]         // vision model (its projector is a separate file)
    [InlineData("Qwen3-8B-Q4_K_M.gguf")]                        // model that may have a sidecar draft next to it
    public void A_completed_download_of_any_chat_family_is_queued_and_published_as_queued(string name)
    {
        var tuning = new ModelTuningTracker();
        var queue = new PostDownloadOptimizationQueue();
        var inner = new NoopInner();
        var handler = new PostDownloadOptimizationHandler(inner, queue, tuning);
        string path = Model(name);

        handler.OnGgufDownloaded(path, Path.GetFileNameWithoutExtension(path));

        Assert.Equal(new[] { path }, queue.Pending());
        ModelTuningItemDto item = Assert.Single(tuning.Snapshot().Items);
        Assert.Equal(ModelTuningStates.Queued, item.State);
        Assert.Equal("text", item.Kind);
        Assert.Equal(Path.GetFileNameWithoutExtension(path), item.ModelLabel);
        Assert.True(tuning.Snapshot().Active);
        Assert.Equal(new[] { path }, inner.Seen);
    }

    [Theory]
    [InlineData("mmproj-Qwen2.5-VL-7B-F16.gguf")]
    [InlineData("mtp-Qwen3.8-27B-Q4_0.gguf")]
    [InlineData("Qwen3-8B-draft-Q4_0.gguf")]
    public void Projectors_and_speculative_heads_are_never_queued_or_published(string name)
    {
        var tuning = new ModelTuningTracker();
        var queue = new PostDownloadOptimizationQueue();
        var handler = new PostDownloadOptimizationHandler(new NoopInner(), queue, tuning);

        handler.OnGgufDownloaded(Model(name), "x");

        Assert.Empty(queue.Pending());
        Assert.Empty(tuning.Snapshot().Items);
    }

    [Fact]
    public void A_role_assignment_queues_a_real_chat_model_but_not_a_projector_or_head()
    {
        var tuning = new ModelTuningTracker();
        var queue = new PostDownloadOptimizationQueue();

        IdleCalibrationComposition.OnRoleAssigned(tuning, "coding", Model("Qwen3-Coder-30B-A3B-Q4_K_M.gguf"), queue);
        IdleCalibrationComposition.OnRoleAssigned(tuning, "standard", Model("mmproj-F16.gguf"), queue);
        IdleCalibrationComposition.OnRoleAssigned(tuning, "standard", Model("mtp-Qwen3-Q4_0.gguf"), queue);

        ModelTuningItemDto item = Assert.Single(tuning.Snapshot().Items);
        Assert.Equal("coding", item.Workload);
        Assert.Single(queue.Pending());
    }

    [Fact]
    public void A_media_checkpoint_download_is_handed_to_the_media_queue_not_the_text_queue()
    {
        var queue = new PostDownloadOptimizationQueue();
        var media = new List<(string, string)>();
        var handler = new PostDownloadOptimizationHandler(new NoopInner(), queue, new ModelTuningTracker(), (p, l) => media.Add((p, l)));
        string checkpoint = Path.Combine(_dir, "z_image_turbo.safetensors");

        handler.OnMediaCheckpointDownloaded(checkpoint, "z_image_turbo");

        Assert.Equal(new[] { (checkpoint, "z_image_turbo") }, media);
        Assert.Empty(queue.Pending());
    }

    // ---------- draft-head proposal (never downloaded silently) ----------

    [Fact]
    public void A_draft_head_in_the_same_repo_is_proposed_with_its_size_but_nothing_is_downloaded()
    {
        var tuning = new ModelTuningTracker();
        var queue = new PostDownloadOptimizationQueue();
        string path = Model("Qwen3-8B-Q4_K_M.gguf");
        var handler = new PostDownloadOptimizationHandler(new NoopInner(), queue, tuning,
            repoIdFor: _ => "owner/Qwen3-8B-GGUF", hasLocalDraft: _ => false,
            listRepoFiles: (_, _) => Task.FromResult<IReadOnlyList<(string, long)>>(new List<(string, long)>
            {
                ("Qwen3-8B-Q4_K_M.gguf", 5_000_000_000), ("mmproj-F16.gguf", 900_000_000),
                ("mtp-Qwen3-8B-Q4_0.gguf", 420_000_000), ("mtp-Qwen3-8B-Q8_0.gguf", 800_000_000)
            }));
        string id = tuning.Queued("text", path, "Qwen3-8B-Q4_K_M", "chat");

        handler.ProposeDraftHeadAsync(path, id).GetAwaiter().GetResult();

        ModelTuningProposalDto proposal = tuning.Get(id)!.Proposal!;
        Assert.Equal("mtp-Qwen3-8B-Q4_0.gguf", proposal.FileName);
        Assert.Equal(420_000_000, proposal.SizeBytes);
        Assert.Equal("owner/Qwen3-8B-GGUF", proposal.RepoId);
        Assert.Equal(ModelTuningStates.Queued, tuning.Get(id)!.State);
    }

    [Fact]
    public void No_proposal_when_the_repo_has_no_head_or_the_listing_fails()
    {
        var tuning = new ModelTuningTracker();
        string path = Model("Llama-3.1-8B-Instruct-Q4_K_M.gguf");
        string id = tuning.Queued("text", path, "x", "chat");
        var none = new PostDownloadOptimizationHandler(new NoopInner(), new PostDownloadOptimizationQueue(), tuning,
            repoIdFor: _ => "o/r", hasLocalDraft: _ => false, listRepoFiles: (_, _) => Task.FromResult<IReadOnlyList<(string, long)>>(new List<(string, long)> { ("a-Q4.gguf", 1) }));
        none.ProposeDraftHeadAsync(path, id).GetAwaiter().GetResult();
        var offline = new PostDownloadOptimizationHandler(new NoopInner(), new PostDownloadOptimizationQueue(), tuning,
            repoIdFor: _ => "o/r", hasLocalDraft: _ => false, listRepoFiles: (_, _) => throw new HttpRequestException("offline"));
        offline.ProposeDraftHeadAsync(path, id).GetAwaiter().GetResult();

        Assert.Null(tuning.Get(id)!.Proposal);
    }

    // ---------- scheduling rig ----------

    private sealed class Rig
    {
        public DateTime Now = new(2026, 10, 27, 3, 0, 0, DateTimeKind.Utc);
        public bool Gaming;
        public bool? OnBattery;
        public int? Gpu = 2;
        public readonly List<RuntimeOptimizationRequest> Started = new();
        public readonly List<string> Cancelled = new();
        public RuntimeOptimizationRun? Run;
        public GenerationActivityTracker Activity = null!;
        public IdleCalibrationService Service = null!;
        public IdleCalibrationRegistry Registry = null!;
        public ModelTuningTracker Tuning = null!;
        public List<CalibrationTarget> Targets = new();
        public readonly HashSet<string> Fresh = new(StringComparer.OrdinalIgnoreCase);
        public readonly List<string> Handled = new();
    }

    private Rig NewRig(params string[] models)
    {
        var rig = new Rig();
        foreach (string name in models)
        {
            string path = Model(name);
            rig.Targets.Add(new CalibrationTarget(path, "chat"));
        }
        rig.Activity = new GenerationActivityTracker(() => rig.Now);
        rig.Registry = new IdleCalibrationRegistry(Path.Combine(_dir, "reg"));
        rig.Tuning = new ModelTuningTracker(() => rig.Now);
        var deps = new IdleCalibrationDependencies
        {
            UtcNow = () => rig.Now,
            Settings = () => new AutoMaintenanceSettings { Enabled = true, CalibrateWhenIdle = true, IntervalHours = 24 },
            IsGamingActive = () => rig.Gaming,
            IsOnBattery = () => rig.OnBattery,
            GpuUtilizationPercent = _ => Task.FromResult(rig.Gpu),
            Targets = () => rig.Targets,
            IsFreshModel = p => rig.Fresh.Contains(p),
            FreshModelHandled = p => { rig.Fresh.Remove(p); rig.Handled.Add(p); },
            Tuning = rig.Tuning,
            Environment = _ => Task.FromResult<CalibrationEnvironment?>(new CalibrationEnvironment("text-cuda", "b11871", "rtx5080")),
            Start = request =>
            {
                rig.Started.Add(request);
                rig.Run = new RuntimeOptimizationRun { Id = "run" + rig.Started.Count, Status = "running", Trigger = request.Trigger, Workload = request.Workload, ModelPath = request.ModelPath };
                return rig.Run;
            },
            Get = _ => rig.Run,
            Cancel = id => { rig.Cancelled.Add(id); if (rig.Run != null) rig.Run.Status = "cancelled"; return true; }
        };
        rig.Service = new IdleCalibrationService(deps, rig.Activity, rig.Registry, TimeSpan.FromMinutes(10));
        return rig;
    }

    private static ModelTuningItemDto OnlyItem(Rig rig) => Assert.Single(rig.Tuning.Snapshot().Items);

    [Fact]
    public async Task A_fresh_model_starts_after_the_short_grace_when_the_machine_is_idle_not_after_ten_minutes()
    {
        Rig rig = NewRig("Qwen3-8B-Q4_K_M.gguf");
        rig.Fresh.Add(rig.Targets[0].ModelPath);
        rig.Tuning.Queued("text", rig.Targets[0].ModelPath, "Qwen3-8B-Q4_K_M", "chat");

        rig.Now = rig.Now.AddSeconds(10);
        Assert.Equal("not_idle", await rig.Service.TickAsync(default));
        Assert.Equal(ModelTuningStates.Queued, OnlyItem(rig).State);
        Assert.Equal("Waiting for the PC to be idle", OnlyItem(rig).Reason);

        rig.Now = rig.Now.AddSeconds(15); // 25 s after the download, well short of the 10-minute threshold
        Assert.StartsWith("started:", await rig.Service.TickAsync(default));
        Assert.Single(rig.Started);
        Assert.Equal(ModelTuningStates.Running, OnlyItem(rig).State);
    }

    [Fact]
    public async Task A_model_that_is_not_fresh_still_waits_the_full_idle_period()
    {
        Rig rig = NewRig("Qwen3-8B-Q4_K_M.gguf", "Llama-3.1-8B-Instruct-Q4_K_M.gguf");
        rig.Fresh.Add(rig.Targets[1].ModelPath);

        rig.Now = rig.Now.AddSeconds(30);
        Assert.StartsWith("started:Llama", await rig.Service.TickAsync(default));
        rig.Run!.Status = "completed";
        await rig.Service.TickAsync(default);

        // The non-fresh model is next, but only 30 s of quiet exist: it must not start.
        Assert.Equal("not_idle", await rig.Service.TickAsync(default));
        Assert.Single(rig.Started);
    }

    [Fact]
    public async Task Nothing_starts_while_the_user_is_generating_and_the_reason_is_visible()
    {
        Rig rig = NewRig("Qwen3-8B-Q4_K_M.gguf");
        rig.Fresh.Add(rig.Targets[0].ModelPath);
        rig.Tuning.Queued("text", rig.Targets[0].ModelPath, "m", "chat");
        using IDisposable generating = rig.Activity.Begin();
        rig.Now = rig.Now.AddMinutes(5);

        Assert.Equal("not_idle", await rig.Service.TickAsync(default));
        Assert.Empty(rig.Started);
        Assert.Equal(ModelTuningStates.Queued, OnlyItem(rig).State);
    }

    [Fact]
    public async Task Gaming_battery_and_a_busy_gpu_hold_a_fresh_model_back_and_say_why()
    {
        Rig rig = NewRig("Qwen3-8B-Q4_K_M.gguf");
        rig.Fresh.Add(rig.Targets[0].ModelPath);
        rig.Tuning.Queued("text", rig.Targets[0].ModelPath, "m", "chat");
        rig.Now = rig.Now.AddMinutes(1);

        rig.Gaming = true;
        Assert.Equal("gaming", await rig.Service.TickAsync(default));
        Assert.Contains("Gaming", OnlyItem(rig).Reason);
        rig.Gaming = false; rig.OnBattery = true;
        Assert.Equal("on_battery", await rig.Service.TickAsync(default));
        Assert.Contains("battery", OnlyItem(rig).Reason);
        rig.OnBattery = false; rig.Gpu = 80;
        Assert.Equal("gpu_busy", await rig.Service.TickAsync(default));
        Assert.Contains("GPU", OnlyItem(rig).Reason);
        Assert.Empty(rig.Started);
        Assert.Equal(ModelTuningStates.Queued, OnlyItem(rig).State);
    }

    [Fact]
    public async Task A_prompt_cancels_a_running_tuning_cleanly_and_the_model_goes_back_to_queued()
    {
        Rig rig = NewRig("Qwen3-8B-Q4_K_M.gguf");
        rig.Fresh.Add(rig.Targets[0].ModelPath);
        rig.Now = rig.Now.AddSeconds(30);
        Assert.StartsWith("started:", await rig.Service.TickAsync(default));

        rig.Activity.Touch(); // the user sent a prompt

        Assert.Single(rig.Cancelled);
        Assert.Equal("finished", await rig.Service.TickAsync(default));
        ModelTuningItemDto item = OnlyItem(rig);
        Assert.Equal(ModelTuningStates.Queued, item.State);
        Assert.Contains("retry", item.Reason);
        Assert.Null(rig.Service.ActiveRunId);
    }

    [Fact]
    public async Task A_completed_run_records_the_measured_gain_as_applied()
    {
        Rig rig = NewRig("Qwen3.8-27B-IQ3_XXS.gguf");
        string path = rig.Targets[0].ModelPath;
        rig.Fresh.Add(path);
        rig.Now = rig.Now.AddSeconds(30);
        await rig.Service.TickAsync(default);
        RuntimeOptimizationRun run = rig.Run!;
        run.Status = "completed";
        run.ServerCandidates.Add(Candidate("plain", 50));
        run.ServerCandidates.Add(Candidate("mtp", 102));
        run.Winner = new LlamaPerformanceProfileDto { Id = "mtp" };

        rig.Service.OnOptimizationCompleted(run, new RuntimeOptimizationCompletion(path, "chat", "text-cuda", "b11871", "rtx5080"),
            new LocalBenchmarkHistoryStore(Path.Combine(_dir, "hist")), string.Empty);

        ModelTuningItemDto item = OnlyItem(rig);
        Assert.Equal(ModelTuningStates.Applied, item.State);
        Assert.Equal(104, item.GainPercent!.Value, 0.5);
        Assert.Contains(path, rig.Handled);
        await rig.Service.TickAsync(default);
        Assert.Equal(ModelTuningStates.Applied, OnlyItem(rig).State); // reaping must not overwrite the result
    }

    [Fact]
    public async Task No_gain_is_reported_honestly_as_skipped()
    {
        Rig rig = NewRig("Qwen3-8B-Q4_K_M.gguf");
        string path = rig.Targets[0].ModelPath;
        rig.Fresh.Add(path);
        rig.Now = rig.Now.AddSeconds(30);
        await rig.Service.TickAsync(default);
        RuntimeOptimizationRun run = rig.Run!;
        run.Status = "completed";
        run.ServerCandidates.Add(Candidate("plain", 50));
        run.ServerCandidates.Add(Candidate("kv", 51));
        run.Winner = new LlamaPerformanceProfileDto { Id = "kv" };

        rig.Service.OnOptimizationCompleted(run, new RuntimeOptimizationCompletion(path, "chat", "text-cuda", "b11871", "rtx5080"),
            new LocalBenchmarkHistoryStore(Path.Combine(_dir, "hist")), string.Empty);

        ModelTuningItemDto item = OnlyItem(rig);
        Assert.Equal(ModelTuningStates.Skipped, item.State);
        Assert.Equal("No faster profile found", item.Reason);
    }

    [Fact]
    public async Task A_failed_run_is_published_as_failed()
    {
        Rig rig = NewRig("Qwen3-8B-Q4_K_M.gguf");
        rig.Fresh.Add(rig.Targets[0].ModelPath);
        rig.Now = rig.Now.AddSeconds(30);
        await rig.Service.TickAsync(default);
        rig.Run!.Status = "failed";
        rig.Run.Error = "server did not start";

        await rig.Service.TickAsync(default);

        ModelTuningItemDto item = OnlyItem(rig);
        Assert.Equal(ModelTuningStates.Failed, item.State);
        Assert.Contains("server did not start", item.Reason);
    }

    [Theory]
    [InlineData("Media generation is active; stop it before tuning text inference.")]
    [InlineData("Gaming Mode is active; performance tuning is blocked.")]
    public async Task A_run_refused_because_the_machine_is_busy_is_queued_again_never_failed(string error)
    {
        Rig rig = NewRig("Qwen3-8B-Q4_K_M.gguf");
        rig.Fresh.Add(rig.Targets[0].ModelPath);
        rig.Now = rig.Now.AddSeconds(30);
        await rig.Service.TickAsync(default);
        rig.Run!.Status = "failed";
        rig.Run.Error = error;

        await rig.Service.TickAsync(default);

        ModelTuningItemDto item = OnlyItem(rig);
        Assert.Equal(ModelTuningStates.Queued, item.State);
        Assert.DoesNotContain("failed", item.Reason ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.False(string.IsNullOrWhiteSpace(item.Reason));
        Assert.True(rig.Tuning.Snapshot().Active);
    }

    [Fact]
    public async Task A_fresh_model_that_is_already_tuned_is_skipped_and_leaves_the_fresh_queue()
    {
        Rig rig = NewRig("Qwen3-8B-Q4_K_M.gguf");
        string path = rig.Targets[0].ModelPath;
        rig.Fresh.Add(path);
        string modelId = LocalBenchmarkIdentity.ModelId(path)!;
        rig.Registry.MarkCalibrated(IdleCalibrationRegistry.KeyOf(modelId, "rtx5080", "b11871", "chat"), "m", "chat", "idle", rig.Now);
        rig.Now = rig.Now.AddSeconds(30);

        await rig.Service.TickAsync(default);

        Assert.Equal(ModelTuningStates.Skipped, OnlyItem(rig).State);
        Assert.Equal("Already tuned for this PC", OnlyItem(rig).Reason);
        Assert.Contains(path, rig.Handled);
        Assert.Empty(rig.Started);
    }

    [Fact]
    public async Task Cancelling_a_queued_model_keeps_it_from_starting()
    {
        Rig rig = NewRig("Qwen3-8B-Q4_K_M.gguf");
        string path = rig.Targets[0].ModelPath;
        rig.Fresh.Add(path);
        rig.Tuning.Queued("text", path, "m", "chat");

        Assert.False(rig.Service.CancelByUser(path)); // nothing running: the queued job is simply declined
        rig.Now = rig.Now.AddMinutes(11);

        Assert.NotEqual("started", (await rig.Service.TickAsync(default)).Split(':')[0]);
        Assert.Empty(rig.Started);
    }

    [Fact]
    public async Task Cancelling_a_running_tuning_marks_it_skipped_by_the_user()
    {
        Rig rig = NewRig("Qwen3-8B-Q4_K_M.gguf");
        string path = rig.Targets[0].ModelPath;
        rig.Fresh.Add(path);
        rig.Now = rig.Now.AddSeconds(30);
        await rig.Service.TickAsync(default);

        Assert.True(rig.Service.CancelByUser(path));
        await rig.Service.TickAsync(default);

        Assert.Equal(ModelTuningStates.Skipped, OnlyItem(rig).State);
        Assert.Equal("Cancelled by you", OnlyItem(rig).Reason);
    }

    private static RuntimeOptimizationServerResult Candidate(string id, double decode) => new()
    {
        Status = "completed",
        Profile = new LlamaPerformanceProfileDto { Id = id },
        DecodeTokensPerSecond = decode
    };

    // ---------- state endpoint content ----------

    [Fact]
    public void The_tracker_snapshot_is_what_the_state_endpoint_publishes_newest_first_with_only_the_five_states()
    {
        DateTime now = new(2026, 10, 27, 3, 0, 0, DateTimeKind.Utc);
        var tuning = new ModelTuningTracker(() => now);
        string a = tuning.Queued("text", "C:\\m\\a.gguf", "a", "chat");
        now = now.AddSeconds(1);
        string b = tuning.Queued("media", "C:\\m\\b.safetensors", "b", "z-image");
        now = now.AddSeconds(1);
        tuning.Running("text", "C:\\m\\a.gguf", "a", "chat");
        tuning.Applied(a, 104.4);
        tuning.Failed(b, "boom");

        ModelTuningStatusDto status = tuning.Snapshot();

        Assert.False(status.Active);
        Assert.Equal(2, status.Items.Count);
        Assert.All(status.Items, i => Assert.Contains(i.State, new[] { "queued", "running", "applied", "skipped", "failed" }));
        ModelTuningItemDto applied = status.Items.Single(i => i.Id == a);
        Assert.Equal("applied", applied.State);
        Assert.Equal(104.4, applied.GainPercent);
        Assert.Equal("failed", status.Items.Single(i => i.Id == b).State);
        Assert.Equal("media", status.Items.Single(i => i.Id == b).Kind);
    }

    [Fact]
    public void The_tracker_is_bounded()
    {
        var tuning = new ModelTuningTracker();
        for (int i = 0; i < ModelTuningTracker.MaximumItems + 10; i++) tuning.Queued("text", "C:\\m\\" + i + ".gguf", "m" + i, "chat");
        Assert.Equal(ModelTuningTracker.MaximumItems, tuning.Snapshot().Items.Count);
    }

    [Fact]
    public void Automatic_media_tuning_is_on_for_new_settings()
    {
        Assert.True(new AutoMaintenanceSettings().AutoOptimizeMedia);
        Assert.True(new MaintenanceSettingsDto().AutoOptimizeMedia);
        Assert.True(AutoMaintenanceModelsRoundTrip(false) == false); // a stored explicit "off" stays off
    }

    private static bool AutoMaintenanceModelsRoundTrip(bool stored)
    {
        var parsed = System.Text.Json.JsonSerializer.Deserialize<AutoMaintenanceSettings>(
            "{\"autoOptimizeMedia\":" + (stored ? "true" : "false") + "}",
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        return parsed!.AutoOptimizeMedia;
    }
}
