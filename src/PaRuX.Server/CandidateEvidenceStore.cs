using System.Text;
using System.Text.Json;
using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>Atomic, bounded, local Candidate evidence ledger. Invalid existing data is never replaced.</summary>
internal sealed class CandidateEvidenceStore
{
    internal const string FileName = "candidate-evidence.json";
    internal const int MaximumRecordsPerKind = 128;
    private const int MaximumDocumentBytes = 2 * 1024 * 1024;
    private const int MaximumCases = 256;
    private const int MaximumMetrics = 32;
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        MaxDepth = 32
    };
    private readonly string _path;

    internal CandidateEvidenceStore() : this(ServerState.StateDirectory) { }

    internal CandidateEvidenceStore(string stateDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDirectory);
        _path = Path.Combine(stateDirectory, FileName);
    }

    /// <summary>Runs a synchronous snapshot/CAS callback under the ledger gate (no writer can interleave).</summary>
    internal static T WithGate<T>(Func<T> body) { lock (Gate) return body(); }

    internal CandidateEvidenceLedger Load()
    {
        lock (Gate) return LoadCore();
    }

    internal void AddQuality(CandidateQualityEvidence evidence)
    {
        Validate(evidence);
        lock (Gate)
        {
            CandidateEvidenceLedger ledger = LoadCore();
            ledger.Quality.Insert(0, evidence);
            ledger.Quality = ledger.Quality.OrderByDescending(item => item.RecordedUtc)
                .Take(MaximumRecordsPerKind).ToList();
            SaveCore(ledger);
        }
    }

    internal void AddPerformance(CandidatePerformanceEvidence evidence)
    {
        Validate(evidence);
        lock (Gate)
        {
            CandidateEvidenceLedger ledger = LoadCore();
            ledger.Performance.Insert(0, evidence);
            ledger.Performance = ledger.Performance.OrderByDescending(item => item.RecordedUtc)
                .Take(MaximumRecordsPerKind).ToList();
            SaveCore(ledger);
        }
    }

    private CandidateEvidenceLedger LoadCore()
    {
        if (!File.Exists(_path)) return new CandidateEvidenceLedger();
        try
        {
            if (File.GetAttributes(_path).HasFlag(FileAttributes.ReparsePoint)
                || new FileInfo(_path).Length > MaximumDocumentBytes)
                throw new InvalidDataException("The Candidate evidence ledger is unsafe or oversized.");
            string json = File.ReadAllText(_path);
            using JsonDocument document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("schemaVersion", out _)
                || !document.RootElement.TryGetProperty("quality", out _)
                || !document.RootElement.TryGetProperty("performance", out _))
                throw new InvalidDataException("The Candidate evidence ledger lacks required fields.");
            CandidateEvidenceLedger? ledger = JsonSerializer.Deserialize<CandidateEvidenceLedger>(json, JsonOptions);
            Validate(ledger);
            return ledger!;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            throw new InvalidDataException("The Candidate evidence ledger is malformed.", ex);
        }
    }

    private void SaveCore(CandidateEvidenceLedger ledger)
    {
        Validate(ledger);
        string json = JsonSerializer.Serialize(ledger, JsonOptions);
        if (Encoding.UTF8.GetByteCount(json) > MaximumDocumentBytes)
            throw new InvalidDataException("The Candidate evidence ledger exceeds its size limit.");
        string directory = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);
        string temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, json);
            File.Move(temporary, _path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    internal static void Validate(CandidateEvidenceLedger? ledger)
    {
        if (ledger is null || ledger.SchemaVersion != 1 || ledger.Quality is null || ledger.Performance is null
            || ledger.Quality.Count > MaximumRecordsPerKind || ledger.Performance.Count > MaximumRecordsPerKind)
            throw new InvalidDataException("The Candidate evidence ledger has invalid or unsupported data.");
        foreach (CandidateQualityEvidence item in ledger.Quality) Validate(item);
        foreach (CandidatePerformanceEvidence item in ledger.Performance) Validate(item);
        string[] qualityInvocationIds = ledger.Quality.Where(item => item.RecordVersion == 3)
            .Select(item => item.InvocationId).ToArray();
        if (qualityInvocationIds.Distinct(StringComparer.Ordinal).Count() != qualityInvocationIds.Length)
            throw new InvalidDataException("Candidate quality evidence contains duplicate invocation identities.");
    }

    internal static void Validate(CandidateQualityEvidence? item)
    {
        if (item is null || item.RecordVersion is not (1 or 2 or 3) || !ValidIdentity(item.Identity)
            || !ValidLabel(item.Capability, 96) || !ValidLabel(item.SuiteId, 128)
            || item.SuiteVersion <= 0 || !ValidSha(item.SuiteSha256)
            || item.ExpectedCaseCount is < 1 or > MaximumCases || item.Cases is null
            || item.Cases.Count > item.ExpectedCaseCount || !ValidUtc(item.RecordedUtc)
            || item.PassCount < 0 || item.FailCount < 0 || item.InconclusiveCount < 0 || item.ErrorCount < 0)
            throw new InvalidDataException("Invalid Candidate quality evidence.");

        // Version 1 predates scoped evidence identities. Keep it structurally readable as history;
        // qualification paths already require record version 3 and an exact current profile.
        CandidateEvaluationProfileDefinition profile = CandidateEvaluationProfileCatalog.ChatInstructionFollowingSmoke;
        if (item.RecordVersion is 2 or 3
            && !CandidateEvaluationProfileCatalog.TryResolveStoredQualityEvidence(item, out profile))
            throw new InvalidDataException("Candidate quality evidence uses an unknown suite scope.");

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (CandidateQualityCaseResult result in item.Cases)
        {
            if (result is null || !ValidLabel(result.CaseId, 128) || !ids.Add(result.CaseId)
                || result.Outcome is not ("pass" or "fail" or "inconclusive" or "error"))
                throw new InvalidDataException("Invalid Candidate quality case outcome.");
        }
        if (item.RecordVersion is 2 or 3)
        {
            if (!profile.MatchesQualitySuiteIdentity(item) || item.ExpectedCaseCount != profile.QualityExpectedCaseCount
                || item.ExecutionStatus is not ("Completed" or "Failed" or "Cancelled" or "Unavailable")
                || (item.ExecutionStatus == "Completed") != item.Completed
                || item.Cancelled != (item.ExecutionStatus == "Cancelled")
                || item.ExternalFallbackUsed
                || (item.ExecutionStatus != "Unavailable" && !ValidEnvironment(item.Environment))
                || (item.Environment is not null && (!ValidEnvironment(item.Environment)
                    || item.Environment.WorkloadFingerprint != profile.QualityWorkloadFingerprint))
                || item.Cases.Count > profile.QualityExpectedCaseCount)
                throw new InvalidDataException("Invalid v2 Candidate quality evidence.");
            for (int index = 0; index < item.Cases.Count; index++)
            {
                CandidateQualityCaseResult result = item.Cases[index];
                CandidateTextQualitySuite.Case expected = profile.QualityCases[index];
                if (result.CaseId != expected.Id || result.Critical != expected.Critical
                    || result.FinishReason is not ("stop" or "length" or "missing" or "invalid" or "error")
                    || (result.Outcome == "pass" && (result.FinishReason != "stop" || result.Truncated))
                    || (result.FinishReason == "length" && !result.Truncated))
                    throw new InvalidDataException("Invalid v2 Candidate quality case metadata.");
            }
            if (item.Completed && item.Cases.Count != profile.QualityExpectedCaseCount)
                throw new InvalidDataException("Completed Candidate quality run lacks cases.");
        }
        if (item.RecordVersion == 3
            && (!Guid.TryParseExact(item.InvocationId, "N", out _)
                || !profile.MatchesQualityProfile(item)
                || !ValidSha(item.QualityProfileSha256)))
            throw new InvalidDataException("Invalid v3 Candidate quality invocation or profile scope.");
        if (item.PassCount != item.Cases.Count(c => c.Outcome == "pass")
            || item.FailCount != item.Cases.Count(c => c.Outcome == "fail")
            || item.InconclusiveCount != item.Cases.Count(c => c.Outcome == "inconclusive")
            || item.ErrorCount != item.Cases.Count(c => c.Outcome == "error"))
            throw new InvalidDataException("Candidate quality case counts disagree.");
    }

    internal static void Validate(CandidatePerformanceEvidence? item)
    {
        CandidateEvaluationProfileDefinition profile = CandidateEvaluationProfileCatalog.ChatInstructionFollowingSmoke;
        if (item is null || item.RecordVersion is not (1 or 2 or 3) || !ValidIdentity(item.Identity)
            || !ValidLabel(item.Capability, 96) || item.Environment is null
            || (item.RecordVersion == 1 && !ValidEnvironment(item.Environment))
            || item.Repetitions is < 0 or > 100000 || item.TokenCount is < 0 or > 1000000000
            || item.Metrics is null || item.Metrics.Count > MaximumMetrics || !ValidUtc(item.RecordedUtc)
            || (item.RecordVersion == 1 && (item.Repetitions < 1 || item.TokenCount < 1 || item.Metrics.Count < 1)))
            throw new InvalidDataException("Invalid Candidate performance evidence.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (CandidateMeasuredMetric metric in item.Metrics)
        {
            if (metric is null || !ValidLabel(metric.Name, 96) || !ValidLabel(metric.Unit, 32)
                || !double.IsFinite(metric.Value) || metric.Value < 0 || !names.Add(metric.Name))
                throw new InvalidDataException("Invalid Candidate measured metric.");
        }
        if (item.RecordVersion is 2 or 3)
        {
            bool currentVersion = item.RecordVersion == 3;
            // Record v3 may carry the retained workload v2 identity (history only, never current).
            if (currentVersion) profile = CandidateEvaluationProfileCatalog.ResolveStoredPerformanceEvidence(item);
            if ((currentVersion && !profile.MatchesPerformance(item))
                || (!currentVersion && (item.WorkloadId != CandidateEvaluationScope.ChatInstructionFollowingPerformanceWorkloadId
                    || item.WorkloadVersion != 1 || item.ExpectedRepetitions != CandidateTextPerformanceWorkload.Repetitions))
                || (item.ExecutionStatus != "Unavailable" && (!ValidEnvironment(item.Environment)
                    || (currentVersion && item.Environment.WorkloadFingerprint != profile.PerformanceWorkloadFingerprint)))
                || (currentVersion && ValidEnvironment(item.Environment)
                    && item.Environment.WorkloadFingerprint != profile.PerformanceWorkloadFingerprint)
                || item.ExecutionStatus is not ("Completed" or "Failed" or "Cancelled" or "Unavailable")
                || item.Completed != (item.ExecutionStatus == "Completed")
                || item.Cancelled != (item.ExecutionStatus == "Cancelled")
                || item.ExternalFallbackUsed
                || (item.FailureReason is not null && (item.ExecutionStatus != "Failed"
                    || item.FailureReason.Length is < 1 or > 48
                    || !item.FailureReason.All(c => c is (>= 'a' and <= 'z') or '_')))
                || item.Samples is null || item.Samples.Count > item.ExpectedRepetitions
                || item.Repetitions != item.Samples.Count
                || item.TokenCount != item.Samples.Sum(s => (long)(s?.GeneratedTokens ?? 0)))
                throw new InvalidDataException("Invalid v2 Candidate performance evidence.");
            for (int index = 0; index < item.Samples.Count; index++)
            {
                CandidatePerformanceSample sample = item.Samples[index];
                if (sample is null || sample.Repetition != index + 1 || sample.Outcome != "pass"
                    || (sample.FinishReason != "stop"
                        && !(currentVersion && sample.FinishReason == "length"
                            && sample.GeneratedTokens == profile.MaximumPerformanceGeneratedTokens))
                    || sample.PromptTokens is <= 0
                    || sample.PromptTokens > profile.MaximumPerformancePromptTokens
                    || sample.GeneratedTokens is <= 0
                    || sample.GeneratedTokens > profile.MaximumPerformanceGeneratedTokens
                    || !Positive(sample.PromptMilliseconds) || !Positive(sample.DecodeMilliseconds)
                    || !Positive(sample.CompletionMilliseconds)
                    || (currentVersion && (!Positive(sample.TimeToFirstTokenMilliseconds)
                        || sample.TimeToFirstTokenMilliseconds > sample.CompletionMilliseconds)))
                    throw new InvalidDataException("Invalid v2 Candidate performance sample.");
            }
            if (item.Completed && (item.Truncated || item.Samples.Count != item.ExpectedRepetitions
                || item.TokenCount <= 0 || item.Metrics.Count != (currentVersion ? profile.PerformanceMetrics.Count : 3)
                || !HasMetric(item, profile.PerformanceMetrics[0].Name, profile.PerformanceMetrics[0].Unit,
                    1000d * item.Samples.Sum(s => s.PromptTokens) / item.Samples.Sum(s => s.PromptMilliseconds))
                || !HasMetric(item, profile.PerformanceMetrics[1].Name, profile.PerformanceMetrics[1].Unit,
                    1000d * item.TokenCount / item.Samples.Sum(s => s.DecodeMilliseconds))
                || !HasMetric(item, profile.PerformanceMetrics[2].Name, profile.PerformanceMetrics[2].Unit,
                    item.Samples.Average(s => s.CompletionMilliseconds))
                || (currentVersion && !HasMetric(item, CandidatePerformanceMetric.TimeToFirstTokenMillisecondsId,
                    CandidatePerformanceMetric.MillisecondsUnit, item.Samples.Average(s => s.TimeToFirstTokenMilliseconds)))))
                throw new InvalidDataException("Incomplete v2 Candidate performance evidence.");
        }
    }

    private static bool Positive(double value) => double.IsFinite(value) && value > 0;

    private static bool HasMetric(CandidatePerformanceEvidence item, string name, string unit, double expected) =>
        Positive(expected) && item.Metrics.Any(m => m.Name == name && m.Unit == unit && Positive(m.Value)
            && Math.Abs(m.Value - expected) <= Math.Max(1e-9, expected * 1e-9));

    internal static bool ValidIdentity(CandidateEvidenceIdentity? identity) => identity is not null
        && identity.Provider == ModelDiscoveryIdentity.Provider
        && ModelDiscoveryIdentity.IsSafeRepoId(identity.RepoId)
        && !string.IsNullOrWhiteSpace(identity.Revision)
        && ModelRevisionIdentity.Normalize(identity.Revision) == identity.Revision
        && ModelDiscoveryIdentity.IsSafeRepoFilePath(identity.RepoFilePath)
        && identity.CandidateKey == ModelDiscoveryIdentity.Key(identity.RepoId, identity.Revision, identity.RepoFilePath)
        && identity.PinnedSourceUrl == ModelDiscoveryIdentity.PinnedUrl(identity.RepoId, identity.Revision, identity.RepoFilePath)
        && ModelDownloadProvenanceManifestStore.IsSafeFileName(identity.ManagedFileName)
        && identity.ManagedFileName == identity.RepoFilePath.Split('/')[^1]
        && identity.SizeBytes >= 0 && ValidSha(identity.LocalSha256);

    internal static bool ValidEnvironment(CandidateEvaluationEnvironment? value) => value is not null
        && ValidFingerprint(value.HardwareFingerprint) && ValidFingerprint(value.RuntimeFingerprint)
        && ValidFingerprint(value.BinaryFingerprint) && ValidFingerprint(value.ConfigurationFingerprint)
        && ValidFingerprint(value.WorkloadFingerprint);

    private static bool ValidFingerprint(string? value) => ValidLabel(value, 256)
        && !string.Equals(value, "unknown", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(value, "unavailable", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(value, "none", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(value, "n/a", StringComparison.OrdinalIgnoreCase);

    private static bool ValidLabel(string? value, int max) => !string.IsNullOrWhiteSpace(value)
        && value.Length <= max && value == value.Trim() && value.All(c => !char.IsControl(c));

    private static bool ValidSha(string? value) => value is { Length: 64 }
        && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool ValidUtc(DateTime value) => value != default && value.Kind == DateTimeKind.Utc;
}
