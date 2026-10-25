using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json.Nodes;
using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class CandidateRecommendationStoreTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 3, 12, 0, 0, DateTimeKind.Utc);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parux-recommendation-store-" + Guid.NewGuid().ToString("N"));
    private string PathToDocument => Path.Combine(_root, CandidateRecommendationStore.FileName);

    [Fact]
    public void Pending_is_durable_but_never_selectable_and_first_commit_retains_baseline_only_as_fallback()
    {
        CandidateRecommendationStore store = new(_root);
        CandidateUtilityComparisonProof proof = Proof("org/challenger", "challenger.gguf", 'a',
            "org/baseline", "baseline.gguf", 'b');
        Assert.Equal(0, store.Load().Generation);
        Assert.True(store.TryBeginPending(0, Guid.NewGuid(), proof));
        CandidateRecommendationSnapshot pending = new CandidateRecommendationStore(_root).Load();
        Assert.Equal(1, pending.Generation);
        Assert.NotNull(pending.Pending);
        Assert.Null(pending.GetActiveRecord());
        Assert.Null(pending.Active);

        Assert.True(new CandidateRecommendationStore(_root).TryCommitPending(1, pending.Pending!.TransactionId));
        CandidateRecommendationSnapshot active = new CandidateRecommendationStore(_root).Load();
        Assert.Equal(2, active.Generation);
        Assert.Null(active.Pending);
        Assert.Equal(proof.ProofSha256, active.GetActiveRecord()!.Proof.ProofSha256);
        Assert.Null(active.Previous); // The original incumbent was never a qualified recommendation.
        Assert.Equal(proof.Incumbent.Identity, active.Fallback!.Identity);
    }

    [Fact]
    public void Later_commit_keeps_one_previous_winner_and_rejects_mismatched_incumbent()
    {
        CandidateRecommendationStore store = new(_root);
        CandidateUtilityComparisonProof first = Proof("org/first", "first.gguf", 'a',
            "org/baseline", "baseline.gguf", 'b');
        Promote(store, first);
        CandidateUtilityComparisonProof wrong = Proof("org/second", "second.gguf", 'c',
            "org/other", "other.gguf", 'd');
        Assert.False(store.TryBeginPending(2, Guid.NewGuid(), wrong));

        CandidateUtilityComparisonProof second = Proof("org/second", "second.gguf", 'c',
            "org/first", "first.gguf", 'a');
        Promote(store, second);
        CandidateRecommendationSnapshot afterSecond = store.Load();
        Assert.Equal(second.ProofSha256, afterSecond.Active!.Proof.ProofSha256);
        Assert.Equal(first.ProofSha256, afterSecond.Previous!.Proof.ProofSha256);
        Assert.Null(afterSecond.Fallback);

        CandidateUtilityComparisonProof third = Proof("org/third", "third.gguf", 'e',
            "org/second", "second.gguf", 'c');
        Promote(store, third);
        CandidateRecommendationSnapshot afterThird = new CandidateRecommendationStore(_root).Load();
        Assert.Equal(third.ProofSha256, afterThird.Active!.Proof.ProofSha256);
        Assert.Equal(second.ProofSha256, afterThird.Previous!.Proof.ProofSha256);
        Assert.NotEqual(first.ProofSha256, afterThird.Previous.Proof.ProofSha256);
    }

    [Fact]
    public async Task Generation_cas_serializes_two_store_instances()
    {
        CandidateUtilityComparisonProof proof = Proof("org/challenger", "challenger.gguf", 'a',
            "org/baseline", "baseline.gguf", 'b');
        Task<bool> first = Task.Run(() => new CandidateRecommendationStore(_root)
            .TryBeginPending(0, Guid.NewGuid(), proof));
        Task<bool> second = Task.Run(() => new CandidateRecommendationStore(_root)
            .TryBeginPending(0, Guid.NewGuid(), proof));
        bool[] results = await Task.WhenAll(first, second);
        Assert.Equal(1, results.Count(value => value));
        Assert.Equal(1, new CandidateRecommendationStore(_root).Load().Generation);
    }

    [Fact]
    public async Task Another_process_holding_store_lock_prevents_mutation_without_losing_prior_document()
    {
        if (!OperatingSystem.IsWindows()) return;
        CandidateRecommendationStore store = new(_root);
        CandidateUtilityComparisonProof first = Proof("org/first", "first.gguf", 'a',
            "org/baseline", "baseline.gguf", 'b');
        Promote(store, first);
        byte[] previous = File.ReadAllBytes(PathToDocument);
        CandidateUtilityComparisonProof second = Proof("org/second", "second.gguf", 'c',
            "org/first", "first.gguf", 'a');
        var start = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-Command");
        start.ArgumentList.Add("$s=[System.IO.File]::Open($env:PARUX_TEST_LOCK_PATH,'OpenOrCreate','ReadWrite','None'); "
            + "[Console]::Out.WriteLine('LOCKED'); [Console]::ReadLine() | Out-Null; $s.Dispose()");
        start.Environment["PARUX_TEST_LOCK_PATH"] = PathToDocument + ".lock";
        using Process holder = Process.Start(start)!;
        try
        {
            Assert.Equal("LOCKED", await holder.StandardOutput.ReadLineAsync()
                .WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Throws<IOException>(() => store.TryBeginPending(2, Guid.NewGuid(), second));
            Assert.Equal(previous, File.ReadAllBytes(PathToDocument));
        }
        finally
        {
            await holder.StandardInput.WriteLineAsync();
            if (!holder.WaitForExit(5000)) holder.Kill(entireProcessTree: true);
        }
        Assert.True(store.TryBeginPending(2, Guid.NewGuid(), second));
    }

    [Fact]
    public void Abort_requires_generation_and_transaction_and_leaves_active_winner()
    {
        CandidateRecommendationStore store = new(_root);
        CandidateUtilityComparisonProof first = Proof("org/first", "first.gguf", 'a',
            "org/baseline", "baseline.gguf", 'b');
        Promote(store, first);
        CandidateUtilityComparisonProof second = Proof("org/second", "second.gguf", 'c',
            "org/first", "first.gguf", 'a');
        Guid tx = Guid.NewGuid();
        Assert.True(store.TryBeginPending(2, tx, second));
        Assert.Null(store.Load().GetActiveRecord());
        Assert.False(store.TryAbortPending(2, tx));
        Assert.False(store.TryAbortPending(3, Guid.NewGuid()));
        Assert.True(store.TryAbortPending(3, tx));
        Assert.Equal(first.ProofSha256, store.Load().GetActiveRecord()!.Proof.ProofSha256);
    }

    [Fact]
    public void First_pending_abort_does_not_create_a_recommendation_or_fallback()
    {
        CandidateRecommendationStore store = new(_root);
        CandidateUtilityComparisonProof proof = Proof("org/challenger", "challenger.gguf", 'a',
            "org/baseline", "baseline.gguf", 'b');
        Guid tx = Guid.NewGuid();
        Assert.True(store.TryBeginPending(0, tx, proof));
        Assert.True(new CandidateRecommendationStore(_root).TryAbortPending(1, tx));
        CandidateRecommendationSnapshot snapshot = store.Load();
        Assert.Equal(2, snapshot.Generation);
        Assert.Null(snapshot.Pending);
        Assert.Null(snapshot.GetActiveRecord());
        Assert.Null(snapshot.Active);
        Assert.Null(snapshot.Previous);
        Assert.Null(snapshot.Fallback);
    }

    [Fact]
    public void Invalid_scope_proof_digest_unknown_fields_and_oversize_fail_closed()
    {
        CandidateRecommendationStore store = new(_root);
        CandidateUtilityComparisonProof proof = Proof("org/challenger", "challenger.gguf", 'a',
            "org/baseline", "baseline.gguf", 'b');
        Assert.Throws<InvalidDataException>(() => store.TryBeginPending(0, Guid.NewGuid(),
            proof with { ProofSha256 = new string('f', 64) }));
        CandidateUtilityComparisonProof wrongScope = proof with { RouteRole = "coding", ProofSha256 = string.Empty };
        wrongScope = wrongScope with { ProofSha256 = CandidateUtilityProofDigest.Proof(wrongScope) };
        Assert.Throws<InvalidDataException>(() => store.TryBeginPending(0, Guid.NewGuid(), wrongScope));
        CandidateProofSide tooManyRuns = proof.Challenger with
        {
            QualityInvocationIds = Enumerable.Range(1, CandidateEvidenceStore.MaximumRecordsPerKind + 1)
                .Select(index => new Guid(index, 0, 0, new byte[8]).ToString("N"))
                .ToImmutableArray()
        };
        CandidateUtilityComparisonProof oversizedCohort = proof with
        { Challenger = tooManyRuns, ProofSha256 = string.Empty };
        oversizedCohort = oversizedCohort with
        { ProofSha256 = CandidateUtilityProofDigest.Proof(oversizedCohort) };
        Assert.Throws<InvalidDataException>(() => store.TryBeginPending(0, Guid.NewGuid(), oversizedCohort));
        CandidateUtilityComparisonProof unsafePath = proof with
        {
            Challenger = proof.Challenger with
            { Identity = proof.Challenger.Identity with { ManagedFileName = "../unsafe.gguf" } },
            ProofSha256 = string.Empty
        };
        unsafePath = unsafePath with { ProofSha256 = CandidateUtilityProofDigest.Proof(unsafePath) };
        Assert.Throws<InvalidDataException>(() => store.TryBeginPending(0, Guid.NewGuid(), unsafePath));
        Promote(store, proof);

        string valid = File.ReadAllText(PathToDocument);
        JsonNode unknown = JsonNode.Parse(valid)!;
        unknown["unexpected"] = 1;
        File.WriteAllText(PathToDocument, unknown.ToJsonString());
        Assert.Throws<InvalidDataException>(() => store.Load());
        File.WriteAllText(PathToDocument, valid.Replace("\"schemaVersion\": 1",
            "\"schemaVersion\": 99", StringComparison.Ordinal));
        Assert.Throws<InvalidDataException>(() => store.Load());
        File.WriteAllText(PathToDocument, "{ broken");
        Assert.Throws<InvalidDataException>(() => store.Load());
        File.WriteAllText(PathToDocument, valid.Replace("\"schemaVersion\": 1,",
            "\"schemaVersion\": 1, \"schemaVersion\": 1,", StringComparison.Ordinal));
        Assert.Throws<InvalidDataException>(() => store.Load());
        File.WriteAllText(PathToDocument, valid.Replace("\"schemaVersion\": 1,",
            "\"schemaVersion\": 1, \"SchemaVersion\": 1,", StringComparison.Ordinal));
        Assert.Throws<InvalidDataException>(() => store.Load());
        File.WriteAllText(PathToDocument, new string('x', 128 * 1024 + 1));
        Assert.Throws<InvalidDataException>(() => store.Load());
    }

    [Fact]
    public void Intent_requires_one_identical_actual_launch_descriptor_for_quality_and_performance()
    {
        CandidateRecommendationStore store = new(_root);
        CandidateUtilityComparisonProof proof = Proof("org/challenger", "challenger.gguf", 'a',
            "org/baseline", "baseline.gguf", 'b');
        CandidateUtilityComparisonProof Resign(CandidateUtilityComparisonProof value) =>
            value with { ProofSha256 = CandidateUtilityProofDigest.Proof(value with { ProofSha256 = string.Empty }) };

        // Historical fixed evaluator profile (ctx 2048 / one slot / reasoning off) is not an AUTO launch.
        CandidateUtilityComparisonProof fixedProfile = Resign(proof with
        {
            QualityEnvironment = proof.QualityEnvironment with
            { ConfigurationFingerprint = LlamaTextQualitySession.ConfigurationFingerprint },
            PerformanceEnvironment = proof.PerformanceEnvironment with
            { ConfigurationFingerprint = LlamaTextQualitySession.ConfigurationFingerprint }
        });
        Assert.Throws<InvalidDataException>(() => store.TryBeginPending(0, Guid.NewGuid(), fixedProfile));

        CandidateUtilityComparisonProof splitLaunch = Resign(proof with
        {
            PerformanceEnvironment = proof.PerformanceEnvironment with
            { ConfigurationFingerprint = "ld1-sha256:" + new string('e', 64) }
        });
        Assert.Throws<InvalidDataException>(() => store.TryBeginPending(0, Guid.NewGuid(), splitLaunch));
        Assert.False(File.Exists(PathToDocument));
        Assert.Equal(0, store.Load().Generation);
        Assert.True(store.TryBeginPending(0, Guid.NewGuid(), proof));
    }

    [Fact]
    public void Intent_and_commit_require_a_fresh_admission_matching_a_live_fence_read_inside_the_cas()
    {
        CandidateRecommendationStore store = new(_root);
        CandidateUtilityComparisonProof proof = Proof("org/challenger", "challenger.gguf", 'a',
            "org/baseline", "baseline.gguf", 'b');
        CandidateLaunchAdmission live = RecommendationStoreAdmissionExtensions.Admission(proof, proof.Incumbent);
        TextLaunchDescriptor fence = RecommendationStoreAdmissionExtensions.Fence(live);
        RecommendationTransitionChecks ok = RecommendationStoreAdmissionExtensions.Checks(live);

        // Stale admission, wrong live model, hardware drift, and a fence replaced before the CAS: no intent.
        Assert.False(store.TryBeginPending(0, Guid.NewGuid(), proof,
            RecommendationStoreAdmissionExtensions.Admission(proof, proof.Incumbent, DateTime.UtcNow.AddMinutes(-3)), ok));
        Assert.False(store.TryBeginPending(0, Guid.NewGuid(), proof,
            RecommendationStoreAdmissionExtensions.Admission(proof, proof.Challenger), ok));
        Assert.False(store.TryBeginPending(0, Guid.NewGuid(), proof,
            live with { HardwareFingerprint = "other-hardware" }, ok));
        Assert.False(store.TryBeginPending(0, Guid.NewGuid(), proof, live, ok with { ReadFence = () => null }));
        Assert.False(store.TryBeginPending(0, Guid.NewGuid(), proof, live,
            ok with { ReadFence = () => fence with { Fingerprint = "ld1-sha256:" + new string('e', 64) } }));
        Assert.False(store.TryBeginPending(0, Guid.NewGuid(), proof, live,
            ok with { ReadFence = () => fence with { ModelPath = "C:/elsewhere/baseline.gguf" } }));
        // Exact model bytes are enforced at the consumer, inside the CAS, for the side that must be live.
        string? askedSha = null;
        Assert.False(store.TryBeginPending(0, Guid.NewGuid(), proof, live,
            ok with { LiveModelIsExact = (path, sha) => { askedSha = sha; return false; } }));
        Assert.Equal(proof.Incumbent.Identity.LocalSha256, askedSha);
        // A stale policy/provenance/ledger snapshot and any manual pin each refuse; nothing is written.
        Assert.False(store.TryBeginPending(0, Guid.NewGuid(), proof, live, ok with { ProofSnapshotCurrent = _ => false }));
        Assert.False(store.TryBeginPending(0, Guid.NewGuid(), proof, live, ok with { NoManualPin = () => false }));
        Assert.Equal(0, store.Load().Generation);
        Assert.False(File.Exists(PathToDocument));

        Guid tx = Guid.NewGuid();
        Assert.True(store.TryBeginPending(0, tx, proof, live, ok));
        // Commit needs the challenger live under the same descriptor; the incumbent fence is not enough.
        Assert.False(store.TryCommitPending(1, tx, live, ok));
        CandidateLaunchAdmission challenger = RecommendationStoreAdmissionExtensions.Admission(proof, proof.Challenger);
        RecommendationTransitionChecks challengerOk = RecommendationStoreAdmissionExtensions.Checks(challenger);
        Assert.False(store.TryCommitPending(1, tx, challenger, challengerOk with { ReadFence = () => null }));
        Assert.False(store.TryCommitPending(1, tx, challenger,
            challengerOk with { LiveModelIsExact = (path, sha) => sha != proof.Challenger.Identity.LocalSha256 }));
        // A pin created between Pending and commit blocks activation and leaves Pending (recovery marker) intact.
        Assert.False(store.TryCommitPending(1, tx, challenger, challengerOk with { NoManualPin = () => false }));
        Assert.False(store.TryCommitPending(1, tx, challenger, challengerOk with { ProofSnapshotCurrent = _ => false }));
        Assert.NotNull(store.Load().Pending);
        Assert.Null(store.Load().Active);
        Assert.True(store.TryCommitPending(1, tx, challenger, challengerOk));
        Assert.Equal(proof.ProofSha256, store.Load().GetActiveRecord()!.Proof.ProofSha256);
    }

    [Fact]
    public void Failure_before_atomic_replace_preserves_previous_document_and_cleans_staging_file()
    {
        CandidateRecommendationStore store = new(_root);
        CandidateUtilityComparisonProof first = Proof("org/first", "first.gguf", 'a',
            "org/baseline", "baseline.gguf", 'b');
        Promote(store, first);
        byte[] previous = File.ReadAllBytes(PathToDocument);
        CandidateUtilityComparisonProof second = Proof("org/second", "second.gguf", 'c',
            "org/first", "first.gguf", 'a');
        CandidateRecommendationStore failing = new(_root, () => throw new IOException("injected replace failure"));
        Assert.Throws<IOException>(() => failing.TryBeginPending(2, Guid.NewGuid(), second));
        Assert.Equal(previous, File.ReadAllBytes(PathToDocument));
        Assert.Equal(first.ProofSha256, new CandidateRecommendationStore(_root).Load().GetActiveRecord()!.Proof.ProofSha256);
        Assert.Empty(Directory.GetFiles(_root, CandidateRecommendationStore.FileName + ".*.tmp"));
    }

    [Fact]
    public void Manual_pin_writers_and_pending_intent_are_linearized_and_a_pin_is_never_overridden()
    {
        // Pinners hammer the shared role-override writer while Pending intent is attempted. Inside the CAS (the
        // pre-replace hook runs under every gate) the pin must be absent whenever a Pending is being saved.
        Directory.CreateDirectory(_root);
        string gguf = Path.Combine(_root, "pin.gguf");
        File.WriteAllText(gguf, "x");
        int violations = 0, pendings = 0, refusals = 0;
        CandidateUtilityComparisonProof proof = Proof("org/challenger", "challenger.gguf", 'a', "org/baseline", "baseline.gguf", 'b');
        CandidateLaunchAdmission admission = RecommendationStoreAdmissionExtensions.Admission(proof, proof.Incumbent);
        CandidateRecommendationStore? store = null;
        store = new CandidateRecommendationStore(_root, () =>
        {
            if (store!.Load().Pending is null && LocalAiPaths.GetRoleOverride("reasoning") is not null) violations++;
        });
        RecommendationTransitionChecks checks = RecommendationStoreAdmissionExtensions.Checks(admission) with
        {
            NoManualPin = () => !ManualSelectionGate.HasUserPin("reasoning", "balanced", _root)
        };
        using var stop = new CancellationTokenSource();
        Task[] pinners = Enumerable.Range(0, 3).Select(_ => Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                LocalAiPaths.SetRoleOverride("reasoning", gguf);
                LocalAiPaths.ClearRoleOverride("reasoning");
            }
        })).ToArray();
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(4);
            while (DateTime.UtcNow < deadline)
            {
                long generation = store.Load().Generation;
                Guid tx = Guid.NewGuid();
                if (store.TryBeginPending(generation, tx, proof, admission, checks))
                { pendings++; Assert.True(store.TryAbortPending(generation + 1, tx)); }
                else refusals++;
            }
        }
        finally
        {
            stop.Cancel();
            Assert.True(Task.WaitAll(pinners, TimeSpan.FromSeconds(20)), "pin writers deadlocked against the transition");
            LocalAiPaths.ClearRoleOverride("reasoning");
        }
        Assert.Equal(0, violations);
        Assert.True(pendings + refusals > 0);
    }

    private static void Promote(CandidateRecommendationStore store, CandidateUtilityComparisonProof proof)
    {
        long generation = store.Load().Generation;
        Guid tx = Guid.NewGuid();
        Assert.True(store.TryBeginPending(generation, tx, proof));
        Assert.True(store.TryCommitPending(generation + 1, tx));
    }

    private static CandidateUtilityComparisonProof Proof(string challengerRepo, string challengerFile,
        char challengerHash, string incumbentRepo, string incumbentFile, char incumbentHash)
    {
        CandidateProofSide challenger = Side(challengerRepo, challengerFile, challengerHash);
        CandidateProofSide incumbent = Side(incumbentRepo, incumbentFile, incumbentHash);
        CandidateProofEnvironment quality = new("hardware", "runtime", "binary",
            CandidateEvidenceServiceTests.ActualLaunchConfiguration,
            CandidateGeneralChatQualitySuite.WorkloadFingerprint);
        CandidateProofEnvironment performance = quality with
        { WorkloadFingerprint = CandidateTextPerformanceWorkload.WorkloadFingerprint };
        CandidateUtilityComparisonProof proof = new(1, CandidateGeneralChatQualitySuite.Capability,
            CandidateGeneralChatQualitySuite.RouteRole, CandidateGeneralChatQualitySuite.AutoProfile,
            4, new string('1', 64), CandidateGeneralChatQualitySuite.Id,
            CandidateGeneralChatQualitySuite.Version, CandidateGeneralChatQualitySuite.Sha256,
            CandidateGeneralChatQualitySuite.ProfileId, CandidateGeneralChatQualitySuite.ProfileVersion,
            CandidateGeneralChatQualitySuite.ProfileSha256, CandidateTextPerformanceWorkload.Id,
            CandidateTextPerformanceWorkload.Version, CandidateTextPerformanceWorkload.Sha256,
            quality, performance, new string('2', 64), challenger, incumbent, string.Empty);
        return proof with { ProofSha256 = CandidateUtilityProofDigest.Proof(proof) };
    }

    private static CandidateProofSide Side(string repo, string file, char hash)
    {
        const string revision = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        string repoPath = "weights/" + file;
        CandidateProofIdentity identity = new(ModelDiscoveryIdentity.Provider,
            ModelDiscoveryIdentity.Key(repo, revision, repoPath), repo, revision, repoPath,
            ModelDiscoveryIdentity.PinnedUrl(repo, revision, repoPath), file, 123,
            new string(hash, 64));
        ImmutableArray<string> runs = Enumerable.Range(1, 5)
            .Select(index => new Guid(index, 0, 0, new byte[8]).ToString("N"))
            .OrderBy(id => id, StringComparer.Ordinal).ToImmutableArray();
        return new CandidateProofSide(identity, new string('3', 64), runs,
            new string('4', 64), new string('5', 64));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

/// <summary>
/// Test shorthand: the live actual-launch admission and in-memory fence for the side that is live at that
/// transition (incumbent for Pending, challenger for Commit), exactly as a transaction coordinator supplies.
/// </summary>
internal static class RecommendationStoreAdmissionExtensions
{
    internal const string ModelsRoot = "C:/parux-test-models";
    internal static bool ExactModel(string path, string sha256) => true;

    /// <summary>Permissive production-shaped checks: live fence = the admission, exact bytes, current snapshot, no pin.</summary>
    internal static RecommendationTransitionChecks Checks(CandidateLaunchAdmission? admission) => new(
        () => admission is null ? null : Fence(admission), ExactModel, _ => true, () => true);

    internal static CandidateLaunchAdmission Admission(CandidateUtilityComparisonProof proof, CandidateProofSide live,
        DateTime? checkedUtc = null) => new(proof.QualityEnvironment.ConfigurationFingerprint, "text-cuda",
        proof.QualityEnvironment.HardwareFingerprint, proof.QualityEnvironment.RuntimeFingerprint,
        proof.QualityEnvironment.BinaryFingerprint, ModelsRoot + "/" + live.Identity.ManagedFileName,
        checkedUtc ?? DateTime.UtcNow);

    internal static TextLaunchDescriptor Fence(CandidateLaunchAdmission admission) => new(1, admission.Backend,
        System.Collections.Immutable.ImmutableArray<string>.Empty, System.Collections.Immutable.ImmutableArray<string>.Empty,
        admission.ConfigurationFingerprint, admission.ModelPath);

    internal static bool TryBeginPending(this CandidateRecommendationStore store, long expectedGeneration,
        Guid transactionId, CandidateUtilityComparisonProof proof)
    {
        CandidateLaunchAdmission admission = Admission(proof, proof.Incumbent);
        return store.TryBeginPending(expectedGeneration, transactionId, proof, admission, Checks(admission));
    }

    internal static bool TryCommitPending(this CandidateRecommendationStore store, long expectedGeneration,
        Guid transactionId)
    {
        CandidateUtilityComparisonProof? pending = store.Load().Pending?.Proof;
        if (pending is null)
            return store.TryCommitPending(expectedGeneration, transactionId,
                new CandidateLaunchAdmission("none", "text-cuda", "", "", "", "none", DateTime.UtcNow), Checks(null) with { ReadFence = () => null });
        CandidateLaunchAdmission admission = Admission(pending, pending.Challenger);
        return store.TryCommitPending(expectedGeneration, transactionId, admission, Checks(admission));
    }
}