using System.Buffers.Binary;
using System.Text.Json;

namespace PaRuX.Server;

/// <summary>Metadata of one tensor from a safetensors header. Holds no weight bytes.</summary>
internal sealed record SafetensorsTensor(string Name, string Dtype, long[] Shape, long DataBegin, long DataEnd);

/// <summary>Validated header of one safetensors file. <see cref="HeaderBytes"/> is the only region of the
/// file that was read, apart from the 8-byte length prefix.</summary>
internal sealed record SafetensorsHeader(
    long HeaderBytes,
    long FileBytes,
    IReadOnlyList<SafetensorsTensor> Tensors,
    IReadOnlyDictionary<string, string> Metadata);

/// <summary>
/// Reads only the 8-byte little-endian length prefix and the JSON header of a safetensors file, then
/// validates every tensor entry against the file size. Fail-closed for hostile or truncated input: an
/// oversized header length is rejected before any buffer is allocated, offsets must lie inside the data
/// section, and duplicate names are refused. Dtypes this reader does not know are accepted with bounds
/// checks only, so a newer quantisation format is not refused just for being new.
/// </summary>
internal static class SafetensorsHeaderReader
{
    /// <summary>Largest header accepted. Real diffusion-model headers are a few megabytes; this bounds memory.</summary>
    public const int MaxHeaderBytes = 16 * 1024 * 1024;

    public const int MaxTensorCount = 200_000;
    private const int MaxRank = 8;
    private const int MaxNameLength = 512;

