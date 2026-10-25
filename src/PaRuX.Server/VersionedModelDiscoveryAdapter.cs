using System.Net;
using System.Text.Json;
using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>
/// Versioned discovery adapter contract (v1). A feed is one JSON document
/// <c>{"schemaVersion":1,"candidates":[{"repoId","revision","filePath","license?","sizeBytes?","sha256?"}]}</c>.
/// The adapter only produces candidate metadata; it never grants trust, never follows a redirect, accepts only
/// <c>application/json</c>, bounds the body, and refuses any schema version it does not know. The feed endpoint must be
/// https, or plain http on a loopback address (local fixtures/mirrors). Failures use <see cref="ModelDiscoveryFetchException"/> codes.
/// </summary>
internal sealed class VersionedModelDiscoveryAdapter : IModelDiscoveryProvider
{
    internal const int SupportedSchemaVersion = 1;
    internal const int MaxBodyBytes = 1024 * 1024;
    internal const int MaxCandidates = 200;
    private readonly Uri _feed;
    private readonly HttpClient _http;

    internal VersionedModelDiscoveryAdapter(Uri feed, HttpClient? http = null)
    {
        if (!feed.IsAbsoluteUri || !string.IsNullOrEmpty(feed.UserInfo)
            || !(feed.Scheme == Uri.UriSchemeHttps || feed.Scheme == Uri.UriSchemeHttp && feed.IsLoopback))
            throw new ModelDiscoveryFetchException("unsafe_endpoint");
        _feed = feed;
        _http = http ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
    }

    public async Task<IReadOnlyList<ModelDiscoveryCandidate>> DiscoverAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, _feed);
            using HttpResponseMessage response = await _http.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if ((int)response.StatusCode is >= 300 and < 400 || response.RequestMessage?.RequestUri != _feed)
                throw new ModelDiscoveryFetchException("redirect_rejected");
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                throw new ModelDiscoveryFetchException("rate_limited", response.Headers.RetryAfter?.Delta);
            if (!response.IsSuccessStatusCode) throw new ModelDiscoveryFetchException("http_error");
            if (!string.Equals(response.Content.Headers.ContentType?.MediaType, "application/json",
                StringComparison.OrdinalIgnoreCase))
                throw new ModelDiscoveryFetchException("unsupported_content_type");
            if (response.Content.Headers.ContentLength > MaxBodyBytes)
                throw new ModelDiscoveryFetchException("response_too_large");

            await using Stream stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var buffer = new MemoryStream();
            byte[] block = new byte[16 * 1024];
            int read;
            while ((read = await stream.ReadAsync(block, timeout.Token)) > 0)
            {
                buffer.Write(block, 0, read);
                if (buffer.Length > MaxBodyBytes) throw new ModelDiscoveryFetchException("response_too_large");
            }
            buffer.Position = 0;
            using JsonDocument document = await JsonDocument.ParseAsync(buffer, cancellationToken: timeout.Token);
            return Map(document.RootElement);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ModelDiscoveryFetchException("timeout");
        }
        catch (HttpRequestException) { throw new ModelDiscoveryFetchException("network_unavailable"); }
        catch (JsonException) { throw new ModelDiscoveryFetchException("invalid_feed"); }
    }

    private static IReadOnlyList<ModelDiscoveryCandidate> Map(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("schemaVersion", out JsonElement version)
            || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out int number))
            throw new ModelDiscoveryFetchException("invalid_feed");
        if (number != SupportedSchemaVersion) throw new ModelDiscoveryFetchException("unsupported_schema_version");
        if (!root.TryGetProperty("candidates", out JsonElement items) || items.ValueKind != JsonValueKind.Array
            || items.GetArrayLength() > MaxCandidates)
            throw new ModelDiscoveryFetchException("invalid_feed");

        var result = new List<ModelDiscoveryCandidate>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonElement item in items.EnumerateArray())
        {
            string? repoId = Text(item, "repoId"), path = Text(item, "filePath");
            string? revision = ModelRevisionIdentity.Normalize(Text(item, "revision"));
            if (!ModelDiscoveryIdentity.IsSafeRepoId(repoId) || revision is null
                || !ModelDiscoveryIdentity.IsSafeRepoFilePath(path)
                || !path!.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase))
                throw new ModelDiscoveryFetchException("invalid_feed");
            string? sha = Text(item, "sha256");
            if (sha is not null && ModelDiscoveryIdentity.NormalizeSha256(sha) is null)
                throw new ModelDiscoveryFetchException("invalid_feed");
            long? size = null;
            if (item.TryGetProperty("sizeBytes", out JsonElement s))
            {
                if (s.ValueKind != JsonValueKind.Number || !s.TryGetInt64(out long value) || value <= 0)
                    throw new ModelDiscoveryFetchException("invalid_feed");
                size = value;
            }
            string key = ModelDiscoveryIdentity.Key(repoId!, revision, path);
            if (!seen.Add(key)) continue;
            result.Add(new ModelDiscoveryCandidate
            {
                Key = key, RepoId = repoId!, Revision = revision, RepoFilePath = path,
                PinnedUrl = ModelDiscoveryIdentity.PinnedUrl(repoId!, revision, path),
                DeclaredLicense = Text(item, "license"), DeclaredSizeBytes = size,
                DeclaredSha256 = ModelDiscoveryIdentity.NormalizeSha256(sha)
            });
        }
        if (result.Count == 0) throw new ModelDiscoveryFetchException("no_safe_gguf_candidate");
        return result;
    }

    private static string? Text(JsonElement item, string name)
    {
        if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty(name, out JsonElement value)) return null;
        if (value.ValueKind != JsonValueKind.String) throw new ModelDiscoveryFetchException("invalid_feed");
        string? text = value.GetString();
        return text is { Length: > 0 and <= 512 } ? text : null;
    }
}
