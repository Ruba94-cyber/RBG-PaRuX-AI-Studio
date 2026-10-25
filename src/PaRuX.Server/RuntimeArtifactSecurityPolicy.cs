namespace PaRuX.Server;

/// <summary>
/// Fail-closed trust boundary for PaRuX-managed runtime archives. Runtime Manager may only install
/// bounded GitHub Release assets from the exact upstream repositories it knows how to parse, with
/// an advertised SHA-256 digest and an exact byte count. Redirects may terminate only on GitHub's
/// own release-asset CDN. USER_MANAGED software never flows through this policy because PaRuX does
/// not download or mutate it.
/// </summary>
internal static class RuntimeArtifactSecurityPolicy
{
    internal const long MaximumRuntimeArchiveBytes = 32L * 1024 * 1024 * 1024;

    internal static void ValidateReleaseIdentity(RuntimeReleaseInfo release, string requestedTag)
    {
        if (release == null) throw new ArgumentNullException(nameof(release));
        if (!string.Equals(release.Tag, requestedTag, StringComparison.Ordinal))
            throw new InvalidDataException("GitHub release metadata did not match the requested runtime tag.");
    }

    internal static void ValidateAsset(
        RuntimeReleaseAsset asset,
        string owner,
        string repo,
        Func<Uri, bool>? isTrustedDownloadUri = null)
    {
        if (asset == null) throw new ArgumentNullException(nameof(asset));
        if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(repo))
            throw new ArgumentException("A trusted GitHub repository identity is required.");
        if (string.IsNullOrWhiteSpace(asset.Name)
            || asset.Name.Length > 240
            || asset.Name.IndexOfAny(new[] { '/', '\\', ':', '\0' }) >= 0
            || !string.Equals(Path.GetFileName(asset.Name), asset.Name, StringComparison.Ordinal))
            throw new InvalidDataException("Runtime release asset has an unsafe file name.");
        if (asset.SizeBytes <= 0 || asset.SizeBytes > MaximumRuntimeArchiveBytes)
            throw new InvalidDataException("Runtime release asset size is outside the allowed safety bound.");
        if (!IsValidSha256Digest(asset.Digest))
            throw new InvalidDataException(
                "Runtime release asset has no usable GitHub SHA-256 digest. PaRuX-managed executable runtimes require authenticated bytes before extraction.");

        if (!Uri.TryCreate(asset.DownloadUrl, UriKind.Absolute, out Uri? uri)
            || !IsTrustedAssetUri(uri, isTrustedDownloadUri)
            || uri.Query.Length != 0
            || uri.Fragment.Length != 0)
            throw new InvalidDataException("Runtime release asset URL is outside the trusted GitHub release channel.");

        string prefix = "/" + owner + "/" + repo + "/releases/download/";
        if (!uri.AbsolutePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Runtime release asset URL does not belong to the expected upstream repository.");
        string actualName = Uri.UnescapeDataString(Path.GetFileName(uri.AbsolutePath));
        if (!string.Equals(actualName, asset.Name, StringComparison.Ordinal))
            throw new InvalidDataException("Runtime release asset URL filename does not match GitHub metadata.");
    }

    internal static void ValidateDownloadResponse(
        HttpResponseMessage response,
        RuntimeReleaseAsset asset,
        Func<Uri, bool>? isTrustedDownloadUri = null)
    {
        if (response == null) throw new ArgumentNullException(nameof(response));
        if (asset == null) throw new ArgumentNullException(nameof(asset));

        Uri? finalUri = response.RequestMessage?.RequestUri;
        if (finalUri == null || !IsTrustedDownloadUri(finalUri, isTrustedDownloadUri))
            throw new InvalidDataException("Runtime download redirected outside GitHub's trusted HTTPS release infrastructure.");

        if (response.Content.Headers.ContentLength is long length && length != asset.SizeBytes)
            throw new InvalidDataException("Runtime download Content-Length does not match GitHub release metadata.");
    }

