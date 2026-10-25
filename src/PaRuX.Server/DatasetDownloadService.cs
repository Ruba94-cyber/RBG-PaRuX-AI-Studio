using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>
/// Hugging Face Dataset Hub discovery/download. Download requests are re-derived from repo id +
/// relative file identity rather than trusting an arbitrary caller URL, storage paths are
/// canonicalized below PaRuX's dataset root, the 700 GB global AI budget is enforced while bytes
/// stream (not only from optimistic metadata), and known LFS hashes/sizes are fail-closed before a
/// .partial file may be promoted to completed content.
/// </summary>
public sealed class DatasetDownloadService
{
    private sealed class Entry
    {
        public readonly object Gate = new();
        public readonly DatasetDownloadStatus Status;
        public readonly long MaximumFinalBytes;
        public readonly long ExpectedSizeBytes;
        public CancellationTokenSource Cancellation = new();

        public Entry(DatasetDownloadStatus status, long maximumFinalBytes, long expectedSizeBytes)
        {
            Status = status;
            MaximumFinalBytes = maximumFinalBytes;
            ExpectedSizeBytes = expectedSizeBytes;
        }
    }

    private readonly HttpClient _http;
    private readonly HttpClient _metadataProbe;
    private readonly ConcurrentDictionary<string, Entry> _jobs = new();

