using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>
/// Conservative admission estimate for media jobs. This is scheduling metadata only; real peak VRAM
/// continues to come from runtime benchmark evidence. Keeping it engine-neutral lets the text hot
/// pool vacate space before ComfyUI starts rather than reacting after an OOM.
/// </summary>
public static class MediaVramReservationEstimator
{
    private const long GiB = 1024L * 1024 * 1024;

    public static long EstimateBytes(MediaJobRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        string type = (request.Type ?? string.Empty).Trim().ToLowerInvariant();
        int width = MediaGenerationService.NormalizeDimension(request.Width, type == "video" ? 1280 : 1024);
        int height = MediaGenerationService.NormalizeDimension(request.Height, type == "video" ? 720 : 1024);
        double megapixels = width * (double)height / 1_000_000d;

        double baseGiB = type switch
        {
            "video" => 7.5,
            "upscale" => 4.0,
            "edit" or "img2img" => 5.0,
            "custom" or "imported" => 6.0,
            _ => 4.5
        };
        double scaleGiB = Math.Max(0d, megapixels - 1d) * (type == "video" ? 2.2 : 1.25);
        if (type == "video")
        {
            double duration = MediaGenerationService.NormalizeDuration(request.DurationSeconds);
            scaleGiB += Math.Min(4d, Math.Max(0d, duration - 5d) * 0.12d);
        }

        double totalGiB = Math.Clamp(baseGiB + scaleGiB, 2d, 24d);
        return (long)Math.Ceiling(totalGiB * GiB);
    }
}
