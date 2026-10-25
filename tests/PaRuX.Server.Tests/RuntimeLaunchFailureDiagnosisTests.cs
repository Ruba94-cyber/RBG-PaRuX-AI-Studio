using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Classification of a launch that died before becoming healthy. The mapping is pure. The last test runs a real child
/// process that exits with the Windows illegal-instruction status, through the production exit path.
/// </summary>
public sealed class RuntimeLaunchFailureDiagnosisTests
{
    [Fact]
    public void The_windows_illegal_instruction_status_is_the_signed_view_of_0xC000001D()
    {
        Assert.Equal(-1073741795, RuntimeLaunchFailureDiagnosis.WindowsIllegalInstructionExitCode);
        Assert.Equal(0xC000001Du, unchecked((uint)RuntimeLaunchFailureDiagnosis.WindowsIllegalInstructionExitCode));
    }

    [Theory]
    [InlineData(-1073741795)]
    [InlineData(132)]
    [InlineData(-4)]
    public void An_illegal_instruction_exit_is_diagnosed_as_a_cpu_mismatch_with_a_baseline_build(int exitCode)
    {
        RuntimeLaunchFailureDiagnosis.Diagnosis? diagnosis = RuntimeLaunchFailureDiagnosis.Diagnose(exitCode, string.Empty);

        Assert.NotNull(diagnosis);
        Assert.Equal(RuntimeLaunchFailureDiagnosis.CauseKind.IllegalInstruction, diagnosis.Kind);
        Assert.Contains("illegal-instruction", diagnosis.Cause);
        Assert.Contains("CPU-baseline", diagnosis.SuggestedBuildVariant);
    }

    [Fact]
    public void An_illegal_instruction_message_is_diagnosed_even_when_the_exit_code_is_ordinary()
    {
        RuntimeLaunchFailureDiagnosis.Diagnosis? diagnosis = RuntimeLaunchFailureDiagnosis.Diagnose(1, "Illegal instruction (core dumped)");

        Assert.NotNull(diagnosis);
        Assert.Equal(RuntimeLaunchFailureDiagnosis.CauseKind.IllegalInstruction, diagnosis.Kind);
    }

    [Fact]
    public void A_feature_list_printed_on_a_healthy_start_is_not_read_as_a_missing_feature()
    {
        const string stderr = "system_info: n_threads = 8 | AVX = 1 | AVX2 = 1 | AVX512 = 0 | FMA = 1 | F16C = 1";

        Assert.Null(RuntimeLaunchFailureDiagnosis.Diagnose(1, stderr));
    }

    [Fact]
    public void An_explicit_refusal_of_a_vector_extension_is_diagnosed_as_a_missing_cpu_feature()
    {
        RuntimeLaunchFailureDiagnosis.Diagnosis? diagnosis = RuntimeLaunchFailureDiagnosis.Diagnose(1, "error: AVX2 is not supported by this processor");

        Assert.NotNull(diagnosis);
        Assert.Equal(RuntimeLaunchFailureDiagnosis.CauseKind.MissingCpuFeature, diagnosis.Kind);
    }

    [Fact]
    public void An_ordinary_failure_with_no_cpu_evidence_is_not_diagnosed()
    {
        Assert.Null(RuntimeLaunchFailureDiagnosis.Diagnose(1, "error: failed to load model: file not found"));
        Assert.Null(RuntimeLaunchFailureDiagnosis.Diagnose(3, null));
    }

    [Fact]
    public async Task A_real_child_that_exits_with_the_illegal_instruction_status_reports_the_cause()
    {
        string work = Path.Combine(Path.GetTempPath(), "parux-illegal-" + Guid.NewGuid().ToString("N"));
        using IDisposable root = LocalAiPaths.UseRootForTests(Path.Combine(work, "localai"));
        using IDisposable state = LocalAiPaths.UseStateDirForTests(Path.Combine(work, "state"));
        try
        {
            string shell = Path.Combine(Environment.SystemDirectory, "cmd.exe");
            var manager = new LocalAiProcessManager(0);

            LocalAiProcessManager.RuntimeResult result = await manager.LaunchAndWaitAsync(
                shell, ["/c", "exit", "-1073741795"], Environment.SystemDirectory, null,
                Path.Combine(LocalAiPaths.StateDir, "diagnosis-probe.pid"), null,
                () => Task.FromResult(false), () => { }, TimeSpan.FromSeconds(60), CancellationToken.None);

            Assert.False(result.Success);
            Assert.Contains("illegal-instruction", result.Message);
            Assert.Contains("CPU-baseline", result.Message);
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
