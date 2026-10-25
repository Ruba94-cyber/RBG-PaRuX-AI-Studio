using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class TrainingHardwareProfileTests
{
    [Theory]
    [InlineData("cuda", "cuda-validated")]
    [InlineData("rocm-cuda-api", "rocm-cuda-api")]
    [InlineData("cpu", "cpu-tts-only")]
    [InlineData("xpu-unvalidated", "accelerator-unvalidated")]
    [InlineData("something-else", "unknown")]
    public void Classify_names_the_profile_from_the_probed_backend(string backend, string expected) =>
        Assert.Equal(expected, TrainingHardwareProfile.Classify(true, backend));

    [Fact]
    public void Not_installed_is_never_reported_as_a_hardware_profile()
    {
        Assert.Equal("not-installed", TrainingHardwareProfile.Classify(false, "cuda"));
        Assert.Empty(TrainingHardwareProfile.Unsupported(false, "cuda", Array.Empty<string>()));
    }

    [Fact]
    public void Cpu_only_machine_such_as_an_amd_igpu_records_five_unsupported_methods_with_reason()
    {
        IReadOnlyList<string> advertised = TrainingRuntimeCatalog.MethodsForProbe(
            new TrainingRuntimeProbe { ImportsValid = true, Backend = "cpu" });
        var unsupported = TrainingHardwareProfile.Unsupported(true, "cpu", advertised);

        Assert.Equal(new[] { TrainingMethod.TtsSft }, advertised);
        Assert.Equal(5, unsupported.Count);
        Assert.DoesNotContain(unsupported, u => u.Method == TrainingMethod.TtsSft);
        Assert.All(unsupported, u => Assert.Contains("CUDA", u.Reason));
        Assert.Contains("ROCm", unsupported[0].Reason);
    }

    [Fact]
    public void Verified_cuda_runtime_has_no_unsupported_methods()
    {
        Assert.Empty(TrainingHardwareProfile.Unsupported(true, "cuda", TrainingMethod.All));
    }

    [Fact]
    public void Contract_carries_profile_and_unsupported_methods_for_a_cpu_runtime()
    {
        var probe = new TrainingRuntimeProbe
        {
            ImportsValid = true,
            PythonVersion = TrainingRuntimeCatalog.PythonVersion,
            UnslothVersion = TrainingRuntimeCatalog.UnslothVersion,
            TorchVersion = "2.9.0+cpu",
            Backend = "cpu"
        };
        var status = new TrainingRuntimeStatus(true, true, probe, null,
            new[] { TrainingMethod.TtsSft }, "cpu");

        TrainingEngineStatus dto = TrainingRuntimeV2Endpoints.ToContract(status);

        Assert.Equal("cpu-tts-only", dto.HardwareProfile);
        Assert.Equal(5, dto.UnsupportedMethods.Count);
        Assert.Equal(new[] { TrainingMethod.TtsSft }, dto.AvailableMethods);
    }
}
