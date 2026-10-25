using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PaRuX.Contracts;

namespace PaRuX.Server;

internal sealed record CandidateUtilityPolicySnapshot(
    CandidateUtilityPolicyDefinition? Policy,
    CandidateUtilityPolicyValidation Validation);

/// <summary>Atomic, bounded persistence for an explicitly configured Candidate utility policy.
/// An absent/reset policy remains unconfigured; this store never chooses calibration values.</summary>
internal sealed class CandidateUtilityPolicyStore
{
    internal const string FileName = "candidate-utility-policy.json";
    private const int DocumentSchemaVersion = 1;
    private const int MaximumDocumentBytes = 32 * 1024;
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        MaxDepth = 12,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    private readonly string _path;

    internal CandidateUtilityPolicyStore() : this(ServerState.StateDirectory) { }

    internal CandidateUtilityPolicyStore(string stateDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDirectory);
        _path = Path.Combine(stateDirectory, FileName);
    }

    /// <summary>Runs a synchronous snapshot/CAS callback under the policy gate (no writer can interleave).</summary>
    internal static T WithGate<T>(Func<T> body) { lock (Gate) return body(); }

    internal CandidateUtilityPolicySnapshot Load()
    {
        lock (Gate)
        {
            if (!File.Exists(_path)) return Snapshot(null);
            try
            {
                FileInfo info = new(_path);
                if (File.GetAttributes(_path).HasFlag(FileAttributes.ReparsePoint)
                    || info.Length > MaximumDocumentBytes)
                    throw new InvalidDataException("Candidate utility policy is unsafe or oversized.");

                string json = File.ReadAllText(_path);
                CandidateUtilityPolicyDocument? document = JsonSerializer.Deserialize<CandidateUtilityPolicyDocument>(json, JsonOptions);
                if (document is null || document.SchemaVersion != DocumentSchemaVersion)
                    throw new InvalidDataException("Candidate utility policy has an unsupported document schema.");

                CandidateUtilityPolicySnapshot snapshot = Snapshot(document.Policy);
                if (document.Policy is not null
                    && snapshot.Validation.Readiness == CandidateUtilityPolicyReadiness.UnsupportedVersion
                    && document.Policy.SchemaVersion != 3)
                    throw new InvalidDataException("Candidate utility policy has an unsupported schema version.");
                if (document.Policy is not null
                    && snapshot.Validation.Readiness is CandidateUtilityPolicyReadiness.Invalid
                        or CandidateUtilityPolicyReadiness.UnsupportedScope)
                    throw new InvalidDataException("Candidate utility policy is invalid: " + snapshot.Validation.ReasonCode + ".");
                return snapshot;
            }
            catch (InvalidDataException) { throw; }
            catch (Exception ex) when (ex is JsonException or NotSupportedException or IOException or UnauthorizedAccessException)
            {
                throw new InvalidDataException("Candidate utility policy is malformed or unavailable.", ex);
            }
        }
    }

    internal CandidateUtilityPolicySnapshot Save(CandidateUtilityPolicyDefinition? policy)
    {
        CandidateUtilityPolicySnapshot snapshot = Snapshot(policy);
        if (policy is not null && snapshot.Validation.Readiness != CandidateUtilityPolicyReadiness.WellFormed)
            throw new InvalidDataException("Candidate utility policy is invalid: " + snapshot.Validation.ReasonCode + ".");

        var document = new CandidateUtilityPolicyDocument
        {
            SchemaVersion = DocumentSchemaVersion,
            Policy = Clone(policy)
        };
        string json = JsonSerializer.Serialize(document, JsonOptions);
        if (Encoding.UTF8.GetByteCount(json) > MaximumDocumentBytes)
            throw new InvalidDataException("Candidate utility policy exceeds its size limit.");

        lock (Gate)
        {
            string directory = Path.GetDirectoryName(_path)!;
            Directory.CreateDirectory(directory);
            if (File.Exists(_path) && File.GetAttributes(_path).HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidDataException("Candidate utility policy path cannot be a reparse point.");

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
        return snapshot;
    }

    private static CandidateUtilityPolicySnapshot Snapshot(CandidateUtilityPolicyDefinition? policy) =>
        new(Clone(policy), CandidateUtilityPolicyValidator.Validate(policy));

    private static CandidateUtilityPolicyDefinition? Clone(CandidateUtilityPolicyDefinition? policy) =>
        policy is null
            ? null
            : JsonSerializer.Deserialize<CandidateUtilityPolicyDefinition>(
                JsonSerializer.Serialize(policy, JsonOptions), JsonOptions);

    private sealed class CandidateUtilityPolicyDocument
    {
        public int SchemaVersion { get; set; }
        public CandidateUtilityPolicyDefinition? Policy { get; set; }
    }
}
