using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using PaRuX.Contracts;

namespace PaRuX.Server;

public sealed class ManagedModelUpdateInstallRequest
{
    public ManagedAiAssetIdentity Asset { get; set; } = new(ManagedAiAssetKind.Model, string.Empty, string.Empty);
}

public sealed class ManagedModelUpdateInstallJobStatus
{
    public string Id { get; set; } = string.Empty;
    public ManagedAiAssetIdentity Asset { get; set; } = new(ManagedAiAssetKind.Model, string.Empty, string.Empty);
    public string State { get; set; } = "queued";
    public long BytesDownloaded { get; set; }
    public long TotalBytes { get; set; }
    public string? StagedFilePath { get; set; }
    public string? InstalledVersion { get; set; }
    public string? Error { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
}

/// <summary>
/// Update Center install job. Hugging Face model updates are downloaded SHA-verified side-by-side;
/// the Windows ML TensorRT-RTX performance pack is delegated to the OS-owned certified provider
/// lifecycle. Neither path promotes the candidate into active inference: successful work ends as
/// STAGED/READY_TO_PROMOTE and the previous production engine/model remains authoritative.
/// </summary>
public sealed class ManagedModelUpdateInstallJobService : IDisposable
{
    private sealed class Entry
    {
        public readonly object Gate = new();
        public readonly ManagedModelUpdateInstallJobStatus Status;
        public readonly CancellationTokenSource Cancellation = new();
        public Entry(ManagedModelUpdateInstallJobStatus status) => Status = status;
    }

    private static readonly HttpClient SharedHttp = CreateHttp();
    private readonly ManagedAiUpdateStore _updates;
    private readonly ManagedModelRevisionStore _revisions;
    private readonly HttpClient _http;
    private readonly WindowsMlPerformancePackService _windowsMl;
    private readonly ConcurrentDictionary<string, Entry> _jobs = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ManagedModelUpdateInstallJobService(
        ManagedAiUpdateStore? updates = null,
        ManagedModelRevisionStore? revisions = null,
        HttpClient? httpClient = null,
        WindowsMlPerformancePackService? windowsMl = null)
    {
        _updates = updates ?? new ManagedAiUpdateStore();
        _revisions = revisions ?? new ManagedModelRevisionStore();
        _http = httpClient ?? SharedHttp;
        _windowsMl = windowsMl ?? new WindowsMlPerformancePackService();
    }

    public ManagedModelUpdateInstallJobStatus Start(ManagedAiAssetIdentity asset)
    {
        ValidateInstallableAsset(asset);
        ManagedAiUpdateRecord record = _updates.Find(asset)
            ?? throw new InvalidOperationException("Run Update Check before installing this managed update.");
        if (record.State is not (ManagedAiUpdateState.Available or ManagedAiUpdateState.Failed
            or ManagedAiUpdateState.Cancelled or ManagedAiUpdateState.Dismissed))
            throw new InvalidOperationException("This managed update is already being processed, staged, or installed.");

        if (asset.Kind == ManagedAiAssetKind.Model)
        {
            if (string.IsNullOrWhiteSpace(record.SourceUri) || string.IsNullOrWhiteSpace(record.ExpectedSha256))
                throw new InvalidOperationException("Model update lacks an authenticated source URI/SHA-256 and cannot be installed automatically.");
        }

        var status = new ManagedModelUpdateInstallJobStatus
        {
            Id = Guid.NewGuid().ToString("N"),
            Asset = asset,
            State = "queued",
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        };
        var entry = new Entry(status);
        _jobs[status.Id] = entry;
        _ = Task.Run(() => RunAsync(entry));
        return Clone(status);
    }

    public ManagedModelUpdateInstallJobStatus? Get(string id) =>
        _jobs.TryGetValue(id, out Entry? entry) ? Snapshot(entry) : null;

    public bool Cancel(string id)
    {
        if (!_jobs.TryGetValue(id, out Entry? entry)) return false;
        lock (entry.Gate)
        {
            if (entry.Status.State is "completed" or "failed" or "cancelled") return false;
            entry.Cancellation.Cancel();
            return true;
        }
    }

