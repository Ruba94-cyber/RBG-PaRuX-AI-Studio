using System.Diagnostics;
using System.Text;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// The topology query is bounded like the VRAM probe. A driver that never answers must not hold the diagnostic
/// endpoint, and the child process is stopped at the deadline. The real process runner is used; only the
/// arguments are replaced, with a PowerShell command that sleeps or answers, because nvidia-smi is not required.
/// </summary>
public sealed class GpuTopologyBoundedTests
{
    private static string PowerShellExecutable() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");

    [Fact]
    public async Task A_topology_query_that_never_answers_returns_null_and_stops_its_child_at_the_deadline()
    {
        string childPidFile = Path.Combine(Path.GetTempPath(), "parux-topology-" + Guid.NewGuid().ToString("N") + ".pid");
        // The stand-in records its own PID before it sleeps, so the test can check that the child is gone, not merely abandoned.
        string script = "Set-Content -LiteralPath '" + childPidFile + "' -Value $PID; Start-Sleep -Seconds 120";
        string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        NvidiaSmiProbe.Runner sleepingDriver = (candidate, _, token) =>
            NvidiaSmiProbe.RunProcessAsync(candidate, "-NoProfile -NonInteractive -EncodedCommand " + encoded, token);
        var clock = Stopwatch.StartNew();
        int? child = null;
        try
        {
            string? topology = await GpuPerformanceTelemetryService.GetTopologyTextAsync(
                new[] { PowerShellExecutable() }, sleepingDriver, TimeSpan.FromSeconds(8), CancellationToken.None);

            Assert.Null(topology);
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(30), "The query waited for the driver instead of its deadline.");
            Assert.True(File.Exists(childPidFile), "The stand-in never started, so its exit cannot be checked.");
            child = int.Parse(File.ReadAllText(childPidFile).Trim());
            Assert.True(await HasExitedAsync(child.Value, TimeSpan.FromSeconds(10)),
                "The topology query's child process was left running after its deadline.");
        }
        finally
        {
            KillIfRunning(child);
            try { File.Delete(childPidFile); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>True once the process has exited, or no longer runs under that PID.</summary>
    private static async Task<bool> HasExitedAsync(int processId, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            try
            {
                using Process process = Process.GetProcessById(processId);
                if (process.HasExited) return true;
            }
            catch (ArgumentException) { return true; }
            if (DateTime.UtcNow >= deadline) return false;
            await Task.Delay(100);
        }
    }

    private static void KillIfRunning(int? processId)
    {
        if (processId is null) return;
        try { Process.GetProcessById(processId.Value).Kill(entireProcessTree: true); } catch { }
    }

    [Fact]
    public async Task A_topology_query_returns_the_output_of_a_driver_that_answers()
    {
        NvidiaSmiProbe.Runner answeringDriver = (candidate, _, token) =>
            NvidiaSmiProbe.RunProcessAsync(candidate, "-NoProfile -NonInteractive -Command Write-Output GPU0", token);

        string? topology = await GpuPerformanceTelemetryService.GetTopologyTextAsync(
            new[] { PowerShellExecutable() }, answeringDriver, TimeSpan.FromSeconds(20), CancellationToken.None);

        Assert.Equal("GPU0", topology);
    }
}
