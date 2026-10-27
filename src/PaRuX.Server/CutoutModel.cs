using System.Security.Cryptography;

namespace PaRuX.Server;

/// <summary>
/// The one background-removal model PaRuX installs: BiRefNet-lite exported to ONNX by onnx-community (licence MIT, declared on both the
/// export and the upstream ZhengPeng7/BiRefNet_lite model cards, not gated). Size and SHA-256 were read from the Hugging Face API
/// (<c>/api/models/onnx-community/BiRefNet_lite-ONNX?blobs=true</c>) at the commit below; a file that differs is never kept.
/// </summary>
internal static class CutoutModel
{
    internal const string Title = "BiRefNet-lite (ONNX)";
    internal const string Repository = "onnx-community/BiRefNet_lite-ONNX";
    /// <summary>Commit of the repository the size and digest below were read from.</summary>
    internal const string Revision = "de15b22ba131738a16dff04aab8bdf8dc32e3ac1";
    internal const string RepositoryPath = "onnx/model.onnx";
    internal const string FileName = "birefnet_lite.onnx";
    internal const long Bytes = 224_005_088;
    internal const string Sha256 = "5600024376f572a557870a5eb0afb1e5961636bef4e1e22132025467d0f03333";
    internal const string License = "MIT";
    internal const string RuntimeLicense = "MIT (onnxruntime), BSD-3 (NumPy), HPND (Pillow)";

    internal static Uri DownloadUri => new("https://huggingface.co/" + Repository + "/resolve/" + Revision + "/" + RepositoryPath);
}

/// <summary>Streams a pinned file to disk and keeps it only if its size and SHA-256 match; nothing partial ever sits under the final name.</summary>
internal static class PinnedFileDownloader
{
    /// <summary>Downloads <paramref name="uri"/> to <paramref name="destination"/>. Throws <see cref="InvalidDataException"/> when the bytes are not the pinned ones.</summary>
    internal static async Task DownloadAsync(
        HttpClient http, Uri uri, string destination, long expectedBytes, string expectedSha256,
        IProgress<(long Done, long Total)>? progress, CancellationToken ct)
    {
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Pinned downloads must use HTTPS.");
        string directory = Path.GetDirectoryName(Path.GetFullPath(destination))!;
        Directory.CreateDirectory(directory);
        string part = destination + ".part";
        try { if (File.Exists(part)) File.Delete(part); } catch (IOException) { }

        using HttpResponseMessage response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new IOException("The download failed with HTTP " + (int)response.StatusCode + " (" + uri.Host + ").");
        long? declared = response.Content.Headers.ContentLength;
        if (declared is long length && length != expectedBytes)
            throw new InvalidDataException("The server reported " + length + " bytes but " + expectedBytes + " were expected; nothing was kept.");

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long done = 0;
        try
        {
            await using (Stream source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var target = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            {
                byte[] buffer = new byte[1 << 16];
                int read;
                while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    done += read;
                    if (done > expectedBytes) throw new InvalidDataException("The download is larger than the pinned " + expectedBytes + " bytes; nothing was kept.");
                    hash.AppendData(buffer, 0, read);
                    await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    progress?.Report((done, expectedBytes));
                }
            }
            if (done != expectedBytes)
                throw new InvalidDataException("The download ended after " + done + " of " + expectedBytes + " bytes; nothing was kept.");
            string actual = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The downloaded file failed SHA-256 verification (expected " + expectedSha256 + ", got " + actual + "); nothing was kept.");
            File.Move(part, destination, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(part)) File.Delete(part); } catch (IOException) { }
            throw;
        }
    }

    /// <summary>True when the file exists, has the pinned size and hashes to the pinned SHA-256.</summary>
    internal static async Task<bool> IsVerifiedAsync(string path, long expectedBytes, string expectedSha256, CancellationToken ct)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length != expectedBytes) return false;
            await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
            string actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false)).ToLowerInvariant();
            return string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }
}
