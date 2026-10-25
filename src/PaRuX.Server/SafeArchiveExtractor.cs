using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Readers;

namespace PaRuX.Server;

/// <summary>
/// Extracts archive entries manually instead of delegating path construction to the archive library.
/// This keeps traversal checks, duplicate-file behavior, cancellation, and an expanded-size cap under
/// PaRuX's control. SharpCompress supplies format decoding (including 7z); it never chooses where an
/// entry is written.
/// </summary>
internal static class SafeArchiveExtractor
{
    private const long DefaultMaxExtractedBytes = 64L * 1024 * 1024 * 1024;

    public static async Task ExtractAsync(
        string archivePath,
        string destinationRoot,
        CancellationToken ct,
        long maxExtractedBytes = DefaultMaxExtractedBytes)
    {
        if (maxExtractedBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxExtractedBytes));
        string root = Path.GetFullPath(destinationRoot);
        Directory.CreateDirectory(root);
        string rootPrefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

        long extracted = 0;
        byte[] signature = new byte[6];
        bool isSevenZip;
        using (FileStream signatureStream = File.OpenRead(archivePath))
        {
            isSevenZip = signatureStream.Read(signature, 0, signature.Length) == signature.Length
                && signature.AsSpan().SequenceEqual(new byte[] { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C });
        }

        // Select the forward-only fast path from the archive format, not its filename:
        // installer tests and user mirrors can provide valid ZIP data under a .7z staging name.
        if (isSevenZip)
        {
            using var archive = ArchiveFactory.OpenArchive(archivePath);
            using IReader reader = archive.ExtractAllEntries();
            while (reader.MoveToNextEntry())
            {
                ct.ThrowIfCancellationRequested();
                await ExtractCurrentEntryAsync(reader.Entry,
                    _ => Task.FromResult<Stream>(reader.OpenEntryStream()), root, rootPrefix,
                    ct, maxExtractedBytes, () => extracted, value => extracted = value);
            }
        }
        else
        {
            await using var reader = await ReaderFactory.OpenAsyncReader(archivePath, new ReaderOptions(), ct);
            while (await reader.MoveToNextEntryAsync(ct))
            {
                ct.ThrowIfCancellationRequested();
                await ExtractCurrentEntryAsync(reader.Entry,
                    async token => await reader.OpenEntryStreamAsync(token), root,
                    rootPrefix, ct, maxExtractedBytes, () => extracted, value => extracted = value);
            }
        }
    }

    private static async Task ExtractCurrentEntryAsync(
        IEntry entry,
        Func<CancellationToken, Task<Stream>> openInput,
        string root,
        string rootPrefix,
        CancellationToken ct,
        long maxExtractedBytes,
        Func<long> getExtracted,
        Action<long> setExtracted)
    {
        string key = entry.Key ?? string.Empty;
        if (string.IsNullOrWhiteSpace(key)) return;

        string normalized = key.Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(normalized) || (OperatingSystem.IsWindows() && normalized.Contains(':')))
            throw new InvalidDataException("Archive contains an unsafe path: " + key);

        string outputPath = Path.GetFullPath(Path.Combine(root, normalized));
        if (!outputPath.StartsWith(rootPrefix, PaRuXPathSafety.PathComparison))
            throw new InvalidDataException("Archive entry escapes the extraction directory: " + key);

        if (entry.IsDirectory)
        {
            Directory.CreateDirectory(outputPath);
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        await using (Stream input = await openInput(ct))
        {
            await using var output = new FileStream(
                outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            byte[] buffer = new byte[1024 * 1024];
            int read;
            while ((read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
            {
                long extracted = getExtracted() + read;
                if (extracted > maxExtractedBytes)
                    throw new InvalidDataException(
                        "Archive expanded beyond the safety limit of " + maxExtractedBytes + " bytes.");
                setExtracted(extracted);
                await output.WriteAsync(buffer.AsMemory(0, read), ct);
            }
        }
    }
}
