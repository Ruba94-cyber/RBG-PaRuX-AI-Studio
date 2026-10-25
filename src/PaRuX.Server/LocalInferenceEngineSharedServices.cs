namespace PaRuX.Server;

/// <summary>
/// Process-wide BIC runtime state shared by Chat and the runtime/performance endpoint module.
/// These services are intentionally filesystem-backed/process-scoped and have no request data.
/// Keeping one Registry/pack/evidence/coordinator identity matters because a measured winner must
/// be the same engine object later stopped by media, Gaming Mode, OFF and baseline handoff.
/// </summary>
internal static class LocalInferenceEngineSharedServices
{
    private static readonly object Gate = new();
    private static LocalInferenceEngineCoordinator? _coordinator;

    public static LocalInferenceEngineRegistry Registry { get; } = new();
    public static ExLlamaV3ManagedPackService ExLlamaPacks { get; } = new();
    public static RuntimeEngineRaceEvidenceStore EngineRaceEvidence { get; } = new();

    public static LocalInferenceEngineCoordinator GetOrCreateCoordinator(
        ModelDownloadService models,
        LocalAiProcessManager runtime)
    {
        ArgumentNullException.ThrowIfNull(models);
        ArgumentNullException.ThrowIfNull(runtime);
        lock (Gate)
        {
            return _coordinator ??= new LocalInferenceEngineCoordinator(
                models,
                Registry,
                runtime,
                ExLlamaPacks,
                EngineRaceEvidence);
        }
    }

    public static LocalInferenceEngineCoordinator? TryGetCoordinator()
    {
        lock (Gate) return _coordinator;
    }
}
