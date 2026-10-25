using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>Atomic, bounded persistence for sanitized Candidate evaluation job summaries.</summary>
internal sealed class CandidateEvaluationJobStore
{
    internal const string FileName = "candidate-evaluation-jobs.json";
    internal const int MaximumJobs = 33; // one active job plus 32 terminal summaries
    private const int MaximumDocumentBytes = 256 * 1024;
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        MaxDepth = 16,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    private readonly string _path;

    internal CandidateEvaluationJobStore() : this(ServerState.StateDirectory) { }

    internal CandidateEvaluationJobStore(string stateDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDirectory);
        _path = Path.Combine(stateDirectory, FileName);
    }

    internal List<CandidateEvaluationJobStatus> Load()
    {
        lock (Gate)
        {
            if (!File.Exists(_path)) return new List<CandidateEvaluationJobStatus>();
            try
            {
                FileInfo info = new(_path);
                if (File.GetAttributes(_path).HasFlag(FileAttributes.ReparsePoint)
                    || info.Length > MaximumDocumentBytes)
                    throw new InvalidDataException("Candidate evaluation job history is unsafe or oversized.");

                string json = File.ReadAllText(_path);
                using JsonDocument document = JsonDocument.Parse(json);
                if (document.RootElement.ValueKind != JsonValueKind.Object
                    || !document.RootElement.TryGetProperty("schemaVersion", out JsonElement version)
                    || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out int schemaVersion)
                    || schemaVersion != 1
                    || !document.RootElement.TryGetProperty("jobs", out _))
                    throw new InvalidDataException("Candidate evaluation job history has an unsupported schema.");

                CandidateEvaluationJobLedger? ledger = JsonSerializer.Deserialize<CandidateEvaluationJobLedger>(json, JsonOptions);
                Validate(ledger);
                return ledger!.Jobs;
            }
            catch (InvalidDataException) { throw; }
            catch (Exception ex) when (ex is JsonException or NotSupportedException or IOException or UnauthorizedAccessException)
            {
                throw new InvalidDataException("Candidate evaluation job history is malformed or unavailable.", ex);
            }
        }
    }

    internal void Save(IReadOnlyCollection<CandidateEvaluationJobStatus> jobs)
    {
        var ledger = new CandidateEvaluationJobLedger { Jobs = jobs.ToList() };
        Validate(ledger);
        string json = JsonSerializer.Serialize(ledger, JsonOptions);
        if (Encoding.UTF8.GetByteCount(json) > MaximumDocumentBytes)
            throw new InvalidDataException("Candidate evaluation job history exceeds its size limit.");

        lock (Gate)
        {
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
    }

    internal static void Validate(CandidateEvaluationJobLedger? ledger)
    {
        if (ledger is null || ledger.SchemaVersion != 1 || ledger.Jobs is null
            || ledger.Jobs.Count > MaximumJobs)
            throw new InvalidDataException("Candidate evaluation job history has invalid or unsupported data.");

        var ids = new HashSet<string>(StringComparer.Ordinal);
        int activeCount = 0;
        int terminalCount = 0;
        foreach (CandidateEvaluationJobStatus status in ledger.Jobs)
        {
            Validate(status);
            if (!ids.Add(status.JobId))
                throw new InvalidDataException("Candidate evaluation job history contains duplicate identities.");
            if (status.Status is "Queued" or "Running" or "Cancelling") activeCount++;
            else terminalCount++;
        }
        if (activeCount > 1 || terminalCount > CandidateEvaluationJobService.MaximumTerminalJobs)
            throw new InvalidDataException("Candidate evaluation job history exceeds its active or terminal job limit.");
    }

    internal static void Validate(CandidateEvaluationJobStatus? status)
    {
        if (status is null || !Guid.TryParseExact(status.JobId, "N", out _)
            || !ModelDiscoveryIdentity.IsSafeKey(status.CandidateKey)
            || status.Status is not ("Queued" or "Running" or "Cancelling" or "Completed" or "Failed"
                or "Cancelled" or "Unavailable" or "Interrupted")
            || status.Phase is not ("Quality" or "Performance" or "Eligibility" or "Done")
            || status.StartedUtc.Kind != DateTimeKind.Utc
            || (status.CompletedUtc.HasValue && status.CompletedUtc.Value.Kind != DateTimeKind.Utc)
            || (status.Status is "Queued" or "Running" or "Cancelling") != !status.CompletedUtc.HasValue
            || (status.QualityPassCount is < 0 or > 256)
            || (status.QualityExpectedCount is < 0 or > 256)
            || (status.QualityPassCount.HasValue && status.QualityExpectedCount.HasValue
                && status.QualityPassCount > status.QualityExpectedCount)
            || !ValidStageStatus(status.QualityStatus)
            || !ValidStageStatus(status.PerformanceStatus)
            || (status.Repetitions is < 0 or > 100000)
            || (status.GeneratedTokenCount is < 0 or > 1000000000)
            || !FinitePositiveOrNull(status.PromptTokensPerSecond)
            || !FinitePositiveOrNull(status.DecodeTokensPerSecond)
            || !FinitePositiveOrNull(status.CompletionMilliseconds)
            || !FinitePositiveOrNull(status.TimeToFirstTokenMilliseconds)
            || (status.Eligibility is not null && status.Eligibility is not (
                CandidateEligibilityOutcome.EligibleForFurtherReview or CandidateEligibilityOutcome.Ineligible))
            || (status.QualityInvocationId is not null
                && !Guid.TryParseExact(status.QualityInvocationId, "N", out _))
            || !ValidEvidenceScope(status)
            || !ValidToken(status.ErrorCode, 64)
            || !ValidLaunch(status))
            throw new InvalidDataException("Invalid Candidate evaluation job summary.");
    }

    private static bool ValidLaunch(CandidateEvaluationJobStatus status) =>
        (status.EvaluationLaunchFingerprint is null
            || TextLaunchDescriptor.IsActualLaunchFingerprint(status.EvaluationLaunchFingerprint))
        && (status.EvaluationLaunchArguments is null || (status.EvaluationLaunchArguments.Count <= 128
            && status.EvaluationLaunchArguments.All(item => item is { Length: > 0 and <= 160 }
                && item.All(character => character is >= '!' and <= '~'))))
        && (status.EvaluationLaunchEnvironmentNames is null || (status.EvaluationLaunchEnvironmentNames.Count <= 64
            && status.EvaluationLaunchEnvironmentNames.All(item => item is { Length: > 0 and <= 96 }
                && item.All(character => char.IsAsciiLetterOrDigit(character) || character == '_'))))
        && (status.QualityFailedCaseIds is null || (status.QualityFailedCaseIds.Count <= 64
            && status.QualityFailedCaseIds.All(item => item is { Length: > 0 and <= 64 }
                && item.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.'))))
        && ValidToken(status.EvaluationLaunchRefusal, 96)
        && (status.RequestParity is null || ValidScopeToken(status.RequestParity));

    /// <summary>Maps a refusal reason onto the persisted token vocabulary (lowercase letters, digits, underscore).</summary>
    internal static string SanitizeReason(string reason)
    {
        string token = new(reason.Where(character => char.IsAsciiLetterOrDigit(character) || character == '_').ToArray());
        return token.Length is 0 ? "launch_refused" : token.Length > 96 ? token[..96] : token;
    }

    private static bool ValidEvidenceScope(CandidateEvaluationJobStatus status)
    {
        bool anyScopeField = status.Capability is not null || status.QualitySuiteId is not null
            || status.QualitySuiteVersion.HasValue || status.PerformanceWorkloadId is not null
            || status.PerformanceWorkloadVersion.HasValue;
        if (!anyScopeField) return true; // Legacy schema-v1 summaries did not record evidence scope.

        bool anyProfileField = status.QualityProfileId is not null || status.QualityProfileVersion.HasValue;
        return ValidScopeToken(status.Capability) && ValidScopeToken(status.QualitySuiteId)
            && status.QualitySuiteVersion is > 0 and <= 100000
            && (!anyProfileField || (ValidScopeToken(status.QualityProfileId)
                && status.QualityProfileVersion is > 0 and <= 100000))
            && ValidScopeToken(status.PerformanceWorkloadId)
            && status.PerformanceWorkloadVersion is > 0 and <= 100000;
    }

    private static bool ValidScopeToken(string? value) => value is { Length: > 0 and <= 128 }
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-');

    private static bool FinitePositiveOrNull(double? value) => value is null
        || (double.IsFinite(value.Value) && value.Value > 0);

    private static bool ValidStageStatus(string? value) => value is null
        or "Completed" or "Failed" or "Unavailable" or "Cancelled";

    private static bool ValidToken(string? value, int maximumLength) => value is null
        || (value.Length is > 0 && value.Length <= maximumLength
            && value.All(character => char.IsAsciiLetterOrDigit(character) || character == '_'));
}

internal sealed class CandidateEvaluationJobLedger
{
    public int SchemaVersion { get; set; } = 1;
    public List<CandidateEvaluationJobStatus> Jobs { get; set; } = new();
}