    public DatasetDownloadService(HttpClient? httpClient = null, HttpClient? metadataProbe = null)
    {
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        _metadataProbe = metadataProbe ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromSeconds(30),
        };
    }

    /// <summary>Gaming Mode signal; production reads the persisted marker, tests inject.</summary>
    internal Func<bool> GamingActive { get; set; } = GamingHardOffWatch.MarkerActive;

    public static string DatasetsDirectory => Path.Combine(
        PaRuX.Contracts.PaRuXDataRoot.Path, "datasets");

    public async Task<List<DatasetSearchResult>> SearchAsync(string query, int limit, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query)) return new List<DatasetSearchResult>();
        limit = Math.Clamp(limit <= 0 ? 15 : limit, 1, 50);

        string url = "https://huggingface.co/api/datasets?search=" + Uri.EscapeDataString(query) + "&limit=" + limit;
        using HttpRequestMessage request = new(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("PaRuX/1.0");
        using HttpResponseMessage response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        await using Stream stream = await response.Content.ReadAsStreamAsync(ct);
        List<HfDataset>? datasets = await JsonSerializer.DeserializeAsync<List<HfDataset>>(stream, JsonOptions, ct);
        if (datasets == null) return new List<DatasetSearchResult>();

        return datasets
            .Where(dataset => TryNormalizeRepoId(dataset.Id, out _))
            .Select(dataset => new DatasetSearchResult
            {
                RepoId = dataset.Id,
                Author = dataset.Id.Contains('/') ? dataset.Id[..dataset.Id.IndexOf('/')] : string.Empty,
                Downloads = dataset.Downloads,
                Likes = dataset.Likes,
                Files = new List<DatasetFileOption>(),
            }).ToList();
    }

    public async Task<DatasetRepoDetails> GetDetailsAsync(string repoId, CancellationToken ct)
    {
        if (!TryNormalizeRepoId(repoId, out string normalizedRepo)) return new DatasetRepoDetails();

        string url = "https://huggingface.co/api/datasets/" + EscapeRepositoryPath(normalizedRepo);
        using HttpRequestMessage request = new(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("PaRuX/1.0");
        using HttpResponseMessage response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        await using Stream stream = await response.Content.ReadAsStreamAsync(ct);
        HfDataset? dataset = await JsonSerializer.DeserializeAsync<HfDataset>(stream, JsonOptions, ct);
        if (dataset == null) return new DatasetRepoDetails();

        var files = new List<DatasetFileOption>();
        foreach (HfSibling sibling in dataset.Siblings ?? new List<HfSibling>())
        {
            if (!TryNormalizeRelativeFileName(sibling.RFilename, out string safeFile)) continue;
            Uri uri = BuildDatasetDownloadUri(normalizedRepo, safeFile);
            files.Add(new DatasetFileOption
            {
                FileName = safeFile,
                SizeBytes = 0,
                DownloadUrl = uri.AbsoluteUri,
            });
        }

        string? license = dataset.CardData != null ? NormalizeLicense(dataset.CardData.License) : null;
        return new DatasetRepoDetails { License = license, Files = files };
    }

    public Task<AiStorageBudgetStatus> GetBudgetAsync(long additionalBytes = 0) =>
        AiStorageBudget.GetStatusAsync(additionalBytes);

    public async Task<DatasetDownloadStatus> CreateDownloadAsync(DatasetDownloadRequest request, CancellationToken ct)
    {
        if (request == null) throw new ArgumentNullException(nameof(request));
        if (!TryNormalizeRepoId(request.RepoId, out string repoId))
            throw new InvalidDataException("Invalid dataset repo id.");
        if (!TryNormalizeRelativeFileName(request.FileName, out string fileName))
            throw new InvalidDataException("Dataset fileName must be a safe relative path inside the selected repository.");

        Uri authoritativeUri = BuildDatasetDownloadUri(repoId, fileName);
        if (!MatchesAuthoritativeDownloadUri(request.DownloadUrl, authoritativeUri))
            throw new InvalidDataException("downloadUrl does not match the selected Hugging Face dataset repo/file.");

        long knownSize = 0;
        string? sha256 = null;
        try
        {
            using var head = new HttpRequestMessage(HttpMethod.Head, authoritativeUri);
            head.Headers.UserAgent.ParseAdd("PaRuX/1.0");
            using HttpResponseMessage headResponse = await _metadataProbe.SendAsync(head, ct);
            sha256 = ExtractSha256FromHeaders(headResponse);
            knownSize = ExtractLinkedSize(headResponse) ?? headResponse.Content.Headers.ContentLength ?? 0;

            if (knownSize <= 0 && (int)headResponse.StatusCode is >= 300 and < 400)
            {
                using var followHead = new HttpRequestMessage(HttpMethod.Head, authoritativeUri);
                followHead.Headers.UserAgent.ParseAdd("PaRuX/1.0");
                using HttpResponseMessage followed = await _http.SendAsync(followHead, ct);
                knownSize = followed.Content.Headers.ContentLength ?? 0;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            // The caller's size is only a progress/budget hint when source metadata cannot be
            // obtained; streaming still enforces the live global storage ceiling below.
            knownSize = Math.Max(0, request.SizeBytes);
        }

        string repoDirectory = Path.Combine(DatasetsDirectory, SanitizeRepoId(repoId));
        string finalPath = ResolveSafeDatasetPath(repoDirectory, fileName);
        string partialPath = finalPath + ".partial";
        long alreadyOnDisk = File.Exists(partialPath) ? new FileInfo(partialPath).Length : 0;

        AiStorageBudgetStatus currentBudget = await GetBudgetAsync(0);
        long maximumFinalBytes = Math.Max(0, currentBudget.LimitBytes - currentBudget.UsedBytes + alreadyOnDisk);
        if (maximumFinalBytes <= 0)
            throw new InvalidOperationException("The 700 GB AI storage budget is already exhausted.");
        if (knownSize > 0 && knownSize > maximumFinalBytes)
            throw new InvalidOperationException(
                "Downloading " + fileName + " (" + AiStorageBudget.FormatBytes(knownSize) + ") would exceed the 700 GB AI storage budget "
                + "(currently using " + AiStorageBudget.FormatBytes(currentBudget.UsedBytes) + "). Free up space or choose a smaller dataset.");

        var status = new DatasetDownloadStatus
        {
            Id = Guid.NewGuid().ToString("N"),
            RepoId = repoId,
            FileName = fileName,
            TotalBytes = knownSize,
            Sha256 = sha256,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow,
        };
        var entry = new Entry(status, maximumFinalBytes, knownSize);
        _jobs[status.Id] = entry;
        _ = Task.Run(() => RunDownloadAsync(entry, authoritativeUri, repoDirectory, fileName));
        return Snapshot(entry);
    }

    public IReadOnlyList<DatasetDownloadStatus> List() => _jobs.Values.Select(Snapshot).OrderByDescending(s => s.CreatedUtc).ToList();
    public DatasetDownloadStatus? Get(string id) => _jobs.TryGetValue(id, out Entry? entry) ? Snapshot(entry) : null;

    public bool Cancel(string id)
    {
        if (!_jobs.TryGetValue(id, out Entry? entry)) return false;
        lock (entry.Gate)
        {
            if (entry.Status.Status is "completed" or "failed" or "cancelled") return false;
        }
        entry.Cancellation.Cancel();
        return true;
    }

    internal static string SanitizeRepoId(string repoId)
    {
        if (!TryNormalizeRepoId(repoId, out string normalized))
            throw new InvalidDataException("Invalid dataset repo id.");
        return normalized.Replace('/', '_');
    }

    internal static bool TryNormalizeRepoId(string? repoId, out string normalized)
    {
        normalized = (repoId ?? string.Empty).Trim();
        if (normalized.Length is < 1 or > 200 || normalized.Contains('\\') || normalized.StartsWith('/') || normalized.EndsWith('/'))
            return false;
        string[] segments = normalized.Split('/');
        if (segments.Length is < 1 or > 2) return false;
        foreach (string segment in segments)
        {
            if (segment.Length is < 1 or > 100 || segment is "." or "..") return false;
            if (segment.Any(c => char.IsControl(c) || c is ':' or '?' or '#' or '%')) return false;
        }
        return true;
    }

    internal static bool TryNormalizeRelativeFileName(string? fileName, out string normalized)
    {
        // Backslashes are folded to '/' so a Windows-shaped payload is examined segment by segment
        // rather than passing through as one long file name - which is exactly what happens on
        // Linux, where a backslash is an ordinary character and Path.IsPathRooted("C:/x") is false.
        normalized = (fileName ?? string.Empty).Trim().Replace('\\', '/');
        if (normalized.Length is < 1 or > 1024 || normalized.StartsWith('/') || Path.IsPathRooted(normalized)) return false;
        string[] segments = normalized.Split('/');
        if (segments.Length > 64) return false;
        foreach (string segment in segments)
        {
            if (segment.Length is < 1 or > 255 || segment is "." or "..") return false;
            if (segment.Any(c => char.IsControl(c) || c == '\0' || c == ':')) return false;
            if (PaRuXPathSafety.LooksLikeWindowsDrive(segment)) return false;
        }
        normalized = string.Join('/', segments);
        return true;
    }

    internal static string ResolveSafeDatasetPath(string repoDirectory, string relativeFileName)
    {
        if (!TryNormalizeRelativeFileName(relativeFileName, out string safe))
            throw new InvalidDataException("Invalid dataset relative file path.");
        string candidate = Path.GetFullPath(Path.Combine(repoDirectory,
            safe.Replace('/', Path.DirectorySeparatorChar)));
        if (!PaRuXPathSafety.IsWithin(repoDirectory, candidate))
            throw new InvalidDataException("Dataset file path escapes the PaRuX dataset repository directory.");
        return candidate;
    }

    internal static Uri BuildDatasetDownloadUri(string repoId, string relativeFileName)
    {
        if (!TryNormalizeRepoId(repoId, out string safeRepo)
            || !TryNormalizeRelativeFileName(relativeFileName, out string safeFile))
            throw new InvalidDataException("Invalid Hugging Face dataset identity.");
        string escapedRepo = EscapeRepositoryPath(safeRepo);
        string escapedFile = string.Join('/', safeFile.Split('/').Select(Uri.EscapeDataString));
        return new Uri("https://huggingface.co/datasets/" + escapedRepo + "/resolve/main/" + escapedFile, UriKind.Absolute);
    }

    internal static bool MatchesAuthoritativeDownloadUri(string? candidate, Uri authoritative)
    {
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out Uri? uri)) return false;
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(uri.Host, "huggingface.co", StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment)) return false;
        return string.Equals(
            uri.GetComponents(UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.UriEscaped).TrimEnd('/'),
            authoritative.GetComponents(UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.UriEscaped).TrimEnd('/'),
            StringComparison.Ordinal);
    }

    private static string EscapeRepositoryPath(string repoId) =>
        string.Join('/', repoId.Split('/').Select(Uri.EscapeDataString));

    private async Task RunDownloadAsync(Entry entry, Uri authoritativeUri, string repoDirectory, string fileName)
    {
        Directory.CreateDirectory(repoDirectory);
        string finalPath = ResolveSafeDatasetPath(repoDirectory, fileName);
        string partialPath = finalPath + ".partial";
        Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);

        lock (entry.Gate) { entry.Status.Status = "downloading"; entry.Status.UpdatedUtc = DateTime.UtcNow; }
        // Gaming Mode pauses the transfer; the .partial is kept so a later start resumes it.
        using var gaming = new GamingHardOffWatch(GamingActive, entry.Cancellation.Token);
        CancellationToken ct = gaming.Token;
        try
        {
            long alreadyDownloaded = File.Exists(partialPath) ? new FileInfo(partialPath).Length : 0;
            if (alreadyDownloaded > entry.MaximumFinalBytes
                || (entry.ExpectedSizeBytes > 0 && alreadyDownloaded > entry.ExpectedSizeBytes))
            {
                File.Delete(partialPath);
                alreadyDownloaded = 0;
            }

            bool resumed = false;
            HttpResponseMessage? response = null;
            try
            {
                if (alreadyDownloaded > 0)
                {
                    using var rangeRequest = new HttpRequestMessage(HttpMethod.Get, authoritativeUri);
                    rangeRequest.Headers.Range = new RangeHeaderValue(alreadyDownloaded, null);
                    rangeRequest.Headers.UserAgent.ParseAdd("PaRuX/1.0");
                    HttpResponseMessage rangeResponse = await _http.SendAsync(
                        rangeRequest, HttpCompletionOption.ResponseHeadersRead, ct);
                    ContentRangeHeaderValue? contentRange = rangeResponse.Content.Headers.ContentRange;
                    bool validRange = rangeResponse.StatusCode == HttpStatusCode.PartialContent
                        && contentRange?.From == alreadyDownloaded
                        && (!entry.ExpectedSizeBytes.Equals(0L) ? contentRange.Length is null || contentRange.Length == entry.ExpectedSizeBytes : true);
                    if (validRange)
                    {
                        response = rangeResponse;
                        resumed = true;
                    }
                    else
                    {
                        rangeResponse.Dispose();
                        try { File.Delete(partialPath); } catch { }
                        alreadyDownloaded = 0;
                    }
                }

                if (response == null)
                {
                    using var fullRequest = new HttpRequestMessage(HttpMethod.Get, authoritativeUri);
                    fullRequest.Headers.UserAgent.ParseAdd("PaRuX/1.0");
                    response = await _http.SendAsync(
                        fullRequest, HttpCompletionOption.ResponseHeadersRead, ct);
                }

                response.EnsureSuccessStatusCode();

                long responseTotal;
                if (resumed)
                {
                    long? remaining = response.Content.Headers.ContentLength;
                    responseTotal = remaining.HasValue ? alreadyDownloaded + remaining.Value : Math.Max(entry.Status.TotalBytes, alreadyDownloaded);
                }
                else
                {
                    responseTotal = response.Content.Headers.ContentLength ?? entry.Status.TotalBytes;
                }
                if (responseTotal > 0 && responseTotal > entry.MaximumFinalBytes)
                    throw new InvalidDataException("Dataset download exceeds the remaining PaRuX AI storage budget.");
                if (entry.ExpectedSizeBytes > 0 && responseTotal > 0 && responseTotal != entry.ExpectedSizeBytes)
                    throw new InvalidDataException("Dataset response size does not match authenticated/observed source metadata.");

                lock (entry.Gate)
                {
                    entry.Status.TotalBytes = responseTotal > 0 ? responseTotal : entry.ExpectedSizeBytes;
                    entry.Status.BytesDownloaded = alreadyDownloaded;
                }

                FileMode fileMode = resumed ? FileMode.Append : FileMode.Create;
                await using Stream source = await response.Content.ReadAsStreamAsync(ct);
                await using (FileStream target = new(partialPath, fileMode, FileAccess.Write, FileShare.None, 1024 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    byte[] buffer = new byte[1024 * 1024];
                    long downloaded = alreadyDownloaded;
                    int read;
                    while ((read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
                    {
                        long next = checked(downloaded + read);
                        if (next > entry.MaximumFinalBytes)
                            throw new InvalidDataException("Dataset download exceeded the remaining PaRuX AI storage budget.");
                        if (entry.ExpectedSizeBytes > 0 && next > entry.ExpectedSizeBytes)
                            throw new InvalidDataException("Dataset download exceeded the expected source size.");
                        await target.WriteAsync(buffer.AsMemory(0, read), ct);
                        downloaded = next;
                        lock (entry.Gate)
                        {
                            entry.Status.BytesDownloaded = downloaded;
                            entry.Status.UpdatedUtc = DateTime.UtcNow;
                        }
                    }
                    await target.FlushAsync(ct);
                }
            }
            finally
            {
                response?.Dispose();
            }

            long finalSize = new FileInfo(partialPath).Length;
            if (entry.ExpectedSizeBytes > 0 && finalSize != entry.ExpectedSizeBytes)
            {
                TryDelete(partialPath);
                lock (entry.Gate) entry.Status.VerificationStatus = "failed";
                throw new InvalidDataException("Downloaded dataset size does not match source metadata.");
            }

            string verificationStatus = "unavailable";
            if (!string.IsNullOrEmpty(entry.Status.Sha256))
            {
                string actualHash = await ComputeSha256Async(partialPath, ct);
                if (!string.Equals(actualHash, entry.Status.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    TryDelete(partialPath);
                    lock (entry.Gate) entry.Status.VerificationStatus = "failed";
                    throw new InvalidDataException("Downloaded dataset SHA-256 does not match Hugging Face LFS metadata.");
                }
                verificationStatus = "verified";
            }

            if (File.Exists(finalPath)) File.Delete(finalPath);
            File.Move(partialPath, finalPath);

            lock (entry.Gate)
            {
                entry.Status.Status = "completed";
                entry.Status.LocalPath = finalPath;
                entry.Status.VerificationStatus = verificationStatus;
                entry.Status.UpdatedUtc = DateTime.UtcNow;
            }
        }
        catch (OperationCanceledException)
        {
            lock (entry.Gate)
            {
                entry.Status.Status = "cancelled";
                if (gaming.Triggered && !entry.Cancellation.IsCancellationRequested)
                    entry.Status.Error = ModelDownloadService.GamingPauseReason;
                entry.Status.UpdatedUtc = DateTime.UtcNow;
            }
        }
        catch (Exception ex)
        {
            lock (entry.Gate)
            {
                entry.Status.Status = "failed";
                entry.Status.Error = ex.Message;
                entry.Status.UpdatedUtc = DateTime.UtcNow;
            }
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken ct)
    {
        await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        byte[] hash = await SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string? ExtractSha256FromHeaders(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("X-Linked-ETag", out IEnumerable<string>? values)) return null;
        string? raw = values.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(raw)) return null;
        string trimmed = raw.Trim().Trim('"');
        return trimmed.Length == 64 && trimmed.All(Uri.IsHexDigit) ? trimmed.ToLowerInvariant() : null;
    }

    private static long? ExtractLinkedSize(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("X-Linked-Size", out IEnumerable<string>? values)) return null;
        string? raw = values.FirstOrDefault();
        return long.TryParse(raw, out long size) && size > 0 ? size : null;
    }

    private static DatasetDownloadStatus Snapshot(Entry entry)
    {
        lock (entry.Gate)
        {
            DatasetDownloadStatus s = entry.Status;
            return new DatasetDownloadStatus
            {
                Id = s.Id, RepoId = s.RepoId, Status = s.Status, FileName = s.FileName,
                BytesDownloaded = s.BytesDownloaded, TotalBytes = s.TotalBytes,
                Error = s.Error, LocalPath = s.LocalPath,
                CreatedUtc = s.CreatedUtc, UpdatedUtc = s.UpdatedUtc,
                Sha256 = s.Sha256, VerificationStatus = s.VerificationStatus,
            };
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private sealed class HfDataset
    {
        [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
        [JsonPropertyName("downloads")] public int Downloads { get; set; }
        [JsonPropertyName("likes")] public int Likes { get; set; }
        [JsonPropertyName("siblings")] public List<HfSibling>? Siblings { get; set; }
        [JsonPropertyName("cardData")] public HfCardData? CardData { get; set; }
    }

    private sealed class HfSibling
    {
        [JsonPropertyName("rfilename")] public string RFilename { get; set; } = string.Empty;
    }

    private sealed class HfCardData
    {
        [JsonPropertyName("license")] public JsonElement License { get; set; }
    }

    internal static string? NormalizeLicense(JsonElement license)
    {
        string? result = license.ValueKind switch
        {
            JsonValueKind.String => license.GetString(),
            JsonValueKind.Array => string.Join(", ", license.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.String)
                .Select(e => e.GetString())),
            _ => null,
        };
        return string.IsNullOrWhiteSpace(result) ? null : result;
    }
}
