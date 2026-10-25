using System.Diagnostics;
using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Gaming Mode stops local AI work that is already running, not only work that starts later. Each test starts a real
/// stand-in runtime through the real entry point, writes the game marker while the stand-in is running, and checks that
/// the stand-in is gone afterwards. The stand-in is the staged PaRuX.RuntimeStub: it ignores llama.cpp arguments and never
/// answers HTTP, so the probe is still waiting for health when the marker arrives. Only the copy this test installed under
/// its own temp root is ever matched or stopped.
/// </summary>
public sealed class GamingMidRunStopTests : IDisposable
{
    private readonly string _work = Path.Combine(Path.GetTempPath(), "parux-midrun-" + Guid.NewGuid().ToString("N"));
    private readonly List<Process> _standIns = new();

    public void Dispose()
    {
        foreach (Process standIn in _standIns)
        {
            try { if (!standIn.HasExited) standIn.Kill(entireProcessTree: true); } catch { }
            standIn.Dispose();
        }
        try { Directory.Delete(_work, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>Installs the staged RuntimeStub under the executable name a runtime expects. The apphost needs the
    /// assembly files beside it, so they are copied with it unchanged.</summary>
    private static string InstallStandIn(string directory, string executableName)
    {
        string staged = Path.Combine(AppContext.BaseDirectory, "runtime-stub");
        Directory.CreateDirectory(directory);
        foreach (string source in Directory.EnumerateFiles(staged, "llama-server*", SearchOption.TopDirectoryOnly))
            File.Copy(source, Path.Combine(directory, Path.GetFileName(source)), overwrite: true);
        string executable = Path.Combine(directory, executableName);
        if (!File.Exists(executable))
            File.Copy(Path.Combine(directory, LocalAiPaths.LlamaServerExecutable), executable, overwrite: true);
        return executable;
    }

    /// <summary>Finds the stand-in this test launched. Only an image path equal to the installed copy matches; nothing else is stopped.</summary>
    private async Task<Process?> WaitForStandInAsync(string executable, TimeSpan timeout)
    {
        string processName = Path.GetFileNameWithoutExtension(executable);
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            foreach (Process candidate in Process.GetProcessesByName(processName))
            {
                try
                {
                    if (string.Equals(candidate.MainModule?.FileName, executable, StringComparison.OrdinalIgnoreCase))
                    {
                        _standIns.Add(candidate);
                        return candidate;
                    }
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
                candidate.Dispose();
            }
            await Task.Delay(100);
        }
        return null;
    }

    /// <summary>What a run did when it never reached its stand-in, so a failure names the cause instead of only a missing process.</summary>
    private static string DescribeRun<T>(Task<T> run) =>
        run.IsFaulted ? run.Exception!.GetBaseException().Message
        : run.IsCompleted ? "the run completed with status " + run.Status
        : "the run was still waiting";

    [Fact]
    public async Task A_probe_running_when_the_game_marker_appears_is_stopped_and_reports_gaming()
    {
        using IDisposable root = LocalAiPaths.UseRootForTests(Path.Combine(_work, "localai"));
        using IDisposable state = LocalAiPaths.UseStateDirForTests(Path.Combine(_work, "state"));
        // A LocalAiProcessManager(0) probe never probes VRAM, so it always launches the Vulkan runtime.
        string executable = InstallStandIn(LocalAiPaths.RuntimeVulkanPath, LocalAiPaths.LlamaServerExecutable);
        string model = Path.Combine(_work, "probe-model.gguf");
        File.WriteAllBytes(model, new byte[16]);
        var probe = new LlamaServerPerformanceProbe(new LocalAiProcessManager(0), new GpuPerformanceTelemetryService());

        Task<LlamaServerPerformanceMeasurement> run = probe.RunAsync(
            model, "chat", new LlamaPerformanceProfileDto(), sustainedValidation: false, CancellationToken.None);
        Process? standIn = await WaitForStandInAsync(executable, TimeSpan.FromSeconds(30));
        Assert.NotNull(standIn);
        // The stand-in is running and never answers /health, so the probe is mid-run when Gaming Mode starts.
        File.WriteAllText(LocalAiPaths.GameMarkerFile, "{}");

        // A probe that ignores the marker keeps waiting for health for minutes, so the wait for it is bounded.
        Assert.Same(run, await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(60))));
        InvalidOperationException stopped = await Assert.ThrowsAsync<InvalidOperationException>(() => run);
        Assert.Contains("Gaming Mode started", stopped.Message, StringComparison.Ordinal);
        Assert.True(standIn!.WaitForExit(15_000), "The probe's llama-server kept running after Gaming Mode started.");
    }

    [Fact]
    public async Task A_benchmark_running_when_the_game_marker_appears_is_stopped_and_reports_gaming()
    {
        using IDisposable root = LocalAiPaths.UseRootForTests(Path.Combine(_work, "localai"));
        using IDisposable state = LocalAiPaths.UseStateDirForTests(Path.Combine(_work, "state"));
        string executable = InstallStandIn(LocalAiPaths.RuntimeVulkanPath, LocalAiPaths.LlamaBenchExecutable);
        string model = Path.Combine(_work, "bench-model.gguf");
        File.WriteAllBytes(model, new byte[16]);
        // The benchmark refuses to start while the canonical text or media port serves a runtime. Those ports belong to whatever
        // else runs on the host, so the precondition is fixed here: this test is about the Gaming Mode stop, not the port checks.
        var manager = new LocalAiProcessManager(0)
        {
            TextHealthProbeForTests = _ => Task.FromResult(false),
            MediaHealthProbeForTests = _ => Task.FromResult(false)
        };
        var runner = new LlamaBenchRunner(manager, () => GpuInventoryFixtures.Inventory());

        Task<ModelBenchmarkMeasurement> run = runner.RunAsync(model, CancellationToken.None);
        Process? standIn = await WaitForStandInAsync(executable, TimeSpan.FromSeconds(30));
        if (standIn is null) Assert.Fail("llama-bench did not start: " + DescribeRun(run));
        File.WriteAllText(LocalAiPaths.GameMarkerFile, "{}");

        Assert.Same(run, await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(60))));
        InvalidOperationException stopped = await Assert.ThrowsAsync<InvalidOperationException>(() => run);
        Assert.Contains("Gaming Mode started", stopped.Message, StringComparison.Ordinal);
        Assert.True(standIn!.WaitForExit(15_000), "llama-bench kept running after Gaming Mode started.");
    }
}
