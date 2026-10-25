using System.Diagnostics;
using System.Text.Json;
using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Real-process checks for the PID-file stop and launch paths. Windows reuses PIDs, so a PID file can name a
/// process PaRuX never launched, or one that now runs under a different start time. Each test starts a real
/// process from a temp copy of a Windows binary renamed to the expected executable name (ping.exe keeps
/// running with any arguments PaRuX passes). The runtime and state roots are isolated, so no machine-wide
/// llama-server or ComfyUI process is touched.
/// </summary>
public sealed class RuntimeProcessIdentityTests : IDisposable
{
    private readonly string _work = Path.Combine(Path.GetTempPath(), "parux-identity-" + Guid.NewGuid().ToString("N"));
    private readonly List<Process> _started = new();

    public void Dispose()
    {
        foreach (Process process in _started)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            process.Dispose();
        }
        try { Directory.Delete(_work, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string CopyNamedBinary(string executableName)
    {
        string dir = Path.Combine(_work, "bin-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string copy = Path.Combine(dir, executableName);
        File.Copy(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "PING.EXE"), copy);
        return copy;
    }

    /// <summary>A live process that PaRuX did not launch, under the executable name a runtime PID file expects.</summary>
    private Process StartNamedProcess(string executableName)
    {
        var start = new ProcessStartInfo(CopyNamedBinary(executableName))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("-n");
        start.ArgumentList.Add("120");
        start.ArgumentList.Add("127.0.0.1");
        Process process = Process.Start(start)!;
        process.OutputDataReceived += (_, _) => { };
        process.ErrorDataReceived += (_, _) => { };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        _started.Add(process);
        return process;
    }

    private static Process? RunningProcess(int pid)
    {
        try
        {
            Process process = Process.GetProcessById(pid);
            return process.HasExited ? null : process;
        }
        catch (ArgumentException) { return null; }
    }

    [Fact]
    public void StopMedia_never_kills_a_python_process_that_PaRuX_did_not_launch()
    {
        using IDisposable root = LocalAiPaths.UseRootForTests(Path.Combine(_work, "localai"));
        using IDisposable state = LocalAiPaths.UseStateDirForTests(Path.Combine(_work, "state"));
        Process unrelated = StartNamedProcess("python.exe");
        // The PID file names a live python.exe, but nothing records that PaRuX launched it. It is not PaRuX's to stop.
        File.WriteAllText(LocalAiPaths.MediaPidFile, unrelated.Id.ToString());

        Assert.True(new LocalAiProcessManager().StopMedia());

        Assert.False(unrelated.HasExited);
        Assert.False(File.Exists(LocalAiPaths.MediaPidFile));
    }

    [Fact]
    public void StopMedia_stops_the_python_process_PaRuX_recorded_when_it_launched_it()
    {
        using IDisposable root = LocalAiPaths.UseRootForTests(Path.Combine(_work, "localai"));
        using IDisposable state = LocalAiPaths.UseStateDirForTests(Path.Combine(_work, "state"));
        Process owned = StartNamedProcess("python.exe");
        LocalAiProcessManager.RecordManagedProcess(LocalAiPaths.MediaPidFile, owned);
        Assert.True(File.Exists(LocalAiPaths.MediaPidFile + ".identity.json"));

        Assert.True(new LocalAiProcessManager().StopMedia());

        Assert.True(owned.WaitForExit(10_000));
        Assert.False(File.Exists(LocalAiPaths.MediaPidFile));
    }

    [Fact]
    public void StopText_refuses_a_recorded_PID_whose_start_time_no_longer_matches()
    {
        using IDisposable root = LocalAiPaths.UseRootForTests(Path.Combine(_work, "localai"));
        using IDisposable state = LocalAiPaths.UseStateDirForTests(Path.Combine(_work, "state"));
        Process reused = StartNamedProcess("llama-server.exe");
        LocalAiProcessManager.RecordManagedProcess(LocalAiPaths.TextPidFile, reused);
        Assert.True(File.Exists(LocalAiPaths.TextPidFile + ".identity.json"));
        // Rewrite the record as if this PID had belonged to an earlier process with a different start time.
        var stale = new LocalAiProcessManager.ManagedProcessRecord(
            reused.Id, reused.StartTime.ToUniversalTime().Ticks - TimeSpan.TicksPerHour, null);
        File.WriteAllText(LocalAiPaths.TextPidFile + ".identity.json", JsonSerializer.Serialize(stale));

        Assert.True(new LocalAiProcessManager().StopText());

        Assert.False(reused.HasExited);
        Assert.False(File.Exists(LocalAiPaths.TextPidFile));
    }

    [Fact]
    public void GetTextProcessId_restores_the_saved_port_after_a_core_restart()
    {
        using IDisposable root = LocalAiPaths.UseRootForTests(Path.Combine(_work, "localai"));
        using IDisposable state = LocalAiPaths.UseStateDirForTests(Path.Combine(_work, "state"));
        using IDisposable port = LocalAiPaths.UseTextPortForTests(41231);
        Process runtime = StartNamedProcess("llama-server.exe");
        const int movedPort = 41247;
        LocalAiPaths.SetChosenTextPort(movedPort);

        Assert.True(LocalAiProcessManager.RecordManagedProcess(LocalAiPaths.TextPidFile, runtime));
        LocalAiProcessManager.ManagedProcessRecord? record = JsonSerializer.Deserialize<LocalAiProcessManager.ManagedProcessRecord>(
            File.ReadAllText(LocalAiPaths.TextPidFile + ".identity.json"));
        Assert.Equal(movedPort, record?.TextPort);

        // A new Core starts with the preferred port; the identity check restores the saved choice only after proving ownership.
        LocalAiPaths.SetChosenTextPort(41231);
        Assert.Equal(41231, LocalAiPaths.TextPort);
        Assert.Equal(runtime.Id, new LocalAiProcessManager().GetTextProcessId());
        Assert.Equal(movedPort, LocalAiPaths.TextPort);
    }

    [Fact]
    public void A_stale_identity_does_not_restore_its_saved_port()
    {
        using IDisposable root = LocalAiPaths.UseRootForTests(Path.Combine(_work, "localai"));
        using IDisposable state = LocalAiPaths.UseStateDirForTests(Path.Combine(_work, "state"));
        using IDisposable port = LocalAiPaths.UseTextPortForTests(41301);
        Process unrelated = StartNamedProcess("llama-server.exe");
        const int stalePort = 41319;
        LocalAiPaths.SetChosenTextPort(stalePort);
        Assert.True(LocalAiProcessManager.RecordManagedProcess(LocalAiPaths.TextPidFile, unrelated));
        var stale = new LocalAiProcessManager.ManagedProcessRecord(
            unrelated.Id,
            unrelated.StartTime.ToUniversalTime().Ticks - TimeSpan.TicksPerHour,
            null,
            stalePort);
        File.WriteAllText(LocalAiPaths.TextPidFile + ".identity.json", JsonSerializer.Serialize(stale));

        LocalAiPaths.SetChosenTextPort(41301);
        Assert.Null(new LocalAiProcessManager().GetTextProcessId());

        Assert.Equal(41301, LocalAiPaths.TextPort);
        Assert.False(unrelated.HasExited);
    }

    [Fact]
    public void GetTextProcessId_restores_the_saved_port_when_the_image_path_was_not_recorded()
    {
        using IDisposable root = LocalAiPaths.UseRootForTests(Path.Combine(_work, "localai"));
        using IDisposable state = LocalAiPaths.UseStateDirForTests(Path.Combine(_work, "state"));
        using IDisposable port = LocalAiPaths.UseTextPortForTests(41231);
        Process runtime = StartNamedProcess("llama-server.exe");
        const int movedPort = 41247;
        LocalAiPaths.SetChosenTextPort(movedPort);
        Assert.True(LocalAiProcessManager.RecordManagedProcess(LocalAiPaths.TextPidFile, runtime));
        string identityPath = LocalAiPaths.TextPidFile + ".identity.json";
        LocalAiProcessManager.ManagedProcessRecord recorded = JsonSerializer.Deserialize<LocalAiProcessManager.ManagedProcessRecord>(
            File.ReadAllText(identityPath))!;
        File.WriteAllText(identityPath, JsonSerializer.Serialize(recorded with { ImagePath = null }));

        // With no recorded image path, the matching start time alone proves ownership, so the saved port is still restored.
        LocalAiPaths.SetChosenTextPort(41231);
        Assert.Equal(runtime.Id, new LocalAiProcessManager().GetTextProcessId());
        Assert.Equal(movedPort, LocalAiPaths.TextPort);
    }

    [Fact]
    public async Task A_launch_that_finishes_loading_during_a_game_is_stopped_and_reported_as_failed()
    {
        using IDisposable root = LocalAiPaths.UseRootForTests(Path.Combine(_work, "localai"));
        using IDisposable state = LocalAiPaths.UseStateDirForTests(Path.Combine(_work, "state"));
        string exe = CopyNamedBinary("fake-runtime.exe");
        string pidFile = Path.Combine(_work, "state", "fake-runtime.pid");
        int launchedPid = 0;

        LocalAiProcessManager.RuntimeResult result = await new LocalAiProcessManager(0).LaunchAndWaitAsync(
            exe, new List<string> { "-n", "120", "127.0.0.1" }, Path.GetDirectoryName(exe)!, null, pidFile, null,
            () =>
            {
                launchedPid = int.Parse(File.ReadAllText(pidFile).Trim());
                // GameStart writes its marker before it stops the runtimes; this one arrives while the model loads.
                File.WriteAllText(LocalAiPaths.GameMarkerFile, "{}");
                return Task.FromResult(true);
            },
            () => { },
            TimeSpan.FromSeconds(30),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("game started", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(pidFile));
        Assert.Null(RunningProcess(launchedPid));
    }

    [Fact]
    public async Task StartText_does_not_launch_beside_a_runtime_that_survives_its_stop()
    {
        using IDisposable root = LocalAiPaths.UseRootForTests(Path.Combine(_work, "localai"));
        using IDisposable state = LocalAiPaths.UseStateDirForTests(Path.Combine(_work, "state"));
        // Every check before the stop passes, so the start reaches the stop decision with a runtime on disk.
        Directory.CreateDirectory(LocalAiPaths.RuntimeVulkanPath);
        File.Copy(CopyNamedBinary("fake-llama.exe"), Path.Combine(LocalAiPaths.RuntimeVulkanPath, LocalAiPaths.LlamaServerExecutable));
        Process survivor = StartNamedProcess("llama-server.exe");
        LocalAiProcessManager.RecordManagedProcess(LocalAiPaths.TextPidFile, survivor);
        using IDisposable survives = LocalAiProcessManager.OverrideTerminateForTests(_ => false);

        LocalAiProcessManager.RuntimeResult result = await new LocalAiProcessManager(0).StartTextAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("did not stop", result.Message, StringComparison.OrdinalIgnoreCase);
        // The surviving runtime stays the tracked one; no second llama-server was launched over its PID record.
        Assert.Equal(survivor.Id.ToString(), File.ReadAllText(LocalAiPaths.TextPidFile).Trim());
    }

    [Fact]
    public void StopText_refuses_a_recorded_runtime_whose_start_time_cannot_be_verified()
    {
        using IDisposable root = LocalAiPaths.UseRootForTests(Path.Combine(_work, "localai"));
        using IDisposable state = LocalAiPaths.UseStateDirForTests(Path.Combine(_work, "state"));
        Process runtime = StartNamedProcess("llama-server.exe");
        using IDisposable unreadable = LocalAiProcessManager.OverrideStartTimeForTests(
            _ => throw new InvalidOperationException("start time unavailable"));
        using IDisposable survives = LocalAiProcessManager.OverrideTerminateForTests(_ => false);

        Assert.False(LocalAiProcessManager.RecordManagedProcess(LocalAiPaths.TextPidFile, runtime));

        // The PID stays on record and nothing can prove it is PaRuX's, so a stop neither kills it nor reports it gone.
        Assert.False(new LocalAiProcessManager().StopText());
        Assert.False(runtime.HasExited);
        Assert.Equal(runtime.Id.ToString(), File.ReadAllText(LocalAiPaths.TextPidFile).Trim());
    }

    [Fact]
    public async Task A_launch_whose_start_time_cannot_be_read_is_stopped_and_reported_as_failed()
    {
        using IDisposable root = LocalAiPaths.UseRootForTests(Path.Combine(_work, "localai"));
        using IDisposable state = LocalAiPaths.UseStateDirForTests(Path.Combine(_work, "state"));
        string exe = CopyNamedBinary("fake-runtime.exe");
        string pidFile = Path.Combine(_work, "state", "fake-runtime.pid");
        Process? launched = null;
        using IDisposable unreadable = LocalAiProcessManager.OverrideStartTimeForTests(process =>
        {
            launched = process;
            throw new InvalidOperationException("start time unavailable");
        });

        LocalAiProcessManager.RuntimeResult result = await new LocalAiProcessManager(0).LaunchAndWaitAsync(
            exe, new List<string> { "-n", "120", "127.0.0.1" }, Path.GetDirectoryName(exe)!, null, pidFile, null,
            () => Task.FromResult(true),
            () => { },
            TimeSpan.FromSeconds(30),
            CancellationToken.None);

        // The runtime reports healthy, so only the identity check can stop this launch: the process is killed, not left running untracked.
        Assert.False(result.Success);
        Assert.Contains("start time", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(pidFile));
        Assert.NotNull(launched);
        Assert.True(launched!.WaitForExit(10_000));
    }

    [Fact]
    public void Unload_keeps_the_role_recorded_and_reports_failure_while_the_text_runtime_survives()
    {
        using IDisposable root = LocalAiPaths.UseRootForTests(Path.Combine(_work, "localai"));
        using IDisposable state = LocalAiPaths.UseStateDirForTests(Path.Combine(_work, "state"));
        Process survivor = StartNamedProcess("llama-server.exe");
        Assert.True(LocalAiProcessManager.RecordManagedProcess(LocalAiPaths.TextPidFile, survivor));
        File.WriteAllText(LocalAiPaths.ActiveRoleFile, "standard");
        using IDisposable survives = LocalAiProcessManager.OverrideTerminateForTests(_ => false);

        RuntimeLoadResult result = new RuntimeManagerService(new LocalAiProcessManager()).Unload();

        Assert.False(result.Success);
        Assert.Contains("still running", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("standard", File.ReadAllText(LocalAiPaths.ActiveRoleFile).Trim());
        Assert.False(survivor.HasExited);
    }
}
