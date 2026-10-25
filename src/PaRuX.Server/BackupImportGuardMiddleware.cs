using System.IO.Compression;

namespace PaRuX.Server;

/// <summary>
/// Fail-closed guard in front of BackupService/ExecutionProfileBackupService import. Kestrel already
/// caps the compressed request body globally, but ZIP compression can turn a small request into an
/// unbounded amount of local disk/CPU work. This middleware validates the exact raw desktop backup
/// upload before the restore service sees it: desktop-only ownership, compressed-size bound,
/// flat/unique entry names, entry-count bound, and an ACTUAL decompressed-byte budget (not just the
/// ZIP header's claimed uncompressed sizes). The validated request body is then rewound for the
/// existing validate-then-swap restore flow.
/// </summary>
public sealed class BackupImportGuardMiddleware
{
    // Sized for real exports: a desktop with indexed repositories routinely produces a context
    // graph of 1+ GB (live test: 270 MB archive, 1.4 GB context-graph.db), which the previous
    // 64 MiB / 512 MiB bounds made impossible to restore. The zip-bomb defence is the real
    // decompression count below plus a free-disk-space budget, not an unrealistically small cap.
    internal const long MaximumCompressedBytes = 8L * 1024 * 1024 * 1024;
    internal const long MaximumExpandedBytes = 32L * 1024 * 1024 * 1024;
    internal const long MaximumSingleEntryBytes = 16L * 1024 * 1024 * 1024;
    internal const int MaximumEntries = 64;
    /// <summary>Space always left free on the restore volume while validating and restoring.</summary>
    internal const long DiskHeadroomBytes = 2L * 1024 * 1024 * 1024;

    private readonly RequestDelegate _next;

