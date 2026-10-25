using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Gaming Mode is a hard-off for local AI work that is already running, not only for work that starts later.
/// These tests drive the real game-marker file (isolated under a temp state root) and the real probe entry point.
/// </summary>
public sealed class GamingHardOffTests : IDisposable
{
    private readonly string _work = Path.Combine(Path.GetTempPath(), "parux-gaming-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_work, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task The_work_token_is_cancelled_and_flagged_as_gaming_when_the_game_marker_appears()
    {
        bool gaming = false;
        using GamingHardOffWatch work = new(() => gaming, CancellationToken.None, TimeSpan.FromMilliseconds(20));

        await Task.Delay(100);
        Assert.False(work.Token.IsCancellationRequested);

        gaming = true;
        for (int i = 0; i < 500 && !work.Token.IsCancellationRequested; i++)
            await Task.Delay(10);

        Assert.True(work.Token.IsCancellationRequested);
        Assert.True(work.Triggered);
    }

    [Fact]
    public void A_caller_cancellation_is_not_reported_as_gaming()
    {
        using var caller = new CancellationTokenSource();
        using GamingHardOffWatch work = new(() => false, caller.Token, TimeSpan.FromMilliseconds(20));

        caller.Cancel();

        Assert.True(work.Token.IsCancellationRequested);
        Assert.False(work.Triggered);
    }

    [Fact]
    public async Task The_performance_probe_refuses_to_launch_a_llama_server_while_gaming_mode_is_active()
    {
        using IDisposable root = LocalAiPaths.UseRootForTests(Path.Combine(_work, "localai"));
        using IDisposable state = LocalAiPaths.UseStateDirForTests(Path.Combine(_work, "state"));
        // A runtime and a model are both present, so without the gate the probe would start a process.
        Directory.CreateDirectory(LocalAiPaths.RuntimeVulkanPath);
        string probeExe = Path.Combine(_work, "probe-runtime.exe");
        File.Copy(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "PING.EXE"), probeExe);
        File.Copy(probeExe, Path.Combine(LocalAiPaths.RuntimeVulkanPath, LocalAiPaths.LlamaServerExecutable));
        string model = Path.Combine(_work, "probe-model.gguf");
        File.WriteAllBytes(model, new byte[16]);
        File.WriteAllText(LocalAiPaths.GameMarkerFile, "{}");

        var probe = new LlamaServerPerformanceProbe(new LocalAiProcessManager(0), new GpuPerformanceTelemetryService());
        InvalidOperationException refused = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            probe.RunAsync(model, "chat", new LlamaPerformanceProfileDto(), sustainedValidation: false, CancellationToken.None));

        Assert.Contains("Gaming Mode", refused.Message);
    }
}
