using System.Buffers.Binary;
using System.Net;
using System.Text.Json;

namespace PaRuX.Server;

/// <summary>Search hit as Hugging Face reports it. Everything here is publisher-declared and untrusted.</summary>
internal sealed class MediaRepoSummary
{
    public string Id { get; set; } = string.Empty;
    public string? Sha { get; set; }
    public long Downloads { get; set; }
    public long Likes { get; set; }
    public DateTime? LastModifiedUtc { get; set; }
    /// <summary>false, or a gating mode (auto / manual / true) as text. Anything but "false" is refused.</summary>
    public string Gated { get; set; } = "false";
    public bool Private { get; set; }
    public bool Disabled { get; set; }
    public string? License { get; set; }
    public string? LicenseName { get; set; }
    public string? PipelineTag { get; set; }
    public List<string> Tags { get; set; } = new();
}

internal sealed record MediaSearchPage(bool NotModified, string? ETag, IReadOnlyList<MediaRepoSummary> Repos);

/// <summary>One file of a repository tree. Sha256 is the LFS oid exactly as listed (null for a non-LFS file).</summary>
internal sealed record MediaRepoFile(string Path, long SizeBytes, string? Sha256);

internal sealed class MediaDiscoveryFetchException(string code, string? detail = null) : Exception(detail ?? code)
{
    internal string Code { get; } = code;
}

/// <summary>Everything network-facing in media discovery. Tests use a fake; production is <see cref="HuggingFaceMediaDiscoveryProvider"/>.</summary>
internal interface IMediaDiscoveryProvider
{
    /// <summary>Searches models with one pipeline tag. <paramref name="knownETag"/> makes the first page a conditional request;
    /// when nothing changed the answer is NotModified and the caller reuses its cache. Pagination is bounded inside.</summary>
    Task<MediaSearchPage> SearchAsync(MediaSearchQuery query, string? knownETag, CancellationToken ct);
    Task<IReadOnlyList<MediaRepoFile>> ListFilesAsync(string repoId, string revision, CancellationToken ct);
    /// <summary>The model card text (README), truncated; null when the repository has none.</summary>
    Task<string?> GetModelCardAsync(string repoId, string revision, CancellationToken ct);
    /// <summary>Reads ONLY the safetensors header (8-byte length prefix then the JSON header) with HTTP Range requests.</summary>
    Task<SafetensorsHeader> ReadSafetensorsHeaderAsync(string repoId, string revision, string path, long fileBytes, CancellationToken ct);
}

internal sealed record MediaSearchQuery(string PipelineTag, string Sort, string[] Filters, int MaxPages)
{
    internal string CacheKey => PipelineTag + "|" + Sort + "|" + string.Join(',', Filters);
}

/// <summary>
/// Hugging Face provider. Every request is an https GET to huggingface.co / hf.co hosts only (redirects are followed manually and
/// re-checked), is identified as PaRuX, and is bounded (page counts, body sizes, header size). The API base can be overridden with
/// <c>PARUX_MEDIA_DISCOVERY_API_BASE</c> for tests (https, or http on a loopback host).
/// </summary>
internal sealed class HuggingFaceMediaDiscoveryProvider : IMediaDiscoveryProvider
{
    internal const string BaseVariable = "PARUX_MEDIA_DISCOVERY_API_BASE";
    internal const int PageSize = 50;
    internal const int MaxJsonBytes = 4 * 1024 * 1024;
    internal const int MaxModelCardBytes = 64 * 1024;
    internal const int MaxTreePages = 3;
    private const int MaxRedirects = 4;
    private readonly HttpClient _http;
    private readonly Uri _base;
    private readonly string _root;

