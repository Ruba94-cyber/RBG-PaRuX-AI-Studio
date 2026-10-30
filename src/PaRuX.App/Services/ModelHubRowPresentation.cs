using PaRuX.Intelligence;

namespace PaRuX.Services;

public static class ModelHubRowPresentation
{
    public static string HardwareFit(ModelFitVerdict verdict, string summary) =>
        ModelFitVerdictWords.Of(verdict) + " · " + summary;

    public static string Quantization(string fileName, string? metadataQuantization) =>
        metadataQuantization ?? GgufFileNaming.QuantizationOrUnknown(fileName);

    /// <summary>Catalogue preview only. The full description remains available in Details.</summary>
    public static string CompactDescription(string? description, int maxLength = 108)
    {
        if (string.IsNullOrWhiteSpace(description)) return "Open Details to learn more.";
        string text = string.Join(" ", description.Split((char[]?)null,
            System.StringSplitOptions.RemoveEmptyEntries));
        if (maxLength < 8) maxLength = 8;
        if (text.Length <= maxLength) return text;
        int boundary = text.LastIndexOf(' ', maxLength - 1);
        if (boundary < maxLength / 2) boundary = maxLength - 1;
        return text[..boundary].TrimEnd(' ', ',', ';', ':') + "…";
    }
}
