namespace PaRuX.Contracts;

/// <summary>
/// Read-only status of the scoped recommendation (Chat / Standard / Balanced). Reading it never changes routing.
/// "CanPromote" only says nothing prevents a promotion attempt right now (no Pending, no manual pin, no Gaming);
/// a promotion additionally requires a positive evidence proof and an explicit confirmation.
/// </summary>
public sealed record CandidateRecommendationStatusResponse(
    long Generation,
    string State,
    string? ActiveCandidateKey,
    string? ActiveManagedFileName,
    string? ActiveRevision,
    DateTime? PromotedUtc,
    bool PreviousAvailable,
    bool FallbackAvailable,
    string? ManualPinReason,
    string? ManualPinExplanation,
    bool GamingActive,
    bool CanPromote,
    bool CanRollback);

/// <summary>Explicit, confirmed promotion of one exact managed Candidate over the live reference model.</summary>
public sealed record PromoteCandidateRecommendationRequest(
    string CandidateKey, string ManagedFileName, long ExpectedGeneration, bool Confirmed);

/// <summary>Explicit, confirmed return to the previously recommended winner.</summary>
public sealed record RollbackCandidateRecommendationRequest(long ExpectedGeneration, bool Confirmed);

/// <summary>
/// Typed outcome of a promote/rollback attempt. HTTP: 200 Committed; 409 stale/blocked/refused (including a failed
/// transition whose runtime was restored); 503 unavailable (Gaming, shutdown, unreadable state, runtime left stopped).
/// </summary>
public sealed record CandidateRecommendationTransitionResponse(
    string Outcome, string ReasonCode, string Message, long Generation, bool? RuntimeRestored);
