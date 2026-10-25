using System.IO.Compression;
using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>
/// Engine-independent PNG quality evidence for text-to-image races. This deliberately does not
/// trust engine-reported metadata: it decodes the returned PNG scanlines, reverses PNG filters and
/// measures real pixels. It rejects corrupt/blank/low-detail output and scores resolution fidelity,
/// luminance variance, local edge energy and compressed content density. Semantic prompt adherence
/// can be layered on later by a local vision judge, but speed alone can never pass this gate.
/// </summary>
public static class MediaImageQualityEvaluator
{
    public static Task<MediaEngineQualityEvidence> EvaluateAsync(
        MediaJobRequest request,
        MediaEngineRaceCandidate candidate,
        MediaAsset asset,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            byte[] png = Convert.FromBase64String(asset.Base64Data);
            PngMetrics metrics = AnalyzePng(png, ct);
            int targetWidth = MediaGenerationService.NormalizeDimension(request.Width, 1024);
            int targetHeight = MediaGenerationService.NormalizeDimension(request.Height, 1024);

            double widthRatio = Math.Min(metrics.Width, targetWidth) / (double)Math.Max(metrics.Width, targetWidth);
            double heightRatio = Math.Min(metrics.Height, targetHeight) / (double)Math.Max(metrics.Height, targetHeight);
            double resolution = Math.Clamp(widthRatio * heightRatio, 0, 1);
            double detail = Math.Clamp(metrics.MeanEdgeDifference / 18.0, 0, 1);
            double variance = Math.Clamp(metrics.LuminanceStdDev / 48.0, 0, 1);
            double density = Math.Clamp((png.Length / (double)(metrics.Width * metrics.Height)) / 1.25, 0, 1);
            double score = 0.35 * resolution + 0.30 * detail + 0.25 * variance + 0.10 * density;

            bool accepted = resolution >= 0.90
                && metrics.MeanEdgeDifference >= 1.2
                && metrics.LuminanceStdDev >= 4.0
                && png.Length >= 24 * 1024;
            string reason = accepted
                ? $"Decoded PNG {metrics.Width}x{metrics.Height}; resolution={resolution:0.###}, edge={metrics.MeanEdgeDifference:0.##}, luminance σ={metrics.LuminanceStdDev:0.##}, density={density:0.###}."
                : $"Rejected decoded PNG: resolution={resolution:0.###}, edge={metrics.MeanEdgeDifference:0.##}, luminance σ={metrics.LuminanceStdDev:0.##}, bytes={png.Length}.";
            return Task.FromResult(new MediaEngineQualityEvidence(accepted, score, reason));
        }
        catch (Exception ex) when (ex is FormatException or InvalidDataException or IOException)
        {
            return Task.FromResult(new MediaEngineQualityEvidence(false, 0,
                candidate.EngineId + " output could not pass independent PNG analysis: " + Trim(ex.Message, 300)));
        }
    }

    private static PngMetrics AnalyzePng(byte[] png, CancellationToken ct)
    {
        ReadOnlySpan<byte> signature = stackalloc byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        if (png.Length < 33 || !png.AsSpan(0, 8).SequenceEqual(signature))
            throw new InvalidDataException("Artifact is not a PNG.");

        int offset = 8;
        int width = 0, height = 0, bitDepth = 0, colorType = 0, interlace = 0;
        using var idat = new MemoryStream();
        bool sawIhdr = false, sawIend = false;
        while (offset + 12 <= png.Length)
        {
            ct.ThrowIfCancellationRequested();
            int length = ReadInt32BigEndian(png, offset);
            if (length < 0 || offset + 12L + length > png.Length)
                throw new InvalidDataException("PNG chunk length is invalid.");
            string type = System.Text.Encoding.ASCII.GetString(png, offset + 4, 4);
            int data = offset + 8;
            if (type == "IHDR")
            {
                if (length != 13) throw new InvalidDataException("PNG IHDR has invalid length.");
                width = ReadInt32BigEndian(png, data);
                height = ReadInt32BigEndian(png, data + 4);
                bitDepth = png[data + 8];
                colorType = png[data + 9];
                interlace = png[data + 12];
                sawIhdr = true;
            }
            else if (type == "IDAT") idat.Write(png, data, length);
            else if (type == "IEND") { sawIend = true; break; }
            offset += checked(12 + length);
        }
        if (!sawIhdr || !sawIend || width <= 0 || height <= 0 || idat.Length == 0)
            throw new InvalidDataException("PNG is missing required chunks.");
        if (width > 8192 || height > 8192 || (long)width * height > 40_000_000)
            throw new InvalidDataException("PNG exceeds bounded quality-analysis dimensions.");
        if (bitDepth != 8 || (colorType != 2 && colorType != 6) || interlace != 0)
            throw new InvalidDataException("PNG quality analysis supports non-interlaced 8-bit RGB/RGBA output.");

        int channels = colorType == 6 ? 4 : 3;
        int stride = checked(width * channels);
        int expected = checked((stride + 1) * height);
        byte[] filtered = new byte[expected];
        idat.Position = 0;
        using (var zlib = new ZLibStream(idat, CompressionMode.Decompress, leaveOpen: true))
        {
            int total = 0;
            while (total < expected)
            {
                ct.ThrowIfCancellationRequested();
                int read = zlib.Read(filtered, total, expected - total);
                if (read == 0) break;
                total += read;
            }
            if (total != expected) throw new InvalidDataException("PNG decompressed pixel payload is truncated.");
            if (zlib.ReadByte() != -1) throw new InvalidDataException("PNG decompressed payload exceeds expected dimensions.");
        }

        byte[] previous = new byte[stride];
        byte[] current = new byte[stride];
        double sum = 0, sumSquares = 0, edgeSum = 0;
        long samples = 0, edges = 0;
        int sourceOffset = 0;
        for (int y = 0; y < height; y++)
        {
            ct.ThrowIfCancellationRequested();
            int filter = filtered[sourceOffset++];
            for (int x = 0; x < stride; x++)
            {
                int raw = filtered[sourceOffset++];
                int left = x >= channels ? current[x - channels] : 0;
                int up = previous[x];
                int upLeft = x >= channels ? previous[x - channels] : 0;
                current[x] = (byte)((raw + Predictor(filter, left, up, upLeft)) & 0xFF);
            }

            double priorLuminance = double.NaN;
            for (int x = 0; x < width; x++)
            {
                int p = x * channels;
                double luminance = 0.2126 * current[p] + 0.7152 * current[p + 1] + 0.0722 * current[p + 2];
                sum += luminance;
                sumSquares += luminance * luminance;
                samples++;
                if (!double.IsNaN(priorLuminance)) { edgeSum += Math.Abs(luminance - priorLuminance); edges++; }
                if (y > 0)
                {
                    double above = 0.2126 * previous[p] + 0.7152 * previous[p + 1] + 0.0722 * previous[p + 2];
                    edgeSum += Math.Abs(luminance - above);
                    edges++;
                }
                priorLuminance = luminance;
            }
            (previous, current) = (current, previous);
        }

        if (samples == 0) throw new InvalidDataException("PNG contains no pixels.");
        double mean = sum / samples;
        double variance = Math.Max(0, sumSquares / samples - mean * mean);
        return new PngMetrics(width, height, Math.Sqrt(variance), edges == 0 ? 0 : edgeSum / edges);
    }

    private static int Predictor(int filter, int left, int up, int upLeft) => filter switch
    {
        0 => 0,
        1 => left,
        2 => up,
        3 => (left + up) / 2,
        4 => Paeth(left, up, upLeft),
        _ => throw new InvalidDataException("PNG uses an unknown scanline filter.")
    };

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c;
        int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    private static int ReadInt32BigEndian(byte[] bytes, int offset) =>
        (bytes[offset] << 24) | (bytes[offset + 1] << 16) | (bytes[offset + 2] << 8) | bytes[offset + 3];

    private static string Trim(string? value, int max)
    {
        string text = (value ?? string.Empty).Trim();
        return text.Length <= max ? text : text[..max] + "…";
    }

    private sealed record PngMetrics(int Width, int Height, double LuminanceStdDev, double MeanEdgeDifference);
}
