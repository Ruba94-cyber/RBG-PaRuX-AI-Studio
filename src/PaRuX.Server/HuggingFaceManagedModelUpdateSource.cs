using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>
/// Update source for one existing PaRuX-managed Hugging Face model file. It compares the real
/// local SHA-256 to the current LFS SHA-256 for the same upstream file. Repo revision alone is not
/// enough to call a model update: a README-only commit must not make PaRuX redownload a 20 GB GGUF.
/// </summary>
public sealed class HuggingFaceManagedModelUpdateSource : IManagedAiUpdateSource
{
    private static readonly HttpClient SharedHttp = CreateHttp();
    private readonly ManagedModelInfo _model;
    private readonly ManagedModelRevisionStore _revisions;
    private readonly HttpClient _http;

    public HuggingFaceManagedModelUpdateSource(
        ManagedModelInfo model,
        ManagedModelRevisionStore? revisions = null,
        HttpClient? httpClient = null)
    {
        if (string.IsNullOrWhiteSpace(model.RepoId))
            throw new ArgumentException("A managed model RepoId is required for update discovery.", nameof(model));
        ValidateRepoId(model.RepoId);
        _model = model;
        _revisions = revisions ?? new ManagedModelRevisionStore();
        _http = httpClient ?? SharedHttp;
        Asset = new ManagedAiAssetIdentity(
            ManagedAiAssetKind.Model,
            model.RepoId,
            string.IsNullOrWhiteSpace(model.Alias) ? model.FileName : model.Alias!,
            VariantId: model.FileName,
            EngineId: LocalInferenceEngineRegistry.LlamaCppCudaId);
    }

    public ManagedAiAssetIdentity Asset { get; }

