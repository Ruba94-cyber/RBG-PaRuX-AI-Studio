using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>Hermetic: the ComfyUI component reuses ManagedRuntimeUpdater with ComfyBuildPolicy; a fake host replaces network, disk, GPU.</summary>
public sealed class ComfyRuntimeUpdaterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "parux-comfyupd-" + Guid.NewGuid().ToString("N"));
    private DateTime _now = new(2026, 10, 25, 12, 0, 0, DateTimeKind.Utc);
    public ComfyRuntimeUpdaterTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private const string B = ComfyBuildPolicy.Backend;
    private static RuntimeBuildId Id(string tag, string? toolkit) => new(B, tag, toolkit);

    private sealed class FakeHost : IManagedRuntimeHost
    {
        public bool Gaming, Generating;
        public ComponentOwnership Ownership = ComponentOwnership.PARUX_MANAGED;
        public CudaHardware Hardware = new(12.0, "32.0.15.8120");
        public RuntimeBuildId Active = Id("v0.33.1", "13.0");
        public List<InstalledBuild> Installed = new();
        public List<RuntimeCandidate> Catalog = new();
        public Func<RuntimeBuildId, RuntimeSmokeResult> Smoke = _ => new RuntimeSmokeResult(true, "served", "ok");
        public Func<RuntimeBuildId, RuntimeBenchResult?> Bench = _ => new RuntimeBenchResult(0.03, 0.4, "z-image", "img/s", "cold", "warm");
        public List<string> Calls = new();
        public List<RuntimeBuildId> Activations = new();
        public FakeHost() { Installed.Add(new InstalledBuild(Active, true)); }

        public bool IsGamingActive() => Gaming;
        public bool IsGenerationRunning() => Generating;
        public Task<CudaHardware> ReadCudaHardwareAsync(CancellationToken ct) => Task.FromResult(Hardware);
        public Task<IReadOnlyList<RuntimeUpdateTarget>> GetTargetsAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<RuntimeUpdateTarget>>(new[] { new RuntimeUpdateTarget(B, Ownership, Ownership == ComponentOwnership.PARUX_MANAGED ? Active : null) });
        public Task<IReadOnlyList<RuntimeCandidate>> ListAvailableAsync(string backend, int cudaMajor, CancellationToken ct)
        {
            Calls.Add("list:" + cudaMajor);
            return Task.FromResult<IReadOnlyList<RuntimeCandidate>>(Catalog.ToList());
        }
        public IReadOnlyList<InstalledBuild> ListInstalled(string backend) => Installed.ToList();
        public RuntimeBuildId? GetActive(string backend) => Active;
        public Task<RuntimeStageResult> StageAsync(RuntimeCandidate c, int cudaMajor, CancellationToken ct)
        {
            Calls.Add("stage:" + c.Id.Key);
            Installed.Add(new InstalledBuild(c.Id, true));
            return Task.FromResult(new RuntimeStageResult(true, "ok"));
        }
        public Task<RuntimeSmokeResult> SmokeAsync(RuntimeBuildId b, CancellationToken ct) { Calls.Add("smoke:" + b.Key); return Task.FromResult(Smoke(b)); }
        public Task<RuntimeBenchResult?> BenchmarkAsync(RuntimeBuildId b, CancellationToken ct) { Calls.Add("bench:" + b.Key); return Task.FromResult(Bench(b)); }
        public void Activate(RuntimeBuildId b)
        {
            if (Generating) throw new InvalidOperationException("media job running");
            Calls.Add("activate:" + b.Key);
            Activations.Add(b);
            Active = b;
        }
        public bool Remove(RuntimeBuildId b) { Calls.Add("remove:" + b.Key); return b != Active && Installed.RemoveAll(i => i.Id == b) > 0; }
        public string DirectoryOf(RuntimeBuildId b) => Path.Combine("C:\\fake-comfy", b.Tag + "-" + b.Toolkit);
        public void CleanupStaging() { }
    }

    private ManagedRuntimeUpdater Make(FakeHost h) => new(new ManagedRuntimeUpdateStore(_dir), h, () => _now,
        ComfyBuildPolicy.ParseBuild, ComfyBuildPolicy.CudaBackends, "ComfyUI");

    private static RuntimeCandidate Cand(string tag, string toolkit) => new(Id(tag, toolkit), 1);

    [Fact]
    public void Release_tags_are_ordered_numerically_and_legacy_is_unorderable()
    {
        Assert.True(ComfyBuildPolicy.ParseBuild("v0.37.0") > ComfyBuildPolicy.ParseBuild("v0.9.2"));
        Assert.True(ComfyBuildPolicy.ParseBuild("0.10.0") > ComfyBuildPolicy.ParseBuild("0.9.12"));
        Assert.Null(ComfyBuildPolicy.ParseBuild("legacy"));
        Assert.Null(ComfyBuildPolicy.ParseBuild("v0.37.0-rc1"));
    }

    [Fact]
    public async Task Newer_comfyui_is_staged_tested_gated_and_switched_with_the_old_build_kept_for_rollback()
    {
        var h = new FakeHost { Catalog = { Cand("v0.33.1", "13.0"), Cand("v0.37.0", "13.0") } };
        RuntimeUpdateRunResult r = await Make(h).RunAsync(true, false, default);
        Assert.Equal("updated", r.Outcome);
        Assert.Equal(Id("v0.37.0", "13.0"), h.Active);
        Assert.Equal(new[] { "list:13", "stage:v0.37.0|13.0", "smoke:v0.37.0|13.0", "bench:v0.33.1|13.0", "bench:v0.37.0|13.0", "activate:v0.37.0|13.0" },
            h.Calls.Where(c => c is not "cleanup").ToArray());
        Assert.Contains(h.Installed, i => i.Id == Id("v0.33.1", "13.0")); // previous kept
        Assert.Contains("img/s", string.Join(' ', r.Steps));
    }

    [Fact]
    public async Task CPU_only_pytorch_in_the_active_build_is_repaired_by_a_cuda_build_of_the_same_version()
    {
        var h = new FakeHost { Active = Id("v0.37.0", null), Catalog = { Cand("v0.37.0", "13.0") } };
        h.Installed.Clear();
        h.Installed.Add(new InstalledBuild(h.Active, true));
        RuntimeUpdateRunResult r = await Make(h).RunAsync(true, false, default);
        Assert.Equal("updated", r.Outcome);
        Assert.Equal(Id("v0.37.0", "13.0"), h.Active);
    }

    [Fact]
    public async Task Driver_too_old_for_cuda_13_only_considers_the_cuda_12_6_build()
    {
        var h = new FakeHost { Hardware = new CudaHardware(8.9, "32.0.15.7000"), Catalog = { Cand("v0.37.0", "13.0"), Cand("v0.37.0", "12.6") } };
        h.Active = Id("v0.33.1", "12.6");
        h.Installed.Clear();
        h.Installed.Add(new InstalledBuild(h.Active, true));
        await Make(h).RunAsync(true, false, default);
        Assert.Equal(Id("v0.37.0", "12.6"), h.Active);
        Assert.DoesNotContain(h.Calls, c => c.StartsWith("stage:", StringComparison.Ordinal) && c.Contains("13.0"));
    }

    [Fact]
    public async Task User_managed_comfyui_is_reported_and_never_staged_or_switched()
    {
        var h = new FakeHost { Ownership = ComponentOwnership.USER_MANAGED, Catalog = { Cand("v0.37.0", "13.0") } };
        RuntimeUpdateRunResult r = await Make(h).RunAsync(true, false, default);
        Assert.Contains("informational only", string.Join(' ', r.Steps));
        Assert.DoesNotContain(h.Calls, c => c.StartsWith("stage:", StringComparison.Ordinal) || c.StartsWith("activate:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_off_switch_runs_nothing_but_an_explicit_update_still_works()
    {
        var h = new FakeHost { Catalog = { Cand("v0.37.0", "13.0") } };
        Assert.Equal("disabled", (await Make(h).RunAsync(false, false, default)).Outcome);
        Assert.Empty(h.Calls);
        Assert.Equal("updated", (await Make(h).RunAsync(false, true, default)).Outcome);
    }

    [Fact]
    public async Task Gaming_mode_defers_everything_and_nothing_is_downloaded()
    {
        var h = new FakeHost { Gaming = true, Catalog = { Cand("v0.37.0", "13.0") } };
        Assert.Equal("deferred", (await Make(h).RunAsync(true, false, default)).Outcome);
        Assert.DoesNotContain(h.Calls, c => c.StartsWith("stage:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_running_media_job_defers_the_switch_until_it_ends()
    {
        var h = new FakeHost { Generating = true, Catalog = { Cand("v0.37.0", "13.0") } };
        ManagedRuntimeUpdater u = Make(h);
        Assert.Equal("deferred", (await u.RunAsync(true, false, default)).Outcome);
        Assert.True(u.HasPendingActivation());
        Assert.Equal(Id("v0.33.1", "13.0"), h.Active);
        h.Generating = false;
        Assert.Equal("updated", (await u.CompletePendingAsync(default)).Outcome);
        Assert.Equal(Id("v0.37.0", "13.0"), h.Active);
    }

    [Fact]
    public async Task A_build_that_is_slower_beyond_the_tolerance_is_rejected_removed_and_never_retried()
    {
        var h = new FakeHost { Catalog = { Cand("v0.37.0", "13.0") } };
        h.Bench = b => b.Tag == "v0.37.0" ? new RuntimeBenchResult(0.03, 0.30, "z-image", "img/s", "cold", "warm") : new RuntimeBenchResult(0.03, 0.40, "z-image", "img/s", "cold", "warm");
        ManagedRuntimeUpdater u = Make(h);
        Assert.Equal("rejected", (await u.RunAsync(true, false, default)).Outcome);
        Assert.Equal(Id("v0.33.1", "13.0"), h.Active);
        Assert.Contains("remove:v0.37.0|13.0", h.Calls);
        h.Calls.Clear();
        await u.RunAsync(true, false, default);
        Assert.DoesNotContain(h.Calls, c => c.StartsWith("stage:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_failed_smoke_test_rejects_the_build_without_touching_the_active_one()
    {
        var h = new FakeHost { Catalog = { Cand("v0.37.0", "13.0") } };
        h.Smoke = _ => new RuntimeSmokeResult(false, "failed", "image is blank");
        Assert.Equal("rejected", (await Make(h).RunAsync(true, false, default)).Outcome);
        Assert.Empty(h.Activations);
    }

    [Fact]
    public async Task A_failed_first_real_launch_rolls_back_to_the_previous_build()
    {
        var h = new FakeHost { Catalog = { Cand("v0.37.0", "13.0") } };
        ManagedRuntimeUpdater u = Make(h);
        await u.RunAsync(true, false, default);
        Assert.Equal(Id("v0.37.0", "13.0"), h.Active);
        u.ObserveLaunch(h.DirectoryOf(Id("v0.37.0", "13.0")), false, "exited with code 1");
        Assert.Equal(Id("v0.33.1", "13.0"), h.Active);
    }

    [Fact]
    public async Task Two_good_launches_adopt_the_build_and_prune_older_ones()
    {
        var h = new FakeHost { Catalog = { Cand("v0.37.0", "13.0") } };
        h.Installed.Add(new InstalledBuild(Id("v0.20.0", "13.0"), true));
        ManagedRuntimeUpdater u = Make(h);
        await u.RunAsync(true, false, default);
        string dir = h.DirectoryOf(Id("v0.37.0", "13.0"));
        u.ObserveLaunch(dir, true, null);
        u.ObserveLaunch(dir, true, null);
        Assert.Contains("remove:v0.20.0|13.0", h.Calls);
        Assert.DoesNotContain(h.Calls, c => c == "remove:v0.33.1|13.0"); // the previous build stays for rollback
    }

    // ---- policy / disk facts --------------------------------------------------------------------------------------

    private string FakePortable(string name, string comfyVersion, string torchVersion, string? cuda)
    {
        string root = Path.Combine(_dir, name);
        Directory.CreateDirectory(Path.Combine(root, "ComfyUI"));
        Directory.CreateDirectory(Path.Combine(root, "python_embeded", "Lib", "site-packages", "torch"));
        File.WriteAllText(Path.Combine(root, "ComfyUI", "main.py"), "");
        File.WriteAllText(Path.Combine(root, "python_embeded", "python.exe"), "");
        File.WriteAllText(Path.Combine(root, "ComfyUI", "comfyui_version.py"), $"__version__ = \"{comfyVersion}\"\n");
        File.WriteAllText(Path.Combine(root, "python_embeded", "Lib", "site-packages", "torch", "version.py"),
            $"__version__: str = '{torchVersion}'\ndebug = False\ncuda: Optional[str] = {(cuda is null ? "None" : "'" + cuda + "'")}\n");
        return root;
    }

    [Fact]
    public void Torch_cuda_is_read_from_the_installed_wheel_without_starting_python()
    {
        string gpu = FakePortable("gpu", "0.37.0", "2.14.1+cu130", "13.0");
        string cpu = FakePortable("cpu", "0.37.0", "2.14.1+cpu", null);
        Assert.Equal("13.0", ComfyBuildPolicy.ReadTorchCuda(gpu));
        Assert.Equal("2.14.1+cu130", ComfyBuildPolicy.ReadTorchVersion(gpu));
        Assert.Null(ComfyBuildPolicy.ReadTorchCuda(cpu));
        Assert.Equal("0.37.0", ComfyBuildPolicy.ReadComfyVersion(gpu));
    }

    [Fact]
    public void A_legacy_build_is_ordered_by_the_version_it_contains_and_a_cpu_torch_marks_it_for_repair()
    {
        string cpu = FakePortable("cpu2", "0.37.0", "2.14.1+cpu", null);
        RuntimeBuildId id = ComfyBuildPolicy.IdOf(new ComfyBuildIdentity("legacy", "legacy"), cpu);
        Assert.Equal("v0.37.0", id.Tag);
        Assert.Null(id.Toolkit);
        string gpu = FakePortable("gpu2", "0.37.0", "2.14.1+cu130", "13.0");
        Assert.Equal("13.0", ComfyBuildPolicy.IdOf(new ComfyBuildIdentity("v0.37.0", "nvidia"), gpu).Toolkit);
    }

    [Fact]
    public void Staging_a_build_leaves_the_active_runtime_models_and_markers_untouched_and_the_active_build_cannot_be_removed()
    {
        string active = FakePortable("active", "0.33.1", "2.13.0+cu130", "13.0");
        Directory.CreateDirectory(Path.Combine(active, "ComfyUI", "models", "diffusion_models"));
        File.WriteAllText(Path.Combine(active, "ComfyUI", "models", "diffusion_models", "m.safetensors"), "weights");
        string incoming = FakePortable("incoming", "0.37.0", "2.14.1+cu130", "13.0");
        var store = new ComfyRuntimeStore(active, Path.Combine(_dir, "store"));
        var legacy = new ComfyBuildIdentity("legacy", "legacy");
        Assert.Equal(legacy, store.GetActiveIdentity());

        var staged = new ComfyBuildIdentity("v0.37.0", "nvidia");
        store.StageExtracted(incoming, staged);
        Assert.Equal(legacy, store.GetActiveIdentity());                                   // still the active one
        Assert.True(File.Exists(Path.Combine(active, "ComfyUI", "models", "diffusion_models", "m.safetensors")));
        Assert.Contains(staged, store.ListInstalled());
        Assert.False(store.RemoveStored(legacy));                                          // active: refused

        Assert.True(store.TryActivateInstalled(staged));                                   // the atomic switch
        Assert.Equal(staged, store.GetActiveIdentity());
        Assert.True(File.Exists(Path.Combine(active, "ComfyUI", "models", "diffusion_models", "m.safetensors"))); // models follow the active tree
        Assert.True(store.TryActivateInstalled(legacy));                                   // rollback
        Assert.Equal(legacy, store.GetActiveIdentity());
        Assert.True(store.RemoveStored(staged));
        Assert.DoesNotContain(staged, store.ListInstalled());
    }
}
