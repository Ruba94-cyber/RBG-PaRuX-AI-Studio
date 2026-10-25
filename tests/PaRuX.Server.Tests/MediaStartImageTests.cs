using System.Buffers.Binary;
using System.Text;
using PaRuX.Contracts;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>Header-only image builders for start-image tests. They carry only the bytes the validator reads.</summary>
internal static class StartImageFixtures
{
    public static byte[] Png(int width, int height)
    {
        var bytes = new byte[33];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(8), 13);
        "IHDR"u8.CopyTo(bytes.AsSpan(12));
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(16), width);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(20), height);
        bytes[24] = 8;
        bytes[25] = 6;
        return bytes;
    }

    public static byte[] Jpeg(int width, int height)
    {
        var bytes = new List<byte> { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x04, 0x00, 0x00 }; // SOI, APP0 (empty payload)
        // SOF0: precision, height, width, then three components.
        bytes.AddRange(new byte[]
        {
            0xFF, 0xC0, 0x00, 0x11, 0x08,
            (byte)(height >> 8), (byte)height, (byte)(width >> 8), (byte)width,
            0x03, 0x01, 0x22, 0x00, 0x02, 0x11, 0x01, 0x03, 0x11, 0x01
        });
        bytes.AddRange(new byte[] { 0xFF, 0xD9 });
        return bytes.ToArray();
    }

    public static byte[] WebpLossy(int width, int height)
    {
        var bytes = RiffHeader("VP8 ", 10, 30);
        bytes[23] = 0x9D;
        bytes[24] = 0x01;
        bytes[25] = 0x2A;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(26), (ushort)width);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(28), (ushort)height);
        return bytes;
    }

    public static byte[] WebpLossless(int width, int height)
    {
        var bytes = RiffHeader("VP8L", 5, 25);
        bytes[20] = 0x2F;
        uint bits = (uint)(width - 1) | ((uint)(height - 1) << 14);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(21), bits);
        return bytes;
    }

    public static byte[] WebpExtended(int width, int height)
    {
        var bytes = RiffHeader("VP8X", 10, 30);
        WriteUInt24LittleEndian(bytes.AsSpan(24), width - 1);
        WriteUInt24LittleEndian(bytes.AsSpan(27), height - 1);
        return bytes;
    }

    private static byte[] RiffHeader(string chunk, uint chunkSize, int totalLength)
    {
        var bytes = new byte[totalLength];
        "RIFF"u8.CopyTo(bytes.AsSpan(0));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), (uint)(totalLength - 8));
        "WEBP"u8.CopyTo(bytes.AsSpan(8));
        Encoding.ASCII.GetBytes(chunk).CopyTo(bytes.AsSpan(12));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), chunkSize);
        return bytes;
    }

    private static void WriteUInt24LittleEndian(Span<byte> target, int value)
    {
        target[0] = (byte)value;
        target[1] = (byte)(value >> 8);
        target[2] = (byte)(value >> 16);
    }
}

public sealed class MediaStartImageTests
{
    [Fact]
    public void Header_dimensions_are_read_for_png_jpeg_and_webp_without_decoding()
    {
        var png = MediaStartImageValidator.Inspect(StartImageFixtures.Png(1280, 720));
        Assert.Equal((1280, 720, "png"), (png.Width, png.Height, png.Format));

        var jpeg = MediaStartImageValidator.Inspect(StartImageFixtures.Jpeg(1280, 720));
        Assert.Equal((1280, 720, "jpeg"), (jpeg.Width, jpeg.Height, jpeg.Format));

        foreach (byte[] webp in new[]
        {
            StartImageFixtures.WebpLossy(1280, 720),
            StartImageFixtures.WebpLossless(1280, 720),
            StartImageFixtures.WebpExtended(1280, 720)
        })
        {
            var info = MediaStartImageValidator.Inspect(webp);
            Assert.Equal((1280, 720, "webp"), (info.Width, info.Height, info.Format));
        }
    }

    [Fact]
    public void Unsupported_bytes_are_refused_before_anything_is_queued()
    {
        byte[] gif = Encoding.ASCII.GetBytes("GIF89a" + new string('\0', 64));

        InvalidDataException ex = Assert.Throws<InvalidDataException>(() => MediaStartImageValidator.Inspect(gif));

        Assert.Contains("PNG, JPEG or WebP", ex.Message);
    }

