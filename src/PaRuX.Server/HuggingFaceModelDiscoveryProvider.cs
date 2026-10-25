using System.Buffers;
using System.Net;
using System.Text.Json;
using PaRuX.Contracts;

namespace PaRuX.Server;

internal interface IModelDiscoveryProvider
{
    Task<IReadOnlyList<ModelDiscoveryCandidate>> DiscoverAsync(CancellationToken cancellationToken);
}

internal interface IExplicitModelDiscoveryProvider
{
    Task<IReadOnlyList<ModelDiscoveryCandidate>> DiscoverExplicitAsync(
        IReadOnlyList<string> repositoryIds, CancellationToken cancellationToken);
}

internal sealed class ModelDiscoveryFetchException(string code, TimeSpan? retryAfter = null) : Exception(code)
{
    internal string Code { get; } = code;
    internal TimeSpan? RetryAfter { get; } = retryAfter;
}

/// <summary>Version 1 reads the bounded public GGUF feed and individual repository detail documents.
/// It never fetches README, executes repository code, or follows an HTTP redirect.</summary>
internal sealed class HuggingFaceModelDiscoveryProvider : IModelDiscoveryProvider, IExplicitModelDiscoveryProvider
{
    private const int MaxRepositories = 8;
    private const int MaxFilesPerRepository = 40;
    // Discovery provenance only. Neither cohort membership nor download counts are quality evidence.
    private static readonly (string Label, string Sort, int Limit)[] Cohorts =
    {
        ("popular", "downloads", MaxRepositories),
        ("new", "createdAt", 6),
        ("recently_updated", "lastModified", 6)
    };
    private readonly HttpClient _http;

