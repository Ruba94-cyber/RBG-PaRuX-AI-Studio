using System.Security.Cryptography;
using System.Text.Json;
using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>Persists descriptive provenance for completed managed-model files. Normal listing uses
/// cheap file metadata; backup import hashes existing payloads before attaching evidence. Absent or
/// corrupt metadata remains unknown.</summary>
internal static class ModelDownloadProvenanceManifestStore
{
    private const string ManifestFileName = ".model-provenance.json";
    private const int CurrentSchemaVersion = 1;
    private const int MaximumEntries = 100_000;
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        MaxDepth = 32
    };

    internal static ModelProvenanceManifestDocument Load(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        lock (Gate)
            return LoadCore(directory);
    }

    internal static ModelDownloadProvenance? Get(string directory, string fileName)
    {
        if (!IsSafeFileName(fileName)) return null;
        lock (CandidateEvidenceMetadataGate.SyncRoot)
        lock (Gate)
        {
            var document = LoadCore(directory);
            return document.Entries.TryGetValue(fileName, out var value) ? Clone(value) : null;
        }
    }

    internal static void Upsert(string directory, string fileName, ModelDownloadProvenance provenance)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(provenance);
        if (!IsSafeFileName(fileName)) throw new ArgumentException("A simple managed model filename is required.", nameof(fileName));
        if (!IsValidEntry(provenance, fileName)) throw new ArgumentException("The provenance entry is invalid.", nameof(provenance));
        lock (CandidateEvidenceMetadataGate.SyncRoot)
        lock (Gate)
        {
            if (!TryLoadCore(directory, out var document))
                throw new InvalidDataException("The existing model provenance manifest is corrupt; refusing to overwrite it.");
            document.Entries[fileName] = Clone(provenance);
            WriteCore(directory, document);
        }
    }

    internal static void AssertWritable(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        lock (CandidateEvidenceMetadataGate.SyncRoot)
        lock (Gate)
        {
            if (!TryLoadCore(directory, out _))
                throw new InvalidDataException("The existing model provenance manifest is corrupt; repair or move it before downloading another model.");
        }
    }

    internal static bool Remove(string directory, string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if (!IsSafeFileName(fileName)) return false;
        lock (CandidateEvidenceMetadataGate.SyncRoot)
        lock (Gate)
        {
            if (!TryLoadCore(directory, out var document))
                throw new InvalidDataException("The existing model provenance manifest is corrupt; refusing to modify it.");
            if (!document.Entries.Remove(fileName)) return false;
            WriteCore(directory, document);
            return true;
        }
    }

    internal static ModelProvenanceManifestDocument Export(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        lock (Gate) return Clone(LoadCore(directory));
    }

    /// <summary>Validates a backup payload before the caller applies any restore mutations.</summary>
    internal static void ValidateDocument(ModelProvenanceManifestDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!TryNormalize(document, out _))
            throw new InvalidDataException("The model provenance manifest has an unsupported schema or invalid entries.");
    }

    /// <summary>Replaces the local sidecar only when the complete imported document is valid.</summary>
    internal static async Task<int?> ImportAsync(string directory, ModelProvenanceManifestDocument? document, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if (!TryNormalize(document, out var normalized)) return null;

        // Hash potentially multi-GB model files without holding the process-wide manifest lock.
        var imported = new Dictionary<string, ModelDownloadProvenance>(StringComparer.OrdinalIgnoreCase);
        foreach (var (fileName, value) in normalized.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ModelDownloadProvenance? rebound = await TryBindToCurrentFileAsync(directory, fileName, value, cancellationToken).ConfigureAwait(false);
            if (rebound != null) imported[fileName] = rebound;
        }

        cancellationToken.ThrowIfCancellationRequested();
        lock (CandidateEvidenceMetadataGate.SyncRoot)
        lock (Gate)
        {
            if (!TryLoadCore(directory, out var current))
                throw new InvalidDataException("The existing model provenance manifest is corrupt; refusing to import backup evidence.");

            // Keep only existing records that still identify the current bytes, then apply
            // backup entries only when the target machine already has an exact file match.
            // Model payloads are deliberately not part of this metadata-only backup.
            var merged = Empty();
            foreach (var (fileName, value) in current.Entries)
                if (TryBindToFileMetadata(directory, fileName, value, out var rebound))
                    merged.Entries[fileName] = rebound;
            int importedEntryCount = 0;
            foreach (var (fileName, value) in imported)
            {
                if (!TryBindToFileMetadata(directory, fileName, value, out var rebound)) continue;
                merged.Entries[fileName] = rebound;
                importedEntryCount++;
            }
            WriteCore(directory, merged);
            return importedEntryCount;
        }
    }

    internal static bool IsSafeFileName(string? fileName)
    {
        if (!PaRuXPathSafety.IsSimpleFileName(fileName) || fileName!.Length > 240 || fileName != fileName.Trim()) return false;
        if (!fileName.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase)
            && !fileName.EndsWith(".safetensors", StringComparison.OrdinalIgnoreCase)) return false;
        foreach (var ch in fileName)
            if (ch is '<' or '>' or '"' or '|' or '?' or '*') return false;
        var stem = fileName.Split('.')[0].TrimEnd(' ', '.');
        if (stem.Equals("CON", StringComparison.OrdinalIgnoreCase) || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase)) return false;
        if (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) && stem[3] is >= '1' and <= '9') return false;
        return true;
    }

    private static ModelProvenanceManifestDocument LoadCore(string directory)
    {
        return TryLoadCore(directory, out var document) ? document : Empty();
    }

    private static bool TryLoadCore(string directory, out ModelProvenanceManifestDocument document)
    {
        document = Empty();
        var path = Path.Combine(directory, ManifestFileName);
        try
        {
            if (!File.Exists(path)) return true;
            var json = File.ReadAllText(path);
            if (JsonSerializer.Deserialize<ModelProvenanceManifestDocument>(json, JsonOptions) is not { } parsed ||
                !TryNormalize(parsed, out var normalized)) return false;
            document = normalized;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return false;
        }
    }

    private static void WriteCore(string directory, ModelProvenanceManifestDocument document)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, ManifestFileName);
        var temp = Path.Combine(directory, $"{ManifestFileName}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(document, JsonOptions));
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static bool TryNormalize(ModelProvenanceManifestDocument? source, out ModelProvenanceManifestDocument normalized)
    {
        normalized = Empty();
        if (source is null || source.SchemaVersion != CurrentSchemaVersion || source.Entries is null || source.Entries.Count > MaximumEntries) return false;
        foreach (var (fileName, value) in source.Entries)
        {
            if (!IsSafeFileName(fileName) || value is null || !IsValidEntry(value, fileName) || normalized.Entries.ContainsKey(fileName)) return false;
            normalized.Entries.Add(fileName, Clone(value));
        }
        return true;
    }

    private static bool IsValidEntry(ModelDownloadProvenance value, string fileName)
    {
        if (!IsRepoId(value.RepoId) || value.SizeBytes < 0 || value.CompletedUtc == default || value.CompletedUtc.Kind != DateTimeKind.Utc
            || value.FileLastWriteUtc == default || value.FileLastWriteUtc.Kind != DateTimeKind.Utc) return false;
        if (value.Revision is not null && !IsRevision(value.Revision)) return false;
        if (!string.Equals(value.ModelCardUrl, BuildPinnedModelCardUrl(value.RepoId, value.Revision), StringComparison.Ordinal)) return false;
        if ((value.RepoFilePath is null) != (value.PinnedSourceUrl is null)) return false;
        if (value.RepoFilePath is { } sourcePath)
        {
            if (value.Revision is null || !string.Equals(sourcePath.Split('/')[^1], fileName, StringComparison.Ordinal)
                || !string.Equals(value.PinnedSourceUrl, BuildPinnedSourceUrl(value.RepoId, value.Revision, sourcePath), StringComparison.Ordinal)) return false;
        }
        if (value.LicenseMetadataStatus is not (ModelLicenseMetadataStatus.Declared or ModelLicenseMetadataStatus.NotDeclared or ModelLicenseMetadataStatus.Unavailable or ModelLicenseMetadataStatus.NotProvided)) return false;
        if (value.PublisherDeclaredLicense is { Length: > 200 }) return false;
        if (value.MetadataObservedUtc is { Kind: not DateTimeKind.Utc } || value.ReviewAcknowledgedUtc is { Kind: not DateTimeKind.Utc }) return false;
        if (value.LicenseMetadataStatus == ModelLicenseMetadataStatus.NotProvided)
        {
            if (value.PublisherDeclaredLicense is not null || value.MetadataObservedUtc.HasValue || value.ReviewRequired || value.ReviewAcknowledged || value.ReviewAcknowledgedUtc.HasValue) return false;
        }
        else
        {
            bool retrieved = value.LicenseMetadataStatus is ModelLicenseMetadataStatus.Declared or ModelLicenseMetadataStatus.NotDeclared;
            if (value.MetadataObservedUtc is not DateTime metadataObservedUtc || metadataObservedUtc > value.CompletedUtc) return false;
            if (value.LicenseMetadataStatus == ModelLicenseMetadataStatus.Declared && string.IsNullOrWhiteSpace(value.PublisherDeclaredLicense)) return false;
            if (value.LicenseMetadataStatus != ModelLicenseMetadataStatus.Declared && value.PublisherDeclaredLicense is not null) return false;
            bool requiresReview = ModelLicenseReviewRules.RequiresReview(value.PublisherDeclaredLicense, retrieved);
            if (value.ReviewRequired != requiresReview || value.ReviewAcknowledged != requiresReview) return false;
            if (requiresReview)
            {
                if (value.ReviewAcknowledgedUtc is not DateTime acknowledgedUtc || acknowledgedUtc > value.CompletedUtc) return false;
            }
            else if (value.ReviewAcknowledgedUtc.HasValue)
            {
                return false;
            }
        }
        if (value.ExpectedSha256 is not null && !IsSha256(value.ExpectedSha256)) return false;
        if (value.ComputedSha256 is null || !IsSha256(value.ComputedSha256)) return false;
        return value.IntegrityStatus switch
        {
            "verified" => value.ExpectedSha256 is not null && value.ComputedSha256 is not null && string.Equals(value.ExpectedSha256, value.ComputedSha256, StringComparison.OrdinalIgnoreCase),
            "unavailable" => value.ExpectedSha256 is null,
            _ => false
        };
    }

    private static async Task<ModelDownloadProvenance?> TryBindToCurrentFileAsync(
        string directory, string fileName, ModelDownloadProvenance source, CancellationToken cancellationToken)
    {
        try
        {
            var info = new FileInfo(Path.Combine(directory, fileName));
            if (!info.Exists || info.Length != source.SizeBytes || source.ComputedSha256 is null) return null;
            long expectedLength = info.Length;
            DateTime expectedLastWriteUtc = info.LastWriteTimeUtc;
            await using var stream = new FileStream(info.FullName, FileMode.Open, FileAccess.Read, FileShare.Read,
                1024 * 1024, useAsync: true);
            if (stream.Length != expectedLength) return null;
            string actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false)).ToLowerInvariant();
            var afterHash = new FileInfo(info.FullName);
            if (!afterHash.Exists || afterHash.Length != expectedLength || afterHash.LastWriteTimeUtc != expectedLastWriteUtc
                || !string.Equals(actual, source.ComputedSha256, StringComparison.OrdinalIgnoreCase)) return null;
            ModelDownloadProvenance rebound = Clone(source);
            rebound.FileLastWriteUtc = expectedLastWriteUtc;
            return rebound;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static bool TryBindToFileMetadata(string directory, string fileName, ModelDownloadProvenance source, out ModelDownloadProvenance rebound)
    {
        rebound = Clone(source);
        try
        {
            var info = new FileInfo(Path.Combine(directory, fileName));
            if (!info.Exists || info.Length != source.SizeBytes || info.LastWriteTimeUtc != source.FileLastWriteUtc) return false;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static string? BuildPinnedModelCardUrl(string repoId, string? revision)
    {
        if (revision is null) return null;
        var parts = repoId.Split('/');
        if (parts.Length != 2 || parts.Any(part => part.Length is 0 or > 100 || part is "." or ".."
            || !part.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '.' or '_' or '-'))) return null;
        return "https://huggingface.co/" + Uri.EscapeDataString(parts[0]) + "/" + Uri.EscapeDataString(parts[1]) + "/tree/" + revision.ToLowerInvariant();
    }

    /// <summary>Derive a source identity only after the transfer request's URL security checks.
    /// The identity omits URL query/fragment and cannot be used to replay an authenticated fetch.</summary>
    internal static (string? RepoFilePath, string? PinnedSourceUrl) SourceIdentityFromValidatedUrl(
        string repoId, string? revision, string downloadUrl)
    {
        if (revision is null || !IsRepoId(repoId) || !IsRevision(revision)
            || !Uri.TryCreate(downloadUrl, UriKind.Absolute, out Uri? uri)
            || uri.Scheme != Uri.UriSchemeHttps || uri.Host != "huggingface.co"
            || !uri.IsDefaultPort || uri.UserInfo.Length != 0) return (null, null);

        // Encoded separators can refer to a different remote object than the decoded path.
        // Keep the transfer compatible, but decline an ambiguous Candidate identity.
        if (uri.AbsolutePath.Contains("%2f", StringComparison.OrdinalIgnoreCase)) return (null, null);
        string decodedPath;
        try { decodedPath = Uri.UnescapeDataString(uri.AbsolutePath); }
        catch (UriFormatException) { return (null, null); }
        string prefix = "/" + repoId + "/resolve/" + revision + "/";
        if (!decodedPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return (null, null);
        string sourcePath = decodedPath[prefix.Length..];
        string? pinnedUrl = BuildPinnedSourceUrl(repoId, revision, sourcePath);
        return pinnedUrl is null ? (null, null) : (sourcePath, pinnedUrl);
    }

    private static string? BuildPinnedSourceUrl(string repoId, string revision, string sourcePath)
    {
        if (!IsRepoId(repoId) || !IsRevision(revision) || sourcePath.Length is < 1 or > 2048
            || sourcePath.Contains('\\')) return null;
        string[] parts = sourcePath.Split('/');
        if (parts.Length > 32 || parts.Any(part => part.Length == 0 || part is "." or ".."
            || part.Any(char.IsControl))) return null;
        string[] repoParts = repoId.Split('/');
        string escapedPath = string.Join('/', parts.Select(Uri.EscapeDataString));
        return "https://huggingface.co/" + Uri.EscapeDataString(repoParts[0]) + "/"
            + Uri.EscapeDataString(repoParts[1]) + "/resolve/" + revision.ToLowerInvariant() + "/" + escapedPath;
    }

    private static bool IsRepoId(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 200) return false;
        var parts = value.Split('/');
        return parts.Length == 2 && parts.All(part => part.Length > 0 && part.Length <= 100 && part is not "." and not ".."
            && part.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '.' or '_' or '-'));
    }

    private static bool IsRevision(string value) => value.Length is 40 or 64 && value.All(Uri.IsHexDigit);
    private static bool IsSha256(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);

    private static ModelProvenanceManifestDocument Empty() => new() { SchemaVersion = CurrentSchemaVersion, Entries = new(StringComparer.OrdinalIgnoreCase) };

    private static ModelProvenanceManifestDocument Clone(ModelProvenanceManifestDocument source)
    {
        var result = Empty();
        foreach (var (key, value) in source.Entries) result.Entries.Add(key, Clone(value));
        return result;
    }

    private static ModelDownloadProvenance Clone(ModelDownloadProvenance value) => new()
    {
        RepoId = value.RepoId,
        Revision = value.Revision,
        ModelCardUrl = value.ModelCardUrl,
        RepoFilePath = value.RepoFilePath,
        PinnedSourceUrl = value.PinnedSourceUrl,
        LicenseMetadataStatus = value.LicenseMetadataStatus,
        PublisherDeclaredLicense = value.PublisherDeclaredLicense,
        MetadataObservedUtc = value.MetadataObservedUtc,
        ReviewRequired = value.ReviewRequired,
        ReviewAcknowledged = value.ReviewAcknowledged,
        ReviewAcknowledgedUtc = value.ReviewAcknowledgedUtc,
        ExpectedSha256 = value.ExpectedSha256,
        ComputedSha256 = value.ComputedSha256,
        IntegrityStatus = value.IntegrityStatus,
        SizeBytes = value.SizeBytes,
        FileLastWriteUtc = value.FileLastWriteUtc,
        CompletedUtc = value.CompletedUtc
    };
}
