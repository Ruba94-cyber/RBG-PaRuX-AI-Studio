using System.Security.Cryptography;
using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>Current host/runtime attestation for one text backend, in evidence-identity form.</summary>
internal sealed record CandidateLaunchHost(string Backend, string HardwareFingerprint, string RuntimeFingerprint,
    string BinaryFingerprint);

internal static class CandidateLaunchHostAttestation
{
    /// <summary>
    /// Attests the hardware (hw2), runtime tag and runtime dependency bytes (rtv2) for
    /// <paramref name="backend"/>. Returns null when any part is unavailable: an identity that cannot
    /// distinguish a driver/OS/runtime change is never used as evidence or admission identity.
    /// Hashes runtime files (cached by file identity), never model files; not for the request hot path.
    /// </summary>
    internal static async Task<CandidateLaunchHost?> CaptureAsync(string backend, GpuInventoryStatus inventory,
        CancellationToken cancellationToken)
    {
        string? directory = backend switch
        {
            "text-cuda" => LocalAiPaths.RuntimeCudaPath,
            "text-vulkan" => LocalAiPaths.RuntimeVulkanPath,
            _ => null
        };
        if (directory is null) return null;
        string executable = Path.Combine(directory, LocalAiPaths.LlamaServerExecutable);
        if (!File.Exists(executable)) return null;
        EvaluationHardwareAttestation hardware = EvaluationHardwareFingerprint.Build(inventory,
            HostEvaluationFacts.Current(), backend);
        if (!hardware.Complete) return null;
        string binarySha;
        await using (var file = new FileStream(executable, FileMode.Open, FileAccess.Read, FileShare.Read,
            1024 * 1024, useAsync: true))
        {
            binarySha = Convert.ToHexString(await SHA256.HashDataAsync(file, cancellationToken)
                .ConfigureAwait(false)).ToLowerInvariant();
        }
        string dependencies = await RuntimeDependencyFingerprint.ComputeAsync(directory, binarySha, cancellationToken)
            .ConfigureAwait(false);
        return new CandidateLaunchHost(backend, hardware.Fingerprint,
            backend + ":" + (LocalAiPaths.GetActiveRuntimeBuildTag(backend) ?? LocalAiPaths.DefaultRuntimeBuildTag(backend)),
            dependencies);
    }
}

internal enum CandidateLaunchScopeOutcome
{
    Admissible,
    EvidenceEnvironmentUnavailable,
    EvaluationLaunchNotActual,
    QualityPerformanceLaunchMismatch,
    ActualLaunchUnavailable,
    LaunchModelMismatch,
    LaunchConfigurationMismatch,
    HostAttestationUnavailable,
    BackendMismatch,
    HardwareChanged,
    RuntimeChanged,
    RuntimeDependenciesChanged
}

internal sealed record CandidateLaunchScopeResult(CandidateLaunchScopeOutcome Outcome, string Reason,
    CandidateLaunchAdmission? Admission = null)
{
    internal bool Admissible => Outcome == CandidateLaunchScopeOutcome.Admissible && Admission is not null;
}

/// <summary>
/// The facts an admissible scope check established at one instant. Durable intent requires one, matched
/// against the proof and re-validated against a fresh in-memory fence read inside the store's CAS.
/// </summary>
internal sealed record CandidateLaunchAdmission(string ConfigurationFingerprint, string Backend,
    string HardwareFingerprint, string RuntimeFingerprint, string BinaryFingerprint, string ModelPath,
    DateTime CheckedUtc);

/// <summary>Actual-launch inputs for scope checks, separated so tests can supply exact facts.</summary>
internal interface ICandidateLaunchFenceSource
{
    /// <summary>Synchronous, in-memory: the verified live launch descriptor, or null.</summary>
    TextLaunchDescriptor? ReadVerifiedFence();
    Task<CandidateLaunchHost?> CaptureHostAsync(string backend, CancellationToken cancellationToken);
    /// <summary>The planned exact standard-role launch for a model file (planning only).</summary>
    Task<TextLaunchDescriptorResult> PlanAsync(string modelPath, CancellationToken cancellationToken);
    /// <summary>Synchronous: the live text process (PID + start time verified) serves exactly these model bytes.</summary>
    bool IsLiveExactModel(string modelPath, string expectedSha256);
}

internal sealed class ProcessCandidateLaunchFenceSource : ICandidateLaunchFenceSource
{
    private readonly LocalAiProcessManager _process;

    internal ProcessCandidateLaunchFenceSource(LocalAiProcessManager process) => _process = process;

    public TextLaunchDescriptor? ReadVerifiedFence() => _process.GetVerifiedCurrentTextLaunchDescriptor();

    public Task<CandidateLaunchHost?> CaptureHostAsync(string backend, CancellationToken cancellationToken) =>
        CandidateLaunchHostAttestation.CaptureAsync(backend, new GpuInventoryService().GetStatus(), cancellationToken);

    public bool IsLiveExactModel(string modelPath, string expectedSha256) =>
        _process.GetTextProcessId() is not null
        && _process.IsActiveTextModelIdentity(true, modelPath, _process.GetActiveTextModelPath(), expectedSha256);