    /// <summary>
    /// The download response check for a request that may continue a partial file from <paramref name="startAt"/>. A 206 must prove the
    /// same range start and the asset total, and its length must be the bytes still missing. Any other success is a whole-file
    /// download, so it is checked against the full size and must not have been asked to start anywhere else.
    /// </summary>
    internal static void ValidateDownloadResponse(
        HttpResponseMessage response,
        RuntimeReleaseAsset asset,
        long startAt,
        Func<Uri, bool>? isTrustedDownloadUri = null)
    {
        if (response == null) throw new ArgumentNullException(nameof(response));
        if (asset == null) throw new ArgumentNullException(nameof(asset));
        if (startAt < 0 || startAt >= asset.SizeBytes) throw new ArgumentOutOfRangeException(nameof(startAt));
        if (response.StatusCode != System.Net.HttpStatusCode.PartialContent)
        {
            if (startAt != 0) throw new InvalidDataException("Runtime download did not honour the requested byte range.");
            ValidateDownloadResponse(response, asset, isTrustedDownloadUri);
            return;
        }

        Uri? finalUri = response.RequestMessage?.RequestUri;
        if (finalUri == null || !IsTrustedDownloadUri(finalUri, isTrustedDownloadUri))
            throw new InvalidDataException("Runtime download redirected outside GitHub's trusted HTTPS release infrastructure.");

        System.Net.Http.Headers.ContentRangeHeaderValue? range = response.Content.Headers.ContentRange;
        if (range is null || !range.HasRange || range.From != startAt || range.To != asset.SizeBytes - 1 || range.Length != asset.SizeBytes)
            throw new InvalidDataException("Runtime download range does not continue the partial file it was asked to continue.");
        if (response.Content.Headers.ContentLength is long length && length != asset.SizeBytes - startAt)
            throw new InvalidDataException("Runtime download Content-Length does not match the remaining bytes of the release asset.");
    }

    internal static void ValidateDownloadedByteCount(long downloadedBytes, RuntimeReleaseAsset asset)
    {
        if (downloadedBytes != asset.SizeBytes)
            throw new InvalidDataException("Runtime download byte count does not match GitHub release metadata.");
    }

    internal static void ThrowIfDownloadWouldExceedBounds(long downloadedBytes, int nextRead, RuntimeReleaseAsset asset)
    {
        if (nextRead < 0) throw new ArgumentOutOfRangeException(nameof(nextRead));
        long next;
        try { next = checked(downloadedBytes + nextRead); }
        catch (OverflowException) { throw new InvalidDataException("Runtime download size overflowed the safety counter."); }
        if (next > asset.SizeBytes || next > MaximumRuntimeArchiveBytes)
            throw new InvalidDataException("Runtime download exceeded its authenticated size bound.");
    }

    internal static bool IsValidSha256Digest(string? digest)
    {
        const string Prefix = "sha256:";
        if (string.IsNullOrWhiteSpace(digest) || !digest.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
            return false;
        string hex = digest[Prefix.Length..].Trim();
        return hex.Length == 64 && hex.All(Uri.IsHexDigit);
    }

    /// <summary>
    /// The asset URL comes from GitHub's release metadata, so only github.com itself is accepted for it. The release CDN host is
    /// accepted only for the redirected response, which is checked by IsTrustedDownloadUri.
    /// </summary>
    private static bool IsTrustedAssetUri(Uri uri, Func<Uri, bool>? isTrustedDownloadUri) =>
        isTrustedDownloadUri?.Invoke(uri)
        ?? (string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            && string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase));

    private static bool IsTrustedDownloadUri(Uri uri, Func<Uri, bool>? isTrustedDownloadUri) =>
        isTrustedDownloadUri?.Invoke(uri)
        ?? (string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            && (string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase)
                || string.Equals(uri.Host, "release-assets.githubusercontent.com", StringComparison.OrdinalIgnoreCase)));
}
