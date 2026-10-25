namespace PaRuX.Server;

/// <summary>
/// Serializes Candidate identity metadata changes with the final evidence check and runtime-start
/// handoff. The lock is held only while a runtime start is initiated, never for the model load or
/// evaluation lifetime.
/// </summary>
internal static class CandidateEvidenceMetadataGate
{
    internal static object SyncRoot { get; } = new();
}
