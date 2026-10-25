using System.Buffers.Binary;
using System.Text;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Valid and hostile safetensors inputs for the bounded header reader. Every file is synthetic and lives in a
/// temp directory; nothing here reads a real model.
/// </summary>
public sealed class SafetensorsHeaderReaderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "parux-st-" + Guid.NewGuid().ToString("N"));

    public SafetensorsHeaderReaderTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>Writes the 8-byte little-endian length prefix, the JSON header, then a weight section filled with a marker.</summary>
    private string WriteRaw(string name, ulong declaredHeaderLength, string headerJson, int dataBytes)
    {
        byte[] header = Encoding.UTF8.GetBytes(headerJson);
        var bytes = new byte[8 + header.Length + dataBytes];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(0, 8), declaredHeaderLength);
        header.CopyTo(bytes, 8);
        Array.Fill(bytes, (byte)0xAB, 8 + header.Length, dataBytes);
        string path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private string Write(string name, string headerJson, int dataBytes) =>
        WriteRaw(name, (ulong)Encoding.UTF8.GetByteCount(headerJson), headerJson, dataBytes);

    [Fact]
    public void Reads_valid_header_tensors_and_metadata()
    {
        string path = Write("ok.safetensors",
            "{\"__metadata__\":{\"format\":\"pt\"},\"w\":{\"dtype\":\"F32\",\"shape\":[2,3],\"data_offsets\":[0,24]},\"b\":{\"dtype\":\"F16\",\"shape\":[4],\"data_offsets\":[24,32]}}",
            32);

        SafetensorsHeader header = SafetensorsHeaderReader.ReadFile(path);

        Assert.Equal(2, header.Tensors.Count);
        Assert.Equal("w", header.Tensors[0].Name);
        Assert.Equal(new long[] { 2, 3 }, header.Tensors[0].Shape);
        Assert.Equal("F16", header.Tensors[1].Dtype);
        Assert.Equal("pt", header.Metadata["format"]);
        Assert.Equal(new FileInfo(path).Length, header.FileBytes);
    }

    [Fact]
    public void Reads_only_the_header_and_never_the_weight_section()
    {
        // The data section is 4 MiB of marker bytes. A reader that pulled the weights would move the stream
        // position past the header; this tracking stream proves it stops at the end of the JSON.
        string headerJson = "{\"t\":{\"dtype\":\"U8\",\"shape\":[4194304],\"data_offsets\":[0,4194304]}}";
        string path = Write("tracked.safetensors", headerJson, 4 * 1024 * 1024);
        using var tracking = new TrackingStream(File.OpenRead(path));

        SafetensorsHeader header = SafetensorsHeaderReader.Read(tracking);

        Assert.Single(header.Tensors);
        Assert.Equal(8 + Encoding.UTF8.GetByteCount(headerJson), tracking.HighestPositionRead);
    }

    [Fact]
    public void Rejects_a_header_length_above_the_limit_before_allocating()
    {
        // 1 GiB claimed, file is tiny. Must be refused on the length check, not by attempting the allocation.
        string path = WriteRaw("huge.safetensors", 1024UL * 1024 * 1024, "{}", 16);

        var error = Assert.Throws<InvalidDataException>(() => SafetensorsHeaderReader.ReadFile(path));
        Assert.Contains("larger than", error.Message);
    }

    [Fact]
    public void Rejects_a_header_that_runs_past_the_end_of_the_file()
    {
        string path = WriteRaw("truncated.safetensors", 500, "{\"a\":1}", 0);

        var error = Assert.Throws<InvalidDataException>(() => SafetensorsHeaderReader.ReadFile(path));
        Assert.Contains("truncated", error.Message);
    }

    [Fact]
    public void Rejects_an_empty_header_and_a_file_too_small_for_the_prefix()
    {
        string empty = WriteRaw("empty.safetensors", 0, string.Empty, 0);
        Assert.Throws<InvalidDataException>(() => SafetensorsHeaderReader.ReadFile(empty));

        string tiny = Path.Combine(_dir, "tiny.safetensors");
        File.WriteAllBytes(tiny, new byte[] { 1, 2, 3 });
        Assert.Throws<InvalidDataException>(() => SafetensorsHeaderReader.ReadFile(tiny));
    }

    [Fact]
    public void Rejects_invalid_json_and_a_non_object_root()
    {
        string notJson = Write("notjson.safetensors", "{\"a\":", 0);
        Assert.Throws<InvalidDataException>(() => SafetensorsHeaderReader.ReadFile(notJson));

        string array = Write("array.safetensors", "[1,2]", 0);
        var error = Assert.Throws<InvalidDataException>(() => SafetensorsHeaderReader.ReadFile(array));
        Assert.Contains("JSON object", error.Message);
    }

    [Fact]
    public void Rejects_offsets_outside_the_data_section()
    {
        string path = Write("outside.safetensors",
            "{\"t\":{\"dtype\":\"U8\",\"shape\":[100],\"data_offsets\":[0,100]}}", 8);

        var error = Assert.Throws<InvalidDataException>(() => SafetensorsHeaderReader.ReadFile(path));
        Assert.Contains("outside", error.Message);
    }

    [Fact]
    public void Rejects_a_known_dtype_whose_size_does_not_match_its_shape()
    {
        // F32 shape [2] needs 8 bytes; the offsets describe 4.
        string path = Write("mismatch.safetensors",
            "{\"t\":{\"dtype\":\"F32\",\"shape\":[2],\"data_offsets\":[0,4]}}", 8);

        var error = Assert.Throws<InvalidDataException>(() => SafetensorsHeaderReader.ReadFile(path));
        Assert.Contains("does not match", error.Message);
    }

    [Fact]
    public void Rejects_a_shape_whose_element_count_overflows()
    {
        string path = Write("overflow.safetensors",
            "{\"t\":{\"dtype\":\"F32\",\"shape\":[4294967296,4294967296],\"data_offsets\":[0,8]}}", 8);

        var error = Assert.Throws<InvalidDataException>(() => SafetensorsHeaderReader.ReadFile(path));
        Assert.Contains("too large", error.Message);
    }

    [Fact]
    public void Rejects_duplicate_tensor_names()
    {
        string path = Write("dupe.safetensors",
            "{\"t\":{\"dtype\":\"U8\",\"shape\":[1],\"data_offsets\":[0,1]},\"t\":{\"dtype\":\"U8\",\"shape\":[1],\"data_offsets\":[1,2]}}", 2);

        var error = Assert.Throws<InvalidDataException>(() => SafetensorsHeaderReader.ReadFile(path));
        Assert.Contains("more than once", error.Message);
    }

    [Fact]
    public void Rejects_a_negative_dimension_and_a_non_string_dtype()
    {
        string negative = Write("neg.safetensors",
            "{\"t\":{\"dtype\":\"U8\",\"shape\":[-1],\"data_offsets\":[0,1]}}", 1);
        Assert.Throws<InvalidDataException>(() => SafetensorsHeaderReader.ReadFile(negative));

        string numericDtype = Write("numdtype.safetensors",
            "{\"t\":{\"dtype\":5,\"shape\":[1],\"data_offsets\":[0,1]}}", 1);
        Assert.Throws<InvalidDataException>(() => SafetensorsHeaderReader.ReadFile(numericDtype));
    }

    [Fact]
    public void Accepts_an_unknown_dtype_with_bounds_checks_only()
    {
        // A newer quantisation dtype is not refused for being new; only its offsets are checked.
        string path = Write("newdtype.safetensors",
            "{\"t\":{\"dtype\":\"F4\",\"shape\":[8],\"data_offsets\":[0,4]}}", 4);

        SafetensorsHeader header = SafetensorsHeaderReader.ReadFile(path);

        Assert.Equal("F4", header.Tensors[0].Dtype);
    }

    [Fact]
    public void Rejects_non_string_metadata_values()
    {
        string path = Write("meta.safetensors", "{\"__metadata__\":{\"n\":3}}", 0);

        var error = Assert.Throws<InvalidDataException>(() => SafetensorsHeaderReader.ReadFile(path));
        Assert.Contains("__metadata__", error.Message);
    }

    [Fact]
    public void Accepts_exactly_eight_dimensions_and_rejects_a_ninth()
    {
        string eight = Write("rank8.safetensors",
            "{\"t\":{\"dtype\":\"U8\",\"shape\":[1,1,1,1,1,1,1,1],\"data_offsets\":[0,1]}}", 1);
        Assert.Single(SafetensorsHeaderReader.ReadFile(eight).Tensors);

        string nine = Write("rank9.safetensors",
            "{\"t\":{\"dtype\":\"U8\",\"shape\":[1,1,1,1,1,1,1,1,1],\"data_offsets\":[0,1]}}", 1);
        var error = Assert.Throws<InvalidDataException>(() => SafetensorsHeaderReader.ReadFile(nine));
        Assert.Contains("invalid shape", error.Message);
    }

    /// <summary>Stream wrapper that records the highest byte offset ever read, so a test can prove what was touched.</summary>
    private sealed class TrackingStream : Stream
    {
        private readonly Stream _inner;

        public TrackingStream(Stream inner) => _inner = inner;

        public long HighestPositionRead { get; private set; }

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => _inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => _inner.Position = value; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int read = _inner.Read(buffer, offset, count);
            HighestPositionRead = Math.Max(HighestPositionRead, _inner.Position);
            return read;
        }

        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