    public BackupImportGuardMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context)
    {
        bool backupImportPath = context.Request.Path == "/api/v1/backup/import"
            || context.Request.Path == "/api/v2/backup/import";
        if (!HttpMethods.IsPost(context.Request.Method) || !backupImportPath)
        {
            await _next(context);
            return;
        }

        // Paired-device auth is identity, not desktop privilege. The paired-device allowlist also
        // rejects backup routes; keeping this independent ownership assertion prevents a future
        // allowlist refactor from accidentally making destructive restore remotely reachable.
        if (!string.Equals(context.Items["PaRuXOwner"] as string, "desktop", StringComparison.Ordinal))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { error = "Backup restore is desktop-only." }, context.RequestAborted);
            return;
        }

        if (context.Request.ContentLength is long declared && declared > MaximumCompressedBytes)
        {
            context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            await context.Response.WriteAsJsonAsync(new { error = "Backup archive exceeds the 8 GiB import limit." }, context.RequestAborted);
            return;
        }

        // The global Kestrel body limit is far below a real backup; this route streams to disk.
        var bodyLimit = context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();
        if (bodyLimit is { IsReadOnly: false }) bodyLimit.MaxRequestBodySize = MaximumCompressedBytes;

        // Spooled to a delete-on-close temp file: buffering hundreds of MB in RAM is not acceptable.
        Stream buffered = new FileStream(
            Path.Combine(Path.GetTempPath(), "parux-backup-upload-" + Guid.NewGuid().ToString("N") + ".zip"),
            FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1024 * 1024,
            FileOptions.DeleteOnClose | FileOptions.Asynchronous | FileOptions.SequentialScan);
        try
        {
            await CopyCompressedBodyBoundedAsync(context.Request.Body, buffered, context.RequestAborted);
            if (buffered.Length == 0) throw new InvalidDataException("A backup .zip file is required.");

            buffered.Position = 0;
            ValidateArchive(buffered, context.RequestAborted);
            buffered.Position = 0;

            Stream original = context.Request.Body;
            long? originalLength = context.Request.ContentLength;
            context.Request.Body = buffered;
            context.Request.ContentLength = buffered.Length;
            try
            {
                await _next(context);
            }
            finally
            {
                context.Request.Body = original;
                context.Request.ContentLength = originalLength;
            }
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        catch (InvalidDataException exception)
        {
            if (!context.Response.HasStarted)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsJsonAsync(new { error = exception.Message }, context.RequestAborted);
            }
        }
        finally
        {
            buffered.Dispose();
        }
    }

    private static async Task CopyCompressedBodyBoundedAsync(Stream source, Stream destination, CancellationToken ct)
    {
        byte[] buffer = new byte[64 * 1024];
        long total = 0;
        while (true)
        {
            int read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), ct);
            if (read == 0) break;
            total = checked(total + read);
            if (total > MaximumCompressedBytes)
                throw new InvalidDataException("Backup archive exceeds the 8 GiB import limit.");
            await destination.WriteAsync(buffer.AsMemory(0, read), ct);
        }
    }

    /// <summary>Reads every entry to completion into a discard buffer so the expansion limit is
    /// based on bytes the deflate stream REALLY emits. This prevents a malicious archive from
    /// bypassing the guard by lying in its central-directory uncompressed-size field.</summary>
    internal static void ValidateArchive(Stream zipStream, CancellationToken ct = default) =>
        ValidateArchive(zipStream, AvailableRestoreBytes(), ct);

    /// <summary>
    /// Export-side check: PaRuX must never hand out a backup its own import would refuse. The
    /// destination disk is unknown at export time, so only the structural and size bounds apply.
    /// </summary>
    internal static void EnsureRestorable(byte[] archive)
    {
        if (archive.LongLength > MaximumCompressedBytes)
            throw new InvalidDataException("This backup would exceed the 8 GiB import limit, so PaRuX could not restore it.");
        using var stream = new MemoryStream(archive, writable: false);
        ValidateArchive(stream, long.MaxValue);
    }

    /// <summary>Free space on the volume restores are unpacked to, less a safety headroom.</summary>
    internal static long AvailableRestoreBytes()
    {
        try
        {
            string? root = Path.GetPathRoot(Path.GetTempPath());
            return root == null ? long.MaxValue : Math.Max(0, new DriveInfo(root).AvailableFreeSpace - DiskHeadroomBytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return long.MaxValue;
        }
    }

    internal static void ValidateArchive(Stream zipStream, long availableBytes, CancellationToken ct = default)
    {
        if (!zipStream.CanSeek) throw new InvalidDataException("Backup archive must be seekable after upload buffering.");
        long originalPosition = zipStream.Position;
        try
        {
            zipStream.Position = 0;
            using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read, leaveOpen: true);
            if (archive.Entries.Count is < 1 or > MaximumEntries)
                throw new InvalidDataException($"Backup archive must contain between 1 and {MaximumEntries} entries.");

            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long expandedTotal = 0;
            byte[] buffer = new byte[64 * 1024];

            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                ct.ThrowIfCancellationRequested();
                string name = entry.FullName;
                if (string.IsNullOrWhiteSpace(name)
                    || name.Length > 160
                    || name.Contains('/')
                    || name.Contains('\\')
                    || name is "." or ".."
                    || !names.Add(name))
                    throw new InvalidDataException("Backup archive contains an invalid or duplicate entry name.");

                if (entry.Length < 0 || entry.Length > MaximumSingleEntryBytes)
                    throw new InvalidDataException($"Backup entry '{name}' exceeds the allowed expanded size.");
                if (entry.CompressedLength < 0 || entry.CompressedLength > MaximumCompressedBytes)
                    throw new InvalidDataException($"Backup entry '{name}' has an invalid compressed size.");

                long entryExpanded = 0;
                using Stream input = entry.Open();
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    int read = input.Read(buffer, 0, buffer.Length);
                    if (read == 0) break;
                    entryExpanded = checked(entryExpanded + read);
                    expandedTotal = checked(expandedTotal + read);
                    if (entryExpanded > MaximumSingleEntryBytes)
                        throw new InvalidDataException($"Backup entry '{name}' exceeded the allowed expanded size while decompressing.");
                    if (expandedTotal > MaximumExpandedBytes)
                        throw new InvalidDataException("Backup archive expands beyond the 32 GiB restore safety limit.");
                    if (expandedTotal > availableBytes)
                        throw new InvalidDataException("Restoring this backup needs more free disk space than this PC has available.");
                }

                if (entryExpanded != entry.Length)
                    throw new InvalidDataException($"Backup entry '{name}' decompressed length does not match its ZIP metadata.");
            }
        }
        catch (InvalidDataException) { throw; }
        catch (Exception exception) when (exception is IOException or NotSupportedException)
        {
            throw new InvalidDataException("Backup archive is not a readable supported ZIP: " + exception.Message, exception);
        }
        finally
        {
            zipStream.Position = originalPosition;
        }
    }
}
