using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class TrainingRuntimeLockTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "parux-trainlock-" + Guid.NewGuid().ToString("N"));
    private static readonly string Hash = new('a', 64);

    public TrainingRuntimeLockTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private string Write(string content)
    {
        string path = Path.Combine(_dir, TrainingRuntimeLock.LockFileName);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public async Task Missing_lock_refuses_install_without_invoking_pip()
    {
        int calls = 0;
        var ex = await Assert.ThrowsAsync<TrainingRuntimeRefusalException>(() =>
            TrainingRuntimeLock.InstallAsync(Path.Combine(_dir, "absent.txt"), _ => { calls++; return Task.FromResult(""); }));
        Assert.Equal(TrainingRuntimeLock.LockMissing, ex.Code);
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData("numpy\n")]
    [InlineData("numpy>=1.0 --hash=sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\n")]
    [InlineData("numpy==1.26.4\n")]
    [InlineData("# only a comment\n")]
    public async Task Unpinned_unhashed_or_empty_lock_is_refused(string content)
    {
        int calls = 0;
        var ex = await Assert.ThrowsAsync<TrainingRuntimeRefusalException>(() =>
            TrainingRuntimeLock.InstallAsync(Write(content), _ => { calls++; return Task.FromResult(""); }));
        Assert.Equal(TrainingRuntimeLock.LockInvalid, ex.Code);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Valid_lock_installs_with_require_hashes_and_no_deps_only()
    {
        string path = Write("numpy==1.26.4 \\\n    --hash=sha256:" + Hash + " \\\n    --hash=sha256:" + new string('b', 64) + "\n"
            + "# comment\ntrl==0.9.6 --hash=sha256:" + Hash + "\n");
        IReadOnlyList<string>? args = null;
        await TrainingRuntimeLock.InstallAsync(path, a => { args = a; return Task.FromResult("ok"); });
        Assert.NotNull(args);
        Assert.Contains("--require-hashes", args!);
        Assert.Contains("--no-deps", args);
        Assert.Equal(2, TrainingRuntimeLock.Validate(path));
    }

    [Theory]
    [InlineData(TrainingMethod.TtsSft, "cpu", true, TrainingEligibility.Ok)]
    [InlineData(TrainingMethod.Sft, "cpu", true, TrainingEligibility.NonCudaUnsupported)]
    [InlineData(TrainingMethod.Grpo, "cpu", true, TrainingEligibility.NonCudaUnsupported)]
    [InlineData(TrainingMethod.Sft, "cuda", true, TrainingEligibility.Ok)]
    [InlineData(TrainingMethod.Sft, "xpu-unvalidated", true, TrainingEligibility.BackendUnvalidated)]
    [InlineData(TrainingMethod.Sft, "cuda", false, TrainingEligibility.NotInstalled)]
    public void Eligibility_returns_typed_codes(string method, string backend, bool installed, string expectedCode)
    {
        (bool eligible, string code) = TrainingEligibility.Check(method, backend, installed);
        Assert.Equal(expectedCode, code);
        Assert.Equal(expectedCode == TrainingEligibility.Ok, eligible);
    }

    private static string ShippedLock(string name)
    {
        string beside = Path.Combine(AppContext.BaseDirectory, name);
        if (File.Exists(beside)) return beside;
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            string candidate = Path.Combine(d.FullName, "src", "PaRuX.Server", name);
            if (File.Exists(candidate)) return candidate;
        }
        return beside;
    }

    [Theory]
    [InlineData("training-runtime.lock.txt", 50)]
    [InlineData("training-torch-cu128.lock.txt", 2)]
    [InlineData("training-torch-cpu.lock.txt", 2)]
    public void Shipped_locks_parse_and_every_requirement_is_pinned_and_hashed(string name, int minimum)
    {
        string path = ShippedLock(name);
        Assert.True(File.Exists(path), "shipped lock missing: " + name);
        Assert.True(TrainingRuntimeLock.Validate(path) >= minimum);
    }

    [Fact]
    public void Shipped_main_lock_has_no_torch_and_installs_the_expected_engine()
    {
        string text = File.ReadAllText(ShippedLock(TrainingRuntimeLock.LockFileName));
        Assert.DoesNotContain("\ntorch==", "\n" + text.Replace("\r\n", "\n"));
        Assert.Contains("unsloth==" + TrainingRuntimeCatalog.UnslothVersion, text);
        Assert.StartsWith("3.11", ManagedPythonRuntime.DefaultPythonVersion);
    }

    [Theory]
    [InlineData(TorchInstallChannel.Cuda128, "+cu128")]
    [InlineData(TorchInstallChannel.Cpu, "+cpu")]
    internal void Torch_channel_lock_matches_pinned_version_and_channel(TorchInstallChannel channel, string suffix)
    {
        string text = File.ReadAllText(ShippedLock(TrainingRuntimeLock.TorchLockFileName(channel)));
        Assert.Contains("torch==" + ExLlamaV3BuildCatalog.TorchVersion + suffix, text);
        Assert.StartsWith("https://download.pytorch.org/whl/", TrainingRuntimeLock.TorchIndexUrl(channel));
    }

    [Fact]
    public void Ineligible_hardware_is_refused_with_typed_code_before_any_job_starts()
    {
        var probe = new TrainingRuntimeProbe { ImportsValid = true, Backend = "cpu" };
        var status = new TrainingRuntimeStatus(true, true, probe, null, TrainingRuntimeCatalog.MethodsForProbe(probe), "");
        var ex = Assert.Throws<TrainingRuntimeRefusalException>(() => TrainingRuntimeJobGate.ValidateStatus(status, TrainingMethod.Sft));
        Assert.Equal(TrainingEligibility.NonCudaUnsupported, ex.Code);
        TrainingRuntimeJobGate.ValidateStatus(status, TrainingMethod.TtsSft);
        var none = Assert.Throws<TrainingRuntimeRefusalException>(() =>
            TrainingRuntimeJobGate.ValidateStatus(TrainingRuntimeStatus.NotInstalled("x"), TrainingMethod.Sft));
        Assert.Equal(TrainingEligibility.NotInstalled, none.Code);
    }
}
