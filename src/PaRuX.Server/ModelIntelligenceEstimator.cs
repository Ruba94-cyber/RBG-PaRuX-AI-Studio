using System.Text.RegularExpressions;

namespace PaRuX.Server;

/// <summary>
/// Conservative capability prior used only when PaRuX has incomplete empirical evaluation data.
/// It never claims parameter count is "intelligence": measured Capability Profiler evidence wins.
/// The scale prior exists so an unprofiled 27B model is not automatically ranked below an
/// unprofiled 4B model merely because the 4B is faster. Once real capability runs exist, those
/// results dominate this prior.
/// </summary>
public static class ModelIntelligenceEstimator
{
    // The prefix boundary includes "x"/"X" so the common MoE "<experts>x<size>B" naming
    // convention (e.g. "Mixtral-8x7B") yields the per-expert size (7) rather than being skipped
    // entirely because "x" sits between the expert count and the size token.
    private static readonly Regex ParameterCountRegex = new(
        @"(?:^|[-_.\sxX])(?<value>\d+(?:\.\d+)?)\s*[bB](?:[-_.\s]|$)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public sealed record Estimate(
        double? Score,
        double? ParameterBillions,
        string Evidence,
        IReadOnlyList<string> ModelCapabilities);

    public static Estimate EstimateFrom(
        string fileName,
        double? measuredCapabilityScore,
        IReadOnlyList<string>? modelCapabilities = null)
    {
        IReadOnlyList<string> taxonomyCapabilities = modelCapabilities
            ?? ModelCategoryClassifier.CapabilitiesOf(fileName, null, null);
        double? billions = TryParseParameterBillions(fileName);
        double? prior = billions.HasValue ? ScalePrior(billions.Value) : null;

        if (measuredCapabilityScore.HasValue)
        {
            double measured = Math.Clamp(measuredCapabilityScore.Value, 0, 1);
            if (!prior.HasValue)
                return new Estimate(measured, billions, "measured-capability", taxonomyCapabilities);

            // Empirical capability remains the source of truth. Model scale is deliberately a
            // small prior/tie-break signal because architecture, training and quantization can
            // make a smaller model outperform a larger one on real tasks.
            double combined = measured * 0.85 + prior.Value * 0.15;
            return new Estimate(combined, billions, "measured-capability+scale-prior", taxonomyCapabilities);
        }

        return prior.HasValue
            ? new Estimate(prior, billions, "scale-prior-unmeasured", taxonomyCapabilities)
            : new Estimate(null, null, "unmeasured", taxonomyCapabilities);
    }

    public static double? TryParseParameterBillions(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return null;
        MatchCollection matches = ParameterCountRegex.Matches(fileName);
        if (matches.Count == 0) return null;

        // For names such as "30B-A3B" prefer the largest declared parameter count. This is a
        // total-scale prior, not an assertion about active MoE parameters; the Evidence field
        // keeps that distinction visible to callers.
        double? largest = null;
        foreach (Match match in matches)
        {
            if (!double.TryParse(match.Groups["value"].Value,
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out double value))
                continue;
            if (value <= 0 || value > 1000) continue;
            largest = !largest.HasValue || value > largest.Value ? value : largest;
        }
        return largest;
    }

    public static double ScalePrior(double parameterBillions)
    {
        parameterBillions = Math.Clamp(parameterBillions, 0.1, 405);
        // Log scale avoids treating a 70B model as literally 17.5x "smarter" than a 4B model.
        // 70B ~= 1.0 reference; larger models gain only a bounded incremental prior.
        double reference = Math.Log(1 + 70.0);
        return Math.Clamp(Math.Log(1 + parameterBillions) / reference, 0, 1.15);
    }
}