    public async Task<ManagedAiUpdateCheckResult> CheckAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_model.FilePath))
            return NoUpdate("The managed model file no longer exists.");

        Snapshot snapshot = await GetSnapshotAsync(_model.RepoId!, cancellationToken).ConfigureAwait(false);
        List<SnapshotFile> sameBasename = snapshot.Files
            .Where(file => string.Equals(Path.GetFileName(file.Path.Replace('\\', '/')), _model.FileName, StringComparison.Ordinal))
            .ToList();
        if (sameBasename.Count != 1)
            return NoUpdate(sameBasename.Count == 0
                ? "The installed variant no longer exists in the upstream repository."
                : "Multiple upstream files share this basename; PaRuX will not guess which artifact is installed.");

        SnapshotFile upstream = sameBasename[0];
        if (string.IsNullOrWhiteSpace(upstream.Sha256))
            return NoUpdate("Hugging Face did not expose an LFS SHA-256 for this variant; update status remains unverified.");

        FileInfo file = new(_model.FilePath);
        ManagedModelRevisionRecord? cached = _revisions.Find(_model.FileName, _model.RepoId!);
        string localSha;
        if (_revisions.CanReuseLocalHash(cached, file))
        {
            localSha = cached!.LocalSha256;
        }
        else
        {
            await using FileStream input = File.OpenRead(file.FullName);
            localSha = Convert.ToHexString(
                await SHA256.HashDataAsync(input, cancellationToken).ConfigureAwait(false)).ToLowerInvariant();
        }

        string upstreamSha = upstream.Sha256!.ToLowerInvariant();
        bool currentArtifact = string.Equals(localSha, upstreamSha, StringComparison.OrdinalIgnoreCase);
        ManagedModelRevisionRecord stored = _revisions.Save(new ManagedModelRevisionRecord
        {
            FileName = _model.FileName,
            RepoId = _model.RepoId!,
            RepoRevision = currentArtifact ? snapshot.Revision : cached?.RepoRevision,
            LocalSha256 = localSha,
            UpstreamArtifactSha256 = upstreamSha,
            SizeBytes = file.Length,
            LastWriteTimeUtc = file.LastWriteTimeUtc
        });

        if (currentArtifact)
            return NoUpdate("Installed artifact matches the current upstream LFS SHA-256.", snapshot.Revision);

        string installed = !string.IsNullOrWhiteSpace(stored.RepoRevision)
            ? stored.RepoRevision!
            : "artifact:" + localSha[..12];
        string version = string.IsNullOrWhiteSpace(snapshot.Revision)
            ? "artifact:" + upstreamSha[..12]
            : snapshot.Revision!;
        string source = BuildResolveUrl(_model.RepoId!, snapshot.Revision ?? "main", upstream.Path);
        return new ManagedAiUpdateCheckResult(
            Asset,
            UpdateAvailable: true,
            InstalledVersion: installed,
            AvailableVersion: version,
            SourceRevision: snapshot.Revision,
            SourceUri: source,
            ExpectedSha256: upstreamSha,
            Message: "The installed model artifact differs from the current upstream variant. PaRuX can stage the new revision side-by-side before promotion.");
    }

    private ManagedAiUpdateCheckResult NoUpdate(string message, string? installedVersion = null) => new(
        Asset,
        UpdateAvailable: false,
        InstalledVersion: installedVersion,
        AvailableVersion: null,
        SourceRevision: null,
        SourceUri: null,
        ExpectedSha256: null,
        Message: message);

    private async Task<Snapshot> GetSnapshotAsync(string repoId, CancellationToken ct)
    {
        string url = "https://huggingface.co/api/models/" + repoId + "?blobs=true";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("PaRuX-ModelUpdate/1.0");
        using HttpResponseMessage response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using Stream stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        HfInfo? info = await JsonSerializer.DeserializeAsync<HfInfo>(stream, JsonOptions, ct).ConfigureAwait(false);
        if (info == null) throw new InvalidDataException("Hugging Face returned empty model metadata.");

        var files = (info.Siblings ?? new())
            .Select(sibling => new SnapshotFile(
                sibling.RFilename,
                NormalizeSha(sibling.Lfs?.Sha256 ?? sibling.Lfs?.Oid),
                sibling.Size ?? sibling.Lfs?.Size))
            .ToList();
        return new Snapshot(info.Sha, files);
    }

    internal static string BuildResolveUrl(string repoId, string revision, string repoPath)
    {
        ValidateRepoId(repoId);
        if (string.IsNullOrWhiteSpace(revision) || revision.Length > 100 || revision.Any(ch => !(char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.')))
            throw new InvalidDataException("Hugging Face revision contains unsupported characters.");
        string[] segments = repoPath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(segment => segment is "." or ".."))
            throw new InvalidDataException("Hugging Face model path is unsafe.");
        return "https://huggingface.co/" + repoId + "/resolve/" + Uri.EscapeDataString(revision)
            + "/" + string.Join('/', segments.Select(Uri.EscapeDataString));
    }

    internal static void ValidateRepoId(string repoId)
    {
        string[] parts = (repoId ?? string.Empty).Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 || parts.Any(part => part.Length is < 1 or > 100
            || part is "." or ".."
            || part.Any(ch => !(char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.'))))
            throw new InvalidDataException("Hugging Face repo id must be a safe owner/repository pair.");
    }

    private static string? NormalizeSha(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        string sha = value.Trim();
        if (sha.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)) sha = sha[7..];
        return sha.Length == 64 && sha.All(Uri.IsHexDigit) ? sha.ToLowerInvariant() : null;
    }

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("PaRuX-ModelUpdate/1.0");
        return http;
    }

    private sealed record Snapshot(string? Revision, List<SnapshotFile> Files);
    private sealed record SnapshotFile(string Path, string? Sha256, long? Size);

    private sealed class HfInfo
    {
        [JsonPropertyName("sha")] public string? Sha { get; set; }
        [JsonPropertyName("siblings")] public List<HfSibling>? Siblings { get; set; }
    }

    private sealed class HfSibling
    {
        [JsonPropertyName("rfilename")] public string RFilename { get; set; } = string.Empty;
        [JsonPropertyName("size")] public long? Size { get; set; }
        [JsonPropertyName("lfs")] public HfLfs? Lfs { get; set; }
    }

    private sealed class HfLfs
    {
        [JsonPropertyName("size")] public long? Size { get; set; }
        [JsonPropertyName("sha256")] public string? Sha256 { get; set; }
        [JsonPropertyName("oid")] public string? Oid { get; set; }
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
}

public static class HuggingFaceManagedModelUpdateSources
{
    public static IReadOnlyList<IManagedAiUpdateSource> Build(ModelDownloadService models)
    {
        return models.ListManagedModels()
            .Where(model => !string.IsNullOrWhiteSpace(model.RepoId))
            .Select(model => (IManagedAiUpdateSource)new HuggingFaceManagedModelUpdateSource(model))
            .ToList();
    }
}