    internal HuggingFaceMediaDiscoveryProvider(HttpClient? http = null, string? baseUrl = null)
    {
        _http = http ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };
        string configured = (baseUrl ?? Environment.GetEnvironmentVariable(BaseVariable) ?? "https://huggingface.co").Trim().TrimEnd('/');
        if (!Uri.TryCreate(configured, UriKind.Absolute, out Uri? uri) || !(uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)))
            throw new MediaDiscoveryFetchException("unsafe_endpoint");
        _base = uri;
        _root = uri.GetLeftPart(UriPartial.Authority);
    }

    public async Task<MediaSearchPage> SearchAsync(MediaSearchQuery query, string? knownETag, CancellationToken ct)
    {
        string url = _root + "/api/models?pipeline_tag=" + Uri.EscapeDataString(query.PipelineTag)
            + (query.Filters.Length > 0 ? "&filter=" + Uri.EscapeDataString(string.Join(',', query.Filters)) : string.Empty)
            + "&sort=" + Uri.EscapeDataString(query.Sort) + "&direction=-1&limit=" + PageSize
            + "&expand[]=downloads&expand[]=likes&expand[]=lastModified&expand[]=gated&expand[]=tags&expand[]=pipeline_tag"
            + "&expand[]=sha&expand[]=cardData&expand[]=private&expand[]=disabled";
        var repos = new List<MediaRepoSummary>();
        string? etag = null;
        for (int page = 0; page < Math.Max(1, query.MaxPages) && url is not null; page++)
        {
            using HttpResponseMessage response = await GetAsync(url, page == 0 ? knownETag : null, null, ct).ConfigureAwait(false);
            if (page == 0 && response.StatusCode == HttpStatusCode.NotModified)
                return new MediaSearchPage(true, knownETag, Array.Empty<MediaRepoSummary>());
            EnsureSuccess(response);
            if (page == 0) etag = response.Headers.ETag?.ToString();
            string json = await ReadBoundedStringAsync(response, MaxJsonBytes, ct).ConfigureAwait(false);
            repos.AddRange(ParseSearch(json));
            url = NextLink(response);
        }
        return new MediaSearchPage(false, etag, repos);
    }

    public async Task<IReadOnlyList<MediaRepoFile>> ListFilesAsync(string repoId, string revision, CancellationToken ct)
    {
        string? url = _root + "/api/models/" + EscapePath(repoId) + "/tree/" + Uri.EscapeDataString(revision) + "?recursive=true&limit=1000";
        var files = new List<MediaRepoFile>();
        for (int page = 0; page < MaxTreePages && url is not null; page++)
        {
            using HttpResponseMessage response = await GetAsync(url, null, null, ct).ConfigureAwait(false);
            EnsureSuccess(response);
            files.AddRange(ParseTree(await ReadBoundedStringAsync(response, MaxJsonBytes, ct).ConfigureAwait(false)));
            url = NextLink(response);
        }
        return files;
    }

    public async Task<string?> GetModelCardAsync(string repoId, string revision, CancellationToken ct)
    {
        string url = _root + "/" + EscapePath(repoId) + "/raw/" + Uri.EscapeDataString(revision) + "/README.md";
        using HttpResponseMessage response = await GetAsync(url, null, null, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        EnsureSuccess(response);
        return await ReadBoundedStringAsync(response, MaxModelCardBytes, ct, truncate: true).ConfigureAwait(false);
    }

    public async Task<SafetensorsHeader> ReadSafetensorsHeaderAsync(string repoId, string revision, string path, long fileBytes, CancellationToken ct)
    {
        string url = _root + "/" + EscapePath(repoId) + "/resolve/" + Uri.EscapeDataString(revision) + "/" + EscapePath(path);
        byte[] prefix = await RangeAsync(url, 0, 7, ct).ConfigureAwait(false);
        if (prefix.Length != 8) throw new MediaDiscoveryFetchException("bad_range_response");
        ulong length = BinaryPrimitives.ReadUInt64LittleEndian(prefix);
        if (length == 0 || length > SafetensorsHeaderReader.MaxHeaderBytes)
            throw new InvalidDataException("The safetensors header length is outside the accepted range.");
        byte[] header = await RangeAsync(url, 8, 8 + (long)length - 1, ct).ConfigureAwait(false);
        if ((ulong)header.Length != length) throw new MediaDiscoveryFetchException("bad_range_response");
        return ReadHeader(prefix, header, fileBytes);
    }

    /// <summary>Pure: validates a header that was fetched in two ranges, using the file size from the listing.</summary>
    internal static SafetensorsHeader ReadHeader(byte[] prefix, byte[] header, long fileBytes)
    {
        var all = new byte[prefix.Length + header.Length];
        Buffer.BlockCopy(prefix, 0, all, 0, prefix.Length);
        Buffer.BlockCopy(header, 0, all, prefix.Length, header.Length);
        using var stream = new PrefixOnlyStream(all, fileBytes);
        return SafetensorsHeaderReader.Read(stream);
    }

    private async Task<byte[]> RangeAsync(string url, long from, long to, CancellationToken ct)
    {
        using HttpResponseMessage response = await GetAsync(url, null, (from, to), ct).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.PartialContent)
            throw new MediaDiscoveryFetchException("range_not_supported", "The host did not honour a Range request, so the header was not read.");
        long expected = to - from + 1;
        if (response.Content.Headers.ContentLength is { } cl && cl != expected) throw new MediaDiscoveryFetchException("bad_range_response");
        using Stream body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var buffer = new byte[expected];
        int read = 0;
        while (read < buffer.Length)
        {
            int n = await body.ReadAsync(buffer.AsMemory(read), ct).ConfigureAwait(false);
            if (n == 0) break;
            read += n;
        }
        if (read != buffer.Length) throw new MediaDiscoveryFetchException("bad_range_response");
        return buffer;
    }

    private async Task<HttpResponseMessage> GetAsync(string url, string? ifNoneMatch, (long From, long To)? range, CancellationToken ct)
    {
        for (int hop = 0; hop <= MaxRedirects; hop++)
        {
            Uri uri = new(url);
            if (!IsAllowedHost(uri)) throw new MediaDiscoveryFetchException("unsafe_redirect");
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd("PaRuX/1.0");
            if (ifNoneMatch is not null) request.Headers.TryAddWithoutValidation("If-None-Match", ifNoneMatch);
            if (range is { } r) request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(r.From, r.To);
            HttpResponseMessage response;
            try { response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false); }
            catch (HttpRequestException ex) { throw new MediaDiscoveryFetchException("offline", ex.Message); }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested) { throw new MediaDiscoveryFetchException("timeout"); }
            if ((int)response.StatusCode is >= 300 and < 400 && response.StatusCode != HttpStatusCode.NotModified)
            {
                Uri? target = response.Headers.Location is { } loc ? (loc.IsAbsoluteUri ? loc : new Uri(uri, loc)) : null;
                response.Dispose();
                if (target is null) throw new MediaDiscoveryFetchException("bad_redirect");
                url = target.ToString();
                continue;
            }
            return response;
        }
        throw new MediaDiscoveryFetchException("too_many_redirects");
    }

    private bool IsAllowedHost(Uri uri)
    {
        if (uri.Scheme == Uri.UriSchemeHttps)
        {
            string h = uri.Host.ToLowerInvariant();
            if (h == "huggingface.co" || h.EndsWith(".huggingface.co", StringComparison.Ordinal) || h == "hf.co" || h.EndsWith(".hf.co", StringComparison.Ordinal))
                return true;
        }
        // The (test-only) configured base host is always allowed, nothing else.
        return string.Equals(uri.Host, _base.Host, StringComparison.OrdinalIgnoreCase) && uri.Scheme == _base.Scheme && uri.Port == _base.Port;
    }

    private static void EnsureSuccess(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return;
        int code = (int)response.StatusCode;
        throw new MediaDiscoveryFetchException(code == 429 ? "rate_limited" : code >= 500 ? "server_error" : "http_" + code);
    }

    private static string? NextLink(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Link", out IEnumerable<string>? values)) return null;
        foreach (string value in values)
            foreach (string part in value.Split(','))
            {
                if (!part.Contains("rel=\"next\"", StringComparison.Ordinal)) continue;
                int a = part.IndexOf('<'), b = part.IndexOf('>');
                if (a >= 0 && b > a) return part[(a + 1)..b];
            }
        return null;
    }

    private static async Task<string> ReadBoundedStringAsync(HttpResponseMessage response, int maxBytes, CancellationToken ct, bool truncate = false)
    {
        using Stream stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var buffer = new byte[Math.Min(maxBytes, 64 * 1024)];
        using var ms = new MemoryStream();
        while (true)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
            if (n == 0) break;
            if (ms.Length + n > maxBytes)
            {
                if (!truncate) throw new MediaDiscoveryFetchException("response_too_large");
                ms.Write(buffer, 0, (int)(maxBytes - ms.Length));
                break;
            }
            ms.Write(buffer, 0, n);
        }
        return System.Text.Encoding.UTF8.GetString(ms.ToArray());
    }

    private static string EscapePath(string path) => string.Join('/', path.Split('/').Select(Uri.EscapeDataString));

    /// <summary>Pure parser for the model-search array. Unknown or malformed rows are skipped, never guessed.</summary>
    internal static List<MediaRepoSummary> ParseSearch(string json)
    {
        var list = new List<MediaRepoSummary>();
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 }); }
        catch (JsonException) { throw new MediaDiscoveryFetchException("bad_json"); }
        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Array) throw new MediaDiscoveryFetchException("bad_json");
            foreach (JsonElement e in doc.RootElement.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object) continue;
                string? id = Text(e, "id") ?? Text(e, "modelId");
                if (id is null || !ModelDiscoveryIdentity.IsSafeRepoId(id)) continue;
                var s = new MediaRepoSummary
                {
                    Id = id, Sha = Text(e, "sha"), Downloads = Num(e, "downloads"), Likes = Num(e, "likes"),
                    PipelineTag = Text(e, "pipeline_tag"),
                    Private = e.TryGetProperty("private", out JsonElement pv) && pv.ValueKind == JsonValueKind.True,
                    Disabled = e.TryGetProperty("disabled", out JsonElement dv) && dv.ValueKind == JsonValueKind.True,
                    Gated = e.TryGetProperty("gated", out JsonElement g)
                        ? (g.ValueKind == JsonValueKind.False ? "false" : g.ValueKind == JsonValueKind.True ? "true" : g.ValueKind == JsonValueKind.String ? g.GetString() ?? "true" : "true")
                        : "false"
                };
                if (Text(e, "lastModified") is { } lm && DateTime.TryParse(lm, System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out DateTime dt))
                    s.LastModifiedUtc = dt;
                if (e.TryGetProperty("tags", out JsonElement tags) && tags.ValueKind == JsonValueKind.Array)
                    foreach (JsonElement t in tags.EnumerateArray())
                        if (t.ValueKind == JsonValueKind.String && t.GetString() is { Length: > 0 and <= 128 } tag && s.Tags.Count < 64) s.Tags.Add(tag);
                if (e.TryGetProperty("cardData", out JsonElement card) && card.ValueKind == JsonValueKind.Object)
                {
                    if (card.TryGetProperty("license", out JsonElement lic))
                        s.License = lic.ValueKind == JsonValueKind.String ? lic.GetString()
                            : lic.ValueKind == JsonValueKind.Array && lic.GetArrayLength() > 0 && lic[0].ValueKind == JsonValueKind.String ? lic[0].GetString() : null;
                    s.LicenseName = Text(card, "license_name");
                }
                // Tags also carry "license:<id>"; the card wins when both exist.
                s.License ??= s.Tags.FirstOrDefault(t => t.StartsWith("license:", StringComparison.Ordinal))?["license:".Length..];
                list.Add(s);
            }
        }
        return list;
    }

    /// <summary>Pure parser for a tree page: only files, with the LFS oid when present.</summary>
    internal static List<MediaRepoFile> ParseTree(string json)
    {
        var list = new List<MediaRepoFile>();
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 }); }
        catch (JsonException) { throw new MediaDiscoveryFetchException("bad_json"); }
        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Array) throw new MediaDiscoveryFetchException("bad_json");
            foreach (JsonElement e in doc.RootElement.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object || Text(e, "type") != "file" || Text(e, "path") is not { } path) continue;
                long size = Num(e, "size");
                string? sha = null;
                if (e.TryGetProperty("lfs", out JsonElement lfs) && lfs.ValueKind == JsonValueKind.Object)
                {
                    long lfsSize = Num(lfs, "size");
                    if (lfsSize > 0) size = lfsSize;
                    string? oid = Text(lfs, "oid")?.ToLowerInvariant();
                    if (oid is { Length: 64 } && oid.All(Uri.IsHexDigit)) sha = oid;
                }
                if (size > 0) list.Add(new MediaRepoFile(path, size, sha));
            }
        }
        return list;
    }

    private static string? Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static long Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out long n) ? Math.Max(0, n) : 0;

    /// <summary>A seekable stream that reports the real file length but only holds the first bytes. Reading past them is a bug.</summary>
    private sealed class PrefixOnlyStream(byte[] prefix, long length) : Stream
    {
        private long _position;
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => _position; set => _position = value; }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_position >= prefix.Length) throw new InvalidDataException("The header reader asked for bytes beyond the downloaded range.");
            int n = (int)Math.Min(count, prefix.Length - _position);
            Buffer.BlockCopy(prefix, (int)_position, buffer, offset, n);
            _position += n;
            return n;
        }
        public override long Seek(long offset, SeekOrigin origin) =>
            _position = origin switch { SeekOrigin.Begin => offset, SeekOrigin.Current => _position + offset, _ => length + offset };
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
