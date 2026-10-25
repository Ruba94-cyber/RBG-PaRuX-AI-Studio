using System.Diagnostics;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Kill-mid-transaction evidence with a REAL child process: the harness kills itself at each durable step and the
/// parent proves recovery lands on a consistent Active (or the prior winner) without inferring success.
/// </summary>
public sealed class CandidateRecommendationCrashTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parux-rec-crash", Guid.NewGuid().ToString("N"));

    private static string HarnessDll()
    {
        string dll = Path.Combine(AppContext.BaseDirectory, "PaRuX.Server.Tests.dll");
        Assert.True(File.Exists(dll), "test assembly not found: " + dll);
        return dll;
    }

    private (int ExitCode, string Output) RunChild(string state, string mode, string crashPoint, long generation)
    {
        Directory.CreateDirectory(state);
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        // Hermetic: the child must never read or write the developer's real PaRuX data.
        start.Environment["PARUX_DATA_ROOT"] = Path.Combine(state, "data");
        foreach (string argument in new[] { HarnessDll(), state, mode, crashPoint, generation.ToString() })
            start.ArgumentList.Add(argument);
        using Process child = Process.Start(start)!;
        string output = child.StandardOutput.ReadToEnd();
        Assert.True(child.WaitForExit(60000), "harness did not exit");
        if (crashPoint != "none") Assert.Equal(crashPoint, File.ReadAllText(Path.Combine(state, "crashed.txt")));
        return (child.ExitCode, output);
    }

    private (FakeRecRuntime Runtime, CandidateRecommendationService Service, CandidateRecommendationStore Store) Recovery(
        string state, CandidateProofSide? live, Action<FakeRecRuntime>? tweak = null)
    {
        var runtime = new FakeRecRuntime();
        if (live is not null) runtime.Serve(live);
        tweak?.Invoke(runtime);
        var store = new CandidateRecommendationStore(state);
        var proofs = new FakeRecProofs(null);
        return (runtime, new CandidateRecommendationService(store, runtime, proofs, new FakeRecFence(runtime),
            RecFx.Models, null, TimeSpan.FromSeconds(2), Path.Combine(state, "pins")), store);
    }

    [Theory]
    [InlineData("before_pending", false)]
    [InlineData("after_pending", true)]
    [InlineData("after_load", true)]
    [InlineData("before_commit", true)]
    public async Task Promote_killed_before_the_durable_commit_recovers_to_the_prior_state(string crashPoint, bool pendingLeft)
    {
        string state = Path.Combine(_root, "promote-" + crashPoint);
        CandidateUtilityComparisonProof proof = RecFx.Proof("org/challenger", "challenger.gguf", 'a', "org/baseline", "baseline.gguf", 'b');
        (int exit, _) = RunChild(state, "promote", crashPoint, 0);
        Assert.NotEqual(0, exit); // really killed, no result produced

        var probe = new CandidateRecommendationStore(state);
        Assert.Equal(pendingLeft, probe.Load().Pending is not null);
        Assert.Null(probe.Load().Active); // Pending is never a winner

        // The runtime outlived Core: at/after the challenger load it is serving the unverified challenger.
        bool challengerLive = crashPoint is "after_load" or "before_commit";
        var (runtime, service, store) = Recovery(state, challengerLive ? proof.Challenger : proof.Incumbent);
        RecommendationTransitionResult result = await service.ReconcileAsync(CancellationToken.None);
        Assert.Null(store.Load().Pending);
        Assert.Null(store.Load().Active);
        if (!pendingLeft) Assert.Equal("nothing_to_reconcile", result.Reason);
        else
        {
            Assert.Equal(RecommendationTransitionResult.FailedRestored, result.Outcome);
            Assert.Equal(challengerLive ? 1 : 0, runtime.Loads.Count);
        }
        Assert.True(new FakeRecFence(runtime).IsLiveExactModel(RecFx.PathOf(proof.Incumbent), proof.Incumbent.Identity.LocalSha256));
        // Idempotent: a second reconcile changes nothing.
        long generation = store.Load().Generation;
        await service.ReconcileAsync(CancellationToken.None);
        Assert.Equal(generation, store.Load().Generation);
    }

    [Fact]
    public async Task Promote_killed_right_after_the_durable_commit_recovers_to_the_consistent_Active()
    {
        string state = Path.Combine(_root, "promote-after_commit");
        CandidateUtilityComparisonProof proof = RecFx.Proof("org/challenger", "challenger.gguf", 'a', "org/baseline", "baseline.gguf", 'b');
        (int exit, _) = RunChild(state, "promote", "after_commit", 0);
        Assert.NotEqual(0, exit);
        var (runtime, service, store) = Recovery(state, proof.Challenger);
        Assert.Equal(proof.ProofSha256, store.Load().GetActiveRecord()!.Proof.ProofSha256);
        Assert.Equal(2, store.Load().Generation);
        RecommendationTransitionResult result = await service.ReconcileAsync(CancellationToken.None);
        Assert.Equal("nothing_to_reconcile", result.Reason);
        Assert.Empty(runtime.Loads); // nothing was reloaded or swapped
        Assert.Equal(proof.ProofSha256, store.Load().GetActiveRecord()!.Proof.ProofSha256);
    }

    private CandidateRecommendationStore SeedRollbackState(string state, out CandidateUtilityComparisonProof first,
        out CandidateUtilityComparisonProof second)
    {
        first = RecFx.Proof("org/challenger", "challenger.gguf", 'a', "org/baseline", "baseline.gguf", 'b');
        second = RecFx.Proof("org/second", "second.gguf", 'c', "org/challenger", "challenger.gguf", 'a');
        Directory.CreateDirectory(state);
        var store = new CandidateRecommendationStore(state);
        CandidateLaunchAdmission AdmissionFor(CandidateProofSide side) => new(RecFx.ActualConfig, "text-cuda",
            RecFx.Host.HardwareFingerprint, RecFx.Host.RuntimeFingerprint, RecFx.Host.BinaryFingerprint,
            RecFx.PathOf(side), DateTime.UtcNow);
        RecommendationTransitionChecks ChecksFor(CandidateProofSide side) => new(
            () => new TextLaunchDescriptor(1, "text-cuda", System.Collections.Immutable.ImmutableArray<string>.Empty,
                System.Collections.Immutable.ImmutableArray<string>.Empty, RecFx.ActualConfig, RecFx.PathOf(side)),
            (_, _) => true, _ => true, () => true);
        foreach (CandidateUtilityComparisonProof proof in new[] { first, second })
        {
            long generation = store.Load().Generation;
            Guid tx = Guid.NewGuid();
            Assert.True(store.TryBeginPending(generation, tx, proof, AdmissionFor(proof.Incumbent), ChecksFor(proof.Incumbent)));
            Assert.True(store.TryCommitPending(generation + 1, tx, AdmissionFor(proof.Challenger), ChecksFor(proof.Challenger)));
        }
        return store;
    }

    [Theory]
    [InlineData("after_pending")]
    [InlineData("after_load")]
    [InlineData("before_commit")]
    public async Task Rollback_killed_before_commit_keeps_the_current_winner_and_restores_its_runtime(string crashPoint)
    {
        string state = Path.Combine(_root, "rollback-" + crashPoint);
        CandidateRecommendationStore seeded = SeedRollbackState(state, out var first, out var second);
        long generation = seeded.Load().Generation;
        (int exit, _) = RunChild(state, "rollback", crashPoint, generation);
        Assert.NotEqual(0, exit);
        Assert.NotNull(seeded.Load().Pending);
        Assert.Null(seeded.Load().GetActiveRecord()); // Pending masks all automatic authority

        bool targetLive = crashPoint != "after_pending";
        var (runtime, service, store) = Recovery(state, targetLive ? first.Challenger : second.Challenger);
        RecommendationTransitionResult result = await service.ReconcileAsync(CancellationToken.None);
        Assert.Equal(RecommendationTransitionResult.FailedRestored, result.Outcome);
        Assert.Null(store.Load().Pending);
        Assert.Equal(second.ProofSha256, store.Load().GetActiveRecord()!.Proof.ProofSha256);
        Assert.NotNull(store.Load().Previous); // the rollback target is still available
        Assert.True(new FakeRecFence(runtime).IsLiveExactModel(RecFx.PathOf(second.Challenger), second.Challenger.Identity.LocalSha256));
    }

    [Fact]
    public async Task Rollback_killed_after_commit_recovers_to_the_rolled_back_winner()
    {
        string state = Path.Combine(_root, "rollback-after_commit");
        CandidateRecommendationStore seeded = SeedRollbackState(state, out var first, out _);
        (int exit, _) = RunChild(state, "rollback", "after_commit", seeded.Load().Generation);
        Assert.NotEqual(0, exit);
        var (runtime, service, store) = Recovery(state, first.Challenger);
        Assert.Equal(first.ProofSha256, store.Load().GetActiveRecord()!.Proof.ProofSha256);
        Assert.Equal("nothing_to_reconcile", (await service.ReconcileAsync(CancellationToken.None)).Reason);
        Assert.Empty(runtime.Loads);
    }

    [Fact]
    public void An_uninterrupted_child_commits_and_exits_cleanly()
    {
        string state = Path.Combine(_root, "promote-none");
        (int exit, string output) = RunChild(state, "promote", "none", 0);
        Assert.Equal(0, exit);
        Assert.StartsWith("Committed:committed:2", output.Trim());
        Assert.NotNull(new CandidateRecommendationStore(state).Load().GetActiveRecord());
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
