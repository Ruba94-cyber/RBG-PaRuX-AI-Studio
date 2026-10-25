using System.Buffers.Binary;
using System.Globalization;

namespace PaRuX.Server;

/// <summary>
/// Header-only checks for the optional start image of a video job. The image becomes the first frame
/// of clip one. PNG, JPEG and WebP sizes are read from the file header, so an unsupported or oversized
/// file is refused before any GPU time is spent. The image is never decoded here.
/// </summary>
internal static class MediaStartImageValidator
{
    internal const long MaximumBytes = 25L * 1024 * 1024;
    internal const int MinimumSide = 64;
    internal const int MaximumSide = 8192;

    /// <summary>The reference must match the requested video's aspect ratio. PaRuX does not stretch or crop it.</summary>
    internal const double MaximumAspectDeviation = 0.02;

    // Base64 turns every 3 bytes into 4 characters. The limit is checked on the encoded text before decoding.
    private const long MaximumBase64Characters = (MaximumBytes + 2) / 3 * 4;

    private static readonly byte[] PngSignature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

    internal readonly record struct ImageInfo(int Width, int Height, string Format, string Extension);

    /// <summary>Decodes raw base64 or a data: URL. Throws InvalidDataException with the reason on failure.</summary>
    internal static byte[] DecodePayload(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new InvalidDataException("The start image is empty.");
        string encoded = text.Trim();
        if (encoded.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            int comma = encoded.IndexOf(',');
            if (comma < 0) throw new InvalidDataException("The start image data URL has no payload.");
            encoded = encoded[(comma + 1)..];
        }
        if (encoded.Length > MaximumBase64Characters)
            throw new InvalidDataException("The start image is larger than the 25 MB limit.");
        try
        {
            return Convert.FromBase64String(encoded);
        }
        catch (FormatException)
        {
            throw new InvalidDataException("The start image is not valid base64 data.");
        }
    }

    /// <summary>Reads the image header. Rejects unsupported formats and sizes outside the supported range.</summary>
    internal static ImageInfo Inspect(byte[] image)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (image.Length == 0) throw new InvalidDataException("The start image is empty.");
        if (image.LongLength > MaximumBytes) throw new InvalidDataException("The start image is larger than the 25 MB limit.");

        ImageInfo info = ReadPng(image) ?? ReadJpeg(image) ?? ReadWebp(image)
            ?? throw new InvalidDataException("The start image must be a PNG, JPEG or WebP file.");
        if (info.Width < MinimumSide || info.Height < MinimumSide)
            throw new InvalidDataException(string.Format(CultureInfo.InvariantCulture,
                "The start image is {0}x{1}; each side must be at least {2} px.", info.Width, info.Height, MinimumSide));
        if (info.Width > MaximumSide || info.Height > MaximumSide)
            throw new InvalidDataException(string.Format(CultureInfo.InvariantCulture,
                "The start image is {0}x{1}; each side must be at most {2} px.", info.Width, info.Height, MaximumSide));
        return info;
    }

    /// <summary>Checks the start image against the requested video size. The aspect ratio must match.</summary>
    internal static ImageInfo ValidateForVideo(byte[] image, int videoWidth, int videoHeight)
    {
        if (videoWidth <= 0 || videoHeight <= 0)
            throw new InvalidDataException("The video size must be positive to check the start image.");
        ImageInfo info = Inspect(image);
        double imageAspect = (double)info.Width / info.Height;
        double videoAspect = (double)videoWidth / videoHeight;
        if (Math.Abs(imageAspect / videoAspect - 1.0) > MaximumAspectDeviation)
            throw new InvalidDataException(string.Format(CultureInfo.InvariantCulture,
                "The start image is {0}x{1}, which does not match the {2}x{3} video aspect ratio. "
                + "Use an image with the same proportions; PaRuX does not stretch or crop it.",
                info.Width, info.Height, videoWidth, videoHeight));
        return info;
    }

    private static ImageInfo? ReadPng(byte[] data)
    {
        if (data.Length < 24 || !data.AsSpan(0, 8).SequenceEqual(PngSignature.AsSpan())) return null;
        if (!data.AsSpan(12, 4).SequenceEqual("IHDR"u8)) return null;
        int width = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(16, 4));
        int height = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(20, 4));
        return new ImageInfo(width, height, "png", ".png");
    }

    private static ImageInfo? ReadJpeg(byte[] data)
    {
        if (data.Length < 4 || data[0] != 0xFF || data[1] != 0xD8) return null;
        int position = 2;
        while (position + 4 <= data.Length)
        {
            if (data[position] != 0xFF) return null;
            byte marker = data[position + 1];
            if (marker == 0xFF)
            {
                position++; // fill byte
                continue;
            }
            // Standalone markers carry no length field.
            if (marker is 0x01 or 0xD8 or >= 0xD0 and <= 0xD7)
            {
                position += 2;
                continue;
            }
            if (marker == 0xD9) return null; // end of image before any frame header

            // Start-of-frame markers, excluding DHT (C4), JPG (C8) and DAC (CC).
            if (marker is >= 0xC0 and <= 0xCF && marker is not (0xC4 or 0xC8 or 0xCC))
            {
                if (position + 9 > data.Length) return null;
                int height = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(position + 5, 2));
                int width = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(position + 7, 2));
                return new ImageInfo(width, height, "jpeg", ".jpg");
            }

            int length = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(position + 2, 2));
            if (length < 2) return null;
            position += 2 + length;
        }
        return null;
    }

    private static ImageInfo? ReadWebp(byte[] data)
    {
        if (data.Length < 25 || !data.AsSpan(0, 4).SequenceEqual("RIFF"u8) || !data.AsSpan(8, 4).SequenceEqual("WEBP"u8))
            return null;
        ReadOnlySpan<byte> chunk = data.AsSpan(12, 4);
        if (chunk.SequenceEqual("VP8X"u8))
        {
            if (data.Length < 30) return null;
            int width = ReadUInt24LittleEndian(data.AsSpan(24, 3)) + 1;
            int height = ReadUInt24LittleEndian(data.AsSpan(27, 3)) + 1;
            return new ImageInfo(width, height, "webp", ".webp");
        }
        if (chunk.SequenceEqual("VP8L"u8))
        {
            if (data[20] != 0x2F) return null;
            uint bits = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(21, 4));
            int width = (int)(bits & 0x3FFF) + 1;
            int height = (int)((bits >> 14) & 0x3FFF) + 1;
            return new ImageInfo(width, height, "webp", ".webp");
        }
        if (chunk.SequenceEqual("VP8 "u8))
        {
            if (data.Length < 30 || data[23] != 0x9D || data[24] != 0x01 || data[25] != 0x2A) return null;
            int width = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(26, 2)) & 0x3FFF;
            int height = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(28, 2)) & 0x3FFF;
            return new ImageInfo(width, height, "webp", ".webp");
        }
        return null;
    }

    private static int ReadUInt24LittleEndian(ReadOnlySpan<byte> bytes) =>
        bytes[0] | (bytes[1] << 8) | (bytes[2] << 16);
}
