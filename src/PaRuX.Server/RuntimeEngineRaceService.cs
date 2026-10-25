using System.Collections.Concurrent;

namespace PaRuX.Server;

public sealed record RuntimeEngineBenchmarkMeasurement(
    double PromptTokensPerSecond,
    double DecodeTokensPerSecond,
    double TimeToFirstTokenMilliseconds,
    double SustainedDecodeTokensPerSecond,
    double PeakVramMib,
    double MaxGpuTemperatureC,
    double ErrorRate = 0,
    double? LoadMilliseconds = null,
    double? AverageGpuPowerWatts = null,
    string? ExecutionDeviceIdentity = null,
    string? ExecutionDeviceEvidence = null,
    IReadOnlyList<string>? MissingTelemetry = null);

public interface IRuntimeEngineBenchmarkAdapter
{
    string EngineId { get; }
    /// <summary>Exact runtime/pack identity measured by this adapter. A caller may bind the race
    /// to this value so a side-by-side installed update cannot silently change the candidate.</summary>
    string RuntimeVersion => "unknown";
    /// <summary>
    /// Identity of one benchmark competitor. EngineId is the engine family; RuntimeVersion is the
    /// exact build/provider identity inside that family. Keeping those concepts separate allows,
    /// for example, OpenVINO and VitisAI Windows ML providers to race without overwriting each
    /// other in the registry.
    /// </summary>
    string CompetitionId => EngineId.Trim() + "|" + RuntimeVersion.Trim();
    bool CanBenchmark(ManagedModelVariantIdentity variant);
    Task<RuntimeEngineBenchmarkMeasurement> MeasureAsync(
        ManagedModelVariantIdentity variant,
        string workload,
        bool sustainedValidation,
        CancellationToken cancellationToken);
}

public sealed record RuntimeEngineRaceRequest(
    string BaselineEngineId,
    ManagedModelVariantIdentity BaselineVariant,
    string CandidateEngineId,
    ManagedModelVariantIdentity CandidateVariant,
    string Workload,
    bool SameWeightsAndQualityComparable,
    double? BaselineQualityScore,
    double? CandidateQualityScore,
    bool SustainedValidation = true,
    string? BaselineEngineVersion = null,
    string? CandidateEngineVersion = null);

public sealed class RuntimeEngineRaceResult
{
    public DateTime MeasuredUtc { get; set; } = DateTime.UtcNow;
    public string BaselineEngineId { get; set; } = string.Empty;
    public string CandidateEngineId { get; set; } = string.Empty;
    public string BaselineEngineVersion { get; set; } = string.Empty;
    public string CandidateEngineVersion { get; set; } = string.Empty;
    public RuntimeEngineBenchmarkMeasurement? BaselineMeasurement { get; set; }
    public RuntimeEngineBenchmarkMeasurement? CandidateMeasurement { get; set; }
    public RuntimePromotionDecision? Promotion { get; set; }
    public string Status { get; set; } = "running";
    public string? Error { get; set; }
}

/// <summary>
/// Provider-neutral Engine Race. Adapters are measured sequentially under the existing process-wide
/// RuntimePerformanceCoordinator so two engines never accidentally benchmark against each other
/// while competing for the same GPU. Quality evidence is deliberately external to the speed probe:
/// when comparable quality/weights are not confirmed the race can still report measurements but
/// automatic promotion is fail-closed. Compact results are appended to EngineRaceEvidenceStore so
/// observability can compare real runtime evidence across time without persisting model weights.
///
/// The registry is keyed by competitor identity (engine family + exact runtime/provider), not only
/// EngineId. This is important for provider races within one engine family such as Windows ML.
/// </summary>
public sealed class RuntimeEngineRaceService
{
    private readonly ConcurrentDictionary<string, IRuntimeEngineBenchmarkAdapter> _adapters =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly RuntimePromotionPolicyOptions _policy;

    public RuntimeEngineRaceService(
        IEnumerable<IRuntimeEngineBenchmarkAdapter>? adapters = null,
        RuntimePromotionPolicyOptions? policy = null)
    {
        _policy = policy ?? new RuntimePromotionPolicyOptions();
        if (adapters == null) return;
        foreach (IRuntimeEngineBenchmarkAdapter adapter in adapters) Register(adapter);
    }

