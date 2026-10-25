using System.Text;
using System.Text.Json;
using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>Separate, versioned Candidate metadata. Corrupt or future schema is never reset.</summary>
internal sealed class ModelDiscoveryCatalogStore
{
    internal const string FileName = "model-discovery-candidates.json";
    internal const int MaximumCandidates = 1000;
    private const int MaximumDocumentBytes = 8 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        MaxDepth = 32
    };
    private readonly string _path;

    internal ModelDiscoveryCatalogStore() : this(ServerState.StateDirectory) { }

    internal ModelDiscoveryCatalogStore(string stateDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDirectory);
        _path = Path.Combine(stateDirectory, FileName);
    }

    internal ModelDiscoveryCatalog Load()
    {
        if (!File.Exists(_path)) return new ModelDiscoveryCatalog();
        try
        {
            if (new FileInfo(_path).Length > MaximumDocumentBytes)
                throw new InvalidDataException("The model discovery catalog exceeds its size limit.");
            ModelDiscoveryCatalog? catalog = JsonSerializer.Deserialize<ModelDiscoveryCatalog>(File.ReadAllText(_path), JsonOptions);
            Validate(catalog);
            return catalog!;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            throw new InvalidDataException("The model discovery catalog is corrupt or has an unsupported schema.", ex);
        }
    }

    internal void Save(ModelDiscoveryCatalog catalog)
    {
        lock (CandidateEvidenceMetadataGate.SyncRoot)
        {
            Validate(catalog);
            string json = JsonSerializer.Serialize(catalog, JsonOptions);
            if (Encoding.UTF8.GetByteCount(json) > MaximumDocumentBytes)
                throw new InvalidDataException("The model discovery catalog exceeds its size limit.");
            // Check existing bytes before any replacement, including an externally changed document.
            _ = Load();
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

    internal static void Validate(ModelDiscoveryCatalog? catalog)
    {
        if (catalog is null || catalog.SchemaVersion != 1 || catalog.Candidates is null
            || catalog.Candidates.Count > MaximumCandidates || !ValidUtc(catalog.LastAttemptUtc)
            || !ValidUtc(catalog.LastSuccessUtc) || !ValidUtc(catalog.NextRefreshUtc)
            || catalog.LastErrorCode is { Length: > 64 })
            throw new InvalidDataException("The model discovery catalog has invalid or unsupported data.");
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (ModelDiscoveryCandidate candidate in catalog.Candidates)
        {
            if (candidate is null || !ModelDiscoveryIdentity.IsSafeRepoId(candidate.RepoId)
                || ModelRevisionIdentity.Normalize(candidate.Revision) != candidate.Revision
                || !ModelDiscoveryIdentity.IsSafeRepoFilePath(candidate.RepoFilePath)
                || !candidate.RepoFilePath.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase)
                || candidate.Provider != ModelDiscoveryIdentity.Provider
                || candidate.ProviderAdapterVersion != ModelDiscoveryIdentity.AdapterVersion
                || candidate.Lifecycle != "Candidate"
                || candidate.Disposition is not ("active" or "ignored" or "stale")
                || candidate.IntegrityStatus != "unverified"
                || candidate.Key != ModelDiscoveryIdentity.Key(candidate.RepoId, candidate.Revision, candidate.RepoFilePath)
                || candidate.PinnedUrl != ModelDiscoveryIdentity.PinnedUrl(candidate.RepoId, candidate.Revision, candidate.RepoFilePath)
                || !keys.Add(candidate.Key)
                || !ValidUtc(candidate.FirstSeenUtc) || !ValidUtc(candidate.LastSeenUtc)
                || !ValidUtc(candidate.IgnoredUtc) || !ValidUtc(candidate.NoticeDeferredUntilUtc)
                || candidate.Disposition != "ignored" && candidate.IgnoredUtc is not null
                || candidate.Disposition != "active" && (candidate.NoticePending || candidate.NoticeDeferredUntilUtc is not null)
                || candidate.NoticePending && candidate.NoticeDeferredUntilUtc is not null
                || candidate.FirstSeenUtc > candidate.LastSeenUtc
                || candidate.DeclaredLicense is { Length: > 256 }
                || candidate.DeclaredPipelineTag is { Length: > 128 }
                || candidate.DeclaredSizeBytes is <= 0
                || candidate.DeclaredSha256 is not null && ModelDiscoveryIdentity.NormalizeSha256(candidate.DeclaredSha256) != candidate.DeclaredSha256
                || candidate.DeclaredTags is null || candidate.DeclaredTags.Count > 32
                || candidate.DeclaredTags.Any(tag => tag is null or { Length: 0 or > 128 }))
                throw new InvalidDataException("The model discovery catalog contains an invalid Candidate.");
        }
    }

    private static bool ValidUtc(DateTime value) => value != default && value.Kind == DateTimeKind.Utc;
    private static bool ValidUtc(DateTime? value) => value is null || ValidUtc(value.Value);
}
