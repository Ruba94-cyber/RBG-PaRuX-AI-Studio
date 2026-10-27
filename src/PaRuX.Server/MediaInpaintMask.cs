using System.Globalization;
using System.Text.Json.Nodes;

namespace PaRuX.Server;

/// <summary>
/// Request-side checks and graph patching for the "inpaint" media type: the source image and a PNG mask (white =
/// repaint). Header-only, so a bad pair is refused before any GPU time is spent. The mask may be lower resolution
/// than the source because the graph scales it to the (resolution-capped) source, but its aspect ratio must match,
/// otherwise the repaint region would land in the wrong place.
/// </summary>
internal static class MediaInpaintMask
{
    internal const int MinimumSide = 8;
    internal const int MaximumSide = 4096;
    internal const int DefaultGrowPixels = 6;
    internal const int MaximumGrowPixels = 64;
    internal const double MaximumAspectDeviation = 0.02;

    // Node ids in z-image-inpaint-api.json; the runner patches exactly these.
    internal const string SourceImageNode = "6";
    internal const string MaskImageNode = "8";
    internal const string PromptNode = "4";
    internal const string GrowNode = "12";
    internal const string SamplerNode = "16";
    internal const string SaveNode = "19";

    /// <summary>Checks the pair and returns the decoded mask bytes. Throws InvalidDataException with the user-facing reason.</summary>
    internal static byte[] Validate(byte[] source, string maskBase64)
    {
        if (source == null || source.Length == 0) throw new InvalidDataException("inpaint requires a source image.");
        if (string.IsNullOrWhiteSpace(maskBase64))
            throw new InvalidDataException("inpaint requires maskImageBase64: paint the area to replace first.");
        byte[] mask;
        try { mask = MediaStartImageValidator.DecodePayload(maskBase64); }
        catch (InvalidDataException) { throw new InvalidDataException("The mask is not valid base64 data."); }

        MediaStartImageValidator.ImageInfo? maskInfo = MediaStartImageValidator.TryReadInfo(mask);
        if (maskInfo is not { Format: "png" })
            throw new InvalidDataException("The mask must be a PNG image.");
        MediaStartImageValidator.ImageInfo m = maskInfo.Value;
        if (m.Width < MinimumSide || m.Height < MinimumSide || m.Width > MaximumSide || m.Height > MaximumSide)
            throw new InvalidDataException(string.Format(CultureInfo.InvariantCulture,
                "The mask is {0}x{1}; each side must be between {2} and {3} px.", m.Width, m.Height, MinimumSide, MaximumSide));

        MediaStartImageValidator.ImageInfo? sourceInfo = MediaStartImageValidator.TryReadInfo(source);
        if (sourceInfo is null)
            throw new InvalidDataException("The source image must be a PNG, JPEG or WebP file.");
        MediaStartImageValidator.ImageInfo s = sourceInfo.Value;
        double deviation = Math.Abs(((double)m.Width / m.Height) / ((double)s.Width / s.Height) - 1.0);
        if (deviation > MaximumAspectDeviation)
            throw new InvalidDataException(string.Format(CultureInfo.InvariantCulture,
                "The mask ({0}x{1}) does not have the same proportions as the source image ({2}x{3}). Repaint the mask on this image.",
                m.Width, m.Height, s.Width, s.Height));
        return mask;
    }

    internal static int NormalizeGrow(int? requested) => Math.Clamp(requested ?? DefaultGrowPixels, 0, MaximumGrowPixels);

    /// <summary>Writes the per-job values into the loaded graph. Kept apart from the HTTP upload so it can be tested.</summary>
    internal static void Patch(JsonObject request, string prompt, long seed, string uploadedSourceName, string uploadedMaskName, int growPixels)
    {
        JsonObject graph = (JsonObject)request["prompt"]!;
        void Set(string node, string field, JsonNode value) => ((JsonObject)graph[node]!["inputs"]!)[field] = value;
        Set(SourceImageNode, "image", uploadedSourceName);
        Set(MaskImageNode, "image", uploadedMaskName);
        Set(PromptNode, "text", prompt);
        Set(SamplerNode, "seed", seed);
        Set(GrowNode, "expand", growPixels);
        Set(SaveNode, "filename_prefix", "LocalAI_images/AI_Studio_inpaint");
    }
}