    internal HuggingFaceModelDiscoveryProvider(HttpClient? http = null) =>
        _http = http ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });

    public Task<IReadOnlyList<ModelDiscoveryCandidate>> DiscoverAsync(CancellationToken cancellationToken) =>
        DiscoverCoreAsync(null, cancellationToken);

    public Task<IReadOnlyList<ModelDiscoveryCandidate>> DiscoverExplicitAsync(
        IReadOnlyList<string> repositoryIds, CancellationToken cancellationToken)
    {
        if (!ModelDiscoveryService.TryValidateExplicitRepositoryIds(repositoryIds, out string[] ids))
            throw new ModelDiscoveryFetchException("invalid_repository_ids");
        return DiscoverCoreAsync(ids, cancellationToken);
    }

    private async Task<IReadOnlyList<ModelDiscoveryCandidate>> DiscoverCoreAsync(
        IReadOnlyList<string>? explicitRepositoryIds, CancellationToken cancellationToken)
    {
        var candidates = new List<ModelDiscoveryCandidate>();
        if (explicitRepositoryIds is not null)
        {
            // An exact user-requested repository lookup must not depend on the popularity feed.
            // Resolve only the validated targets: no unrelated models, extra requests or
            // failure because the broad discovery endpoint is unavailable.
            foreach (string repoId in explicitRepositoryIds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await AppendDetailsAsync(repoId, true, candidates, cancellationToken);
            }
            return candidates;
        }

        // Each cohort is an independent, bounded public feed. A failing cohort never discards the
        // others; each repository's details are fetched once and the resulting candidate keeps ONE
        // identity (repo+revision+file) with every cohort label that listed it.
        var byKey = new Dictionary<string, ModelDiscoveryCandidate>(StringComparer.Ordinal);
        var detailsByRepo = new Dictionary<string, List<ModelDiscoveryCandidate>>(StringComparer.Ordinal);
        ModelDiscoveryFetchException? firstFailure = null;
        int succeededCohorts = 0;
        foreach ((string label, string sort, int limit) in Cohorts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using JsonDocument feed = await FetchAsync(
                    new Uri($"https://huggingface.co/api/models?filter=gguf&sort={sort}&direction=-1&limit={limit}&full=true"),
                    2 * 1024 * 1024, cancellationToken);
                if (feed.RootElement.ValueKind != JsonValueKind.Array)
                    throw new ModelDiscoveryFetchException("invalid_feed");

                var seenRepositories = new HashSet<string>(StringComparer.Ordinal);
                foreach (JsonElement item in feed.RootElement.EnumerateArray().Take(limit))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string? repoId = ReadString(item, "id", 193);
                    if (!ModelDiscoveryIdentity.IsSafeRepoId(repoId) || !seenRepositories.Add(repoId!)) continue;
                    if (!detailsByRepo.TryGetValue(repoId!, out List<ModelDiscoveryCandidate>? repoCandidates))
                    {
                        repoCandidates = new List<ModelDiscoveryCandidate>();
                        try
                        {
                            await AppendDetailsAsync(repoId!, false, repoCandidates, cancellationToken);
                        }
                        catch (ModelDiscoveryFetchException ex) when (ex.Code is "http_error" or "invalid_feed" or "response_too_large")
                        {
                            // A single deleted or malformed public repository must not discard otherwise
                            // valid candidates. Do not swallow rate limits, redirects, timeouts or cancellation.
                            repoCandidates.Clear();
                        }
                        detailsByRepo[repoId!] = repoCandidates;
                    }
                    foreach (ModelDiscoveryCandidate candidate in repoCandidates)
                    {
                        if (!byKey.TryGetValue(candidate.Key, out ModelDiscoveryCandidate? known))
                            byKey[candidate.Key] = known = candidate;
                        if (!known.DiscoveryCohorts.Contains(label)) known.DiscoveryCohorts.Add(label);
                    }
                }
                succeededCohorts++;
            }
            catch (ModelDiscoveryFetchException ex) when (ex.Code is not ("rate_limited" or "redirect_rejected"
                or "unsafe_endpoint" or "timeout"))
            {
                firstFailure ??= ex;
            }
        }
        // Never turn the previously usable catalog stale because every cohort failed, or the public
        // feeds supplied no safe GGUF files or all of their individual detail requests failed.
        if (succeededCohorts == 0 && firstFailure is not null) throw firstFailure;
        if (byKey.Count == 0)
            throw new ModelDiscoveryFetchException("no_safe_gguf_candidate");
        return byKey.Values.ToList();
    }

    private async Task AppendDetailsAsync(string repoId, bool required,
        List<ModelDiscoveryCandidate> candidates, CancellationToken cancellationToken)
    {
        Uri detailsUrl = new($"https://huggingface.co/api/models/{repoId}?blobs=true");
        using JsonDocument details = await FetchAsync(detailsUrl, 4 * 1024 * 1024, cancellationToken);
        JsonElement root = details.RootElement;
        if (root.ValueKind != JsonValueKind.Object || ReadString(root, "id", 193) != repoId)
        {
            if (required) throw new ModelDiscoveryFetchException("invalid_repository_details");
            return;
        }
        string? revision = ModelRevisionIdentity.Normalize(ReadString(root, "sha", 64));
        if (revision is null || !root.TryGetProperty("siblings", out JsonElement siblings)
            || siblings.ValueKind != JsonValueKind.Array
            || required && siblings.GetArrayLength() is < 1 or > MaxFilesPerRepository)
        {
            if (required) throw new ModelDiscoveryFetchException("invalid_repository_details");
            return;
        }

        string? license = root.TryGetProperty("cardData", out JsonElement card)
            ? ReadString(card, "license", 256) : null;
        string? pipeline = ReadString(root, "pipeline_tag", 128);
        if (required && (root.TryGetProperty("cardData", out card) &&
                (card.ValueKind != JsonValueKind.Object || !IsOptionalString(card, "license", 256))
            || !IsOptionalString(root, "pipeline_tag", 128)
            || root.TryGetProperty("tags", out JsonElement strictTags) &&
                (strictTags.ValueKind != JsonValueKind.Array || strictTags.GetArrayLength() > 32 ||
                 strictTags.EnumerateArray().Any(tag => tag.ValueKind != JsonValueKind.String ||
                     tag.GetString() is not { Length: > 0 and <= 128 }))))
            throw new ModelDiscoveryFetchException("invalid_repository_details");
        List<string> tags = root.TryGetProperty("tags", out JsonElement tagArray)
            && tagArray.ValueKind == JsonValueKind.Array
            ? tagArray.EnumerateArray().Take(32).Where(t => t.ValueKind == JsonValueKind.String)
                .Select(t => t.GetString()).Where(t => t is { Length: > 0 and <= 128 })
                .Select(t => t!).ToList()
            : new List<string>();
        int before = candidates.Count;
        foreach (JsonElement sibling in siblings.EnumerateArray().Take(MaxFilesPerRepository))
        {
            string? path = ReadString(sibling, "rfilename", 512);
            if (!ModelDiscoveryIdentity.IsSafeRepoFilePath(path))
            {
                if (required) throw new ModelDiscoveryFetchException("invalid_repository_details");
                continue;
            }
            JsonElement lfs = sibling.TryGetProperty("lfs", out JsonElement value) ? value : default;
            if (required && lfs.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Object))
                throw new ModelDiscoveryFetchException("invalid_repository_details");
            if (required && (!IsOptionalPositiveInt64(sibling, "size")
                || !IsOptionalPositiveInt64(lfs, "size")
                || !IsOptionalSha256(lfs, "oid")))
                throw new ModelDiscoveryFetchException("invalid_repository_details");
            string? oid = ReadString(lfs, "oid", 72);
            if (required && oid is not null && ModelDiscoveryIdentity.NormalizeSha256(oid) is null)
                throw new ModelDiscoveryFetchException("invalid_repository_details");
            if (!path!.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase)) continue;
            long? size = ReadPositiveInt64(sibling, "size") ?? ReadPositiveInt64(lfs, "size");
            candidates.Add(new ModelDiscoveryCandidate
            {
                Key = ModelDiscoveryIdentity.Key(repoId, revision, path),
                RepoId = repoId, Revision = revision, RepoFilePath = path,
                PinnedUrl = ModelDiscoveryIdentity.PinnedUrl(repoId, revision, path),
                DeclaredLicense = license, DeclaredPipelineTag = pipeline, DeclaredTags = tags.ToList(),
                DeclaredSizeBytes = size, DeclaredSha256 = ModelDiscoveryIdentity.NormalizeSha256(oid)
            });
        }
        if (required && candidates.Count == before)
            throw new ModelDiscoveryFetchException("no_safe_gguf_candidate");
    }

    private async Task<JsonDocument> FetchAsync(Uri uri, int maxBytes, CancellationToken cancellationToken)
    {
        if (uri.Scheme != Uri.UriSchemeHttps || uri.Host != "huggingface.co" || !uri.IsDefaultPort
            || !string.IsNullOrEmpty(uri.UserInfo)) throw new ModelDiscoveryFetchException("unsafe_endpoint");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd("PaRuX/1.0");
        try
        {
            using HttpResponseMessage response = await _http.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            Uri? finalUri = response.RequestMessage?.RequestUri;
            if (finalUri is not null && finalUri != uri)
                throw new ModelDiscoveryFetchException("redirect_rejected");
            if ((int)response.StatusCode is >= 300 and < 400)
                throw new ModelDiscoveryFetchException("redirect_rejected");
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                TimeSpan? retry = response.Headers.RetryAfter?.Delta;
                throw new ModelDiscoveryFetchException("rate_limited", retry);
            }
            if (!response.IsSuccessStatusCode)
                throw new ModelDiscoveryFetchException("http_error");
            if (response.Content.Headers.ContentLength > maxBytes)
                throw new ModelDiscoveryFetchException("response_too_large");
            await using Stream stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var buffer = new MemoryStream();
            byte[] block = ArrayPool<byte>.Shared.Rent(32 * 1024);
            try
            {
                int read;
                while ((read = await stream.ReadAsync(block.AsMemory(0, Math.Min(block.Length,
                    maxBytes + 1 - (int)buffer.Length)), timeout.Token)) > 0)
                {
                    buffer.Write(block, 0, read);
                    if (buffer.Length > maxBytes) throw new ModelDiscoveryFetchException("response_too_large");
                }
            }
            finally { ArrayPool<byte>.Shared.Return(block); }
            buffer.Position = 0;
            return await JsonDocument.ParseAsync(buffer, cancellationToken: timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ModelDiscoveryFetchException("timeout");
        }
        catch (HttpRequestException)
        {
            throw new ModelDiscoveryFetchException("network_unavailable");
        }
        catch (JsonException)
        {
            throw new ModelDiscoveryFetchException("invalid_feed");
        }
    }

    private static string? ReadString(JsonElement source, string property, int maxLength)
    {
        if (source.ValueKind != JsonValueKind.Object || !source.TryGetProperty(property, out JsonElement value)
            || value.ValueKind != JsonValueKind.String) return null;
        string? text = value.GetString();
        return text is { Length: > 0 } && text.Length <= maxLength ? text : null;
    }

    private static bool IsOptionalString(JsonElement source, string property, int maxLength) =>
        !source.TryGetProperty(property, out JsonElement value)
        || value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text
            && text.Length <= maxLength;

    private static bool IsOptionalPositiveInt64(JsonElement source, string property) =>
        source.ValueKind != JsonValueKind.Object || !source.TryGetProperty(property, out _)
        || ReadPositiveInt64(source, property).HasValue;

    private static bool IsOptionalSha256(JsonElement source, string property) =>
        source.ValueKind != JsonValueKind.Object || !source.TryGetProperty(property, out _)
        || ModelDiscoveryIdentity.NormalizeSha256(ReadString(source, property, 72)) is not null;

    private static long? ReadPositiveInt64(JsonElement source, string property)
    {
        if (source.ValueKind != JsonValueKind.Object || !source.TryGetProperty(property, out JsonElement value)
            || value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out long bytes)) return null;
        return bytes > 0 ? bytes : null;
    }
}
