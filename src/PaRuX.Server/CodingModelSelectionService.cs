using System.Text.Json;
using PaRuX.Contracts;
using PaRuX.Server.Orchestration;

namespace PaRuX.Server;

/// <summary>
/// Core-owned coding-model choice. Outcomes are derived by Core from each coding task's own state,
/// not reported as scores by the client:
/// <list type="bullet">
/// <item>Attribution: when Core resolves the coding role while exactly one coding task is running,
/// that task is attributed to the model file that actually served it. Concurrent coding tasks, or
/// a task served by more than one model, are ambiguous and never become evidence.</item>
/// <item>Verified = the independent verifier passed AND the agent reported that an accepted,
/// deterministic verification command succeeded after its last change. The verifier often runs on
/// the same local model, so its verdict alone is not independent quality evidence.</item>
/// <item>Unverified = the verifier's verdict was "fail", or the attempt paused with changed code and
/// no successful verification. Verifier crashes/errors, runtime failures, sandbox infrastructure
/// failures and cancellations say nothing about the model and are excluded.</item>
/// <item>Speed = the agent's own wall time for verified attempts (steps, builds and tests included,
/// because that is the time the user waits).</item>
/// </list>
/// Evidence is scoped to the current hardware fingerprint.
/// </summary>
public sealed class CodingModelSelectionService
{
    public const string CodingRole = "coding";
    private const int MaxAttributions = 1_000;
    private static readonly TimeSpan DecisionCacheLifetime = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan FingerprintCacheLifetime = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan FailedExactLoadBackoff = TimeSpan.FromMinutes(5);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>Log text written by the coding agent/worker when the model broke protocol or looped.</summary>
    internal static readonly string[] ProtocolTroubleMarkers =
    {
        "invalid action protocol",
        "Paused after",
        "Coding attempt paused without verification",
        "Paused by the progress watchdog",
        "Paused at the runaway safety ceiling"
    };

    internal const string UnverifiedPauseStage = "Coding attempt paused without verification";

    private readonly TaskStore _tasks;
    private readonly Func<CancellationToken, Task<List<CandidateEvidenceDto>>> _candidates;
    private readonly Func<string> _hardwareFingerprint;
    private readonly string _stateDirectory;
    private readonly object _gate = new();
    private readonly Dictionary<string, DateTime> _failedExactLoadUntil = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, Attribution>? _attributions;
    private CodingModelSelectionDto? _cached;
    private DateTime _cachedUtc;
    private string? _fingerprint;
    private DateTime _fingerprintUtc;

    public CodingModelSelectionService(
        TaskStore tasks,
        Func<CancellationToken, Task<List<CandidateEvidenceDto>>> candidates,
        Func<string> hardwareFingerprint,
        string stateDirectory)
    {
        _tasks = tasks;
        _candidates = candidates;
        _hardwareFingerprint = hardwareFingerprint;
        _stateDirectory = stateDirectory;
    }

    private string AttributionPath => Path.Combine(_stateDirectory, "coding-model-attribution.json");
    private string PreferencePath => Path.Combine(_stateDirectory, "coding-model-selection.json");

