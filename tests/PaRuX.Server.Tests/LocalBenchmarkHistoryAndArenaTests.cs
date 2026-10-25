using PaRuX.Contracts;
using PaRuX.Server;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class LocalBenchmarkHistoryAndArenaTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "parux-arena-" + Guid.NewGuid().ToString("N"));
    public LocalBenchmarkHistoryAndArenaTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private static LocalBenchmarkEntry Entry(string backend, double tps, string model = "m1", string hw = "hw1", DateTime? at = null) => new()
    {
        HardwareDigest = hw, ModelId = model, ModelLabel = model, RuntimeBackend = backend, DecodeTokensPerSecond = tps,
        TimestampUtc = at ?? DateTime.UtcNow, Workload = "w"
    };

    [Fact]
    public void History_roundtrips_atomically_and_labels_provenance()
    {
        var store = new LocalBenchmarkHistoryStore(_dir);
        Assert.True(store.Append(Entry("text-cuda", 50)));
        var reread = new LocalBenchmarkHistoryStore(_dir).List();
        Assert.Single(reread);
        Assert.Equal("measured_locally", reread[0].Provenance);
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    public void History_rejects_entries_without_measurement_or_identity_and_bad_numbers()
    {
        var store = new LocalBenchmarkHistoryStore(_dir);
        Assert.False(store.Append(new LocalBenchmarkEntry { HardwareDigest = "h", ModelId = "m" }));
        Assert.False(store.Append(Entry("text-cuda", double.NaN)));
        Assert.False(store.Append(new LocalBenchmarkEntry { DecodeTokensPerSecond = 5 }));
        Assert.Empty(store.List());
    }

    [Fact]
    public void History_is_bounded_dropping_oldest()
    {
        var store = new LocalBenchmarkHistoryStore(_dir);
        for (int i = 0; i < LocalBenchmarkHistoryStore.MaximumEntries + 5; i++)
            store.Append(Entry("text-cuda", 10 + i, at: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(i)));
        IReadOnlyList<LocalBenchmarkEntry> all = store.List();
        Assert.Equal(LocalBenchmarkHistoryStore.MaximumEntries, all.Count);
        Assert.DoesNotContain(all, e => e.DecodeTokensPerSecond == 10);
    }

    [Fact]
    public void History_ignores_corrupt_and_newer_schema_files()
    {
        File.WriteAllText(Path.Combine(_dir, LocalBenchmarkHistoryStore.FileName), "{ not json");
        Assert.Empty(new LocalBenchmarkHistoryStore(_dir).List());
        File.WriteAllText(Path.Combine(_dir, LocalBenchmarkHistoryStore.FileName), "{\"schemaVersion\":99,\"entries\":[]}");
        var store = new LocalBenchmarkHistoryStore(_dir);
        Assert.Empty(store.List());
        Assert.True(store.Append(Entry("text-cuda", 5)));
    }

    [Fact]
    public void Measured_lookup_compares_backends_on_one_model_and_one_machine()
    {
        var store = new LocalBenchmarkHistoryStore(_dir);
        store.Append(Entry("text-cuda", 80, "big"));
        store.Append(Entry("text-vulkan", 50, "big"));
        store.Append(Entry("text-cuda", 300, "tiny")); // different model, single backend: must not be compared
        store.Append(Entry("text-vulkan", 999, "big", hw: "other-pc"));
        IReadOnlyList<AdvisorBenchmark> result = store.MeasuredDecodeByBackend("hw1");
        Assert.Equal(2, result.Count);
        Assert.Equal(80, result.Single(b => b.Backend == "text-cuda").TokensPerSecond);
        Assert.Equal(50, result.Single(b => b.Backend == "text-vulkan").TokensPerSecond);
        Assert.Empty(store.MeasuredDecodeByBackend("unknown-hw"));
    }

    [Fact]
    public void Advisor_says_measured_locally_when_history_shows_a_faster_installed_backend()
    {
        var store = new LocalBenchmarkHistoryStore(_dir);
        store.Append(Entry("text-cuda", 100));
        store.Append(Entry("text-vulkan", 60));
        var input = new AdvisorInput(true, new List<AdvisorGpu>(), new AdvisorCpu(),
            new List<AdvisorRuntime> { new("text-vulkan", "b1", ComponentOwnership.PARUX_MANAGED, Active: true), new("text-cuda", "b1", ComponentOwnership.PARUX_MANAGED) },
            new Dictionary<string, string>(), store.MeasuredDecodeByBackend("hw1"));
        var suggestions = RuntimeBackendAdvisor.Advise(input);
        Assert.Contains(suggestions, s => s.Backend == "text-cuda" && s.EvidenceLevel == "measured_locally");
    }

    [Fact]
    public void Model_identity_is_stable_across_rename_and_changes_with_content()
    {
        string a = Path.Combine(_dir, "a.gguf"), b = Path.Combine(_dir, "b.gguf");
        File.WriteAllBytes(a, new byte[] { 1, 2, 3, 4 });
        File.Copy(a, b);
        Assert.Equal(LocalBenchmarkIdentity.ModelId(a), LocalBenchmarkIdentity.ModelId(b));
        File.WriteAllBytes(b, new byte[] { 9, 2, 3, 4 });
        Assert.NotEqual(LocalBenchmarkIdentity.ModelId(a), LocalBenchmarkIdentity.ModelId(b));
        Assert.Null(LocalBenchmarkIdentity.ModelId(Path.Combine(_dir, "missing.gguf")));
    }

    [Fact]
    public void Hardware_digest_ignores_runtimes_but_not_gpu_or_ram()
    {
        string d1 = LocalBenchmarkIdentity.HardwareDigest(null, 16, "cpu");
        Assert.Equal(d1, LocalBenchmarkIdentity.HardwareDigest(null, 16, "cpu"));
        Assert.NotEqual(d1, LocalBenchmarkIdentity.HardwareDigest(null, 32, "cpu"));
    }

    // ---- Arena ----

    private sealed class FakeRunner : IArenaModelRunner
    {
        public readonly List<string> Events = new();
        public int Active, MaxActive;
        public string? FailLoad, HangOn;
        public Func<string, string> Answer = _ => "8";
        public bool Restored;
        public Task<IArenaModelSession> LoadAsync(string modelPath, CancellationToken ct)
        {
            Events.Add("load:" + Path.GetFileName(modelPath));
            if (FailLoad is not null && modelPath.Contains(FailLoad)) throw new InvalidOperationException("boom");
            Active++; MaxActive = Math.Max(MaxActive, Active);
            return Task.FromResult<IArenaModelSession>(new Session(this, modelPath));
        }
        public Task RestoreAsync(CancellationToken ct) { Restored = true; return Task.CompletedTask; }
        private sealed class Session(FakeRunner o, string path) : IArenaModelSession
        {
            public double LoadMs => 5; public string Backend => "text-cuda"; public string Build => "b1"; public string LaunchPlan => "plan";
            public async Task<ArenaGeneration> GenerateAsync(string prompt, int maxTokens, CancellationToken ct)
            {
                if (o.HangOn is not null && path.Contains(o.HangOn)) await Task.Delay(Timeout.Infinite, ct);
                return new ArenaGeneration(o.Answer(path), 100, 500, 40, 5, 300);
            }
            public ValueTask DisposeAsync() { o.Active--; return ValueTask.CompletedTask; }
        }
    }

    private ModelArenaService Arena(FakeRunner runner, Func<bool>? gaming = null, TimeSpan? timeout = null)
    {
        foreach (string n in new[] { "a", "b", "c" }) File.WriteAllText(Path.Combine(_dir, n + ".gguf"), n);
        return new ModelArenaService(runner, new ModelArenaStore(_dir), new LocalBenchmarkHistoryStore(_dir),
            name => File.Exists(Path.Combine(_dir, name)) ? Path.Combine(_dir, name) : null,
            gaming ?? (() => false), () => "hw1", _ => new ValueTask<IAsyncDisposable>(new Noop()), timeout);
    }

    private sealed class Noop : IAsyncDisposable { public ValueTask DisposeAsync() => ValueTask.CompletedTask; }

    private static ArenaStartRequest Req(params string[] models) =>
        new() { ModelFileNames = models.ToList(), TaskIds = new() { "reason-pens", "chat-explain" } };

    [Fact]
    public async Task Arena_runs_models_sequentially_collects_timings_checks_and_history()
    {
        var runner = new FakeRunner();
        ModelArenaService arena = Arena(runner);
        ArenaRunDto started = arena.Start(Req("a.gguf", "b.gguf"));
        await arena.WaitForIdleAsync();
        ArenaRunDto run = arena.Get(started.Id)!;
        Assert.Equal("completed", run.Status);
        Assert.Equal(1, runner.MaxActive);
        Assert.True(runner.Restored);
        Assert.Equal(new[] { "load:a.gguf", "load:b.gguf" }, runner.Events);
        ArenaPromptResultDto pens = run.Prompts[0];
        Assert.Equal(2, pens.Results.Count);
        Assert.All(pens.Results, r => { Assert.Equal("automatic-check", r.Check.Kind); Assert.True(r.Check.Passed); Assert.Equal(100, r.TtftMs); });
        Assert.Equal("none", run.Prompts[1].Results[0].Check.Kind);
        Assert.Equal(4, new LocalBenchmarkHistoryStore(_dir).List().Count);
        Assert.Null(pens.UserPick); // never auto-picked
    }

    [Fact]
    public async Task Arena_judgement_is_user_only_validated_and_reversible()
    {
        ModelArenaService arena = Arena(new FakeRunner());
        ArenaRunDto run = arena.Start(Req("a.gguf", "b.gguf"));
        await arena.WaitForIdleAsync();
        Assert.Throws<InvalidDataException>(() => arena.Judge(run.Id, new ArenaJudgeRequest { PromptIndex = 0, ModelFileName = "c.gguf" }));
        Assert.Throws<InvalidDataException>(() => arena.Judge(run.Id, new ArenaJudgeRequest { PromptIndex = 9, ModelFileName = "a.gguf" }));
        Assert.Equal("a.gguf", arena.Judge(run.Id, new ArenaJudgeRequest { PromptIndex = 0, ModelFileName = "a.gguf" })!.Prompts[0].UserPick);
        IReadOnlyList<ArenaEvidenceRow> evidence = arena.GetEvidence();
        ArenaEvidenceRow a = evidence.Single(r => r.ModelFileName == "a.gguf" && r.Category == "reasoning");
        Assert.Equal(1, a.UserJudgedWins);
        Assert.Equal(1, a.UserJudgedComparisons);
        Assert.Equal(1, a.AutomaticPasses); // counted separately from the user's pick
        Assert.Null(arena.Judge(run.Id, new ArenaJudgeRequest { PromptIndex = 0, ModelFileName = null })!.Prompts[0].UserPick);
        Assert.Null(arena.Judge("missing", new ArenaJudgeRequest()));
    }

    [Fact]
    public void Arena_validates_requests_and_gaming_hard_off()
    {
        ModelArenaService arena = Arena(new FakeRunner());
        Assert.Throws<InvalidDataException>(() => arena.Start(Req("a.gguf")));
        Assert.Throws<InvalidDataException>(() => arena.Start(Req("a.gguf", "a.gguf")));
        Assert.Throws<InvalidDataException>(() => arena.Start(Req("a.gguf", "nope.gguf")));
        Assert.Throws<InvalidDataException>(() => arena.Start(new ArenaStartRequest { ModelFileNames = new() { "a.gguf", "b.gguf" } }));
        Assert.Throws<InvalidDataException>(() => arena.Start(new ArenaStartRequest { ModelFileNames = new() { "a.gguf", "b.gguf" }, TaskIds = new() { "zzz" } }));
        ModelArenaService gaming = Arena(new FakeRunner(), () => true);
        Assert.Throws<InvalidOperationException>(() => gaming.Start(Req("a.gguf", "b.gguf")));
    }

    [Fact]
    public async Task Arena_isolates_a_failing_model_and_times_out_a_hanging_one()
    {
        var runner = new FakeRunner { FailLoad = "a.gguf", HangOn = "b.gguf" };
        ModelArenaService arena = Arena(runner, timeout: TimeSpan.FromMilliseconds(200));
        ArenaRunDto run = arena.Start(Req("a.gguf", "b.gguf", "c.gguf"));
        await arena.WaitForIdleAsync();
        ArenaRunDto done = arena.Get(run.Id)!;
        Assert.Equal("completed", done.Status);
        Assert.Equal("failed", done.Prompts[0].Results.Single(r => r.ModelFileName == "a.gguf").Status);
        Assert.Equal("timed_out", done.Prompts[0].Results.Single(r => r.ModelFileName == "b.gguf").Status);
        Assert.Equal("completed", done.Prompts[0].Results.Single(r => r.ModelFileName == "c.gguf").Status);
    }

    [Fact]
    public async Task Arena_cancel_stops_the_run_and_a_second_start_is_rejected_while_busy()
    {
        var runner = new FakeRunner { HangOn = "a.gguf" };
        ModelArenaService arena = Arena(runner);
        ArenaRunDto run = arena.Start(Req("a.gguf", "b.gguf"));
        Assert.Throws<InvalidOperationException>(() => arena.Start(Req("a.gguf", "b.gguf")));
        Assert.True(arena.Cancel(run.Id));
        await arena.WaitForIdleAsync();
        Assert.Equal("cancelled", arena.Get(run.Id)!.Status);
        Assert.True(runner.Restored);
        Assert.False(arena.Cancel(run.Id));
    }

    [Fact]
    public void Arena_static_checks_never_execute_code_and_are_labelled()
    {
        ArenaCheckResultDto ok = ArenaTaskCatalog.Find("code-fizzbuzz")!.Check!("def fizzbuzz(n):\n  return ['Fizz' if i%3==0 else 'Buzz' for i in range(n)]");
        Assert.True(ok.Passed);
        Assert.Contains("not executed", ok.Detail);
        Assert.False(ArenaTaskCatalog.Find("code-fizzbuzz")!.Check!("no code").Passed);
        Assert.True(ArenaTaskCatalog.Find("reason-order")!.Check!("Raj").Passed);
        Assert.False(ArenaTaskCatalog.Find("reason-pens")!.Check!("It is 9").Passed);
    }

    [Fact]
    public void Presenter_validates_selection_and_formats_unknowns_honestly()
    {
        Assert.False(ModelArenaPresenter.ValidateStart(new[] { "a" }, 1, null, false).CanStart);
        Assert.False(ModelArenaPresenter.ValidateStart(new[] { "a", "b", "c", "d" }, 1, null, false).CanStart);
        Assert.False(ModelArenaPresenter.ValidateStart(new[] { "a", "b" }, 0, " ", false).CanStart);
        Assert.False(ModelArenaPresenter.ValidateStart(new[] { "a", "b" }, 1, null, true).CanStart);
        Assert.True(ModelArenaPresenter.ValidateStart(new[] { "a", "b" }, 0, "hi", false).CanStart);
        string t = ModelArenaPresenter.FormatTiming(new ArenaModelResultDto { Status = "completed", GeneratedTokens = 3 });
        Assert.Contains("first token n/a", t);
        Assert.Contains("speed not reported", t);
        Assert.StartsWith("Timed out", ModelArenaPresenter.FormatTiming(new ArenaModelResultDto { Status = "timed_out" }));
    }
}
