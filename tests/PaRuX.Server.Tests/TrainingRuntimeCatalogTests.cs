using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class TrainingRuntimeCatalogTests
{
    [Fact]
    public void Shipped_main_lock_pins_the_catalog_unsloth_release_with_hashes()
    {
        string lockPath = TrainingRuntimeLock.DefaultPath;
        Assert.True(TrainingRuntimeLock.Validate(lockPath) > 0);
        Assert.Contains("unsloth==" + TrainingRuntimeCatalog.UnslothVersion, File.ReadAllText(lockPath), StringComparison.Ordinal);
    }

    [Fact]
    public void Cpu_probe_exposes_only_the_training_path_the_current_runner_can_honestly_support()
    {
        var probe = new TrainingRuntimeProbe
        {
            ImportsValid = true,
            Backend = "cpu",
            PythonVersion = TrainingRuntimeCatalog.PythonVersion,
            UnslothVersion = TrainingRuntimeCatalog.UnslothVersion
        };

        IReadOnlyList<string> methods = TrainingRuntimeCatalog.MethodsForProbe(probe);
        Assert.Equal(new[] { TrainingMethod.TtsSft }, methods);
        Assert.DoesNotContain(TrainingMethod.Sft, methods);
        Assert.DoesNotContain(TrainingMethod.VisionSft, methods);
    }

    [Theory]
    [InlineData("xpu-unvalidated")]
    [InlineData("unknown")]
    [InlineData("vulkan-unvalidated")]
    public void Unvalidated_backend_never_inherits_upstream_capability_claims(string backend)
    {
        var probe = new TrainingRuntimeProbe { ImportsValid = true, Backend = backend };
        Assert.Empty(TrainingRuntimeCatalog.MethodsForProbe(probe));
    }

    [Theory]
    [InlineData("cuda")]
    [InlineData("rocm-cuda-api")]
    public void Cuda_compatible_accelerator_exposes_all_current_training_methods(string backend)
    {
        var probe = new TrainingRuntimeProbe { ImportsValid = true, Backend = backend };
        Assert.Equal(TrainingMethod.All, TrainingRuntimeCatalog.MethodsForProbe(probe));
    }

    [Fact]
    public void Failed_import_probe_exposes_no_methods_even_on_cuda()
    {
        var probe = new TrainingRuntimeProbe { ImportsValid = false, Backend = "cuda" };
        Assert.Empty(TrainingRuntimeCatalog.MethodsForProbe(probe));
    }

    [Fact]
    public void Freeze_identity_is_order_and_newline_stable()
    {
        const string left = "torch==2.9\r\nunsloth==2026.9.4\r\n# comment\r\nlibrosa==1.0.0\r\n";
        const string right = "librosa==1.0.0\nunsloth==2026.9.4\ntorch==2.9\n";

        string canonicalLeft = TrainingRuntimeCatalog.CanonicalizeFreeze(left);
        string canonicalRight = TrainingRuntimeCatalog.CanonicalizeFreeze(right);
        Assert.Equal(canonicalLeft, canonicalRight);
        Assert.Equal(TrainingRuntimeCatalog.Sha256(canonicalLeft), TrainingRuntimeCatalog.Sha256(canonicalRight));
        Assert.Equal(64, TrainingRuntimeCatalog.Sha256(canonicalLeft).Length);
    }
}
