using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>The hw2 / rtv2 identity relevant to a recommendation. rtv2 is the proof's BinaryFingerprint.</summary>
internal sealed record CandidateRecommendationEvaluationIdentity(string HardwareFingerprint, string RuntimeFingerprint)
{
    internal bool IsValid => IsFingerprint(HardwareFingerprint, EvaluationHardwareFingerprint.CurrentPrefix)
        && IsFingerprint(RuntimeFingerprint, RuntimeDependencyFingerprint.Prefix);

    internal static CandidateRecommendationEvaluationIdentity From(CandidateProofEnvironment environment) =>
        new(environment.HardwareFingerprint, environment.BinaryFingerprint);

    internal static async Task<CandidateRecommendationEvaluationIdentity?> CaptureCurrentAsync(
        CandidateUtilityComparisonProof proof, ICandidateLaunchFenceSource fence,
        CancellationToken cancellationToken)
    {
        CandidateRecommendationEvaluationIdentity quality = From(proof.QualityEnvironment);
        CandidateRecommendationEvaluationIdentity performance = From(proof.PerformanceEnvironment);
        if (!quality.IsValid || quality != performance) return null;
        string? backend = proof.QualityEnvironment.RuntimeFingerprint switch
        {
            string value when value.StartsWith("text-cuda:", StringComparison.Ordinal) => "text-cuda",
            string value when value.StartsWith("text-vulkan:", StringComparison.Ordinal) => "text-vulkan",
            _ => null
        };
        if (backend is null) return null;
        CandidateLaunchHost? host = await fence.CaptureHostAsync(backend, cancellationToken).ConfigureAwait(false);
        if (host is null || host.Backend != backend) return null;
        var current = new CandidateRecommendationEvaluationIdentity(host.HardwareFingerprint, host.BinaryFingerprint);
        return current.IsValid ? current : null;
    }

    private static bool IsFingerprint(string value, string prefix) => value.Length == prefix.Length + 64
        && value.StartsWith(prefix, StringComparison.Ordinal)
        && value.Skip(prefix.Length).All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}

/// <summary>A passive revalidation offer. It has no evaluation job id until a separate policy consumer acts.</summary>
internal sealed record CandidateIdentityRevalidationRequest(
    int Version,
    string RequestId,
    DateTime RequestedUtc,
    string State,
    string Reason,
    string CandidateKey,
    string Revision,
    string ManagedFileName,
    string PreviousHardwareFingerprint,
    string PreviousRuntimeFingerprint,
    string CurrentHardwareFingerprint,
    string CurrentRuntimeFingerprint,
    string? EvaluationJobId);

/// <summary>Stores one immutable request file per distinct Active-to-current identity change.</summary>
internal sealed class CandidateIdentityRevalidationRequestStore
{
    internal const string DirectoryName = "candidate-identity-revalidation";
    internal const string RequestFilePrefix = "request-";
    private const int MaximumRequestBytes = 16 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _directory;

    internal CandidateIdentityRevalidationRequestStore(string stateDirectory) =>
        _directory = Path.Combine(stateDirectory, DirectoryName);

    internal static CandidateIdentityRevalidationRequest Create(
        CandidateProofIdentity winner,
        CandidateRecommendationEvaluationIdentity previous,
        CandidateRecommendationEvaluationIdentity current,
        DateTime? requestedUtc = null)
    {
        var request = new CandidateIdentityRevalidationRequest(1, string.Empty,
            (requestedUtc ?? DateTime.UtcNow).ToUniversalTime(), "AwaitingPolicy", "active_identity_changed",
            winner.CandidateKey, winner.Revision, winner.ManagedFileName,
            previous.HardwareFingerprint, previous.RuntimeFingerprint,
            current.HardwareFingerprint, current.RuntimeFingerprint, null);
        return request with { RequestId = ComputeRequestId(request) };
    }

