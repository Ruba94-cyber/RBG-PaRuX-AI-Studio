using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>The exact managed Candidate a durable Active recommendation names (identity only; nothing is verified here).</summary>
internal sealed record RecommendedRoute(string CandidateKey, string Revision, string ManagedFileName, string Sha256);

/// <summary>
/// Cheap, synchronous routing-time lookup of the Active recommendation for Chat / Standard / Balanced.
/// Cost model on the ordinary chat route: with no recommendation it is ONE file-metadata probe (no JSON, no ledger,
/// no model bytes). With a record it re-parses the small document only when the file changed, then applies
/// precedence: a Pending transition, any user pin (role override, Manual chat, scoped Force/Prefer, unreadable
/// state), a changed policy or a Gaming marker all mean "no recommendation" and the ordinary ranker runs. Exact
/// byte/provenance admission is NOT done here; the router's fresh-exact load does it (and hashes only on a swap).
/// </summary>
internal sealed class RecommendationRouteConsult
{
    private readonly string _path;
    private readonly string _stateDirectory;
    private readonly Func<CandidateUtilityComparisonProof, bool> _policyCurrent;
    private readonly Func<bool> _gaming;
    private readonly Func<CandidateUtilityComparisonProof, CancellationToken,
        Task<CandidateRecommendationEvaluationIdentity?>>? _currentIdentity;
    private readonly string? _pinRoot;
    private readonly object _gate = new();
    private (DateTime Stamp, long Length, CandidateRecommendationSnapshot? Snapshot, bool Unreadable)? _cache;
    /// <summary>Documents parsed since construction (a test hook proving ordinary routes do not parse).</summary>
    internal int Parses;

    internal RecommendationRouteConsult(string stateDirectory, Func<CandidateUtilityComparisonProof, bool> policyCurrent,
        Func<bool> gaming, string? pinRoot = null,
        Func<CandidateUtilityComparisonProof, CancellationToken,
            Task<CandidateRecommendationEvaluationIdentity?>>? currentIdentity = null)
    {
        _stateDirectory = stateDirectory;
        _path = Path.Combine(stateDirectory, CandidateRecommendationStore.FileName);
        _policyCurrent = policyCurrent;
        _gaming = gaming;
        _currentIdentity = currentIdentity;
        _pinRoot = pinRoot;
    }

    /// <summary>(route, null) when a recommendation applies; (null, reason) when one exists but must not be used;
    /// (null, null) when there is simply none (the common case).</summary>
    internal (RecommendedRoute? Route, string? SkipReason) Consult()
    {
        if (_currentIdentity is not null) return ConsultAsync().GetAwaiter().GetResult();
        (CandidateUtilityComparisonProof? proof, string? reason) = ConsultActiveProof();
        return proof is null ? (null, reason) : (ToRoute(proof), null);
    }

    internal async Task<(RecommendedRoute? Route, string? SkipReason)> ConsultAsync(CancellationToken cancellationToken = default)
    {
        (CandidateUtilityComparisonProof? proof, string? reason) = ConsultActiveProof();
        if (proof is null) return (null, reason);
        if (_currentIdentity is null) return (ToRoute(proof), null);

        CandidateRecommendationEvaluationIdentity previous = CandidateRecommendationEvaluationIdentity.From(proof.QualityEnvironment);
        if (!previous.IsValid || previous != CandidateRecommendationEvaluationIdentity.From(proof.PerformanceEnvironment))
            return (null, "active_identity_unavailable");

        CandidateRecommendationEvaluationIdentity? current;
        try
        {
            current = await _currentIdentity(proof, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return (null, "active_identity_unavailable");
        }
        if (current is not { IsValid: true }) return (null, "active_identity_unavailable");
        if (previous != current)
        {
            CandidateIdentityRevalidationRequest request = CandidateIdentityRevalidationRequestStore.Create(
                proof.Challenger.Identity, previous, current);
            try
            {
                _ = new CandidateIdentityRevalidationRequestStore(_stateDirectory).TryCreate(request);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException
                or ArgumentException)
            {
                // The route remains fail-closed even when a passive revalidation offer cannot be persisted.
            }
            return (null, "active_identity_changed");
        }
        return (ToRoute(proof), null);
    }

    private (CandidateUtilityComparisonProof? Proof, string? SkipReason) ConsultActiveProof()
    {
        FileInfo info = new(_path);
        if (!info.Exists) return (null, null);
        CandidateRecommendationSnapshot? snapshot;
        lock (_gate)
        {
            if (_cache is not { } cached || cached.Stamp != info.LastWriteTimeUtc || cached.Length != info.Length)
            {
                try
                {
                    Parses++;
                    snapshot = new CandidateRecommendationStore(Path.GetDirectoryName(_path)!).Load();
                    _cache = (info.LastWriteTimeUtc, info.Length, snapshot, false);
                }
                catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
                {
                    _cache = null; // retry next time; an unreadable record never selects anything
                    return (null, "recommendation_store_unreadable");
                }
            }
            else snapshot = cached.Snapshot;
        }
        if (snapshot is null) return (null, "recommendation_store_unreadable");
        if (snapshot.Pending is not null) return (null, "recommendation_transition_pending");
        if (snapshot.Active is not { } active) return (null, null);
        CandidateUtilityComparisonProof proof = active.Proof;
        if (proof.RouteRole != CandidateGeneralChatQualitySuite.RouteRole
            || proof.AutoProfile != CandidateGeneralChatQualitySuite.AutoProfile)
            return (null, "recommendation_scope_mismatch");
        if (_gaming()) return (null, "gaming_active");
        if (ManualSelectionGate.GetUserPinReason(proof.RouteRole, proof.AutoProfile, _pinRoot) is { } pin)
            return (null, pin);
        // Any role assignment at all (even an auto-assigned one) already decides which Standard model is the
        // default, so the recommendation must not silently replace it.
        if (LocalAiPaths.GetRoleOverride(proof.RouteRole) is not null) return (null, "manual_pin_role_override");
        if (!_policyCurrent(proof)) return (null, "recommendation_policy_changed");
        return (proof, null);
    }

    private static RecommendedRoute ToRoute(CandidateUtilityComparisonProof proof)
    {
        CandidateProofIdentity winner = proof.Challenger.Identity;
        return new RecommendedRoute(winner.CandidateKey, winner.Revision, winner.ManagedFileName, winner.LocalSha256);
    }
}
