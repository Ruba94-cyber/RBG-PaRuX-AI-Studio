using System.Buffers.Binary;
using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>A validated background-removal input: real PNG/JPEG/WebP bytes with a header-read size.</summary>
internal sealed record CutoutImage(byte[] Bytes, string Extension, string MediaType, int Width, int Height);

/// <summary>
/// Pure request validation for background removal: bounded size, real image magic bytes (the file name is never trusted),
/// and a header-read pixel count so a tiny file that declares a huge canvas cannot exhaust memory in the Python engine.
/// </summary>
internal static class CutoutImageInspector
{
    internal const int MaxEncodedBytes = 25 * 1024 * 1024;
    internal const long MaxPixels = 36_000_000;
    internal const int MaxSide = 12_000;

    internal static CutoutImage Validate(CutoutRemoveRequest? request)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.ImageBase64))
            throw new InvalidDataException("imageBase64 is required.");
        // 4 base64 characters carry 3 bytes: refuse oversized payloads before allocating the decoded buffer.
        if (request.ImageBase64.Length > (long)MaxEncodedBytes * 4 / 3 + 16)
            throw new InvalidDataException("The image is larger than " + MaxEncodedBytes / (1024 * 1024) + " MB.");
        byte[] bytes;
        try { bytes = Convert.FromBase64String(request.ImageBase64); }
        catch (FormatException) { throw new InvalidDataException("imageBase64 is not valid Base64 image data."); }
        if (bytes.Length == 0) throw new InvalidDataException("imageBase64 decoded to an empty image payload.");
        if (bytes.Length > MaxEncodedBytes)
            throw new InvalidDataException("The image is larger than " + MaxEncodedBytes / (1024 * 1024) + " MB.");

        string? kind = Sniff(bytes);
        if (kind is null) throw new InvalidDataException("Background removal accepts PNG, JPEG or WebP images only.");
        if (!TryReadSize(bytes, kind, out int width, out int height) || width <= 0 || height <= 0)
            throw new InvalidDataException("The image header is unreadable or truncated.");
        if (width > MaxSide || height > MaxSide || (long)width * height > MaxPixels)
            throw new InvalidDataException("The image is too large (" + width + "x" + height + "); the limit is "
                + MaxPixels / 1_000_000 + " megapixels.");
        return kind switch
        {
            "png" => new CutoutImage(bytes, ".png", "image/png", width, height),
            "jpeg" => new CutoutImage(bytes, ".jpg", "image/jpeg", width, height),
            _ => new CutoutImage(bytes, ".webp", "image/webp", width, height)
        };
    }

    /// <summary>"png", "jpeg", "webp" or null, from the leading bytes only.</summary>
    internal static string? Sniff(ReadOnlySpan<byte> b)
    {
        if (b.Length >= 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47 && b[4] == 0x0D && b[5] == 0x0A && b[6] == 0x1A && b[7] == 0x0A)
            return "png";
        if (b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) return "jpeg";
        if (b.Length >= 12 && b[0] == 'R' && b[1] == 'I' && b[2] == 'F' && b[3] == 'F' && b[8] == 'W' && b[9] == 'E' && b[10] == 'B' && b[11] == 'P')
            return "webp";
        return null;
    }

    internal static bool TryReadSize(ReadOnlySpan<byte> b, string kind, out int width, out int height)
    {
        width = height = 0;
        switch (kind)
        {
            case "png":
                // 8 signature + 4 length + "IHDR" + width + height.
                if (b.Length < 24 || b[12] != 'I' || b[13] != 'H' || b[14] != 'D' || b[15] != 'R') return false;
                width = (int)Math.Min(int.MaxValue, BinaryPrimitives.ReadUInt32BigEndian(b.Slice(16, 4)));
                height = (int)Math.Min(int.MaxValue, BinaryPrimitives.ReadUInt32BigEndian(b.Slice(20, 4)));
                return true;
            case "jpeg":
                return TryReadJpegSize(b, out width, out height);
            case "webp":
                return TryReadWebpSize(b, out width, out height);
            default:
                return false;
        }
    }

    private static bool TryReadJpegSize(ReadOnlySpan<byte> b, out int width, out int height)
    {
        width = height = 0;
        int i = 2;
        while (i + 3 < b.Length)
        {
            if (b[i] != 0xFF) { i++; continue; }
            byte marker = b[i + 1];
            if (marker == 0xFF) { i++; continue; }
            if (marker is 0x01 or (>= 0xD0 and <= 0xD9)) { i += 2; continue; }
            int length = BinaryPrimitives.ReadUInt16BigEndian(b.Slice(i + 2, 2));
            bool startOfFrame = marker is >= 0xC0 and <= 0xCF && marker is not (0xC4 or 0xC8 or 0xCC);
            if (startOfFrame)
            {
                if (i + 9 > b.Length) return false;
                height = BinaryPrimitives.ReadUInt16BigEndian(b.Slice(i + 5, 2));
                width = BinaryPrimitives.ReadUInt16BigEndian(b.Slice(i + 7, 2));
                return true;
            }
            if (length < 2) return false;
            i += 2 + length;
        }
        return false;
    }

    private static bool TryReadWebpSize(ReadOnlySpan<byte> b, out int width, out int height)
    {
        width = height = 0;
        if (b.Length < 30) return false;
        if (b[12] == 'V' && b[13] == 'P' && b[14] == '8' && b[15] == 'X')
        {
            width = 1 + (b[24] | b[25] << 8 | b[26] << 16);
            height = 1 + (b[27] | b[28] << 8 | b[29] << 16);
            return true;
        }
        if (b[12] == 'V' && b[13] == 'P' && b[14] == '8' && b[15] == 'L' && b[20] == 0x2F)
        {
            uint bits = BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(21, 4));
            width = (int)(bits & 0x3FFF) + 1;
            height = (int)((bits >> 14) & 0x3FFF) + 1;
            return true;
        }
        if (b[12] == 'V' && b[13] == 'P' && b[14] == '8' && b[15] == ' ' && b[23] == 0x9D && b[24] == 0x01 && b[25] == 0x2A)
        {
            width = BinaryPrimitives.ReadUInt16LittleEndian(b.Slice(26, 2)) & 0x3FFF;
            height = BinaryPrimitives.ReadUInt16LittleEndian(b.Slice(28, 2)) & 0x3FFF;
            return true;
        }
        return false;
    }
}