    public Task<TextLaunchDescriptorResult> PlanAsync(string modelPath, CancellationToken cancellationToken) =>
        _process.PlanFreshExactTextLaunchAsync(modelPath,
            RuntimeManagerService.RoleDisplayLabel(CandidateGeneralChatQualitySuite.RouteRole), cancellationToken);
}

/// <summary>
/// The evaluator/AUTO equivalence rule: <b>exact equality only</b>. Evidence is admissible for an
/// actual launch only when its quality and performance runs were both measured under an ld1 launch
/// descriptor, those two descriptors are identical, the actual (or planned) launch has that same
/// descriptor, and the current host hardware, runtime tag and runtime dependency bytes equal the
/// evidence identity. No relaxed "close enough" rule exists: the historical fixed evaluator profile
/// (ctx 2048, one slot, reasoning off) is never equivalent to an AUTO launch, so evidence produced
/// under it disables recommendation admission and the ordinary ranker remains in charge.
/// Pressure, Gaming and STOP are deliberately not part of this static identity; they stay fresh
/// admission checks at the moment of a transition.
/// </summary>
internal static class CandidateLaunchScope
{
    internal static bool IsActualLaunchEvidence(string? qualityConfiguration, string? performanceConfiguration) =>
        TextLaunchDescriptor.IsActualLaunchFingerprint(qualityConfiguration)
        && string.Equals(qualityConfiguration, performanceConfiguration, StringComparison.Ordinal);

    /// <param name="expectedModelPath">The exact managed file the evidence belongs to. The descriptor's
    /// configuration fingerprint excludes the model, so the launch must also be serving this file;
    /// byte identity is a separate exact-identity check owned by the caller.</param>
    internal static CandidateLaunchScopeResult Check(CandidateProofEnvironment? quality,
        CandidateProofEnvironment? performance, TextLaunchDescriptor? launch, string? expectedModelPath,
        CandidateLaunchHost? host)
    {
        if (quality is null || performance is null)
            return Refuse(CandidateLaunchScopeOutcome.EvidenceEnvironmentUnavailable, "evidence_environment_unavailable");
        if (!TextLaunchDescriptor.IsActualLaunchFingerprint(quality.ConfigurationFingerprint)
            || !TextLaunchDescriptor.IsActualLaunchFingerprint(performance.ConfigurationFingerprint))
            return Refuse(CandidateLaunchScopeOutcome.EvaluationLaunchNotActual, "evaluation_launch_profile_not_actual");
        if (quality.ConfigurationFingerprint != performance.ConfigurationFingerprint)
            return Refuse(CandidateLaunchScopeOutcome.QualityPerformanceLaunchMismatch, "quality_performance_launch_mismatch");
        if (launch is null || launch.Version != TextLaunchDescriptor.CurrentVersion
            || !TextLaunchDescriptor.IsActualLaunchFingerprint(launch.Fingerprint))
            return Refuse(CandidateLaunchScopeOutcome.ActualLaunchUnavailable, "actual_launch_unavailable");
        if (!SamePath(launch.ModelPath, expectedModelPath))
            return Refuse(CandidateLaunchScopeOutcome.LaunchModelMismatch, "launch_model_mismatch");
        if (launch.Fingerprint != quality.ConfigurationFingerprint)
            return Refuse(CandidateLaunchScopeOutcome.LaunchConfigurationMismatch, "launch_configuration_mismatch");
        if (host is null)
            return Refuse(CandidateLaunchScopeOutcome.HostAttestationUnavailable, "host_attestation_unavailable");
        if (host.Backend != launch.Backend
            || !quality.RuntimeFingerprint.StartsWith(launch.Backend + ":", StringComparison.Ordinal)
            || !performance.RuntimeFingerprint.StartsWith(launch.Backend + ":", StringComparison.Ordinal))
            return Refuse(CandidateLaunchScopeOutcome.BackendMismatch, "backend_mismatch");
        if (quality.HardwareFingerprint != host.HardwareFingerprint
            || performance.HardwareFingerprint != host.HardwareFingerprint)
            return Refuse(CandidateLaunchScopeOutcome.HardwareChanged, "hardware_changed");
        if (quality.RuntimeFingerprint != host.RuntimeFingerprint
            || performance.RuntimeFingerprint != host.RuntimeFingerprint)
            return Refuse(CandidateLaunchScopeOutcome.RuntimeChanged, "runtime_changed");
        if (quality.BinaryFingerprint != host.BinaryFingerprint
            || performance.BinaryFingerprint != host.BinaryFingerprint)
            return Refuse(CandidateLaunchScopeOutcome.RuntimeDependenciesChanged, "runtime_dependencies_changed");
        return new CandidateLaunchScopeResult(CandidateLaunchScopeOutcome.Admissible, "admissible",
            new CandidateLaunchAdmission(launch.Fingerprint, launch.Backend, host.HardwareFingerprint,
                host.RuntimeFingerprint, host.BinaryFingerprint, launch.ModelPath, DateTime.UtcNow));
    }

    internal static bool SamePath(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;
        try
        {
            return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static CandidateLaunchScopeResult Refuse(CandidateLaunchScopeOutcome outcome, string reason) =>
        new(outcome, reason);
}
