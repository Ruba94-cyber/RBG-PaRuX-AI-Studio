using System.Text.Json;
using PaRuX.Contracts;
using PaRuX.Server;
using PaRuX.Server.Orchestration.Gaming;
using PaRuX.Server.Orchestration.Gaming.Providers;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// BIC-04 S3: Gaming hard-off interaction with evaluation work and the explicit resume policy.
/// </summary>
public sealed class GamingTransitionIntegrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parux-gaming-s3-" + Guid.NewGuid().ToString("N"));
    private readonly IDisposable _stateScope;
    private readonly IDisposable _rootScope;

    public GamingTransitionIntegrationTests()
    {
        Directory.CreateDirectory(_root);
        _stateScope = LocalAiPaths.UseStateDirForTests(Path.Combine(_root, "state"));
        _rootScope = LocalAiPaths.UseRootForTests(Path.Combine(_root, "root"));
    }

    public void Dispose()
    {
        _rootScope.Dispose();
        _stateScope.Dispose();
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }

    private static void WriteMarker() =>
        File.WriteAllText(LocalAiPaths.GameMarkerFile, JsonSerializer.Serialize(new { started = DateTime.UtcNow }));

    // ---- GamingHardOffWatch -------------------------------------------------------------

    [Fact]
    public async Task Watch_cancels_promptly_when_gaming_becomes_active_and_reports_the_cause()
    {
        bool active = false;
        using var watch = new GamingHardOffWatch(() => Volatile.Read(ref active), CancellationToken.None, TimeSpan.FromMilliseconds(20));
        Assert.False(watch.Token.IsCancellationRequested);

        Volatile.Write(ref active, true);
        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        while (!watch.Token.IsCancellationRequested && DateTime.UtcNow < deadline) await Task.Delay(10);
        Assert.True(watch.Token.IsCancellationRequested);
        Assert.True(watch.Triggered);
    }

    [Fact]
    public void Watch_cancels_before_returning_when_a_game_is_already_running()
    {
        using var watch = new GamingHardOffWatch(() => true, CancellationToken.None, TimeSpan.FromSeconds(30));
        Assert.True(watch.Token.IsCancellationRequested);
        Assert.True(watch.Triggered);
    }

    [Fact]
    public void Watch_does_not_claim_gaming_when_the_caller_cancels_and_survives_an_unreadable_marker()
    {
        using var cts = new CancellationTokenSource();
        using var watch = new GamingHardOffWatch(() => throw new IOException("marker unreadable"), cts.Token, TimeSpan.FromMilliseconds(20));
        Assert.False(watch.Token.IsCancellationRequested);
        cts.Cancel();
        Assert.True(watch.Token.IsCancellationRequested);
        Assert.False(watch.Triggered);
    }

    // ---- Tuner: a game launched mid-run stops GPU benchmarking --------------------------

    private sealed class BlockingProbe : ILlamaPerformanceProbe
    {
        public readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Runs;
        public volatile bool SawCancellation;

        public async Task<LlamaPerformanceProbeMeasurement> RunAsync(
            string modelPath, LlamaPerformanceProfileDto profile, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Runs);
            Started.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
            catch (OperationCanceledException) { SawCancellation = true; throw; }
            throw new InvalidOperationException("unreachable");
        }
    }

    [Fact]
    public async Task Tuner_stops_GPU_candidates_when_Gaming_starts_mid_run_and_keeps_existing_tuning()
    {
        string models = LocalAiPaths.ModelsPath;
        Directory.CreateDirectory(models);
        string model = Path.Combine(models, "gaming-s3-test.gguf");
        File.WriteAllBytes(model, new byte[4096]);
        var store = new RuntimePerformanceProfileStore(Path.Combine(_root, "profiles.json"));
        var probe = new BlockingProbe();
        var tuner = new RuntimePerformanceTuner(new LocalAiProcessManager(), new GpuInventoryService(), probe, store);

        RuntimePerformanceTuneRun run = tuner.Start(new RuntimePerformanceTuneRequest { ModelPath = model, Workload = "chat" });
        await probe.Started.Task.WaitAsync(TimeSpan.FromSeconds(30));

        WriteMarker(); // a game starts while the first candidate is on the GPU

        RuntimePerformanceTuneRun? final = null;
        DateTime deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            final = tuner.Get(run.Id);
            if (final?.Status is "cancelled" or "failed" or "completed") break;
            await Task.Delay(50);
        }

        Assert.NotNull(final);
        Assert.Equal("cancelled", final!.Status);
        Assert.Contains("Gaming Mode started", final.Stage, StringComparison.Ordinal);
        Assert.True(probe.SawCancellation);
        Assert.Equal(1, probe.Runs); // later candidates were never started
        Assert.Empty(store.List());  // nothing was promoted
    }

    // ---- Guard: a failed hard-off is retried and never escapes ---------------------------

    private static GamingGuardService NewGuard(out LocalRuntimeService runtime)
    {
        var env = new TestHostEnvironment { EnvironmentName = "Production" };
        var process = new LocalAiProcessManager();
        runtime = new LocalRuntimeService(env, process, new ExternalTextProviderService());
        return new GamingGuardService(runtime, new MediaJobService(null!), new SpeechService(runtime));
    }

    [Fact]
    public async Task Guard_retries_a_failed_release_while_Gaming_stays_active_and_stops_after_success()
    {
        GamingGuardService guard = NewGuard(out LocalRuntimeService runtime);
        int attempts = 0;
        guard.ReleaseOverrideForTests = _ =>
        {
            if (++attempts < 3) throw new InvalidOperationException("llama-server did not stop");
            return Task.CompletedTask;
        };

        WriteMarker();
        DateTime now = DateTime.UtcNow;
        await guard.TickAsync(now, CancellationToken.None);
        Assert.Equal(1, attempts);
        Assert.Contains("llama-server did not stop", guard.LastReleaseFailure, StringComparison.Ordinal);
        Assert.True(runtime.GamingCancellationToken.IsCancellationRequested);

        await guard.TickAsync(now.AddMilliseconds(250), CancellationToken.None); // inside the back-off
        Assert.Equal(1, attempts);

        await guard.TickAsync(now + GamingGuardService.ReleaseRetryInterval, CancellationToken.None);
        Assert.Equal(2, attempts);
        await guard.TickAsync(now + GamingGuardService.ReleaseRetryInterval * 2, CancellationToken.None);
        Assert.Equal(3, attempts);
        Assert.Null(guard.LastReleaseFailure);

        await guard.TickAsync(now + GamingGuardService.ReleaseRetryInterval * 4, CancellationToken.None);
        Assert.Equal(3, attempts); // released: no further attempts while still gaming
    }

    [Fact]
    public async Task Guard_ending_Gaming_resets_state_and_never_reloads_or_releases_anything()
    {
        GamingGuardService guard = NewGuard(out LocalRuntimeService runtime);
        int attempts = 0;
        guard.ReleaseOverrideForTests = _ => { attempts++; return Task.CompletedTask; };

        WriteMarker();
        await guard.TickAsync(DateTime.UtcNow, CancellationToken.None);
        Assert.Equal(1, attempts);

        File.Delete(LocalAiPaths.GameMarkerFile);
        await guard.TickAsync(DateTime.UtcNow.AddSeconds(5), CancellationToken.None);
        Assert.Equal(1, attempts);
        Assert.False(runtime.GamingMode);
        Assert.False(runtime.GamingCancellationToken.IsCancellationRequested);
    }

    // ---- Orchestrator: resume follows an explicit policy ---------------------------------

    private sealed class OkInput : IInputProvider
    {
        public string Id => "ok-input"; public string Name => "ok"; public bool IsInstalled => true; public string? ExecutablePath => null;
        public Task<bool> StartAsync(GamingProfileDto profile, CancellationToken ct) => Task.FromResult(true);
        public Task<bool> StopAsync(GamingProfileDto profile, CancellationToken ct) => Task.FromResult(true);
    }

    private sealed class FailingLauncher : IGameLauncherProvider
    {
        public string Id => "fail-launcher"; public string Name => "fail"; public bool IsInstalled => true; public string? ExecutablePath => null;
        public Task<bool> LaunchAsync(GamingProfileDto profile, CancellationToken ct) => Task.FromResult(false);
        public Task<bool> StopAsync(GamingProfileDto profile, CancellationToken ct) => Task.FromResult(true);
    }

    private sealed class OkLauncher : IGameLauncherProvider
    {
        public string Id => "ok-launcher"; public string Name => "ok"; public bool IsInstalled => true; public string? ExecutablePath => null;
        public Task<bool> LaunchAsync(GamingProfileDto profile, CancellationToken ct) => Task.FromResult(true);
        public Task<bool> StopAsync(GamingProfileDto profile, CancellationToken ct) => Task.FromResult(true);
    }

    private (WorkloadOrchestrator Orchestrator, Func<int> Restores) NewOrchestrator(
        string name, bool restoreOnExit, bool failingLauncher, IGamingResumable? resumable = null)
    {
        string store = Path.Combine(_root, "profiles-" + Guid.NewGuid().ToString("N") + ".json");
        new GamingProfileStore(store).Save(new GamingProfileDto
        {
            Name = name,
            SessionType = "local",
            LauncherProvider = failingLauncher ? "fail-launcher" : "ok-launcher",
            InputProvider = "ok-input",
            SuspendProvider = "none",
            QuickResumePolicy = "off",
            RestoreAiOnExit = restoreOnExit
        });
        var env = new TestHostEnvironment { EnvironmentName = "Testing" };
        var process = new LocalAiProcessManager();
        var runtime = new LocalRuntimeService(env, process, new ExternalTextProviderService());
        int restores = 0;
        var orchestrator = new WorkloadOrchestrator(
            runtime, process, new MediaJobService(null!), new SpeechService(runtime), new GamingProfileStore(store),
            inputProviders: new IInputProvider[] { new OkInput() },
            launcherProviders: new IGameLauncherProvider[] { failingLauncher ? new FailingLauncher() : new OkLauncher() },
            runtimeHealthCheck: _ => Task.FromResult(true),
            restoreRuntimeToReady: _ => { Interlocked.Increment(ref restores); return Task.CompletedTask; },
            resumables: resumable == null ? null : new[] { resumable });
        return (orchestrator, () => Volatile.Read(ref restores));
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 1)]
    public async Task Failed_entry_rollback_lifts_the_marker_but_reloads_the_runtime_only_under_the_explicit_policy(
        bool restoreOnExit, int expectedReloads)
    {
        using IDisposable finder = WorkloadOrchestrator.UseActiveGameProcessFinderForTests(() => null);
        var (orchestrator, restores) = NewOrchestrator("S3 failed entry", restoreOnExit, failingLauncher: true);

        Assert.False(await orchestrator.EnterGamingSessionAsync("S3 failed entry"));

        Assert.Equal("AiReady", orchestrator.CurrentState);
        Assert.False(File.Exists(LocalAiPaths.GameMarkerFile)); // hard-off lifted, user not locked out
        Assert.Equal(expectedReloads, restores());
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 1)]
    public async Task Automatic_session_end_reloads_the_runtime_only_under_the_explicit_policy(
        bool restoreOnExit, int expectedReloads)
    {
        using IDisposable finder = WorkloadOrchestrator.UseActiveGameProcessFinderForTests(() => null);
        var (orchestrator, restores) = NewOrchestrator("S3 auto exit", restoreOnExit, failingLauncher: false);
        Assert.True(await orchestrator.EnterGamingSessionAsync("S3 auto exit"));
        Assert.True(File.Exists(LocalAiPaths.GameMarkerFile));

        Assert.True(await orchestrator.ExitGamingSessionAutomaticallyAsync());

        Assert.Equal("AiReady", orchestrator.CurrentState);
        Assert.False(File.Exists(LocalAiPaths.GameMarkerFile));
        Assert.Equal(expectedReloads, restores());
    }

    [Fact]
    public async Task Explicit_Return_to_AI_restores_the_runtime_even_without_the_automatic_policy()
    {
        using IDisposable finder = WorkloadOrchestrator.UseActiveGameProcessFinderForTests(() => null);
        var (orchestrator, restores) = NewOrchestrator("S3 explicit exit", restoreOnExit: false, failingLauncher: false);
        Assert.True(await orchestrator.EnterGamingSessionAsync("S3 explicit exit"));

        Assert.True(await orchestrator.ExitGamingSessionAsync());

        Assert.Equal("AiReady", orchestrator.CurrentState);
        Assert.Equal(1, restores());
    }

    private sealed class CountingResumable : IGamingResumable
    {
        public int Calls;
        public Task<int> ResumeGamingPausedAsync(CancellationToken ct) { Interlocked.Increment(ref Calls); return Task.FromResult(1); }
    }

    [Theory]
    [InlineData(false, false, 0)] // explicit Return to AI without the policy: downloads stay paused
    [InlineData(false, true, 1)]  // explicit Return to AI with the policy: downloads resume
    [InlineData(true, true, 1)]   // automatic exit with the policy
    [InlineData(true, false, 0)]  // automatic exit without the policy
    public async Task Paused_downloads_resume_only_under_the_explicit_policy(bool automatic, bool restoreOnExit, int expectedCalls)
    {
        using IDisposable finder = WorkloadOrchestrator.UseActiveGameProcessFinderForTests(() => null);
        var resumable = new CountingResumable();
        var (orchestrator, _) = NewOrchestrator("S3 resume " + automatic + restoreOnExit, restoreOnExit, failingLauncher: false, resumable);
        Assert.True(await orchestrator.EnterGamingSessionAsync("S3 resume " + automatic + restoreOnExit));

        Assert.True(automatic
            ? await orchestrator.ExitGamingSessionAutomaticallyAsync()
            : await orchestrator.ExitGamingSessionAsync());

        Assert.Equal(expectedCalls, resumable.Calls);
    }

    [Fact]
    public void RestoreAiOnExit_defaults_off_and_round_trips_through_the_profile_store()
    {
        Assert.False(new GamingProfileDto().RestoreAiOnExit);
        Assert.False(GamingProfileStore.BuildDefaultLocalPreset().RestoreAiOnExit);
        Assert.False(GamingProfileStore.BuildDefaultRemotePreset().RestoreAiOnExit);

        string path = Path.Combine(_root, "roundtrip.json");
        GamingProfileDto saved = new GamingProfileStore(path).Save(new GamingProfileDto { Name = "rt", RestoreAiOnExit = true });
        Assert.True(new GamingProfileStore(path).Get(saved.Id)!.RestoreAiOnExit);
    }
}
