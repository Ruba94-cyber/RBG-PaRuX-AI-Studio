using System.Text;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class BinaryAnalysisToolsTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "parux-binary-tests-" + Guid.NewGuid().ToString("N"));

    public BinaryAnalysisToolsTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task AnalyzeAsync_recognizes_a_minimal_x64_managed_pe()
    {
        string path = Path.Combine(_tempDir, "sample.exe");
        byte[] bytes = new byte[512];
        bytes[0] = (byte)'M';
        bytes[1] = (byte)'Z';
        BitConverter.GetBytes(0x80).CopyTo(bytes, 0x3C);
        bytes[0x80] = (byte)'P';
        bytes[0x81] = (byte)'E';
        bytes[0x82] = 0;
        bytes[0x83] = 0;
        BitConverter.GetBytes((ushort)0x8664).CopyTo(bytes, 0x84); // AMD64
        BitConverter.GetBytes((ushort)1).CopyTo(bytes, 0x86);     // one section
        BitConverter.GetBytes((uint)1_700_000_000).CopyTo(bytes, 0x88);
        BitConverter.GetBytes((ushort)0xF0).CopyTo(bytes, 0x94);  // PE32+ optional header size

        int optional = 0x98;
        BitConverter.GetBytes((ushort)0x20B).CopyTo(bytes, optional);
        BitConverter.GetBytes((uint)0x1234).CopyTo(bytes, optional + 16);
        BitConverter.GetBytes((ulong)0x140000000).CopyTo(bytes, optional + 24);
        BitConverter.GetBytes((ushort)3).CopyTo(bytes, optional + 68); // console
        BitConverter.GetBytes((uint)0x2000).CopyTo(bytes, optional + 112 + (14 * 8));
        BitConverter.GetBytes((uint)0x48).CopyTo(bytes, optional + 112 + (14 * 8) + 4);

        int section = optional + 0xF0;
        Encoding.ASCII.GetBytes(".text\0\0\0").CopyTo(bytes, section);
        BitConverter.GetBytes((uint)0x1000).CopyTo(bytes, section + 8);
        BitConverter.GetBytes((uint)0x1000).CopyTo(bytes, section + 12);
        BitConverter.GetBytes((uint)0).CopyTo(bytes, section + 16);
        BitConverter.GetBytes((uint)0x60000020).CopyTo(bytes, section + 36);
        await File.WriteAllBytesAsync(path, bytes);

        BinaryAnalysisTools.BinaryAnalysisResult result = await BinaryAnalysisTools.AnalyzeAsync(
            _tempDir, "sample.exe", CancellationToken.None);

        Assert.Equal("PE/COFF", result.Format);
        Assert.Equal("x86-64/AMD64", result.Architecture);
        Assert.True(result.Is64Bit);
        Assert.True(result.IsManagedDotNet);
        Assert.Equal("Windows Console", result.Subsystem);
        Assert.Equal("RVA 0x1234", result.EntryPoint);
        Assert.Single(result.Sections);
        Assert.Equal(".text", result.Sections[0].Name);
        Assert.Equal(64, result.Sha256.Length);
    }

    [Fact]
    public async Task AnalyzeAsync_recognizes_elf64_x86_64()
    {
        byte[] bytes = new byte[64];
        bytes[0] = 0x7F;
        bytes[1] = (byte)'E';
        bytes[2] = (byte)'L';
        bytes[3] = (byte)'F';
        bytes[4] = 2; // 64-bit
        bytes[5] = 1; // little endian
        BitConverter.GetBytes((ushort)62).CopyTo(bytes, 18);
        await File.WriteAllBytesAsync(Path.Combine(_tempDir, "sample.elf"), bytes);

        BinaryAnalysisTools.BinaryAnalysisResult result = await BinaryAnalysisTools.AnalyzeAsync(
            _tempDir, "sample.elf", CancellationToken.None);

        Assert.Equal("ELF", result.Format);
        Assert.True(result.Is64Bit);
        Assert.Equal("x86-64/AMD64", result.Architecture);
        Assert.Equal("little", result.Endianness);
    }

    [Fact]
    public async Task AnalyzeAsync_recognizes_little_endian_macho64_arm64()
    {
        byte[] bytes = new byte[64];
        // MH_MAGIC_64 on disk for a little-endian Mach-O: CF FA ED FE.
        bytes[0] = 0xCF;
        bytes[1] = 0xFA;
        bytes[2] = 0xED;
        bytes[3] = 0xFE;
        BitConverter.GetBytes(0x0100000C).CopyTo(bytes, 4); // CPU_TYPE_ARM64
        await File.WriteAllBytesAsync(Path.Combine(_tempDir, "sample.macho"), bytes);

        BinaryAnalysisTools.BinaryAnalysisResult result = await BinaryAnalysisTools.AnalyzeAsync(
            _tempDir, "sample.macho", CancellationToken.None);

        Assert.Equal("Mach-O", result.Format);
        Assert.True(result.Is64Bit);
        Assert.Equal("ARM64/AArch64", result.Architecture);
        Assert.Equal("little", result.Endianness);
    }

    [Fact]
    public async Task ExtractStringsAsync_finds_ascii_and_utf16le_without_duplicates()
    {
        var bytes = new List<byte>();
        bytes.AddRange(new byte[] { 0, 1, 2, 3 });
        bytes.AddRange(Encoding.ASCII.GetBytes("ASCII_MARKER"));
        bytes.Add(0);
        bytes.AddRange(Encoding.Unicode.GetBytes("WIDE_MARKER"));
        bytes.AddRange(new byte[] { 0, 0 });
        await File.WriteAllBytesAsync(Path.Combine(_tempDir, "strings.bin"), bytes.ToArray());

        List<string> strings = await BinaryAnalysisTools.ExtractStringsAsync(
            _tempDir, "strings.bin", CancellationToken.None, minLength: 5, maxResults: 50);

        Assert.Contains("ASCII_MARKER", strings);
        Assert.Contains("WIDE_MARKER", strings);
        Assert.Equal(strings.Count, strings.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task AnalyzeAsync_rejects_a_path_that_escapes_the_workspace()
    {
        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            BinaryAnalysisTools.AnalyzeAsync(_tempDir, "../outside.bin", CancellationToken.None));
        Assert.Contains("escape", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AnalyzeAsync_keeps_unknown_artifacts_useful()
    {
        await File.WriteAllBytesAsync(
            Path.Combine(_tempDir, "blob.bin"), Enumerable.Repeat((byte)0x41, 256).ToArray());

        BinaryAnalysisTools.BinaryAnalysisResult result = await BinaryAnalysisTools.AnalyzeAsync(
            _tempDir, "blob.bin", CancellationToken.None);

        Assert.Equal("unknown", result.Format);
        Assert.Equal(256, result.SizeBytes);
        Assert.NotEmpty(result.Sha256);
        Assert.Contains(result.Notes, note =>
            note.Contains("No supported executable", StringComparison.Ordinal));
    }
}
