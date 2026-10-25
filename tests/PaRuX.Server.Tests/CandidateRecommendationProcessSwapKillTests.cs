using System.Diagnostics;
using System.Globalization;
using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// BIC-16 real-process promote/rollback: every load is a NEW runtime child process (the old one is stopped only after
/// the new one is healthy). The Core coordinator is a separate child that kills itself at a crash point; the restarted
/// Core adopts the surviving runtime process and reconciles. Process ids prove which process is dead and which one is
/// serving, and that a consistent single-runtime state is reached.
/// </summary>
public sealed class CandidateRecommendationProcessSwapKillTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parux-swap-kill", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Promote_starts_the_new_process_then_stops_the_old_and_rollback_swaps_back()
    {
        string state = StateRoot("happy");
        string models = Path.Combine(state, "models");
        CandidateUtilityComparisonProof proof = RecFx.Proof("org/challenger", "challenger.gguf", 'a',
            "org/baseline", "baseline.gguf", 'b');
        CandidateRecommendationStore store = CandidateRecommendationRealRuntimeKillTests.SeedActive(state, models);
        using var runtime = new ProcessSwapRuntime(Path.Combine(state, "runtime.pid"));
        await runtime.StartAsync(Path.Combine(models, "baseline.gguf"), proof.Incumbent.Identity.LocalSha256, default);
        int oldPid = runtime.CurrentPid!.Value;
        var service = new CandidateRecommendationService(store, runtime, new FakeRecProofs(proof), new SwapFence(runtime),
            models, healthBudget: TimeSpan.FromSeconds(5), pinRoot: Path.Combine(state, "pins"));

        RecommendationTransitionResult promoted = await service.PromoteAsync(
            proof.Challenger.Identity.CandidateKey, "challenger.gguf", store.Load().Generation, true, CancellationToken.None);

        Assert.True(promoted.IsCommitted, promoted.Outcome + ":" + promoted.Reason);
        int newPid = runtime.CurrentPid!.Value;
        Assert.NotEqual(oldPid, newPid);
        Assert.False(ProcessSwapRuntime.IsAlive(oldPid), "the old runtime process must be really dead");
        Assert.True(ProcessSwapRuntime.IsAlive(newPid));
        AssertServing(runtime, "challenger.gguf", models, proof.Challenger.Identity.LocalSha256);
        Assert.True(await runtime.IsHealthyAsync(CancellationToken.None));
        Assert.Equal(proof.Challenger.Identity.CandidateKey,
            store.Load().GetActiveRecord()!.Proof.Challenger.Identity.CandidateKey);

        RecommendationTransitionResult back = await service.RollbackAsync(store.Load().Generation, true, CancellationToken.None);
        Assert.True(back.IsCommitted, back.Outcome + ":" + back.Reason);
        int restoredPid = runtime.CurrentPid!.Value;
        Assert.NotEqual(newPid, restoredPid);
        Assert.False(ProcessSwapRuntime.IsAlive(newPid), "the rolled-back runtime process must be really dead");
        AssertServing(runtime, "baseline.gguf", models, proof.Incumbent.Identity.LocalSha256);
        Assert.Equal(proof.Incumbent.Identity.CandidateKey,
            store.Load().GetActiveRecord()!.Proof.Challenger.Identity.CandidateKey);
    }

    [Theory]
    [InlineData("promote-swap", "before_commit")]
    [InlineData("promote-swap", "after_load")]
    [InlineData("promote-swap", "after_commit")]
    [InlineData("rollback-swap", "after_load")]
    [InlineData("rollback-swap", "after_commit")]
    public async Task Core_killed_mid_transition_reconciles_to_one_consistent_running_model(string mode, string crashPoint)
    {
        bool promote = mode == "promote-swap";
        string state = StateRoot(mode + "-" + crashPoint);
        string models = Path.Combine(state, "models");
        string runtimeFile = Path.Combine(state, "runtime.pid");
        CandidateUtilityComparisonProof first = RecFx.Proof("org/challenger", "challenger.gguf", 'a',
            "org/baseline", "baseline.gguf", 'b');
        CandidateUtilityComparisonProof second = RecFx.Proof("org/second", "second.gguf", 'c',
            "org/challenger", "challenger.gguf", 'a');
        CandidateRecommendationStore store = promote
            ? CandidateRecommendationRealRuntimeKillTests.SeedActive(state, models)
            : CandidateRecommendationRealRuntimeKillTests.SeedRollback(state, models);
        CandidateProofSide served = promote ? first.Incumbent : second.Challenger;       // running before the transition
        CandidateProofSide target = first.Challenger;                                    // adopted if the commit lands
        

        using var before = new ProcessSwapRuntime(runtimeFile);
        await before.StartAsync(Path.Combine(models, served.Identity.ManagedFileName), served.Identity.LocalSha256, default);
        int originalPid = before.CurrentPid!.Value;

        (int exit, string stderr) = await RunCoordinatorAsync(state, mode, crashPoint, store.Load().Generation,
            runtimeFile, models);
        Assert.True(exit != 0, "coordinator did not die at the requested hook; stderr: " + stderr);
        Assert.Equal(crashPoint, File.ReadAllText(Path.Combine(state, "crashed.txt")));
        // The Core died after the swap: the load already replaced the process, and the survivor is an orphan.
        int orphanPid = int.Parse(File.ReadAllText(runtimeFile).Split(' ')[0], CultureInfo.InvariantCulture);
        Assert.NotEqual(originalPid, orphanPid);
        Assert.False(ProcessSwapRuntime.IsAlive(originalPid));
        Assert.True(ProcessSwapRuntime.IsAlive(orphanPid), "the runtime outlives a killed Core");
        if (crashPoint is "after_load" or "before_commit") Assert.NotNull(store.Load().Pending);
        else Assert.Null(store.Load().Pending);

        // Restarted Core: adopt the surviving runtime process, then reconcile.
        using var restarted = new ProcessSwapRuntime(runtimeFile);
        Assert.True(restarted.Adopt());
        var recovery = new CandidateRecommendationService(store, restarted, new FakeRecProofs(null), new SwapFence(restarted),
            models, healthBudget: TimeSpan.FromSeconds(5), pinRoot: Path.Combine(state, "pins"),
            reconcileWaitBound: TimeSpan.FromSeconds(5));
        await recovery.ReconcileAsync(CancellationToken.None);

        CandidateRecommendationSnapshot recovered = store.Load();
        Assert.Null(recovered.Pending);
        CandidateRecommendationRecord active = Assert.IsType<CandidateRecommendationRecord>(recovered.GetActiveRecord());
        bool committed = crashPoint == "after_commit";
        CandidateProofSide expected = committed ? target : served;
        Assert.Equal(expected.Identity.CandidateKey, active.Proof.Challenger.Identity.CandidateKey);
        AssertServing(restarted, expected.Identity.ManagedFileName, models, expected.Identity.LocalSha256);
        Assert.True(await restarted.IsHealthyAsync(CancellationToken.None));

        // Exactly one runtime process is alive across everything that was ever started in this test.
        int servingPid = restarted.CurrentPid!.Value;
        int[] everyPid = { originalPid, orphanPid, servingPid };
        Assert.Equal(new[] { servingPid }, everyPid.Distinct().Where(ProcessSwapRuntime.IsAlive).ToArray());
        if (!committed) Assert.False(ProcessSwapRuntime.IsAlive(orphanPid), "the unadopted new process must be stopped");

        long generation = recovered.Generation;
        await recovery.ReconcileAsync(CancellationToken.None);
        Assert.Equal(generation, store.Load().Generation);
        Assert.Equal(servingPid, restarted.CurrentPid);
    }

    private static void AssertServing(ProcessSwapRuntime runtime, string managedFile, string models, string sha256)
    {
        var status = Assert.IsType<CandidateRecommendationRealRuntimeKillTests.RuntimeProcessStatus>(runtime.TryStatus());
        Assert.True(status.Running);
        Assert.True(status.Healthy);
        Assert.Equal(Path.GetFullPath(Path.Combine(models, managedFile)), status.ModelPath, ignoreCase: true);
        Assert.Equal(sha256, status.Sha256, ignoreCase: true);
    }

    private static async Task<(int Exit, string Stderr)> RunCoordinatorAsync(string state, string mode, string crashPoint,
        long generation, string runtimeFile, string models)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.Environment["PARUX_DATA_ROOT"] = Path.Combine(state, "data");
        foreach (string argument in new[] { ProcessSwapRuntime.HarnessDll(), state, mode, crashPoint,
                     generation.ToString(CultureInfo.InvariantCulture), runtimeFile, models })
            start.ArgumentList.Add(argument);
        using Process child = Process.Start(start) ?? throw new InvalidOperationException("Could not start coordinator.");
        _ = child.StandardOutput.ReadToEndAsync();
        Task<string> stderr = child.StandardError.ReadToEndAsync();
        try { await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60)); }
        catch
        {
            try { child.Kill(entireProcessTree: false); } catch (InvalidOperationException) { }
            throw;
        }
        // The surviving runtime process inherited the coordinator's stderr pipe, so EOF never arrives while it lives.
        try { return (child.ExitCode, await stderr.WaitAsync(TimeSpan.FromSeconds(1))); }
        catch (TimeoutException) { return (child.ExitCode, string.Empty); }
    }

    private string StateRoot(string name)
    {
        string path = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.Combine(path, "models"));
        return path;
    }

    public void Dispose()
    {
        ProcessSwapRuntime.KillEverythingStarted();
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
