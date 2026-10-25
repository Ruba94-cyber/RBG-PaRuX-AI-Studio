using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>/api/v2/training/engine/install must use the same hash-locked flow as the legacy Lab install.</summary>
public sealed class TrainingRuntimeHashLockedInstallTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "parux-trlock-" + Guid.NewGuid().ToString("N"));
    private static readonly string Hash = "--hash=sha256:" + new string('a', 64);

    public TrainingRuntimeHashLockedInstallTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private string Lock(string name, string content)
    {
        string path = Path.Combine(_dir, name);
        File.WriteAllText(path, content);
        return path;
    }

    private sealed class Recorder
    {
        public readonly List<string> Calls = new();
        public readonly List<IReadOnlyList<string>> PipArgs = new();
    }

    private TrainingRuntimeCatalog.InstallSeams Seams(Recorder rec, string mainLock, string torchLock,
        TorchInstallChannelPlan? plan = null, TrainingRuntimeProbe? probe = null) => new()
    {
        MainLockPath = mainLock,
        TorchLockPath = _ => torchLock,
        SelectTorchPlan = _ => Task.FromResult(plan ?? new TorchInstallChannelPlan(TorchInstallChannel.Cpu, Array.Empty<string>(), "cpu channel")),
        EnsureVenv = (_, _) => { rec.Calls.Add("venv"); return Task.FromResult(Path.Combine(_dir, "python.exe")); },
        Pip = (_, args, _, _) => { rec.Calls.Add("pip"); rec.PipArgs.Add(args); return Task.FromResult("installed\n"); },
        Probe = (_, _) => { rec.Calls.Add("probe"); return Task.FromResult(probe ?? new TrainingRuntimeProbe
            {
                ImportsValid = true, PythonVersion = "3.11.9", UnslothVersion = TrainingRuntimeCatalog.UnslothVersion,
                TorchVersion = "2.10.0", Backend = "cpu"
            }); },
        ReadFreeze = (_, _, _) => Task.FromResult("numpy==1.0\ntorch==2.10.0")
    };

    [Fact]
    public async Task Install_runs_torch_lock_then_main_lock_with_require_hashes_and_no_deps_and_writes_manifest()
    {
        var rec = new Recorder();
        string main = Lock("main.txt", "numpy==1.0 \\\n    " + Hash + "\n");
        string torch = Lock("torch.txt", "torch==2.10.0+cpu \\\n    " + Hash + "\n");
        string root = Path.Combine(_dir, "rt");

        string log = await TrainingRuntimeCatalog.InstallAsync(root, Path.Combine(root, ".venv"), Seams(rec, main, torch), default);

        Assert.Equal(new[] { "venv", "pip", "pip", "probe" }, rec.Calls);
        Assert.All(rec.PipArgs, args =>
        {
            Assert.Contains("--require-hashes", args);
            Assert.Contains("--no-deps", args);
            Assert.DoesNotContain(args, a => a.Contains("--torch-backend", StringComparison.Ordinal));
            Assert.DoesNotContain(args, a => a.StartsWith("unsloth", StringComparison.Ordinal));
        });
        Assert.EndsWith("torch.txt", rec.PipArgs[0][^1]);
        Assert.Contains("--index-url", rec.PipArgs[0]);
        Assert.EndsWith("main.txt", rec.PipArgs[1][^1]);
        Assert.DoesNotContain("--index-url", rec.PipArgs[1]);

        TrainingRuntimeManifest manifest = TrainingRuntimeCatalog.ReadManifest(root)!;
        Assert.Equal("3.11.9", manifest.PythonVersion);
        Assert.Equal(TrainingRuntimeCatalog.Sha256("numpy==1.0\ntorch==2.10.0"), manifest.ResolvedPackagesSha256);
        Assert.Contains("freezeSha256=", log);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("unhashed")]
    [InlineData("unpinned")]
    public async Task Missing_or_unhashed_main_lock_is_refused_before_anything_runs(string kind)
    {
        var rec = new Recorder();
        string torch = Lock("torch.txt", "torch==2.10.0 \\\n  " + Hash + "\n");
        string main = kind switch
        {
            "missing" => Path.Combine(_dir, "absent.txt"),
            "unhashed" => Lock("m.txt", "numpy==1.0\n"),
            _ => Lock("m.txt", "numpy>=1.0 " + Hash + "\n")
        };
        var ex = await Assert.ThrowsAsync<TrainingRuntimeRefusalException>(() =>
            TrainingRuntimeCatalog.InstallAsync(Path.Combine(_dir, "rt"), Path.Combine(_dir, "rt", ".venv"), Seams(rec, main, torch), default));
        Assert.Equal(kind == "missing" ? TrainingRuntimeLock.LockMissing : TrainingRuntimeLock.LockInvalid, ex.Code);
        Assert.Empty(rec.Calls);
    }

    [Fact]
    public async Task Invalid_torch_lock_is_refused_before_the_environment_is_created()
    {
        var rec = new Recorder();
        string main = Lock("main.txt", "numpy==1.0 " + Hash + "\n");
        string torch = Lock("torch.txt", "torch==2.10.0\n");
        var ex = await Assert.ThrowsAsync<TrainingRuntimeRefusalException>(() =>
            TrainingRuntimeCatalog.InstallAsync(Path.Combine(_dir, "rt"), Path.Combine(_dir, "rt", ".venv"), Seams(rec, main, torch), default));
        Assert.Equal(TrainingRuntimeLock.LockInvalid, ex.Code);
        Assert.Empty(rec.Calls);
    }

    [Fact]
    public async Task Unsupported_hardware_is_a_typed_refusal_without_touching_the_environment()
    {
        var rec = new Recorder();
        string main = Lock("main.txt", "numpy==1.0 " + Hash + "\n");
        var refused = new TorchInstallChannelPlan(TorchInstallChannel.Refused, Array.Empty<string>(), "No supported accelerator.");
        var ex = await Assert.ThrowsAsync<TrainingRuntimeRefusalException>(() =>
            TrainingRuntimeCatalog.InstallAsync(Path.Combine(_dir, "rt"), Path.Combine(_dir, "rt", ".venv"), Seams(rec, main, main, refused), default));
        Assert.Equal("hardware_unsupported", ex.Code);
        Assert.Empty(rec.Calls);
    }

    [Fact]
    public async Task Unexpected_resolved_version_after_install_fails_honestly_and_writes_no_manifest()
    {
        var rec = new Recorder();
        string main = Lock("main.txt", "numpy==1.0 " + Hash + "\n");
        string root = Path.Combine(_dir, "rt");
        var wrongPython = new TrainingRuntimeProbe { ImportsValid = true, PythonVersion = "3.13.1", UnslothVersion = TrainingRuntimeCatalog.UnslothVersion, Backend = "cpu" };
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            TrainingRuntimeCatalog.InstallAsync(root, Path.Combine(root, ".venv"), Seams(rec, main, main, probe: wrongPython), default));
        Assert.Null(TrainingRuntimeCatalog.ReadManifest(root));

        var badImports = new TrainingRuntimeProbe { ImportsValid = false, Detail = "ImportError" };
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            TrainingRuntimeCatalog.InstallAsync(root, Path.Combine(root, ".venv"), Seams(rec, main, main, probe: badImports), default));
        Assert.Null(TrainingRuntimeCatalog.ReadManifest(root));
    }
}
