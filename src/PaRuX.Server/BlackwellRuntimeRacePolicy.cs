using PaRuX.Contracts;

namespace PaRuX.Server;

public enum RuntimeBuildTrust
{
    PaRuXVerified = 0,
    UpstreamVerified = 1,
    ExperimentalThirdParty = 2,
    LocalUnverified = 3
}

public sealed record RuntimeBuildFlavor(
    string Id,
    string DisplayName,
    RuntimeBuildTrust Trust,
    string? CudaArchitecture,
    IReadOnlyDictionary<string, string> Environment,
    bool BlackwellOnly,
    bool AllowAutomaticPromotion,
    string Rationale);

/// <summary>
/// Blackwell-specific experiment policy. It does not download community binaries or mutate driver
/// settings. The policy defines isolated race flavors and the trust/promotion boundary so a fast
/// third-party sm_120 build can be measured without ever silently becoming the production runtime.
/// </summary>
public static class BlackwellRuntimeRacePolicy
{
    public const string DisableCudaGraphsVariable = "GGML_CUDA_DISABLE_GRAPHS";

    public static IReadOnlyList<RuntimeBuildFlavor> BuildFlavors(
        IEnumerable<GpuPerformanceTelemetryDto> telemetry,
        bool paRuXNativeBuildAvailable,
        bool includeExperimentalThirdParty = false)
    {
        bool blackwell = GpuPerformanceTelemetryService.IsBlackwellCapable(telemetry);
        var result = new List<RuntimeBuildFlavor>
        {
            new(
                "upstream-default",
                "Upstream default",
                RuntimeBuildTrust.UpstreamVerified,
                CudaArchitecture: null,
                Environment: new Dictionary<string, string>(),
                BlackwellOnly: false,
                AllowAutomaticPromotion: true,
                Rationale: "Current verified upstream/PaRuX-managed baseline." )
        };
        if (!blackwell) return result;

        result.Add(new RuntimeBuildFlavor(
            "blackwell-safe-no-graphs",
            "Blackwell safe · CUDA graphs off",
            RuntimeBuildTrust.UpstreamVerified,
            "sm_120",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [DisableCudaGraphsVariable] = "1"
            },
            BlackwellOnly: true,
            AllowAutomaticPromotion: true,
            Rationale: "Isolates the documented Blackwell CUDA-graph stability risk while keeping the same verified runtime bytes."));

        if (paRuXNativeBuildAvailable)
        {
            result.Add(new RuntimeBuildFlavor(
                "parux-blackwell-native",
                "PaRuX native Blackwell",
                RuntimeBuildTrust.PaRuXVerified,
                "120a-real",
                new Dictionary<string, string>(),
                BlackwellOnly: true,
                AllowAutomaticPromotion: true,
                Rationale: "PaRuX-built/verified native sm_120a candidate; promotion still requires full performance and stability evidence."));
        }

        if (includeExperimentalThirdParty)
        {
            result.Add(new RuntimeBuildFlavor(
                "experimental-third-party-sm120",
                "Experimental third-party sm_120",
                RuntimeBuildTrust.ExperimentalThirdParty,
                "120a-real",
                new Dictionary<string, string>(),
                BlackwellOnly: true,
                AllowAutomaticPromotion: false,
                Rationale: "Benchmark-only. Third-party executable provenance is not sufficient for automatic PaRuX promotion."));
        }
        return result;
    }

    public static bool CanAutoPromote(RuntimeBuildFlavor flavor, bool stabilityPassed, bool qualityPassed)
    {
        ArgumentNullException.ThrowIfNull(flavor);
        return flavor.AllowAutomaticPromotion
            && flavor.Trust is RuntimeBuildTrust.PaRuXVerified or RuntimeBuildTrust.UpstreamVerified
            && stabilityPassed
            && qualityPassed;
    }

    public static void ApplyEnvironment(System.Diagnostics.ProcessStartInfo start, RuntimeBuildFlavor flavor)
    {
        foreach ((string key, string value) in flavor.Environment)
            start.Environment[key] = value;
    }
}
