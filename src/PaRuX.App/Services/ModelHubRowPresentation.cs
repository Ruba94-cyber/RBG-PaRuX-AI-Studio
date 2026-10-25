using PaRuX.Intelligence;

namespace PaRuX.Services;

public static class ModelHubRowPresentation
{
    public static string HardwareFit(ModelFitVerdict verdict, string summary) =>
        ModelFitVerdictWords.Of(verdict) + " · " + summary;

    public static string Quantization(string fileName, string? metadataQuantization) =>
        metadataQuantization ?? GgufFileNaming.QuantizationOrUnknown(fileName);
}