    public void Register(IRuntimeEngineBenchmarkAdapter adapter)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        if (string.IsNullOrWhiteSpace(adapter.EngineId))
            throw new ArgumentException("Engine adapter id is required.", nameof(adapter));
        if (string.IsNullOrWhiteSpace(adapter.RuntimeVersion))
            throw new ArgumentException("Engine adapter runtime version is required.", nameof(adapter));
        if (string.IsNullOrWhiteSpace(adapter.CompetitionId))
            throw new ArgumentException("Engine adapter competition identity is required.", nameof(adapter));
        _adapters[adapter.CompetitionId.Trim()] = adapter;
    }

    /// <summary>Registered engine families. Several competitors can intentionally share one id.</summary>
    public IReadOnlyList<string> RegisteredEngineIds =>
        _adapters.Values
            .Select(adapter => adapter.EngineId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>Exact competitor identities currently available to the race.</summary>
    public IReadOnlyList<string> RegisteredCompetitorIds =>
        _adapters.Keys.Order(StringComparer.OrdinalIgnoreCase).ToList();

    public async Task<RuntimeEngineRaceResult> RunAsync(RuntimeEngineRaceRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var result = new RuntimeEngineRaceResult
        {
            BaselineEngineId = request.BaselineEngineId,
            CandidateEngineId = request.CandidateEngineId
        };

        if (!TryResolveAdapter(request.BaselineEngineId, request.BaselineEngineVersion,
                out IRuntimeEngineBenchmarkAdapter? baselineAdapter, out string? baselineError))
            return PersistBestEffort(request, Fail(result, baselineError!));
        if (!TryResolveAdapter(request.CandidateEngineId, request.CandidateEngineVersion,
                out IRuntimeEngineBenchmarkAdapter? candidateAdapter, out string? candidateError))
            return PersistBestEffort(request, Fail(result, candidateError!));

        if (string.Equals(baselineAdapter!.CompetitionId, candidateAdapter!.CompetitionId, StringComparison.OrdinalIgnoreCase))
            return PersistBestEffort(request, Fail(result,
                "Engine Race requires two distinct competitor identities; baseline and candidate resolve to "
                + baselineAdapter.CompetitionId + "."));

        result.BaselineEngineVersion = baselineAdapter.RuntimeVersion;
        result.CandidateEngineVersion = candidateAdapter.RuntimeVersion;
        if (!VersionMatches(request.BaselineEngineVersion, baselineAdapter.RuntimeVersion))
            return PersistBestEffort(request, Fail(result,
                $"Baseline engine version changed before the race: requested {request.BaselineEngineVersion}, resolved {baselineAdapter.RuntimeVersion}."));
        if (!VersionMatches(request.CandidateEngineVersion, candidateAdapter.RuntimeVersion))
            return PersistBestEffort(request, Fail(result,
                $"Candidate engine version changed before the race: requested {request.CandidateEngineVersion}, resolved {candidateAdapter.RuntimeVersion}."));
        if (!baselineAdapter.CanBenchmark(request.BaselineVariant))
            return PersistBestEffort(request, Fail(result, "Baseline engine cannot benchmark the selected model variant."));
        if (!candidateAdapter.CanBenchmark(request.CandidateVariant))
            return PersistBestEffort(request, Fail(result, "Candidate engine cannot benchmark the selected model variant."));

        await using IAsyncDisposable lane = await RuntimePerformanceCoordinator.Shared
            .AcquireAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            result.BaselineMeasurement = await baselineAdapter.MeasureAsync(
                request.BaselineVariant, request.Workload, request.SustainedValidation, cancellationToken).ConfigureAwait(false);
            ValidateMeasurement(result.BaselineMeasurement, "baseline");
            result.CandidateMeasurement = await candidateAdapter.MeasureAsync(
                request.CandidateVariant, request.Workload, request.SustainedValidation, cancellationToken).ConfigureAwait(false);
            ValidateMeasurement(result.CandidateMeasurement, "candidate");

            bool comparable = request.SameWeightsAndQualityComparable
                && request.BaselineQualityScore.HasValue
                && request.CandidateQualityScore.HasValue;

            RuntimeCandidateEvidence baseline = Evidence(
                request.BaselineEngineId,
                baselineAdapter.RuntimeVersion,
                request.BaselineVariant,
                result.BaselineMeasurement,
                request.BaselineQualityScore ?? 0,
                comparable);
            RuntimeCandidateEvidence candidate = Evidence(
                request.CandidateEngineId,
                candidateAdapter.RuntimeVersion,
                request.CandidateVariant,
                result.CandidateMeasurement,
                request.CandidateQualityScore ?? 0,
                comparable);

            result.Promotion = RuntimeCandidatePromotionPolicy.Evaluate(baseline, candidate, _policy);
            result.Status = "completed";
            return PersistBestEffort(request, result);
        }
        catch (OperationCanceledException)
        {
            result.Status = "cancelled";
            // A cancelled race may contain only one side of the comparison; do not persist an
            // incomplete record as if it were comparable benchmark evidence.
            throw;
        }
        catch (Exception ex)
        {
            return PersistBestEffort(request, Fail(result, ex.Message));
        }
    }

    private bool TryResolveAdapter(
        string engineId,
        string? requestedRuntimeVersion,
        out IRuntimeEngineBenchmarkAdapter? adapter,
        out string? error)
    {
        adapter = null;
        error = null;
        string normalizedEngineId = (engineId ?? string.Empty).Trim();
        List<IRuntimeEngineBenchmarkAdapter> candidates = _adapters.Values
            .Where(value => string.Equals(value.EngineId, normalizedEngineId, StringComparison.OrdinalIgnoreCase))
            .OrderBy(value => value.CompetitionId, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (candidates.Count == 0)
        {
            error = "Engine adapter is not registered: " + normalizedEngineId;
            return false;
        }

        if (!string.IsNullOrWhiteSpace(requestedRuntimeVersion))
        {
            string requested = requestedRuntimeVersion.Trim();
            List<IRuntimeEngineBenchmarkAdapter> matches = candidates
                .Where(value => string.Equals(value.RuntimeVersion, requested, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (matches.Count == 1)
            {
                adapter = matches[0];
                return true;
            }
            error = matches.Count == 0
                ? "Engine adapter runtime/provider identity is not registered: " + normalizedEngineId + " | " + requested
                : "Engine adapter runtime/provider identity is ambiguous: " + normalizedEngineId + " | " + requested;
            return false;
        }

        if (candidates.Count == 1)
        {
            adapter = candidates[0];
            return true;
        }

        error = "Multiple competitor identities are registered for engine " + normalizedEngineId
            + "; pin an exact runtime/provider identity for this side of the race.";
        return false;
    }

    private static RuntimeCandidateEvidence Evidence(
        string engineId,
        string runtimeVersion,
        ManagedModelVariantIdentity variant,
        RuntimeEngineBenchmarkMeasurement measurement,
        double quality,
        bool comparable) =>
        new(
            CandidateId: engineId + "|" + runtimeVersion + "|" + variant.StableKey,
            Comparable: comparable,
            QualityScore: quality,
            TimeToFirstTokenMilliseconds: measurement.TimeToFirstTokenMilliseconds,
            PromptTokensPerSecond: measurement.PromptTokensPerSecond,
            DecodeTokensPerSecond: measurement.DecodeTokensPerSecond,
            SustainedDecodeTokensPerSecond: measurement.SustainedDecodeTokensPerSecond,
            PeakVramMib: measurement.PeakVramMib,
            MaxGpuTemperatureC: measurement.MaxGpuTemperatureC,
            ErrorRate: measurement.ErrorRate,
            EngineId: engineId,
            RuntimeBuild: runtimeVersion,
            ModelVariantId: variant.StableKey,
            LoadMilliseconds: measurement.LoadMilliseconds ?? 0,
            AverageGpuPowerWatts: measurement.AverageGpuPowerWatts ?? 0);

    private static void ValidateMeasurement(RuntimeEngineBenchmarkMeasurement value, string label)
    {
        ArgumentNullException.ThrowIfNull(value);
        Positive(value.PromptTokensPerSecond, label + " prompt throughput");
        Positive(value.DecodeTokensPerSecond, label + " decode throughput");
        Positive(value.TimeToFirstTokenMilliseconds, label + " TTFT");
        Positive(value.SustainedDecodeTokensPerSecond, label + " sustained decode throughput");
        NonNegative(value.PeakVramMib, label + " peak VRAM");
        NonNegative(value.MaxGpuTemperatureC, label + " GPU temperature");
        if (!double.IsFinite(value.ErrorRate) || value.ErrorRate is < 0 or > 1)
            throw new InvalidDataException(label + " runtime error rate is invalid.");
        if (value.LoadMilliseconds.HasValue) NonNegative(value.LoadMilliseconds.Value, label + " load time");
        if (value.AverageGpuPowerWatts.HasValue) NonNegative(value.AverageGpuPowerWatts.Value, label + " GPU power");
        if (value.MissingTelemetry != null
            && value.MissingTelemetry.Any(field => string.IsNullOrWhiteSpace(field)))
            throw new InvalidDataException(label + " missing-telemetry evidence contains an empty field name.");
    }

    private static void Positive(double value, string label)
    {
        if (!double.IsFinite(value) || value <= 0)
            throw new InvalidDataException(label + " must be finite and positive.");
    }

    private static void NonNegative(double value, string label)
    {
        if (!double.IsFinite(value) || value < 0)
            throw new InvalidDataException(label + " must be finite and non-negative.");
    }

    private static bool VersionMatches(string? requested, string actual) =>
        string.IsNullOrWhiteSpace(requested)
        || string.Equals(requested.Trim(), actual, StringComparison.OrdinalIgnoreCase);

    private static RuntimeEngineRaceResult Fail(RuntimeEngineRaceResult result, string error)
    {
        result.Status = "failed";
        result.Error = error;
        return result;
    }

    private static RuntimeEngineRaceResult PersistBestEffort(RuntimeEngineRaceRequest request, RuntimeEngineRaceResult result)
    {
        try
        {
            // Early preflight failures contain no measurements and are not benchmark evidence.
            if (result.BaselineMeasurement != null || result.CandidateMeasurement != null)
                _ = EngineRaceEvidenceStore.Current.RecordRuntime(request, result);
        }
        catch { /* history persistence never changes the measured race result */ }
        return result;
    }
}