using System.IO.Compression;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class BackupImportGuardTests
{
    [Fact]
    public void Valid_flat_backup_archive_is_accepted_and_rewound()
    {
        using MemoryStream zip = CreateArchive(("manifest.json", "{\"formatVersion\":1}"), ("memory.db", "sqlite-placeholder"));
        zip.Position = 3;

        BackupImportGuardMiddleware.ValidateArchive(zip);

        Assert.Equal(3, zip.Position);
    }

    [Fact]
    public void Nested_entry_is_rejected()
    {
        using MemoryStream zip = CreateArchive(("manifest.json", "{}"), ("nested/memory.db", "x"));
        Assert.Throws<InvalidDataException>(() => BackupImportGuardMiddleware.ValidateArchive(zip));
    }

    [Fact]
    public void Duplicate_entry_name_is_rejected_case_insensitively()
    {
        using var zip = new MemoryStream();
        using (var archive = new ZipArchive(zip, ZipArchiveMode.Create, leaveOpen: true))
        {
            Write(archive, "manifest.json", "{}");
            Write(archive, "MANIFEST.JSON", "{}");
        }
        zip.Position = 0;

        Assert.Throws<InvalidDataException>(() => BackupImportGuardMiddleware.ValidateArchive(zip));
    }

    [Fact]
    public void Too_many_entries_are_rejected()
    {
        using var zip = new MemoryStream();
        using (var archive = new ZipArchive(zip, ZipArchiveMode.Create, leaveOpen: true))
        {
            for (int i = 0; i <= BackupImportGuardMiddleware.MaximumEntries; i++)
                Write(archive, $"entry-{i}.json", "{}");
        }
        zip.Position = 0;

        Assert.Throws<InvalidDataException>(() => BackupImportGuardMiddleware.ValidateArchive(zip));
    }

    [Fact]
    public void Empty_archive_is_rejected()
    {
        using var zip = new MemoryStream();
        using (var archive = new ZipArchive(zip, ZipArchiveMode.Create, leaveOpen: true)) { }
        zip.Position = 0;
        Assert.Throws<InvalidDataException>(() => BackupImportGuardMiddleware.ValidateArchive(zip));
    }

    [Fact]
    public void A_backup_larger_than_the_old_512_MiB_bound_is_restorable()
    {
        // Live finding: a real 1.4 GB context graph made every export unrestorable. 600 MiB of
        // zeros compresses to a small archive but really expands past the old limit.
        using var zip = new MemoryStream();
        using (var archive = new ZipArchive(zip, ZipArchiveMode.Create, leaveOpen: true))
        {
            ZipArchiveEntry entry = archive.CreateEntry("context-graph.db", CompressionLevel.Fastest);
            using Stream output = entry.Open();
            byte[] block = new byte[1024 * 1024];
            for (int i = 0; i < 600; i++) output.Write(block, 0, block.Length);
        }
        BackupImportGuardMiddleware.ValidateArchive(zip, availableBytes: long.MaxValue);
        BackupImportGuardMiddleware.EnsureRestorable(zip.ToArray());
    }

    [Fact]
    public void A_backup_that_would_not_fit_on_disk_is_refused_before_restoring()
    {
        using MemoryStream zip = CreateArchive(("manifest.json", "{}"), ("memory.db", new string('x', 4096)));
        var error = Assert.Throws<InvalidDataException>(() => BackupImportGuardMiddleware.ValidateArchive(zip, availableBytes: 1024));
        Assert.Contains("free disk space", error.Message);
    }

    private static MemoryStream CreateArchive(params (string Name, string Content)[] entries)
    {
        var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach ((string name, string content) in entries) Write(archive, name, content);
        }
        stream.Position = 0;
        return stream;
    }

    private static void Write(ZipArchive archive, string name, string content)
    {
        ZipArchiveEntry entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        using StreamWriter writer = new(entry.Open());
        writer.Write(content);
    }
}
