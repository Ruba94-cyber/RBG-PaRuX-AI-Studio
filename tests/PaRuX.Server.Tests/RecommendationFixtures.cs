using System.Collections.Immutable;
using PaRuX.Contracts;
using PaRuX.Server;

// Shared by the recommendation coordinator tests and (compile-linked) the crash harness executable. It must not
// depend on xunit.
namespace PaRuX.Server.Tests;

internal static class RecFx
{
    internal const string Models = "C:/parux-fx-models";
    internal const string ActualConfig = "ld1-sha256:" + "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    internal static readonly CandidateLaunchHost Host = new("text-cuda", "gpu-1", "text-cuda:runtime-v1", "binary-sha");

    internal static string PathOf(CandidateProofSide side) => Path.GetFullPath(Path.Combine(Models, side.Identity.ManagedFileName));

    internal static CandidateUtilityComparisonProof Proof(string challengerRepo, string challengerFile, char challengerHash,
        string incumbentRepo, string incumbentFile, char incumbentHash)
    {
        CandidateProofEnvironment quality = new("gpu-1", "text-cuda:runtime-v1", "binary-sha", ActualConfig,
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
            quality, performance, new string('2', 64), Side(challengerRepo, challengerFile, challengerHash),
            Side(incumbentRepo, incumbentFile, incumbentHash), string.Empty);
        return proof with { ProofSha256 = CandidateUtilityProofDigest.Proof(proof) };
    }

    private static CandidateProofSide Side(string repo, string file, char hash)
    {
        const string revision = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        string repoPath = "weights/" + file;
        CandidateProofIdentity identity = new(ModelDiscoveryIdentity.Provider,
            ModelDiscoveryIdentity.Key(repo, revision, repoPath), repo, revision, repoPath,
            ModelDiscoveryIdentity.PinnedUrl(repo, revision, repoPath), file, 123, new string(hash, 64));
        ImmutableArray<string> runs = Enumerable.Range(1, 5)
            .Select(index => new Guid(index, 0, 0, new byte[8]).ToString("N"))
            .OrderBy(id => id, StringComparer.Ordinal).ToImmutableArray();
        return new CandidateProofSide(identity, new string('3', 64), runs, new string('4', 64), new string('5', 64));
    }
}

public enum FakeHealth { Healthy, Unhealthy, Hang }

internal sealed class FakeRecRuntime : IRecommendationRuntime
{
    internal (string Path, string Sha)? Loaded;
    internal bool Running;
    internal bool Gaming, Stop;
    internal FakeHealth Health = FakeHealth.Healthy;
    internal readonly List<string> Loads = new();
    internal int Stops;
    internal int WritersHeld, MaxWritersHeld;
    /// <summary>Return false to fail the load; may throw/cancel/mutate state (e.g. create a pin).</summary>
    internal Func<string, bool>? OnLoad;

    public bool GamingActive => Gaming;
    public bool StopRequested => Stop;
    public bool TextRuntimeRunning => Running;

    internal void Serve(CandidateProofSide side) { Loaded = (RecFx.PathOf(side), side.Identity.LocalSha256); Running = true; }

    private sealed class Writer(FakeRecRuntime owner) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() { Interlocked.Decrement(ref owner.WritersHeld); return ValueTask.CompletedTask; }
    }

    public async Task<IAsyncDisposable> AcquireWriterAsync(CancellationToken cancellationToken)
    {
        await Task.Yield();
        int held = Interlocked.Increment(ref WritersHeld);
        MaxWritersHeld = Math.Max(MaxWritersHeld, held);
        return new Writer(this);
    }

    public Task<RuntimeLoadResult> LoadExactAsync(IAsyncDisposable writer, string role, string candidateKey, string path,
        string sha256, CancellationToken cancellationToken)
    {
        string full = Path.GetFullPath(path);
        Loads.Add(full);
        if (Gaming || Stop) return Task.FromResult(new RuntimeLoadResult { Success = false, Role = role, Message = "blocked" });
        cancellationToken.ThrowIfCancellationRequested();
        Loaded = null; Running = false; // a fresh load always replaces the resident runtime
        if (OnLoad is not null && !OnLoad(full))
            return Task.FromResult(new RuntimeLoadResult { Success = false, Role = role, Message = "load failed" });
        Loaded = (full, sha256); Running = true;
        return Task.FromResult(new RuntimeLoadResult { Success = true, Role = role, ModelPath = full });
    }

    public async Task<bool> IsHealthyAsync(CancellationToken cancellationToken)
    {
        if (Health == FakeHealth.Hang) { await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false); }
        return Health == FakeHealth.Healthy && Running;
    }

    public bool StopText() { Stops++; Loaded = null; Running = false; return true; }
}

internal sealed class FakeRecFence(FakeRecRuntime runtime) : ICandidateLaunchFenceSource
{
    public TextLaunchDescriptor? ReadVerifiedFence() => runtime.Running && runtime.Loaded is { } loaded
        ? new TextLaunchDescriptor(1, "text-cuda", ImmutableArray<string>.Empty, ImmutableArray<string>.Empty,
            RecFx.ActualConfig, loaded.Path) : null;

    public Task<CandidateLaunchHost?> CaptureHostAsync(string backend, CancellationToken cancellationToken) =>
        Task.FromResult<CandidateLaunchHost?>(RecFx.Host);

    public Task<TextLaunchDescriptorResult> PlanAsync(string modelPath, CancellationToken cancellationToken) =>
        Task.FromResult(new TextLaunchDescriptorResult(null, "not_used"));

    public bool IsLiveExactModel(string modelPath, string expectedSha256) => runtime.Running
        && runtime.Loaded is { } loaded && string.Equals(loaded.Path, Path.GetFullPath(modelPath), StringComparison.OrdinalIgnoreCase)
        && string.Equals(loaded.Sha, expectedSha256, StringComparison.OrdinalIgnoreCase);
}

internal sealed class FakeRecProofs(CandidateUtilityComparisonProof? proof) : IRecommendationProofSource
{
    internal CandidateUtilityComparisonProof? Proof = proof;
    internal bool SnapshotCurrent = true, SideCurrent = true, PolicyCurrent = true;
    internal HashSet<string> StaleFiles = new();

    public Task<CandidateUtilityProofResult> CreateProofAsync(string candidateKey, string managedFileName,
        CancellationToken cancellationToken) => Task.FromResult(Proof is null
            ? new CandidateUtilityProofResult(new CandidateUtilityComparisonResult(
                CandidateUtilityComparisonDisposition.NoWinner, "quality_below_reliability_floor"), null)
            : new CandidateUtilityProofResult(new CandidateUtilityComparisonResult(
                CandidateUtilityComparisonDisposition.CandidateMeetsReferenceMargin, "candidate_meets_reference_margin"), Proof));

    public string? CheckLicenseEvidence(string candidateKey, string managedFileName, CandidateProofSide? evaluatedSide) => null;
    public Task<IDisposable?> CaptureLicenseEvidenceReadLeaseAsync(string candidateKey, string managedFileName,
        CandidateProofSide? evaluatedSide, CancellationToken cancellationToken) =>
        Task.FromResult<IDisposable?>(new MemoryStream());
    public bool IsSnapshotCurrent(CandidateUtilityComparisonProof proof) => SnapshotCurrent;
    internal bool Transient;
    public bool? IsSideCurrent(CandidateProofSide side) => Transient ? null : SideCurrent && !StaleFiles.Contains(side.Identity.ManagedFileName);
    public bool IsPolicyCurrent(CandidateUtilityComparisonProof proof) => PolicyCurrent;
}