    public string GetMode()
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(PreferencePath)) return CodingModelSelectionMode.Auto;
                var document = JsonSerializer.Deserialize<PreferenceDocument>(File.ReadAllText(PreferencePath));
                return CodingModelSelectionMode.Normalize(document?.Mode);
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                // An unreadable preference must not silently enable a different behaviour than the
                // one the user chose; the assigned model is the conservative default here.
                return CodingModelSelectionMode.Assigned;
            }
        }
    }

    public void SetMode(string mode)
    {
        string normalized = mode?.Trim().ToLowerInvariant() switch
        {
            CodingModelSelectionMode.Auto => CodingModelSelectionMode.Auto,
            CodingModelSelectionMode.Assigned => CodingModelSelectionMode.Assigned,
            _ => throw new InvalidDataException("Coding model selection mode must be \"auto\" or \"assigned\".")
        };
        lock (_gate)
        {
            WriteAtomically(PreferencePath, JsonSerializer.Serialize(new PreferenceDocument { Mode = normalized }, JsonOptions));
            _cached = null;
        }
    }

    public async Task<CodingModelSelectionDto> GetDecisionAsync(CancellationToken ct, bool refresh = false)
    {
        lock (_gate)
        {
            if (!refresh && _cached != null && DateTime.UtcNow - _cachedUtc < DecisionCacheLifetime) return _cached;
        }

        string hardware = CurrentFingerprint();
        List<CandidateEvidenceDto> evidence = await _candidates(ct).ConfigureAwait(false);
        var candidates = evidence.Select(c => new CodingModelSelector.Candidate(
            c.FileName, c.FilePath, c.HardwareCompatible != false, c.IsCurrentAssignment)).ToList();
        List<CodingModelSelector.Outcome> outcomes = CollectOutcomes(hardware);
        CodingModelSelectionDto decision = CodingModelSelector.Select(GetMode(), candidates, outcomes, hardware, DateTime.UtcNow);
        lock (_gate)
        {
            _cached = decision;
            _cachedUtc = DateTime.UtcNow;
        }
        return decision;
    }

    /// <summary>True while a recent exact load of this file failed; avoids re-draining the runtime on every step.</summary>
    public bool IsExactLoadBackedOff(string fileName)
    {
        lock (_gate)
            return _failedExactLoadUntil.TryGetValue(fileName, out DateTime until) && DateTime.UtcNow < until;
    }

    public void RecordExactLoadFailure(string fileName)
    {
        lock (_gate) _failedExactLoadUntil[fileName] = DateTime.UtcNow + FailedExactLoadBackoff;
    }

    /// <summary>
    /// Attributes the running coding task to <paramref name="modelPath"/>. With more than one
    /// running coding task the served task cannot be known, so all of them become ambiguous. The
    /// first attribution of a task is written to its log so the choice is visible to the user.
    /// </summary>
    public void RecordCodingResolution(string modelPath, string? selectionNote)
    {
        if (string.IsNullOrWhiteSpace(modelPath)) return;
        List<AgentTaskDto> running = _tasks.ListByStatus(AgentTaskStatus.Running)
            .Where(t => string.Equals(t.Role, CodingRole, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (running.Count == 0) return;

        string fileName = Path.GetFileName(modelPath);
        string hardware = CurrentFingerprint();
        bool ambiguous = running.Count > 1;
        var firstSeen = new List<string>();
        lock (_gate)
        {
            Dictionary<string, Attribution> map = Attributions();
            bool changed = false;
            foreach (AgentTaskDto task in running)
            {
                if (map.TryGetValue(task.Id, out Attribution? existing))
                {
                    bool mixed = existing.Mixed || ambiguous
                        || !string.Equals(existing.FileName, fileName, StringComparison.OrdinalIgnoreCase);
                    if (mixed != existing.Mixed) { existing.Mixed = mixed; changed = true; }
                    continue;
                }
                map[task.Id] = new Attribution
                {
                    TaskId = task.Id, FileName = fileName, ModelPath = modelPath,
                    HardwareFingerprint = hardware, FirstUtc = DateTime.UtcNow, Mixed = ambiguous
                };
                firstSeen.Add(task.Id);
                changed = true;
            }
            if (!changed) return;
            if (map.Count > MaxAttributions)
            {
                foreach (string key in map.Values.OrderBy(a => a.FirstUtc).Take(map.Count - MaxAttributions).Select(a => a.TaskId).ToList())
                    map.Remove(key);
            }
            WriteAtomically(AttributionPath, JsonSerializer.Serialize(map.Values.ToList(), JsonOptions));
        }
        foreach (string taskId in firstSeen)
        {
            try
            {
                _tasks.AppendLog(taskId, "Coding model: " + fileName
                    + (string.IsNullOrWhiteSpace(selectionNote) ? string.Empty : " — " + selectionNote));
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException) { }
        }
    }

    internal List<CodingModelSelector.Outcome> CollectOutcomes(string hardware)
    {
        Dictionary<string, Attribution> map;
        lock (_gate) map = new Dictionary<string, Attribution>(Attributions(), StringComparer.Ordinal);
        var outcomes = new List<CodingModelSelector.Outcome>();
        // Cancelled tasks stay neutral: Universal STOP, a user's change of mind or an external
        // time limit say nothing reliable about the model. Loops and stalls are bounded by the
        // agent itself and end as "paused without verification", which does count.
        foreach (AgentTaskDto task in _tasks.ListByStatus(AgentTaskStatus.Completed, AgentTaskStatus.Failed))
        {
            if (!string.Equals(task.Role, CodingRole, StringComparison.OrdinalIgnoreCase)) continue;
            if (!map.TryGetValue(task.Id, out Attribution? attribution)) continue;
            if (attribution.Mixed || !string.Equals(attribution.HardwareFingerprint, hardware, StringComparison.Ordinal)) continue;

            ResultEvidence result = ReadResultEvidence(task.ResultJson);
            bool verifierPass = task.Status == AgentTaskStatus.Completed
                && string.Equals(task.VerificationStatus, "pass", StringComparison.OrdinalIgnoreCase);
            bool verifierFail = task.Status == AgentTaskStatus.Failed
                && string.Equals(task.VerificationStatus, "fail", StringComparison.OrdinalIgnoreCase);
            bool unverifiedPause = task.Status == AgentTaskStatus.Failed
                && string.Equals(task.Stage, UnverifiedPauseStage, StringComparison.Ordinal);

            // A verifier "fail" is the same (often self-grading) verifier as a "pass": it counts only
            // where deterministic verification existed, symmetric with the verified rule.
            List<string> logs = _tasks.GetLogs(task.Id).Select(l => l.Message).ToList();
            bool verified;
            if (verifierPass && result.VerifiedByCommand) verified = true;
            else if ((verifierFail && result.VerificationAvailable) || unverifiedPause) verified = false;
            else continue; // verifier-only pass, verifier error, runtime/sandbox failure: not model evidence

            bool trouble = ContainsAny(logs, ProtocolTroubleMarkers);
            outcomes.Add(new CodingModelSelector.Outcome(attribution.FileName, verified, trouble,
                verified ? result.ElapsedSeconds : 0));
        }
        return outcomes;
    }

    private static ResultEvidence ReadResultEvidence(string? resultJson)
    {
        if (string.IsNullOrWhiteSpace(resultJson)) return default;
        try
        {
            using JsonDocument document = JsonDocument.Parse(resultJson);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return default;
            bool byCommand = root.TryGetProperty("verifiedByCommand", out JsonElement v) && v.ValueKind == JsonValueKind.True;
            bool available = root.TryGetProperty("verificationAvailable", out JsonElement a) && a.ValueKind == JsonValueKind.True;
            bool repaired = root.TryGetProperty("repairAttempt", out JsonElement r) && r.ValueKind == JsonValueKind.Number
                && r.TryGetInt32(out int attempt) && attempt > 0;
            // The worker times only the final run; a repaired task would look faster than it was.
            double seconds = !repaired && root.TryGetProperty("agentElapsedSeconds", out JsonElement s)
                && s.ValueKind == JsonValueKind.Number && s.TryGetDouble(out double parsed) && parsed > 0 ? parsed : 0;
            return new ResultEvidence(byCommand, available, seconds);
        }
        catch (JsonException)
        {
            return default;
        }
    }

    private readonly record struct ResultEvidence(bool VerifiedByCommand, bool VerificationAvailable, double ElapsedSeconds);

    private static bool ContainsAny(IEnumerable<string> lines, string[] markers) =>
        lines.Any(line => markers.Any(marker => line.Contains(marker, StringComparison.OrdinalIgnoreCase)));

    private string CurrentFingerprint()
    {
        lock (_gate)
        {
            if (_fingerprint != null && DateTime.UtcNow - _fingerprintUtc < FingerprintCacheLifetime) return _fingerprint;
        }
        string value;
        try { value = _hardwareFingerprint(); }
        catch (Exception) { value = "hardware-unknown"; }
        lock (_gate)
        {
            _fingerprint = value;
            _fingerprintUtc = DateTime.UtcNow;
        }
        return value;
    }

    /// <summary>Caller holds <see cref="_gate"/>.</summary>
    private Dictionary<string, Attribution> Attributions()
    {
        if (_attributions != null) return _attributions;
        _attributions = new Dictionary<string, Attribution>(StringComparer.Ordinal);
        if (!File.Exists(AttributionPath)) return _attributions;
        try
        {
            List<Attribution>? list = JsonSerializer.Deserialize<List<Attribution>>(File.ReadAllText(AttributionPath));
            foreach (Attribution a in list ?? new List<Attribution>())
            {
                if (!string.IsNullOrWhiteSpace(a.TaskId) && !string.IsNullOrWhiteSpace(a.FileName))
                    _attributions[a.TaskId] = a;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Keep the unreadable history for diagnosis instead of silently overwriting it.
            try { File.Move(AttributionPath, AttributionPath + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss")); }
            catch (Exception moveError) when (moveError is IOException or UnauthorizedAccessException) { }
        }
        return _attributions;
    }

    private static void WriteAtomically(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, content);
        if (File.Exists(path)) File.Replace(temporary, path, destinationBackupFileName: null);
        else File.Move(temporary, path);
    }

    private sealed class PreferenceDocument
    {
        public string Mode { get; set; } = CodingModelSelectionMode.Auto;
    }

    internal sealed class Attribution
    {
        public string TaskId { get; set; } = string.Empty;
        public string FileName { get; set; } = string.Empty;
        public string ModelPath { get; set; } = string.Empty;
        public string HardwareFingerprint { get; set; } = string.Empty;
        public DateTime FirstUtc { get; set; }
        /// <summary>More than one model, or more than one concurrent coding task: not attributable.</summary>
        public bool Mixed { get; set; }
    }
}