    private async Task RunAsync(Entry entry)
    {
        bool acquired = false;
        try
        {
            await _gate.WaitAsync(entry.Cancellation.Token).ConfigureAwait(false);
            acquired = true;
            if (IsWindowsMlTensorRt(entry.Status.Asset))
                await RunWindowsMlAsync(entry, entry.Cancellation.Token).ConfigureAwait(false);
            else
                await RunModelAsync(entry, entry.Cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Set(entry, "cancelled");
            TryTransition(entry.Status.Asset, ManagedAiUpdateState.Cancelled, "Managed update cancelled.");
        }
        catch (Exception ex)
        {
            lock (entry.Gate)
            {
                entry.Status.State = "failed";
                entry.Status.Error = ex.Message;
                entry.Status.UpdatedUtc = DateTime.UtcNow;
            }
            TryTransition(entry.Status.Asset, ManagedAiUpdateState.Failed, "Managed update failed: " + ex.Message);
        }
        finally
        {
            if (acquired) _gate.Release();
        }
    }

    private async Task RunModelAsync(Entry entry, CancellationToken ct)
    {
        ManagedAiUpdateRecord update = _updates.Find(entry.Status.Asset)
            ?? throw new InvalidOperationException("Model update disappeared before installation.");
        _updates.Transition(entry.Status.Asset, ManagedAiUpdateState.Downloading, "Downloading model update side-by-side…");
        Set(entry, "downloading");
        string staged = await DownloadAndStageAsync(entry, update, ct).ConfigureAwait(false);
        lock (entry.Gate)
        {
            entry.Status.State = "completed";
            entry.Status.StagedFilePath = staged;
            entry.Status.InstalledVersion = update.SourceRevision ?? update.AvailableVersion;
            entry.Status.UpdatedUtc = DateTime.UtcNow;
        }
        _updates.Transition(
            entry.Status.Asset,
            ManagedAiUpdateState.Staged,
            "Model update downloaded and SHA-256 verified side-by-side at " + Path.GetFileName(staged)
                + ". The previous model remains untouched until promotion.");
    }

    private async Task RunWindowsMlAsync(Entry entry, CancellationToken ct)
    {
        _updates.Transition(
            entry.Status.Asset,
            ManagedAiUpdateState.Downloading,
            "Asking Windows ML to make the certified TensorRT-RTX performance pack ready…");
        Set(entry, "installing-system-pack");

        WindowsMlPerformancePackStatus status = await _windowsMl.EnsureTensorRtRtxAsync(ct).ConfigureAwait(false);
        if (!status.TensorRtRtxReady)
            throw new InvalidOperationException(status.Error ?? "TensorRT-RTX did not become Ready.");

        string version = status.TensorRtRtxFingerprint
            ?? status.TensorRtRtxLibraryFileVersion
            ?? "windowsml-ready";
        _updates.Transition(
            entry.Status.Asset,
            ManagedAiUpdateState.Staged,
            "Windows ML made TensorRT-RTX Ready; PaRuX is recording the installed provider identity.");
        _updates.Transition(
            entry.Status.Asset,
            ManagedAiUpdateState.Verifying,
            "TensorRT-RTX provider readiness and library fingerprint verified through the PaRuX Windows ML worker.");
        _updates.Transition(
            entry.Status.Asset,
            ManagedAiUpdateState.ReadyToPromote,
            "TensorRT-RTX is installed and Ready but remains inactive until it passes PaRuX Engine Race.",
            installedVersion: version);

        lock (entry.Gate)
        {
            entry.Status.State = "completed";
            entry.Status.StagedFilePath = status.TensorRtRtxLibraryPath;
            entry.Status.InstalledVersion = version;
            entry.Status.UpdatedUtc = DateTime.UtcNow;
        }
    }

    private async Task<string> DownloadAndStageAsync(
        Entry entry,
        ManagedAiUpdateRecord update,
        CancellationToken ct)
    {
        Uri source = ValidateSource(update);
        string originalName = update.Asset.VariantId!;
        string expected = update.ExpectedSha256!.ToLowerInvariant();
        string revisionToken = SafeRevisionToken(update.SourceRevision ?? update.AvailableVersion);
        string stagedName = BuildStagedFileName(originalName, revisionToken, expected);
        Directory.CreateDirectory(ModelDownloadService.ModelsDirectory);
        string destination = Path.Combine(ModelDownloadService.ModelsDirectory, stagedName);
        string partial = destination + ".partial";

        if (File.Exists(destination))
        {
            await using FileStream existing = File.OpenRead(destination);
            string existingHash = Convert.ToHexString(await SHA256.HashDataAsync(existing, ct).ConfigureAwait(false)).ToLowerInvariant();
            if (string.Equals(existingHash, expected, StringComparison.Ordinal)) return destination;
            throw new InvalidDataException("A staged model file with the target name already exists but has a different SHA-256.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, source);
        using HttpResponseMessage response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        ValidateFinalDownloadHost(response.RequestMessage?.RequestUri);
        long total = response.Content.Headers.ContentLength ?? 0;
        if (total > 0)
        {
            AiStorageBudgetStatus budget = await AiStorageBudget.GetStatusAsync(total).ConfigureAwait(false);
            if (!budget.WithinBudget)
                throw new InvalidOperationException("Model update would exceed PaRuX's AI storage budget.");
        }
        lock (entry.Gate) { entry.Status.TotalBytes = total; entry.Status.UpdatedUtc = DateTime.UtcNow; }

        try
        {
            await using Stream sourceStream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var target = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None);
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] buffer = new byte[1024 * 1024];
            long downloaded = 0;
            while (true)
            {
                int read = await sourceStream.ReadAsync(buffer, ct).ConfigureAwait(false);
                if (read <= 0) break;
                await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                sha.AppendData(buffer, 0, read);
                downloaded = checked(downloaded + read);
                if (total > 0 && downloaded > total)
                    throw new InvalidDataException("Model update exceeded its declared Content-Length.");
                lock (entry.Gate)
                {
                    entry.Status.BytesDownloaded = downloaded;
                    entry.Status.UpdatedUtc = DateTime.UtcNow;
                }
            }
            await target.FlushAsync(ct).ConfigureAwait(false);
            if (total > 0 && downloaded != total)
                throw new InvalidDataException("Model update byte count did not match Content-Length.");
            string actual = Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
            if (!string.Equals(actual, expected, StringComparison.Ordinal))
                throw new InvalidDataException("Model update failed SHA-256 verification; staged bytes were discarded.");
        }
        catch
        {
            try { File.Delete(partial); } catch { }
            throw;
        }

        File.Move(partial, destination, overwrite: false);
        FileInfo info = new(destination);
        _revisions.Save(new ManagedModelRevisionRecord
        {
            FileName = stagedName,
            RepoId = update.Asset.AssetId,
            RepoRevision = update.SourceRevision ?? update.AvailableVersion,
            LocalSha256 = expected,
            UpstreamArtifactSha256 = expected,
            SizeBytes = info.Length,
            LastWriteTimeUtc = info.LastWriteTimeUtc
        });
        return destination;
    }

    internal static string BuildStagedFileName(string originalName, string revisionToken, string sha256)
    {
        string extension = Path.GetExtension(originalName);
        string stem = Path.GetFileNameWithoutExtension(originalName);
        string suffix = "__parux_" + revisionToken + "_" + sha256[..12];
        int maxStem = Math.Max(8, 230 - extension.Length - suffix.Length);
        if (stem.Length > maxStem) stem = stem[..maxStem];
        return stem + suffix + extension;
    }

    private static Uri ValidateSource(ManagedAiUpdateRecord update)
    {
        if (!Uri.TryCreate(update.SourceUri, UriKind.Absolute, out Uri? uri)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(uri.Host, "huggingface.co", StringComparison.OrdinalIgnoreCase)
            || !uri.IsDefaultPort || !string.IsNullOrEmpty(uri.UserInfo))
            throw new InvalidDataException("Model update source must be an HTTPS huggingface.co resolve URL.");
        string prefix = "/" + update.Asset.AssetId + "/resolve/";
        string decoded = Uri.UnescapeDataString(uri.AbsolutePath);
        if (!decoded.StartsWith(prefix, StringComparison.Ordinal))
            throw new InvalidDataException("Model update source is outside the declared Hugging Face repository.");
        if (!string.Equals(Path.GetFileName(decoded), update.Asset.VariantId, StringComparison.Ordinal))
            throw new InvalidDataException("Model update source filename does not match the discovered variant.");
        return uri;
    }

    private static void ValidateFinalDownloadHost(Uri? uri)
    {
        if (uri == null || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Model update download ended outside HTTPS.");
        string host = uri.Host.ToLowerInvariant();
        bool allowed = host == "huggingface.co"
            || host.EndsWith(".huggingface.co", StringComparison.Ordinal)
            || host == "cdn-lfs.hf.co" || host.EndsWith(".hf.co", StringComparison.Ordinal)
            || host.EndsWith(".xethub.hf.co", StringComparison.Ordinal);
        if (!allowed) throw new InvalidDataException("Model update redirected outside trusted Hugging Face download infrastructure.");
    }

    private static string SafeRevisionToken(string value)
    {
        string token = new string(value.Where(char.IsLetterOrDigit).Take(16).ToArray()).ToLowerInvariant();
        return token.Length > 0 ? token : "revision";
    }

    private static void ValidateInstallableAsset(ManagedAiAssetIdentity asset)
    {
        if (asset.Kind == ManagedAiAssetKind.Model)
        {
            if (string.IsNullOrWhiteSpace(asset.AssetId) || string.IsNullOrWhiteSpace(asset.VariantId))
                throw new InvalidDataException("A managed model update asset with repo id and variant id is required.");
            HuggingFaceManagedModelUpdateSource.ValidateRepoId(asset.AssetId);
            return;
        }
        if (IsWindowsMlTensorRt(asset)) return;
        throw new InvalidDataException("This Update Center asset kind does not have an automatic installer.");
    }

    private static bool IsWindowsMlTensorRt(ManagedAiAssetIdentity asset) =>
        asset.Kind == ManagedAiAssetKind.InferenceEngine
        && string.Equals(asset.AssetId, WindowsMlPerformancePackService.TensorRtEngineId, StringComparison.OrdinalIgnoreCase);

    private void TryTransition(ManagedAiAssetIdentity asset, ManagedAiUpdateState state, string message)
    {
        try { _updates.Transition(asset, state, message); } catch { }
    }

    private static void Set(Entry entry, string state)
    {
        lock (entry.Gate)
        {
            entry.Status.State = state;
            entry.Status.UpdatedUtc = DateTime.UtcNow;
        }
    }

    private static ManagedModelUpdateInstallJobStatus Snapshot(Entry entry)
    {
        lock (entry.Gate) return Clone(entry.Status);
    }

    private static ManagedModelUpdateInstallJobStatus Clone(ManagedModelUpdateInstallJobStatus value) => new()
    {
        Id = value.Id,
        Asset = value.Asset,
        State = value.State,
        BytesDownloaded = value.BytesDownloaded,
        TotalBytes = value.TotalBytes,
        StagedFilePath = value.StagedFilePath,
        InstalledVersion = value.InstalledVersion,
        Error = value.Error,
        CreatedUtc = value.CreatedUtc,
        UpdatedUtc = value.UpdatedUtc
    };

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromHours(4) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("PaRuX-ModelUpdate/1.0");
        return http;
    }

    public void Dispose()
    {
        foreach (Entry entry in _jobs.Values)
        {
            try { entry.Cancellation.Cancel(); } catch { }
            entry.Cancellation.Dispose();
        }
        _gate.Dispose();
    }
}