    public static SafetensorsHeader ReadFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Read(stream);
    }

    public static SafetensorsHeader Read(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanSeek) throw new InvalidDataException("A seekable stream is required to read a safetensors header.");
        long fileBytes = stream.Length;
        if (fileBytes < 8) throw new InvalidDataException("The file is too small to be a safetensors file.");

        Span<byte> prefix = stackalloc byte[8];
        ReadExactly(stream, prefix);
        ulong headerLength = BinaryPrimitives.ReadUInt64LittleEndian(prefix);
        if (headerLength == 0) throw new InvalidDataException("The safetensors header is empty.");
        if (headerLength > MaxHeaderBytes)
            throw new InvalidDataException("The safetensors header is larger than the " + (MaxHeaderBytes / (1024 * 1024)) + " MiB limit.");
        if ((ulong)(fileBytes - 8) < headerLength)
            throw new InvalidDataException("The safetensors header extends past the end of the file (truncated download?).");

        byte[] header = new byte[(int)headerLength];
        ReadExactly(stream, header);
        long dataBytes = fileBytes - 8 - (long)headerLength;

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(header, new JsonDocumentOptions { MaxDepth = 16 });
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The safetensors header is not valid JSON: " + exception.Message, exception);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("The safetensors header must be a JSON object.");

            var names = new HashSet<string>(StringComparer.Ordinal);
            var tensors = new List<SafetensorsTensor>();
            var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (JsonProperty property in document.RootElement.EnumerateObject())
            {
                if (!names.Add(property.Name))
                    throw new InvalidDataException("The safetensors header lists \"" + Clip(property.Name) + "\" more than once.");
                if (property.Name == "__metadata__")
                {
                    metadata = ReadMetadata(property.Value);
                    continue;
                }
                if (property.Name.Length == 0 || property.Name.Length > MaxNameLength)
                    throw new InvalidDataException("A tensor name in the safetensors header has an invalid length.");
                if (tensors.Count >= MaxTensorCount)
                    throw new InvalidDataException("The safetensors header lists more than " + MaxTensorCount + " tensors.");
                tensors.Add(ReadTensor(property.Name, property.Value, dataBytes));
            }

            return new SafetensorsHeader(
                (long)headerLength,
                fileBytes,
                tensors,
                metadata);
        }
    }

    private static SafetensorsTensor ReadTensor(string name, JsonElement value, long dataBytes)
    {
        if (value.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Tensor \"" + Clip(name) + "\" is not described by an object.");

        if (!value.TryGetProperty("dtype", out JsonElement dtypeElement) || dtypeElement.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("Tensor \"" + Clip(name) + "\" has no dtype.");
        string dtype = dtypeElement.GetString() ?? string.Empty;
        if (dtype.Length == 0 || dtype.Length > 16)
            throw new InvalidDataException("Tensor \"" + Clip(name) + "\" has an invalid dtype.");

        if (!value.TryGetProperty("shape", out JsonElement shapeElement) || shapeElement.ValueKind != JsonValueKind.Array
            || shapeElement.GetArrayLength() > MaxRank)
            throw new InvalidDataException("Tensor \"" + Clip(name) + "\" has an invalid shape.");
        var shape = new long[shapeElement.GetArrayLength()];
        int index = 0;
        foreach (JsonElement dimension in shapeElement.EnumerateArray())
        {
            if (!dimension.TryGetInt64(out long size) || size < 0)
                throw new InvalidDataException("Tensor \"" + Clip(name) + "\" has a negative or non-integer dimension.");
            shape[index++] = size;
        }

        if (!value.TryGetProperty("data_offsets", out JsonElement offsetsElement) || offsetsElement.ValueKind != JsonValueKind.Array
            || offsetsElement.GetArrayLength() != 2)
            throw new InvalidDataException("Tensor \"" + Clip(name) + "\" has invalid data_offsets.");
        long begin = ReadOffset(offsetsElement[0], name);
        long end = ReadOffset(offsetsElement[1], name);
        if (begin > end || end > dataBytes)
            throw new InvalidDataException("Tensor \"" + Clip(name) + "\" points outside the file's data section.");

        int itemBytes = DtypeBytes(dtype);
        if (itemBytes > 0)
        {
            long elements = 1;
            try
            {
                foreach (long dimension in shape) elements = checked(elements * dimension);
            }
            catch (OverflowException)
            {
                throw new InvalidDataException("Tensor \"" + Clip(name) + "\" has a shape too large to be real.");
            }
            long expected;
            try { expected = checked(elements * itemBytes); }
            catch (OverflowException) { throw new InvalidDataException("Tensor \"" + Clip(name) + "\" has a shape too large to be real."); }
            if (end - begin != expected)
                throw new InvalidDataException("Tensor \"" + Clip(name) + "\" has a data size that does not match its shape and dtype.");
        }

        return new SafetensorsTensor(name, dtype, shape, begin, end);
    }

    private static long ReadOffset(JsonElement element, string name)
    {
        if (!element.TryGetInt64(out long offset) || offset < 0)
            throw new InvalidDataException("Tensor \"" + Clip(name) + "\" has an invalid data offset.");
        return offset;
    }

    private static Dictionary<string, string> ReadMetadata(JsonElement value)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (value.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("The safetensors __metadata__ entry must be a JSON object.");
        foreach (JsonProperty property in value.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.String)
                throw new InvalidDataException("The safetensors __metadata__ values must be strings.");
            result[property.Name] = property.Value.GetString() ?? string.Empty;
        }
        return result;
    }

    /// <summary>Bytes per element for the dtypes in the safetensors specification; 0 when unknown.</summary>
    private static int DtypeBytes(string dtype) => dtype switch
    {
        "BOOL" or "U8" or "I8" or "F8_E5M2" or "F8_E4M3" or "F8_E8M0" => 1,
        "I16" or "U16" or "F16" or "BF16" => 2,
        "I32" or "U32" or "F32" => 4,
        "I64" or "U64" or "F64" => 8,
        _ => 0
    };

    private static void ReadExactly(Stream stream, Span<byte> buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int read = stream.Read(buffer[total..]);
            if (read == 0) throw new InvalidDataException("The file ended before the safetensors header did.");
            total += read;
        }
    }

    private static string Clip(string value) => value.Length <= 80 ? value : value[..80] + "...";
}
