using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class AutoMaintenanceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "parux-automaint-" + Guid.NewGuid().ToString("N"));
    private DateTime _now = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    public AutoMaintenanceTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private sealed class Fakes
    {
        public HardwareSnapshot Snapshot = Snap("RTX 4090", 24L << 30, "535.1");
        public List<ManagedModelInfo> Installed = new();
        public bool RoleAssigned;
        public bool Gaming;
        public int AssignCalls, ListCalls, RefreshCalls, DownloadCalls;
        public Exception? ProbeFailure, RefreshFailure;
        public List<ModelDiscoveryCandidate> Candidates = new();
        public List<ModelDiscoveryCandidate> AddedOnRefresh = new();
        public List<RuntimeUpdateObservation> RuntimeBuilds = new();
        public List<(string, string)> RuntimeUpdates = new();
    }

    private static HardwareSnapshot Snap(string gpu, long vram, string driver, long ram = 64L << 30, string runtime = "text-cuda@b1") =>
        new(new GpuInventoryStatus
        {
            Available = true,
            Adapters = { new GpuAdapterStatus { Name = gpu, DedicatedVideoMemoryBytes = vram, DriverVersion = driver } }
        }, ram, "cpu/16t", new[] { runtime });

    private static ManagedModelInfo Model() => new() { FileName = "m.gguf", FilePath = "m.gguf", SizeBytes = 1 };

    private AutoMaintenanceService Make(Fakes f) => new(new AutoMaintenanceStore(_dir), new AutoMaintenanceDependencies
    {
        Probe = _ => f.ProbeFailure is null ? Task.FromResult(f.Snapshot) : Task.FromException<HardwareSnapshot>(f.ProbeFailure),
        ListInstalledModels = () => { f.ListCalls++; return f.Installed; },
        AutoAssign = (m, s) =>
        {
            f.AssignCalls++;
            f.RoleAssigned = true;
            return new RoleAutoAssignResponse { Success = true, Summary = "assigned", Assignments = { new RoleAutoAssignItem { Role = "standard" } } };
        },
        HasRoleAssignment = () => f.RoleAssigned,
        RefreshDiscovery = _ =>
        {
            f.RefreshCalls++;
            if (f.RefreshFailure is not null) return Task.FromException<ModelDiscoveryRefreshResult>(f.RefreshFailure);
            f.Candidates.AddRange(f.AddedOnRefresh);
            f.AddedOnRefresh.Clear();
            return Task.FromResult(new ModelDiscoveryRefreshResult { Outcome = "refreshed" });
        },
        DiscoveryCandidates = () => f.Candidates.ToArray(),
        CheckRuntimeUpdates = _ => Task.FromResult<IReadOnlyList<RuntimeUpdateObservation>>(f.RuntimeBuilds),
        PendingManagedRuntimeUpdates = () => f.RuntimeUpdates,
        IsGamingActive = () => f.Gaming,
        UtcNow = () => _now
    });

    private static ModelDiscoveryCandidate Cand(string key, string repo, long size, string rev = "r1", params string[] cohorts) => new()
    {
        Key = key, RepoId = repo, Revision = rev, RepoFilePath = "m-Q4.gguf", DeclaredSizeBytes = size,
        DeclaredLicense = "apache-2.0", DiscoveryCohorts = cohorts.ToList()
    };

    [Fact]
    public async Task Managed_runtime_update_is_proposed_user_managed_only_noted()
    {
        var f = new Fakes();
        f.RuntimeBuilds.Add(new RuntimeUpdateObservation("text-cuda", "b100", "b200", ComponentOwnership.PARUX_MANAGED));
        f.RuntimeBuilds.Add(new RuntimeUpdateObservation("text-vulkan", "b100", "b200", ComponentOwnership.USER_MANAGED));
        f.RuntimeBuilds.Add(new RuntimeUpdateObservation("text-x", "b200", "b200", ComponentOwnership.PARUX_MANAGED));
        var r = await Make(f).RunAsync("manual", default);
        Assert.Single(r.Proposals, p => p.Kind == "runtime_update");
        var note = Assert.Single(r.Proposals, p => p.Kind == "runtime_note");
        Assert.Contains("will not modify", note.Detail);
    }

    [Fact]
    public async Task Candidates_are_filtered_capped_and_deduped_by_revision()
    {
        var f = new Fakes { Installed = { Model() } }; // 1-byte "chat" model, fits GPU
        // 7 chat candidates that fit the GPU would not beat an installed GPU-fit chat model; use coding gap instead.
        for (int i = 0; i < 7; i++)
            f.Candidates.Add(Cand("k" + i, $"o/x-coder-{i}", 4L << 30, "r1", i % 2 == 0 ? "new" : "popular"));
        var ignored = Cand("kign", "o/x-coder-ign", 4L << 30); ignored.IgnoredUtc = _now;
        f.Candidates.Add(ignored);
        f.Candidates.Add(Cand("ktoo", "o/x-coder-big", 500L << 30)); // too large
        f.Candidates.Add(Cand("kchat", "o/plain-chat", 4L << 30)); // chat; installed chat fits GPU -> no gain
        var svc = Make(f);
        var r = await svc.RunAsync("manual", default);
        var cands = r.Proposals.Where(p => p.Kind == "new_candidate").ToList();
        Assert.Equal(5, cands.Count);
        Assert.DoesNotContain(cands, p => p.Key is "kign" or "ktoo" or "kchat");
        Assert.All(cands, p => { Assert.Equal("coding", p.Capability); Assert.Equal("FitsOnGpu", p.FitClass); Assert.Equal("apache-2.0", p.License); Assert.Equal("r1", p.Revision); });
        Assert.Equal(new[] { "new" }, cands[0].Cohorts); // new/recently-updated sorts ahead on ties
        // Same revision is not proposed again; remaining ones are.
        var r2 = await svc.RunAsync("manual", default);
        Assert.DoesNotContain(r2.Proposals, p => cands.Any(c => c.Key == p.Key));
        Assert.Equal(2, r2.Proposals.Count(p => p.Kind == "new_candidate"));
        // A new revision of an already-proposed key is proposed again.
        f.Candidates.First(c => c.Key == cands[0].Key).Revision = "r2";
        var r3 = await svc.RunAsync("manual", default);
        Assert.Contains(r3.Proposals, p => p.Key == cands[0].Key && p.Revision == "r2");
    }

    [Fact]
    public async Task Candidate_needing_newer_runtime_says_so_not_download()
    {
        var f = new Fakes();
        f.Snapshot = f.Snapshot with { Runtime = new InstalledRuntime("b100", ComponentOwnership.PARUX_MANAGED) };
        f.Candidates.Add(Cand("kf", "someone/Flint", 4L << 30));
        var r = await Make(f).RunAsync("manual", default);
        var p = Assert.Single(r.Proposals);
        Assert.Equal("candidate_needs_runtime_update", p.Kind);
        Assert.Contains("runtime", p.Detail);
        Assert.DoesNotContain("before any download", p.Detail);
    }

    [Fact]
    public void Resolver_uses_real_ownership_and_backend_tag()
    {
        var comps = new List<ComponentStatus>
        {
            new() { Id = "llama-server-cuda", Detected = false, Ownership = ComponentOwnership.PARUX_MANAGED },
            new() { Id = "llama-server-vulkan", Detected = true, Ownership = ComponentOwnership.USER_MANAGED }
        };
        var r = InstalledRuntimeResolver.Resolve(comps, b => b == "text-vulkan" ? "b321" : "wrong")!;
        Assert.Equal("text-vulkan", r.Backend);
        Assert.Equal("b321", r.Runtime.BuildTag);
        Assert.Equal(ComponentOwnership.USER_MANAGED, r.Runtime.Ownership);
        Assert.Null(InstalledRuntimeResolver.Resolve(new List<ComponentStatus>(), _ => "b1"));
    }

    [Fact]
    public void Fingerprint_is_stable_across_adapter_order_and_changes_on_hardware_change()
    {
        var a = new GpuAdapterStatus { Name = "A", DedicatedVideoMemoryBytes = 8, DriverVersion = "1" };
        var b = new GpuAdapterStatus { Name = "B", DedicatedVideoMemoryBytes = 16, DriverVersion = "1" };
        HardwareSnapshot S(params GpuAdapterStatus[] ad) => new(new GpuInventoryStatus { Adapters = ad.ToList() }, 100, "c", new[] { "x", "y" });
        var f1 = HardwareFingerprint.Build(S(a, b));
        Assert.Equal(f1.Digest, HardwareFingerprint.Build(S(b, a)).Digest);
        Assert.NotEqual(f1.Digest, HardwareFingerprint.Build(S(a)).Digest); // eGPU removed/added
        b.DriverVersion = "2";
        var f2 = HardwareFingerprint.Build(S(a, b));
        Assert.NotEqual(f1.Digest, f2.Digest);
        Assert.Contains("gpu_driver_changed", HardwareFingerprint.Diff(f1, f2));
        var ram = HardwareFingerprint.Build(new HardwareSnapshot(null, 200, "c", new[] { "x", "y" }));
        Assert.NotEqual(HardwareFingerprint.Build(new HardwareSnapshot(null, 100, "c", new[] { "x", "y" })).Digest, ram.Digest);
    }

    [Fact]
    public void Interval_is_clamped_and_persisted()
    {
        var svc = Make(new Fakes());
        Assert.Equal(24, svc.GetState().Settings.IntervalHours);
        Assert.Equal(1, svc.UpdateSettings(null, 0).IntervalHours);
        Assert.Equal(AutoMaintenanceSettings.MaximumIntervalHours, svc.UpdateSettings(null, 999999).IntervalHours);
        svc.UpdateSettings(false, 6);
        var reloaded = Make(new Fakes()).GetState().Settings;
        Assert.False(reloaded.Enabled);
        Assert.Equal(6, reloaded.IntervalHours);
    }

    [Fact]
    public async Task Off_switch_skips_scheduled_runs_but_manual_still_works()
    {
        var f = new Fakes();
        var svc = Make(f);
        svc.UpdateSettings(false, null);
        Assert.Equal("skipped_disabled", (await svc.RunAsync("interval", default)).Outcome);
        Assert.Equal(0, f.RefreshCalls);
        Assert.Null(svc.NextDueUtc());
        Assert.NotEqual("skipped_disabled", (await svc.RunAsync("manual", default)).Outcome);
        Assert.Equal(1, f.RefreshCalls);
    }

    [Fact]
    public async Task Gaming_active_skips_everything()
    {
        var f = new Fakes { Gaming = true, Installed = { Model() } };
        var report = await Make(f).RunAsync("manual", default);
        Assert.Equal("skipped_gaming", report.Outcome);
        Assert.Equal(0, f.RefreshCalls + f.AssignCalls);
    }

    [Fact]
    public async Task First_run_auto_assigns_once_only_and_records_it()
    {
        var f = new Fakes { Installed = { Model() } };
        var svc = Make(f);
        MaintenanceReport first = await svc.RunAsync("startup", default);
        Assert.True(first.FirstRunAutoAssign);
        Assert.Equal(1, f.AssignCalls);
        // Same hardware: no re-assign, first-run never repeats.
        await svc.RunAsync("interval", default);
        Assert.Null(await svc.CheckFirstRunAsync(default));
        Assert.Equal(1, f.AssignCalls);
    }

    [Fact]
    public async Task First_run_waits_for_first_model_then_assigns_once()
    {
        var f = new Fakes();
        var svc = Make(f);
        Assert.Null(await svc.CheckFirstRunAsync(default));
        Assert.Equal(0, f.AssignCalls);
        f.Installed.Add(Model());
        MaintenanceReport r = (await svc.CheckFirstRunAsync(default))!;
        Assert.True(r.FirstRunAutoAssign);
        Assert.Null(await svc.CheckFirstRunAsync(default));
        Assert.Equal(1, f.AssignCalls);
    }

    [Fact]
    public async Task First_run_does_not_assign_when_a_role_is_already_assigned()
    {
        var f = new Fakes { Installed = { Model() }, RoleAssigned = true };
        var svc = Make(f);
        Assert.Null((await svc.CheckFirstRunAsync(default))?.FirstRunAutoAssign is true ? "x" : null);
        Assert.Equal(0, f.AssignCalls);
        Assert.True(svc.GetState().FirstRunAssignDone);
    }

    [Fact]
    public async Task Hardware_change_reassigns_only_installed_models_unchanged_does_not()
    {
        var f = new Fakes { Installed = { Model() } };
        var svc = Make(f);
        await svc.RunAsync("startup", default);
        _now = _now.AddHours(25);
        Assert.Equal("no_change", (await svc.RunAsync("interval", default)).Outcome);
        Assert.Equal(1, f.AssignCalls);

        f.Snapshot = Snap("RTX 4090", 24L << 30, "999.9"); // driver update
        MaintenanceReport r = await svc.RunAsync("interval", default);
        Assert.Equal(2, f.AssignCalls);
        Assert.Contains("gpu_driver_changed", r.Changes);
        Assert.True(r.RolesAutoAssigned);
    }

    [Fact]
    public async Task Proposals_are_recorded_and_nothing_is_downloaded()
    {
        var f = new Fakes();
        f.AddedOnRefresh.Add(Cand("hf:better/model", "better/model-GGUF", 5L << 30));
        f.RuntimeUpdates.Add(("RuntimeBuild|text-cuda||", "llama.cpp b1 -> b2"));
        var report = await Make(f).RunAsync("manual", default);
        Assert.Contains(report.Proposals, p => p.Kind == "new_candidate" && p.Key == "hf:better/model" && p.Detail.Contains("candidate, not validated"));
        Assert.Contains(report.Proposals, p => p.Kind == "runtime_update");
        Assert.Equal(0, f.DownloadCalls);
        // Structural guarantee: the dependency surface has no download/install capability.
        var names = typeof(AutoMaintenanceDependencies).GetProperties().Select(p => p.Name).ToList();
        Assert.DoesNotContain(names, n => n.Contains("Download", StringComparison.OrdinalIgnoreCase)
            || n.Contains("InstallRuntime", StringComparison.OrdinalIgnoreCase)
            || n.Contains("Promote", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Failures_are_recorded_not_thrown_and_backoff_is_bounded()
    {
        var f = new Fakes { ProbeFailure = new InvalidOperationException("boom"), RefreshFailure = new HttpRequestException("net") };
        var svc = Make(f);
        MaintenanceReport r = await svc.RunAsync("startup", default);
        Assert.Equal("error", r.Outcome);
        Assert.Contains(r.Errors, e => e.Contains("boom"));
        Assert.Contains(r.Errors, e => e.Contains("net"));
        var due1 = svc.NextDueUtc()!.Value;
        Assert.Equal(_now + AutoMaintenanceService.BackoffBase, due1);
        await svc.RunAsync("interval", default);
        Assert.Equal(_now + AutoMaintenanceService.BackoffBase * 2, svc.NextDueUtc());
        for (int i = 0; i < 10; i++) await svc.RunAsync("interval", default);
        Assert.Equal(_now + TimeSpan.FromHours(24), svc.NextDueUtc()); // gave up retrying, back to the normal interval
        // Fingerprint not committed on failure, so recovery re-detects change.
        f.ProbeFailure = null; f.RefreshFailure = null;
        Assert.Contains("first_run: no previous hardware fingerprint", (await svc.RunAsync("interval", default)).Changes);
        Assert.Equal(0, svc.GetState().ConsecutiveFailures);
    }

    [Fact]
    public async Task State_survives_restart_and_corrupt_state_falls_back_to_defaults()
    {
        var f = new Fakes { Installed = { Model() } };
        await Make(f).RunAsync("startup", default);
        var restarted = Make(new Fakes { Installed = { Model() }, RoleAssigned = true });
        AutoMaintenanceState s = restarted.GetState();
        Assert.True(s.FirstRunAssignDone);
        Assert.NotNull(s.Fingerprint);
        Assert.Equal("startup", s.LastReport!.Trigger);

        File.WriteAllText(Path.Combine(_dir, AutoMaintenanceStore.FileName), "{ not json");
        var r = await Make(new Fakes()).RunAsync("manual", default);
        Assert.Contains(r.Errors, e => e.Contains("malformed"));
        Assert.NotNull(Make(new Fakes()).GetState().LastReport); // rewritten atomically
    }

    [Fact]
    public async Task Concurrent_run_reports_busy_and_cancellation_propagates()
    {
        var gate = new TaskCompletionSource<ModelDiscoveryRefreshResult>();
        var f = new Fakes();
        var deps = new AutoMaintenanceDependencies
        {
            Probe = _ => Task.FromResult(f.Snapshot),
            ListInstalledModels = () => new List<ManagedModelInfo>(),
            AutoAssign = (_, _) => new RoleAutoAssignResponse(),
            HasRoleAssignment = () => false,
            RefreshDiscovery = _ => gate.Task,
            DiscoveryCandidates = () => Array.Empty<ModelDiscoveryCandidate>(),
            CheckRuntimeUpdates = _ => Task.FromResult<IReadOnlyList<RuntimeUpdateObservation>>(Array.Empty<RuntimeUpdateObservation>()),
            PendingManagedRuntimeUpdates = () => Array.Empty<(string, string)>(),
            IsGamingActive = () => false,
            UtcNow = () => _now
        };
        var svc = new AutoMaintenanceService(new AutoMaintenanceStore(_dir), deps);
        Task<MaintenanceReport> running = svc.RunAsync("startup", default);
        Assert.Equal("busy", (await svc.RunAsync("manual", default)).Outcome);
        gate.SetResult(new ModelDiscoveryRefreshResult { Outcome = "refreshed" });
        await running;

        var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Make(new Fakes()).RunAsync("manual", cts.Token));
    }

    [Fact]
    public async Task Hosted_service_runs_on_startup_and_stops_cleanly()
    {
        var f = new Fakes();
        var svc = Make(f);
        var host = new AutoMaintenanceHostedService(svc, TimeSpan.Zero, TimeSpan.FromMilliseconds(20), () => _now);
        await host.StartAsync(default);
        for (int i = 0; i < 100 && f.RefreshCalls == 0; i++) await Task.Delay(20);
        await host.StopAsync(default);
        Assert.Equal(1, f.RefreshCalls);
        Assert.Equal("startup", svc.GetState().LastReport!.Trigger);
    }
}