    [Fact]
    public void Images_outside_the_supported_side_range_are_refused()
    {
        InvalidDataException small = Assert.Throws<InvalidDataException>(() =>
            MediaStartImageValidator.Inspect(StartImageFixtures.Png(32, 32)));
        Assert.Contains("at least", small.Message);

        InvalidDataException large = Assert.Throws<InvalidDataException>(() =>
            MediaStartImageValidator.Inspect(StartImageFixtures.Png(9000, 9000)));
        Assert.Contains("at most", large.Message);
    }

    [Fact]
    public void Start_image_must_match_the_video_aspect_ratio_within_two_percent()
    {
        MediaStartImageValidator.ValidateForVideo(StartImageFixtures.Png(1280, 720), 1280, 720);
        MediaStartImageValidator.ValidateForVideo(StartImageFixtures.Png(1282, 720), 1280, 720);

        InvalidDataException mismatch = Assert.Throws<InvalidDataException>(() =>
            MediaStartImageValidator.ValidateForVideo(StartImageFixtures.Png(1024, 1024), 1280, 720));
        Assert.Contains("aspect ratio", mismatch.Message);
        Assert.Contains("does not stretch or crop", mismatch.Message);
    }

    [Fact]
    public void Aspect_tolerance_accepts_just_inside_two_percent_and_refuses_just_outside()
    {
        // 1300x720 is 1.56 percent wider than the 16:9 request; 1310x720 is 2.34 percent wider; 1344x720 is 5 percent.
        MediaStartImageValidator.ValidateForVideo(StartImageFixtures.Png(1300, 720), 1280, 720);
        Assert.Throws<InvalidDataException>(() =>
            MediaStartImageValidator.ValidateForVideo(StartImageFixtures.Png(1310, 720), 1280, 720));
        Assert.Throws<InvalidDataException>(() =>
            MediaStartImageValidator.ValidateForVideo(StartImageFixtures.Png(1344, 720), 1280, 720));
    }

    [Fact]
    public void Oversized_start_images_are_refused_before_their_header_is_read()
    {
        byte[] oversized = new byte[MediaStartImageValidator.MaximumBytes + 1];
        oversized[0] = 0x89; // not a valid PNG, so only the size check can refuse it first

        InvalidDataException ex = Assert.Throws<InvalidDataException>(() => MediaStartImageValidator.Inspect(oversized));

        Assert.Contains("25 MB", ex.Message);
    }

    [Fact]
    public void Payloads_accept_data_urls_and_refuse_text_that_is_not_base64()
    {
        byte[] png = StartImageFixtures.Png(1280, 720);

        Assert.Equal(png, MediaStartImageValidator.DecodePayload("data:image/png;base64," + Convert.ToBase64String(png)));
        Assert.Equal(png, MediaStartImageValidator.DecodePayload(Convert.ToBase64String(png)));

        InvalidDataException ex = Assert.Throws<InvalidDataException>(() => MediaStartImageValidator.DecodePayload("%%% not base64 %%%"));
        Assert.Contains("not valid base64", ex.Message);
    }

    [Fact]
    public void Video_job_with_a_mismatched_start_image_is_refused_at_creation()
    {
        var service = new MediaJobService(null!);
        var request = new MediaJobRequest
        {
            Type = "video",
            Prompt = "a slow pan",
            Width = 1280,
            Height = 720,
            SourceImageBase64 = Convert.ToBase64String(StartImageFixtures.Png(1024, 1024))
        };

        Assert.Throws<InvalidDataException>(() => service.Create(request));
        Assert.Empty(service.List());
    }

    [Fact]
    public async Task Start_image_bytes_reach_comfyui_upload_unchanged()
    {
        await using FakeComfyUi comfy = await FakeComfyUi.StartAsync();
        var runner = new ComfyWorkflowRunner(null!, null!, null!) { MediaBaseUrl = comfy.BaseUrl };
        byte[] image = StartImageFixtures.Jpeg(1280, 720);

        string uploadedName = await runner.UploadImageAsync(image, "parux-start.jpg", CancellationToken.None);

        Assert.StartsWith("uploaded-", uploadedName, StringComparison.Ordinal);
        // The fake records the length and a SHA-256 of the bytes it received, so a changed byte fails this test.
        string digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(image));
        Assert.Contains("POST /upload/image " + image.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + " " + digest, comfy.Requests);
    }
}
