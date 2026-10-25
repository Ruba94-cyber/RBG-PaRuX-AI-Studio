using System.Text.Json;
using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class OrphanPartialSidecarBoundTests : IDisposable
{
    private const string Revision = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private readonly string _directory = Path.Combine(AppContext.BaseDirectory, "orphan-sidecar-tests", Guid.NewGuid().ToString("N"));

    public OrphanPartialSidecarBoundTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void Startup_scan_ignores_a_source_sidecar_above_64_KiB()
    {
        const string fileName = "large.gguf";
        WritePartial(fileName, [1]);
        string sidecarPath = Path.Combine(_directory, fileName + ".partial.source.json");
        string identity = CreateIdentity(fileName);
        File.WriteAllText(sidecarPath, identity + new string(' ', (64 * 1024) + 1));
        Assert.True(new FileInfo(sidecarPath).Length > 64 * 1024);
        using (var sidecarBytes = new CountingReadStream(File.ReadAllBytes(sidecarPath)))
        {
            Assert.Null(ModelDownloadService.ReadOrphanPartialIdentityBytes(sidecarBytes));
            Assert.Equal(0, sidecarBytes.BytesRead);
        }

        using var modelsDirectory = ModelDownloadService.UseModelsDirectoryForTests(_directory);
        var service = new ModelDownloadService(scanOrphanPartials: true);

        Assert.Empty(service.List());
    }

    [Fact]
    public void Startup_scan_still_discovers_a_normal_source_sidecar()
    {
        const string fileName = "normal.gguf";
        WritePartial(fileName, [1, 2, 3]);

        using var modelsDirectory = ModelDownloadService.UseModelsDirectoryForTests(_directory);
        var service = new ModelDownloadService(scanOrphanPartials: true);

        ModelDownloadStatus status = Assert.Single(service.List());
        Assert.Equal(fileName, status.FileName);
        Assert.Equal("paused", status.Status);
        Assert.Equal(3, status.BytesDownloaded);
    }

    [Fact]
    public void Startup_scan_skips_reparse_point_partials_and_sidecars()
    {
        const string partialTarget = "partial-target.bin";
        const string partialLinkName = "linked.gguf.partial";
        const string sidecarPartialName = "linked-sidecar.gguf";
        const string sidecarTarget = "identity-target.json";

        File.WriteAllBytes(Path.Combine(_directory, partialTarget), [1]);
        File.WriteAllText(Path.Combine(_directory, partialLinkName + ".source.json"), CreateIdentity("linked.gguf"));
        File.WriteAllText(Path.Combine(_directory, sidecarTarget), CreateIdentity(sidecarPartialName));
        File.WriteAllBytes(Path.Combine(_directory, sidecarPartialName + ".partial"), [2]);

        string partialLink = Path.Combine(_directory, partialLinkName);
        string sidecarLink = Path.Combine(_directory, sidecarPartialName + ".partial.source.json");
        bool partialLinkCreated = TryCreateFileSymbolicLink(partialLink, Path.Combine(_directory, partialTarget));
        bool sidecarLinkCreated = TryCreateFileSymbolicLink(sidecarLink, Path.Combine(_directory, sidecarTarget));

        Assert.True(ModelDownloadService.IsReparsePoint(FileAttributes.ReparsePoint));
        Assert.False(ModelDownloadService.IsReparsePoint(FileAttributes.Normal));
        if (partialLinkCreated)
            Assert.True(ModelDownloadService.IsReparsePoint(File.GetAttributes(partialLink)));
        if (sidecarLinkCreated)
            Assert.True(ModelDownloadService.IsReparsePoint(File.GetAttributes(sidecarLink)));

        using var modelsDirectory = ModelDownloadService.UseModelsDirectoryForTests(_directory);
        var service = new ModelDownloadService(scanOrphanPartials: true);

        Assert.Empty(service.List());
    }

    [Fact]
    public void Non_seekable_identity_read_stops_at_the_cap_and_never_reads_past_it()
    {
        var stream = new NonSeekableCountingStream(new byte[(64 * 1024) * 4]);

        Assert.Null(ModelDownloadService.ReadOrphanPartialIdentityBytes(stream));
        Assert.Equal(64L * 1024 + 1, stream.BytesRead);
    }

    private void WritePartial(string fileName, byte[] contents)
    {
        File.WriteAllBytes(Path.Combine(_directory, fileName + ".partial"), contents);
        File.WriteAllText(Path.Combine(_directory, fileName + ".partial.source.json"), CreateIdentity(fileName));
    }

    private static string CreateIdentity(string fileName) => JsonSerializer.Serialize(new
    {
        RepoId = "owner/model",
        Revision,
        FileName = fileName,
        DownloadUrl = "https://huggingface.co/owner/model/resolve/" + Revision + "/" + fileName,
        ExpectedSizeBytes = 32L,
        ExpectedSha256 = (string?)null
    });

    private static bool TryCreateFileSymbolicLink(string linkPath, string targetPath)
    {
        try
        {
            File.CreateSymbolicLink(linkPath, targetPath);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }

    private sealed class NonSeekableCountingStream(byte[] contents) : Stream
    {
        private readonly MemoryStream _inner = new(contents, writable: false);

        public long BytesRead { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int bytesRead = _inner.Read(buffer, offset, count);
            BytesRead += bytesRead;
            return bytesRead;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class CountingReadStream(byte[] contents) : MemoryStream(contents, writable: false)
    {
        public long BytesRead { get; private set; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int bytesRead = base.Read(buffer, offset, count);
            BytesRead += bytesRead;
            return bytesRead;
        }
    }
}
