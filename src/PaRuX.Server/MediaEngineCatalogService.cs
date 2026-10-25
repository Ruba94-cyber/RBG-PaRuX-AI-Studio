using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>
/// Read-only media engine discovery. The catalog intentionally distinguishes an audited candidate
/// from a callable engine and from an engine eligible for benchmark or automatic routing.
/// USER_MANAGED installations are never modified or adopted by this service.
/// </summary>
public sealed class MediaEngineCatalogService
{
    public const string Ref2VaEngineId = "ref2va-vsa";
    public const string Ref2VaSourceRepository = "Kablex/ComfyUI-Ref2VA-VSA";
    public const string Ref2VaSourceRevision = "92e3f8357c626f63cfe3813ec914f2cf3fbc500c";
    public const string Ref2VaCodeLicense = "Apache-2.0";

    public IReadOnlyList<MediaEngineCatalogEntry> GetCatalog(LocalRuntimeService runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);

        ManagedMediaEngineStatus diffusers = new ManagedDiffusersImageEngine(runtime).GetStatus();
        bool managedComfyReady = File.Exists(LocalAiPaths.ComfyPython)
            && File.Exists(LocalAiPaths.ComfyMain);
        Ref2VaManagedStatus ref2va = Ref2VaManagedExecutor.GetStatus();
        return BuildCatalog(diffusers, managedComfyReady, ref2va);
    }

    /// <summary>
    /// Deterministic compatibility overload used by source/unit checks which do not own physical
    /// GPU/model assets. Ref2VA remains unavailable unless a concrete managed status is supplied.
    /// </summary>
    internal static IReadOnlyList<MediaEngineCatalogEntry> BuildCatalog(
        ManagedMediaEngineStatus diffusers,
        bool managedComfyReady) =>
        BuildCatalog(diffusers, managedComfyReady, new Ref2VaManagedStatus
        {
            EngineId = Ref2VaEngineId,
            SourceRevision = Ref2VaSourceRevision,
            Callable = false,
            Ready = false,
            Reason = "Pinned source is audited, but no verified managed Ref2VA executor status was supplied to this catalog evaluation."
        });

    internal static IReadOnlyList<MediaEngineCatalogEntry> BuildCatalog(
        ManagedMediaEngineStatus diffusers,
        bool managedComfyReady,
        Ref2VaManagedStatus ref2va)
    {
        ArgumentNullException.ThrowIfNull(diffusers);
        ArgumentNullException.ThrowIfNull(ref2va);

        bool diffusersReady = diffusers.Ready;
        bool ref2vaCallable = ref2va.Callable && ref2va.Ready;
        return new[]
        {
            new MediaEngineCatalogEntry
            {
                EngineId = ManagedComfyImageRaceEngine.EngineId,
                DisplayName = "ComfyUI managed race renderer",
                EngineVersion = ManagedComfyImageRaceEngine.EngineVersion,
                Capabilities = new[] { "text-to-image" },
                Ownership = "PARUX_MANAGED",
                Lifecycle = "shipping-race",
                Audited = true,
                Callable = managedComfyReady,
                BenchmarkEligible = managedComfyReady,
                AutoEligible = false,
                Ready = managedComfyReady,
                Reason = managedComfyReady
                    ? "PaRuX-managed ComfyUI renderer files are present; the race endpoint still performs cold-start and health checks per run."
                    : "PaRuX-managed ComfyUI renderer files are incomplete; discovery does not treat an unrelated USER_MANAGED ComfyUI installation as callable."
            },
            new MediaEngineCatalogEntry
            {
                EngineId = ManagedDiffusersImageEngine.EngineId,
                DisplayName = "PaRuX managed Diffusers image renderer",
                EngineVersion = ManagedDiffusersImageEngine.EngineVersion,
                Capabilities = new[] { "text-to-image" },
                Ownership = "PARUX_MANAGED",
                Lifecycle = "shipping-race",
                Audited = true,
                Callable = diffusersReady,
                BenchmarkEligible = diffusersReady,
                AutoEligible = false,
                Ready = diffusersReady,
                Reason = diffusersReady
                    ? "The independent managed Diffusers engine reports ready and may participate in an explicit Engine Race."
                    : "The independent managed Diffusers engine is not ready; install/repair must complete before execution or benchmark."
            },
            new MediaEngineCatalogEntry
            {
                EngineId = Ref2VaEngineId,
                DisplayName = "Ref2VA-VSA reference-video accelerator",
                EngineVersion = "managed-" + Ref2VaSourceRevision[..12],
                Capabilities = new[] { "image-to-video", "reference-to-video-acceleration" },
                Ownership = "PARUX_MANAGED",
                Lifecycle = ref2vaCallable ? "managed-executor" : "audited-candidate",
                SourceRepository = Ref2VaSourceRepository,
                SourceRevision = Ref2VaSourceRevision,
                CodeLicense = Ref2VaCodeLicense,
                DependencyLicenseStatus = "user-provided-model-assets-review-required",
                Audited = true,
                Callable = ref2vaCallable,
                // Callability proves the pinned executor can be invoked. It does NOT prove Ref2VA
                // wins a same-machine challenge or that all third-party model terms have been
                // accepted. Those are physical acceptance/promotion gates, so they stay false.
                BenchmarkEligible = false,
                AutoEligible = false,
                Ready = ref2vaCallable,
                Reason = ref2vaCallable
                    ? "Pinned PaRuX-managed Ref2VA executor is callable with local user-provided assets and eligible hardware. Same-machine benchmark evidence and model-license acceptance are still required before benchmark/AUTO promotion."
                    : ref2va.Reason
            }
        };
    }
}