    internal bool TryCreate(CandidateIdentityRevalidationRequest request)
    {
        if (!ValidRequest(request)) throw new InvalidDataException("Revalidation request is invalid.");
        Directory.CreateDirectory(_directory);
        if (File.GetAttributes(_directory).HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidDataException("Revalidation request directory is unsafe.");

        string path = PathFor(request.RequestId);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(request, JsonOptions);
        if (bytes.Length > MaximumRequestBytes)
            throw new InvalidDataException("Revalidation request exceeds its size limit.");
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            try
            {
                File.Move(temporary, path);
                return true;
            }
            catch (IOException) when (File.Exists(path))
            {
                // The deterministic name makes concurrent or repeated consults idempotent.
                return false;
            }
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    internal CandidateIdentityRevalidationRequest? Load(string requestId)
    {
        if (!IsSha256(requestId)) throw new InvalidDataException("Revalidation request id is invalid.");
        string path = PathFor(requestId);
        if (!File.Exists(path)) return null;
        if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidDataException("Revalidation request is unsafe.");

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.Read | FileShare.Delete);
            byte[] bytes = new byte[MaximumRequestBytes + 1];
            int length = 0;
            while (length < bytes.Length)
            {
                int read = stream.Read(bytes, length, bytes.Length - length);
                if (read == 0) break;
                length += read;
            }
            if (length is 0 or > MaximumRequestBytes)
                throw new InvalidDataException("Revalidation request is unsafe or oversized.");
            ReadOnlyMemory<byte> document = bytes.AsMemory(0, length);
            using JsonDocument parsed = JsonDocument.Parse(document, new JsonDocumentOptions { MaxDepth = 8 });
            if (!NoDuplicateProperties(parsed.RootElement))
                throw new InvalidDataException("Revalidation request has duplicate fields.");
            CandidateIdentityRevalidationRequest? request = JsonSerializer.Deserialize<CandidateIdentityRevalidationRequest>(
                document.Span, JsonOptions);
            if (request is null || request.RequestId != requestId || !ValidRequest(request))
                throw new InvalidDataException("Revalidation request is invalid or incompatible.");
            return request;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            throw new InvalidDataException("Revalidation request is malformed.", ex);
        }
    }

    internal string PathFor(string requestId)
    {
        if (!IsSha256(requestId)) throw new InvalidDataException("Revalidation request id is invalid.");
        return Path.Combine(_directory, RequestFilePrefix + requestId + ".json");
    }

    private static string ComputeRequestId(CandidateIdentityRevalidationRequest request)
    {
        string canonical = string.Join("\n", request.Version.ToString(System.Globalization.CultureInfo.InvariantCulture),
            request.CandidateKey, request.Revision, request.ManagedFileName,
            request.PreviousHardwareFingerprint, request.PreviousRuntimeFingerprint,
            request.CurrentHardwareFingerprint, request.CurrentRuntimeFingerprint);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private static bool ValidRequest(CandidateIdentityRevalidationRequest? request) => request is not null
        && request.Version == 1 && IsSha256(request.RequestId)
        && request.RequestId == ComputeRequestId(request)
        && request.RequestedUtc.Kind == DateTimeKind.Utc
        && request.State == "AwaitingPolicy" && request.Reason == "active_identity_changed"
        && request.EvaluationJobId is null
        && ModelDiscoveryIdentity.IsSafeKey(request.CandidateKey)
        && ModelRevisionIdentity.Normalize(request.Revision) == request.Revision
        && ModelDownloadProvenanceManifestStore.IsSafeFileName(request.ManagedFileName)
        && new CandidateRecommendationEvaluationIdentity(request.PreviousHardwareFingerprint,
            request.PreviousRuntimeFingerprint).IsValid
        && new CandidateRecommendationEvaluationIdentity(request.CurrentHardwareFingerprint,
            request.CurrentRuntimeFingerprint).IsValid
        && (request.PreviousHardwareFingerprint != request.CurrentHardwareFingerprint
            || request.PreviousRuntimeFingerprint != request.CurrentRuntimeFingerprint);

    private static bool IsSha256(string? value) => value is { Length: 64 }
        && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool NoDuplicateProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty property in value.EnumerateObject())
                if (!names.Add(property.Name) || !NoDuplicateProperties(property.Value)) return false;
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in value.EnumerateArray())
                if (!NoDuplicateProperties(item)) return false;
        }
        return true;
    }
}