/// <summary>Pure PNG facts used to refuse a "transparent PNG" result that cannot actually be transparent.</summary>
internal static class PngAlpha
{
    /// <summary>
    /// True when the PNG can carry transparency: colour type 4 (grey+alpha) or 6 (RGBA), or a tRNS chunk on a palette/grey/RGB image.
    /// False for anything that is not a well-formed PNG.
    /// </summary>
    internal static bool HasAlphaChannel(ReadOnlySpan<byte> png)
    {
        if (CutoutImageInspector.Sniff(png) != "png" || png.Length < 33) return false;
        byte colorType = png[25];
        if (colorType is 4 or 6) return true;
        int offset = 8;
        while (offset + 12 <= png.Length)
        {
            uint length = BinaryPrimitives.ReadUInt32BigEndian(png.Slice(offset, 4));
            if (length > int.MaxValue - 12) return false;
            ReadOnlySpan<byte> type = png.Slice(offset + 4, 4);
            if (type[0] == 't' && type[1] == 'R' && type[2] == 'N' && type[3] == 'S') return true;
            if (type[0] == 'I' && type[1] == 'D' && type[2] == 'A' && type[3] == 'T') return false;
            long next = (long)offset + 12 + length;
            if (next > png.Length) return false;
            offset = (int)next;
        }
        return false;
    }
}
