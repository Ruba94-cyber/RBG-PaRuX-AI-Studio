using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>Hermetic: a fake host stands in for the network, the disk runtimes, the GPU and the benchmark.</summary>
public sealed class ManagedRuntimeUpdaterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "parux-rtupdate-" + Guid.NewGuid().ToString("N"));
    private DateTime _now = new(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);

    public ManagedRuntimeUpdaterTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private const string Cuda = "text-cuda";

    private sealed class FakeHost : IManagedRuntimeHost
    {
        public bool Gaming, Generating;
        public ComponentOwnership Ownership = ComponentOwnership.PARUX_MANAGED;
        public CudaHardware Hardware = new(12.0, "32.0.15.8120");     // Blackwell, driver 581.20
        public Exception? HardwareFailure;
        public RuntimeBuildId Active = new(Cuda, "b100", "12.4");
        public List<InstalledBuild> Installed = new();
        public List<RuntimeCandidate> Catalog = new();
        public Exception? CatalogFailure;
        public Func<RuntimeCandidate, RuntimeStageResult> Stage = _ => new RuntimeStageResult(true, "ok");
        public Func<RuntimeBuildId, RuntimeSmokeResult> Smoke = _ => new RuntimeSmokeResult(true, "served", "ok");
        public Func<RuntimeBuildId, RuntimeBenchResult?> Bench = _ => new RuntimeBenchResult(1000, 100, "tiny");
        public Func<Task>? StageHang;
        public List<string> Calls = new();
        public List<RuntimeBuildId> Activations = new();

        public FakeHost()
        {
            Installed.Add(new InstalledBuild(Active, true));
        }

        public bool IsGamingActive() => Gaming;
        public bool IsGenerationRunning() => Generating;
        public Task<CudaHardware> ReadCudaHardwareAsync(CancellationToken ct) =>
            HardwareFailure is null ? Task.FromResult(Hardware) : Task.FromException<CudaHardware>(HardwareFailure);
        public Task<IReadOnlyList<RuntimeUpdateTarget>> GetTargetsAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<RuntimeUpdateTarget>>(new[] { new RuntimeUpdateTarget(Cuda, Ownership, Active) });
        public Task<IReadOnlyList<RuntimeCandidate>> ListAvailableAsync(string backend, int cudaMajor, CancellationToken ct)
        {
            Calls.Add("list:" + cudaMajor);
            if (CatalogFailure is not null) return Task.FromException<IReadOnlyList<RuntimeCandidate>>(CatalogFailure);
            return Task.FromResult<IReadOnlyList<RuntimeCandidate>>(Catalog.ToList());   // deliberately ignores cudaMajor: the updater must still filter
        }
        public IReadOnlyList<InstalledBuild> ListInstalled(string backend) => Installed.ToList();
        public RuntimeBuildId? GetActive(string backend) => Active;
        public async Task<RuntimeStageResult> StageAsync(RuntimeCandidate c, int cudaMajor, CancellationToken ct)
        {
            Calls.Add("stage:" + c.Id.Key);
            if (StageHang is not null) await StageHang();
            ct.ThrowIfCancellationRequested();
            RuntimeStageResult r = Stage(c);
            if (r.Ok) Installed.Add(new InstalledBuild(c.Id, true));
            return r;
        }
        public Task<RuntimeSmokeResult> SmokeAsync(RuntimeBuildId b, CancellationToken ct) { Calls.Add("smoke:" + b.Key); return Task.FromResult(Smoke(b)); }
        public Task<RuntimeBenchResult?> BenchmarkAsync(RuntimeBuildId b, CancellationToken ct) { Calls.Add("bench:" + b.Key); return Task.FromResult(Bench(b)); }
        public void Activate(RuntimeBuildId b)
        {
            if (Generating) throw new InvalidOperationException("generation running");
            Calls.Add("activate:" + b.Key);
            Activations.Add(b);
            Active = b;
        }
        public bool Remove(RuntimeBuildId b)
        {
            Calls.Add("remove:" + b.Key);
            if (b == Active) return false;
            return Installed.RemoveAll(i => i.Id == b && i.ParuxManaged) > 0;
        }
        public string DirectoryOf(RuntimeBuildId b) => Path.Combine("C:\\fake-runtime", b.Backend + "-" + b.Tag + "-" + b.Toolkit);
        public void CleanupStaging() => Calls.Add("cleanup");
    }

    private ManagedRuntimeUpdater Make(FakeHost h) => new(new ManagedRuntimeUpdateStore(_dir), h, () => _now);

    private static RuntimeCandidate Cand(string tag, string? toolkit) => new(new RuntimeBuildId(Cuda, tag, toolkit), 1);

    private static FakeHost HostWithCatalog(params RuntimeCandidate[] c)
    {
        var h = new FakeHost();
        h.Catalog.AddRange(c);
        return h;
    }

    // ---- CUDA / hardware selection ----------------------------------------------------------------------------

    [Fact]
    public async Task Blackwell_with_modern_driver_moves_from_cuda_124_to_cuda_13()
    {
        FakeHost h = HostWithCatalog(Cand("b100", "12.4"), Cand("b200", "12.4"), Cand("b200", "13.1"));
        RuntimeUpdateRunResult r = await Make(h).RunAsync(true, false, default);
        Assert.Equal("updated", r.Outcome);
        Assert.Equal(new RuntimeBuildId(Cuda, "b200", "13.1"), h.Active);
        Assert.Contains("list:13", h.Calls);
    }

    [Fact]
    public async Task Driver_too_old_for_cuda_13_never_picks_a_13_build()
    {
        FakeHost h = HostWithCatalog(Cand("b200", "12.4"), Cand("b200", "13.1"));
        h.Hardware = new CudaHardware(12.0, "32.0.15.7000");            // Blackwell but driver 570.00 (< 580)
        await Make(h).RunAsync(true, false, default);
        Assert.Equal(new RuntimeBuildId(Cuda, "b200", "12.4"), h.Active);
        Assert.Contains("list:12", h.Calls);
        Assert.DoesNotContain(h.Calls, c => c.StartsWith("stage:", StringComparison.Ordinal) && c.Contains("13.1"));
    }

    [Fact]
    public async Task Pascal_stays_on_cuda_12_even_with_a_new_driver()
    {
        FakeHost h = HostWithCatalog(Cand("b200", "12.4"), Cand("b300", "13.1"));
        h.Hardware = new CudaHardware(6.1, "32.0.15.8120");
        await Make(h).RunAsync(true, false, default);
        Assert.Equal(new RuntimeBuildId(Cuda, "b200", "12.4"), h.Active);       // b300 only exists as CUDA 13: not runnable on Pascal
        Assert.DoesNotContain(h.Calls, c => c.StartsWith("stage:", StringComparison.Ordinal) && c.Contains("13.1"));
    }

    [Fact]
    public async Task Unreadable_hardware_falls_back_to_cuda_12()
    {
        FakeHost h = HostWithCatalog(Cand("b200", "13.1"), Cand("b150", "12.4"));
        h.HardwareFailure = new IOException("nvidia-smi missing");
        RuntimeUpdateRunResult r = await Make(h).RunAsync(true, false, default);
        Assert.Equal(new RuntimeBuildId(Cuda, "b150", "12.4"), h.Active);
        Assert.Contains(r.Steps, s => s.Contains("CUDA 12"));
    }

    [Fact]
    public async Task Same_tag_newer_cuda_major_is_an_upgrade_but_older_is_not()
    {
        FakeHost up = HostWithCatalog(Cand("b100", "13.1"));
        await Make(up).RunAsync(true, false, default);
        Assert.Equal(new RuntimeBuildId(Cuda, "b100", "13.1"), up.Active);

        FakeHost none = HostWithCatalog(Cand("b100", "12.4"), Cand("b090", "12.4"));
        RuntimeUpdateRunResult r = await Make(none).RunAsync(true, false, default);
        Assert.Equal("current", r.Outcome);
    }

    // ---- smoke + benchmark gate ----------------------------------------------------------------------------

    [Fact]
    public async Task Healthy_and_faster_build_is_adopted_with_previous_kept_and_probation()
    {
        FakeHost h = HostWithCatalog(Cand("b200", "13.1"));
        h.Bench = b => b.Tag == "b200" ? new RuntimeBenchResult(1300, 130, "tiny") : new RuntimeBenchResult(1000, 100, "tiny");
        ManagedRuntimeUpdater u = Make(h);
        RuntimeUpdateRunResult r = await u.RunAsync(true, false, default);
        Assert.Equal("updated", r.Outcome);
        Assert.Equal("b200", h.Active.Tag);
        Assert.Contains(r.Steps, s => s.Contains("measured locally"));
        RuntimeUpdateStatus status = u.GetStatus(new[] { new RuntimeUpdateTarget(Cuda, ComponentOwnership.PARUX_MANAGED, h.Active) });
        Assert.Equal("b100 (CUDA 12.4)", status.Backends[0].Previous);
        Assert.Equal("probation", status.Backends[0].Phase);
        // The active build was never touched before the switch: first activation is the candidate itself.
        Assert.Equal(new[] { new RuntimeBuildId(Cuda, "b200", "13.1") }, h.Activations);
    }

    [Fact]
    public async Task Healthy_but_slower_build_is_not_adopted_and_is_not_retried()
    {
        FakeHost h = HostWithCatalog(Cand("b200", "13.1"));
        h.Bench = b => b.Tag == "b200" ? new RuntimeBenchResult(1000, 80, "tiny") : new RuntimeBenchResult(1000, 100, "tiny");   // -20% decode
        ManagedRuntimeUpdater u = Make(h);
        RuntimeUpdateRunResult r = await u.RunAsync(true, false, default);
        Assert.Equal("rejected", r.Outcome);
        Assert.Equal("b100", h.Active.Tag);
        Assert.Empty(h.Activations);
        Assert.Contains(r.Steps, s => s.Contains("slower") && s.Contains("not adopted"));

        h.Calls.Clear();
        _now += TimeSpan.FromDays(2);
        RuntimeUpdateRunResult again = await u.RunAsync(true, false, default);
        Assert.Equal("current", again.Outcome);
        Assert.DoesNotContain(h.Calls, c => c.StartsWith("stage:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Slower_within_tolerance_is_still_adopted()
    {
        FakeHost h = HostWithCatalog(Cand("b200", "13.1"));
        h.Bench = b => b.Tag == "b200" ? new RuntimeBenchResult(980, 97, "tiny") : new RuntimeBenchResult(1000, 100, "tiny");     // -2% / -3%
        RuntimeUpdateRunResult r = await Make(h).RunAsync(true, false, default);
        Assert.Equal("updated", r.Outcome);
    }

    [Fact]
    public async Task Unhealthy_build_is_rejected_removed_and_never_activated()
    {
        FakeHost h = HostWithCatalog(Cand("b200", "13.1"));
        h.Smoke = _ => new RuntimeSmokeResult(false, "failed", "garbage output");
        ManagedRuntimeUpdater u = Make(h);
        RuntimeUpdateRunResult r = await u.RunAsync(true, false, default);
        Assert.Equal("rejected", r.Outcome);
        Assert.Empty(h.Activations);
        Assert.DoesNotContain(h.Installed, i => i.Id.Tag == "b200");
        Assert.Contains(r.Steps, s => s.Contains("smoke test") && s.Contains("FAILED"));
        Assert.DoesNotContain(h.Calls, c => c.StartsWith("bench:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Smoke_only_without_a_model_is_adopted_honestly_on_probation()
    {
        FakeHost h = HostWithCatalog(Cand("b200", "13.1"));
        h.Smoke = _ => new RuntimeSmokeResult(true, "binary_only", "no model");
        RuntimeUpdateRunResult r = await Make(h).RunAsync(true, false, default);
        Assert.Equal("updated", r.Outcome);
        Assert.DoesNotContain(h.Calls, c => c.StartsWith("bench:", StringComparison.Ordinal));
    }

    // ---- failures, backoff, crash ---------------------------------------------------------------------------

    [Fact]
    public async Task Stage_failure_leaves_nothing_installed_and_retries_with_bounded_backoff()
    {
        FakeHost h = HostWithCatalog(Cand("b200", "13.1"));
        h.Stage = _ => new RuntimeStageResult(false, "hash mismatch");
        ManagedRuntimeUpdater u = Make(h);
        RuntimeUpdateRunResult first = await u.RunAsync(true, false, default);
        Assert.Equal("failed", first.Outcome);
        Assert.DoesNotContain(h.Installed, i => i.Id.Tag == "b200");
        Assert.Empty(h.Activations);
        Assert.Contains("cleanup", h.Calls);

        h.Calls.Clear();
        RuntimeUpdateRunResult tooSoon = await u.RunAsync(true, false, default);
        Assert.Equal("deferred", tooSoon.Outcome);
        Assert.DoesNotContain(h.Calls, c => c.StartsWith("stage:", StringComparison.Ordinal));
        Assert.False(u.IsRetryDue());

        _now += ManagedRuntimeUpdater.BackoffBase + TimeSpan.FromSeconds(1);
        Assert.True(u.IsRetryDue());
        for (int i = 0; i < ManagedRuntimeUpdater.MaximumAttempts - 1; i++)
        {
            await u.RunAsync(true, false, default);
            _now += TimeSpan.FromDays(1);
        }
        RuntimeUpdateRunResult exhausted = await u.RunAsync(true, false, default);
        Assert.Equal("needs_user", exhausted.Outcome);
        Assert.Contains(Cuda, exhausted.NeedsUserBackends);
        Assert.False(u.IsRetryDue());
        Assert.Equal("b100", h.Active.Tag);
    }

    [Fact]
    public async Task Catalog_failure_is_reported_and_changes_nothing()
    {
        FakeHost h = HostWithCatalog();
        h.CatalogFailure = new HttpRequestException("offline");
        RuntimeUpdateRunResult r = await Make(h).RunAsync(true, false, default);
        Assert.Equal("failed", r.Outcome);
        Assert.Empty(h.Activations);
    }

    [Fact]
    public async Task Crash_mid_install_recovers_to_a_consistent_state_at_restart()
    {
        FakeHost h = HostWithCatalog(Cand("b200", "13.1"));
        var hang = new TaskCompletionSource();
        h.StageHang = () => hang.Task;                       // the "kill": this run never completes
        ManagedRuntimeUpdater first = Make(h);
        Task abandoned = first.RunAsync(true, false, default);
        await Task.Delay(100);
        Assert.False(abandoned.IsCompleted);
        Assert.Equal("b100", h.Active.Tag);                  // the active runtime was never touched
        ManagedRuntimeUpdateState persisted = new ManagedRuntimeUpdateStore(_dir).Load();
        Assert.Equal("staging", persisted.Backends[Cuda].Phase);

        // "Restart": new process objects over the same state directory and the same disk.
        h.StageHang = null;
        h.Calls.Clear();
        ManagedRuntimeUpdater restarted = Make(h);
        RuntimeUpdateRunResult r = await restarted.RunAsync(true, false, default);
        Assert.Contains(r.Steps, s => s.Contains("recovered from an interrupted staging"));
        Assert.Contains("cleanup", h.Calls);
        Assert.Equal("updated", r.Outcome);
        Assert.Equal("b200", h.Active.Tag);
        hang.SetResult();                                    // let the abandoned task unwind
    }

    [Fact]
    public async Task Crash_between_switch_and_probation_resumes_on_probation()
    {
        FakeHost h = HostWithCatalog(Cand("b200", "13.1"));
        var store = new ManagedRuntimeUpdateStore(_dir);
        h.Active = new RuntimeBuildId(Cuda, "b200", "13.1");              // pointer already moved, then the process died
        h.Installed.Add(new InstalledBuild(h.Active, true));
        ManagedRuntimeUpdateState s = store.Load();
        s.Backends[Cuda] = new BackendUpdateState
        {
            Phase = "activating", TargetTag = "b200", TargetToolkit = "13.1", PreviousTag = "b100", PreviousToolkit = "12.4"
        };
        store.Save(s);
        RuntimeUpdateRunResult r = await Make(h).RunAsync(true, false, default);
        Assert.Contains(r.Steps, s2 => s2.Contains("interrupted switch") && s2.Contains("probation"));
        Assert.Equal("probation", store.Load().Backends[Cuda].Phase);
    }

    [Fact]
    public async Task Cancellation_during_staging_resets_state_and_never_touches_the_active_build()
    {
        FakeHost h = HostWithCatalog(Cand("b200", "13.1"));
        using var cts = new CancellationTokenSource();
        h.StageHang = () => { cts.Cancel(); return Task.CompletedTask; };
        ManagedRuntimeUpdater u = Make(h);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => u.RunAsync(true, false, cts.Token));
        Assert.Equal("b100", h.Active.Tag);
        Assert.Empty(h.Activations);
        Assert.Equal("idle", new ManagedRuntimeUpdateStore(_dir).Load().Backends[Cuda].Phase);
    }

    // ---- Gaming, generation, switches, ownership ----------------------------------------------------------------

    [Fact]
    public async Task Gaming_defers_everything()
    {
        FakeHost h = HostWithCatalog(Cand("b200", "13.1"));
        h.Gaming = true;
        RuntimeUpdateRunResult r = await Make(h).RunAsync(true, false, default);
        Assert.Equal("deferred", r.Outcome);
        Assert.Empty(h.Calls);
        Assert.Empty(h.Activations);
    }

    [Fact]
    public async Task Switch_waits_for_the_running_generation_then_completes()
    {
        FakeHost h = HostWithCatalog(Cand("b200", "13.1"));
        h.Generating = true;
        ManagedRuntimeUpdater u = Make(h);
        RuntimeUpdateRunResult r = await u.RunAsync(true, false, default);
        Assert.Equal("deferred", r.Outcome);
        Assert.Equal("b100", h.Active.Tag);
        Assert.True(u.HasPendingActivation());

        RuntimeUpdateRunResult stillBusy = await u.CompletePendingAsync(default);
        Assert.Equal("deferred", stillBusy.Outcome);
        Assert.Equal("b100", h.Active.Tag);

        h.Generating = false;
        RuntimeUpdateRunResult done = await u.CompletePendingAsync(default);
        Assert.Equal("updated", done.Outcome);
        Assert.Equal("b200", h.Active.Tag);
        Assert.False(u.HasPendingActivation());
    }

    [Fact]
    public async Task Pending_switch_is_dropped_when_the_user_changed_the_active_build_meanwhile()
    {
        FakeHost h = HostWithCatalog(Cand("b200", "13.1"));
        h.Generating = true;
        ManagedRuntimeUpdater u = Make(h);
        await u.RunAsync(true, false, default);
        h.Active = new RuntimeBuildId(Cuda, "b050", "12.4");               // manual rollback by the user
        h.Generating = false;
        RuntimeUpdateRunResult r = await u.CompletePendingAsync(default);
        Assert.Equal("current", r.Outcome);
        Assert.Equal("b050", h.Active.Tag);
    }

    [Fact]
    public async Task Off_switch_does_nothing_but_an_explicit_force_still_updates()
    {
        FakeHost h = HostWithCatalog(Cand("b200", "13.1"));
        ManagedRuntimeUpdater u = Make(h);
        RuntimeUpdateRunResult off = await u.RunAsync(enabled: false, force: false, default);
        Assert.Equal("disabled", off.Outcome);
        Assert.Empty(h.Calls);
        RuntimeUpdateRunResult forced = await u.RunAsync(enabled: false, force: true, default);
        Assert.Equal("updated", forced.Outcome);
    }

    [Theory]
    [InlineData(ComponentOwnership.USER_MANAGED)]
    [InlineData(ComponentOwnership.SYSTEM_DETECTED)]
    public async Task Non_managed_runtimes_are_never_touched(ComponentOwnership ownership)
    {
        FakeHost h = HostWithCatalog(Cand("b200", "13.1"));
        h.Ownership = ownership;
        RuntimeUpdateRunResult r = await Make(h).RunAsync(true, true, default);
        Assert.DoesNotContain(h.Calls, c => c is not "cleanup");
        Assert.Empty(h.Activations);
        Assert.Contains(r.Steps, s => s.Contains(ownership.ToString()) && s.Contains("not modified"));
    }

    // ---- probation, rollback, retention ------------------------------------------------------------------------

    [Fact]
    public async Task First_real_launch_failure_rolls_back_automatically_and_blocks_the_build()
    {
        FakeHost h = HostWithCatalog(Cand("b200", "13.1"));
        ManagedRuntimeUpdater u = Make(h);
        await u.RunAsync(true, false, default);
        Assert.Equal("b200", h.Active.Tag);

        u.ObserveLaunch(h.DirectoryOf(h.Active), succeeded: false, "llama-server exited unexpectedly");
        Assert.Equal(new RuntimeBuildId(Cuda, "b100", "12.4"), h.Active);
        RuntimeUpdateStatus status = u.GetStatus();
        Assert.Equal("rolled_back", status.LastOutcome);
        Assert.Contains("rolled back", status.LastResultLine);

        h.Calls.Clear();
        _now += TimeSpan.FromDays(3);
        RuntimeUpdateRunResult again = await u.RunAsync(true, false, default);
        Assert.Equal("current", again.Outcome);
        Assert.DoesNotContain(h.Calls, c => c.StartsWith("stage:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Launches_on_other_builds_do_not_affect_probation()
    {
        FakeHost h = HostWithCatalog(Cand("b200", "13.1"));
        ManagedRuntimeUpdater u = Make(h);
        await u.RunAsync(true, false, default);
        u.ObserveLaunch(h.DirectoryOf(new RuntimeBuildId(Cuda, "b100", "12.4")), false, "unrelated");
        Assert.Equal("b200", h.Active.Tag);
    }

    [Fact]
    public async Task Successful_launches_adopt_the_build_and_retention_keeps_only_active_and_previous()
    {
        FakeHost h = HostWithCatalog(Cand("b200", "13.1"));
        h.Installed.Add(new InstalledBuild(new RuntimeBuildId(Cuda, "b050", "12.4"), true));   // old managed build
        h.Installed.Add(new InstalledBuild(new RuntimeBuildId(Cuda, "b010", "12.4"), false));  // user's legacy folder: never pruned
        ManagedRuntimeUpdater u = Make(h);
        await u.RunAsync(true, false, default);
        for (int i = 0; i < ManagedRuntimeUpdater.ProbationLaunches; i++) u.ObserveLaunch(h.DirectoryOf(h.Active), true, null);
        Assert.Contains(h.Installed, i => i.Id.Tag == "b200");
        Assert.Contains(h.Installed, i => i.Id.Tag == "b100");
        Assert.DoesNotContain(h.Installed, i => i.Id.Tag == "b050");
        Assert.Contains(h.Installed, i => i.Id.Tag == "b010");
        Assert.Equal("idle", new ManagedRuntimeUpdateStore(_dir).Load().Backends[Cuda].Phase);
    }

    [Fact]
    public async Task Corrupt_state_file_reads_as_idle_and_pointer_stays_authoritative()
    {
        File.WriteAllText(Path.Combine(_dir, ManagedRuntimeUpdateStore.FileName), "{ not json");
        FakeHost h = HostWithCatalog(Cand("b200", "13.1"));
        RuntimeUpdateRunResult r = await Make(h).RunAsync(true, false, default);
        Assert.Equal("updated", r.Outcome);
    }

    // ---- integration with the maintenance pass --------------------------------------------------------------------

    private AutoMaintenanceService MakeMaintenance(FakeHost h, List<RuntimeUpdateObservation> observations, out ManagedRuntimeUpdater updater)
    {
        updater = Make(h);
        return new AutoMaintenanceService(new AutoMaintenanceStore(_dir), new AutoMaintenanceDependencies
        {
            Probe = _ => Task.FromResult(new HardwareSnapshot(null, 1, "cpu", Array.Empty<string>())),
            ListInstalledModels = () => Array.Empty<ManagedModelInfo>(),
            AutoAssign = (m, s) => new RoleAutoAssignResponse(),
            HasRoleAssignment = () => true,
            RefreshDiscovery = _ => Task.FromResult(new ModelDiscoveryRefreshResult { Outcome = "refreshed" }),
            DiscoveryCandidates = () => Array.Empty<ModelDiscoveryCandidate>(),
            CheckRuntimeUpdates = _ => Task.FromResult<IReadOnlyList<RuntimeUpdateObservation>>(observations),
            PendingManagedRuntimeUpdates = () => new List<(string, string)>(),
            IsGamingActive = () => h.Gaming,
            RuntimeUpdater = updater,
            UtcNow = () => _now
        });
    }

    [Fact]
    public async Task Maintenance_pass_updates_runtimes_and_suppresses_the_old_proposal()
    {
        FakeHost h = HostWithCatalog(Cand("b200", "13.1"));
        var obs = new List<RuntimeUpdateObservation> { new(Cuda, "b100", "b200", ComponentOwnership.PARUX_MANAGED) };
        AutoMaintenanceService svc = MakeMaintenance(h, obs, out _);
        MaintenanceReport report = await svc.RunAsync("interval", default);
        Assert.Equal("updated", report.RuntimeUpdateOutcome);
        Assert.Equal("ok", report.Outcome);
        Assert.Contains(report.Changes, c => c.StartsWith("runtime_update:", StringComparison.Ordinal));
        Assert.DoesNotContain(report.Proposals, p => p.Kind == "runtime_update");
        Assert.Equal("b200", h.Active.Tag);
    }

    [Fact]
    public async Task Maintenance_off_switch_keeps_the_proposal_and_changes_nothing()
    {
        FakeHost h = HostWithCatalog(Cand("b200", "13.1"));
        var obs = new List<RuntimeUpdateObservation> { new(Cuda, "b100", "b200", ComponentOwnership.PARUX_MANAGED) };
        AutoMaintenanceService svc = MakeMaintenance(h, obs, out _);
        svc.UpdateSettings(null, null, keepRuntimesUpdated: false);
        MaintenanceReport report = await svc.RunAsync("interval", default);
        Assert.Equal("disabled", report.RuntimeUpdateOutcome);
        Assert.Contains(report.Proposals, p => p.Kind == "runtime_update");
        Assert.Equal("b100", h.Active.Tag);
        Assert.False(svc.GetState().Settings.KeepRuntimesUpdated);
    }

    [Fact]
    public async Task Maintenance_keeps_a_proposal_only_when_the_user_must_act()
    {
        FakeHost h = HostWithCatalog(Cand("b200", "13.1"));
        h.Stage = _ => new RuntimeStageResult(false, "disk full");
        var obs = new List<RuntimeUpdateObservation> { new(Cuda, "b100", "b200", ComponentOwnership.PARUX_MANAGED) };
        AutoMaintenanceService svc = MakeMaintenance(h, obs, out _);
        for (int i = 0; i < ManagedRuntimeUpdater.MaximumAttempts; i++)
        {
            await svc.RunAsync("interval", default);
            _now += TimeSpan.FromDays(1);
        }
        MaintenanceReport report = await svc.RunAsync("interval", default);
        Assert.Equal("needs_user", report.RuntimeUpdateOutcome);
        Assert.Contains(report.Proposals, p => p.Kind == "runtime_update");
    }

    [Fact]
    public async Task Maintenance_never_acts_on_user_managed_runtimes_and_still_notes_them()
    {
        FakeHost h = HostWithCatalog(Cand("b200", "13.1"));
        h.Ownership = ComponentOwnership.USER_MANAGED;
        var obs = new List<RuntimeUpdateObservation> { new(Cuda, "b100", "b200", ComponentOwnership.USER_MANAGED) };
        AutoMaintenanceService svc = MakeMaintenance(h, obs, out _);
        MaintenanceReport report = await svc.RunAsync("interval", default);
        Assert.Contains(report.Proposals, p => p.Kind == "runtime_note");
        Assert.Empty(h.Activations);
        Assert.DoesNotContain(h.Calls, c => c.StartsWith("stage:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Maintenance_defers_the_whole_pass_in_gaming_mode()
    {
        FakeHost h = HostWithCatalog(Cand("b200", "13.1"));
        h.Gaming = true;
        AutoMaintenanceService svc = MakeMaintenance(h, new List<RuntimeUpdateObservation>(), out _);
        MaintenanceReport report = await svc.RunAsync("interval", default);
        Assert.Equal("skipped_gaming", report.Outcome);
        Assert.Empty(h.Calls);
    }

    [Fact]
    public void Keep_runtimes_updated_defaults_on_and_survives_a_round_trip()
    {
        var store = new AutoMaintenanceStore(_dir);
        Assert.True(store.Load().Settings.KeepRuntimesUpdated);
        AutoMaintenanceState s = store.Load();
        s.Settings.KeepRuntimesUpdated = false;
        store.Save(s);
        Assert.False(new AutoMaintenanceStore(_dir).Load().Settings.KeepRuntimesUpdated);
    }

    [Fact]
    public async Task Background_tick_does_nothing_when_nothing_is_due_and_respects_the_off_switch()
    {
        FakeHost h = HostWithCatalog(Cand("b200", "13.1"));
        AutoMaintenanceService svc = MakeMaintenance(h, new List<RuntimeUpdateObservation>(), out _);
        await svc.TickRuntimeUpdateAsync(default);
        Assert.Empty(h.Calls);                                                     // no network when nothing is pending or due
        h.Generating = true;
        await svc.RunRuntimeUpdateAsync(force: true, default);                     // leaves a pending switch
        h.Generating = false;
        svc.UpdateSettings(null, null, keepRuntimesUpdated: false);
        await svc.TickRuntimeUpdateAsync(default);
        Assert.Equal("b100", h.Active.Tag);                                        // off switch: the tick does not complete it
        svc.UpdateSettings(null, null, keepRuntimesUpdated: true);
        await svc.TickRuntimeUpdateAsync(default);
        Assert.Equal("b200", h.Active.Tag);
    }
}
