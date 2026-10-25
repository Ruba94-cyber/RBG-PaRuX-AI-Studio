using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>
/// Chooses the coding model from measured local coding-task outcomes on the current hardware.
///
/// Quality, reliability and speed are weighted together; speed never decides alone. Quality is the
/// Wilson lower bound of the verified-task rate, so a model with few or mixed results is ranked
/// conservatively instead of being rewarded for one lucky fast run. Speed only counts verified
/// runs: a fast wrong answer earns nothing. Hardware fit is a gate, because a model that cannot be
/// admitted on this machine is not a choice. Without enough evidence for at least two admissible
/// candidates the coding role's assigned model stays in use and the reason says so.
/// </summary>
public static class CodingModelSelector
{
    public const double QualityWeight = 0.50;
    public const double ReliabilityWeight = 0.30;
    public const double SpeedWeight = 0.20;
    public const int MinimumAttempts = 3;
    /// <summary>A winner must be verifiable at least this often (Wilson lower bound).</summary>
    public const double MinimumQuality = 0.20;
    /// <summary>Models whose quality lower bound trails the best by more than this cannot win on speed.</summary>
    public const double QualityTolerance = 0.10;

    public sealed record Candidate(string FileName, string FilePath, bool HardwareCompatible, bool IsAssigned);

    public sealed record Outcome(string FileName, bool Verified, bool ProtocolTrouble, double WallSeconds);

    public static CodingModelSelectionDto Select(
        string mode,
        IReadOnlyList<Candidate> candidates,
        IReadOnlyList<Outcome> outcomes,
        string hardwareFingerprint,
        DateTime utcNow)
    {
        string normalizedMode = CodingModelSelectionMode.Normalize(mode);
        Candidate? assigned = candidates.FirstOrDefault(c => c.IsAssigned);
        var scores = new List<CodingModelScoreDto>();
        foreach (Candidate candidate in candidates)
        {
            List<Outcome> own = outcomes
                .Where(o => string.Equals(o.FileName, candidate.FileName, StringComparison.OrdinalIgnoreCase))
                .ToList();
            List<double> verifiedSeconds = own.Where(o => o.Verified && o.WallSeconds > 0)
                .Select(o => o.WallSeconds).OrderBy(s => s).ToList();
            scores.Add(new CodingModelScoreDto
            {
                FileName = candidate.FileName,
                FilePath = candidate.FilePath,
                HardwareCompatible = candidate.HardwareCompatible,
                Attempts = own.Count,
                Verified = own.Count(o => o.Verified),
                ProtocolTrouble = own.Count(o => o.ProtocolTrouble),
                MedianVerifiedSeconds = verifiedSeconds.Count == 0 ? null : Median(verifiedSeconds),
                Quality = own.Count == 0 ? 0 : WilsonLowerBound(own.Count(o => o.Verified), own.Count),
                Reliability = own.Count == 0 ? 0 : 1.0 - (double)own.Count(o => o.ProtocolTrouble) / own.Count,
                Status = !candidate.HardwareCompatible ? "excluded: does not fit this hardware"
                    : own.Count < MinimumAttempts ? "insufficient evidence (" + own.Count + "/" + MinimumAttempts + " attempts)"
                    : "ranked"
            });
        }

        List<CodingModelScoreDto> ranked = scores.Where(s => s.Status == "ranked").ToList();
        double? fastest = ranked.Where(s => s.MedianVerifiedSeconds.HasValue)
            .Select(s => s.MedianVerifiedSeconds!.Value).DefaultIfEmpty().Min() is double min && min > 0 ? min : null;
        foreach (CodingModelScoreDto score in ranked)
        {
            score.Speed = fastest.HasValue && score.MedianVerifiedSeconds.HasValue
                ? fastest.Value / score.MedianVerifiedSeconds.Value
                : 0;
            score.Utility = QualityWeight * score.Quality + ReliabilityWeight * score.Reliability + SpeedWeight * score.Speed;
            if (score.Quality < MinimumQuality)
                score.Status = "below quality floor";
        }
        // Speed and reliability may only choose among models of comparable measured quality. A
        // speed advantage can never buy back a clearly lower verified-task rate.
        double bestQuality = ranked.Where(s => s.Status == "ranked").Select(s => s.Quality).DefaultIfEmpty(0).Max();
        foreach (CodingModelScoreDto score in ranked.Where(s => s.Status == "ranked"))
        {
            if (score.Quality < bestQuality - QualityTolerance)
                score.Status = "quality clearly below the best measured candidate";
        }

        CodingModelScoreDto? winner = ranked
            .Where(s => s.Status == "ranked")
            .OrderByDescending(s => s.Utility)
            .ThenByDescending(s => s.Quality)
            .FirstOrDefault();

        var decision = new CodingModelSelectionDto
        {
            Mode = normalizedMode,
            EffectiveSource = CodingModelSelectionMode.Assigned,
            SelectedFilePath = assigned?.FilePath,
            SelectedFileName = assigned?.FileName,
            HardwareFingerprint = hardwareFingerprint,
            QualityWeight = QualityWeight,
            ReliabilityWeight = ReliabilityWeight,
            SpeedWeight = SpeedWeight,
            MinimumAttempts = MinimumAttempts,
            Candidates = scores.OrderByDescending(s => s.Utility ?? -1).ThenBy(s => s.FileName, StringComparer.OrdinalIgnoreCase).ToList(),
            DecidedUtc = utcNow,
            Reason = string.Empty
        };

        if (normalizedMode == CodingModelSelectionMode.Assigned)
        {
            decision.Reason = "Automatic coding-model selection is off; the coding role's assigned model is used.";
            return decision;
        }
        if (ranked.Count < 2)
        {
            decision.Reason = "Not enough measured coding outcomes on this hardware: " + ranked.Count
                + " candidate(s) have at least " + MinimumAttempts + " attempts and fit; at least 2 are needed to compare. The assigned coding model is used.";
            return decision;
        }
        if (winner == null)
        {
            decision.Reason = "No measured candidate reached the verified-task quality floor; the assigned coding model is used.";
            return decision;
        }

        decision.EffectiveSource = CodingModelSelectionMode.Auto;
        decision.SelectedFilePath = winner.FilePath;
        decision.SelectedFileName = winner.FileName;
        decision.Reason = "Measured winner on this hardware: " + winner.FileName
            + string.Format(System.Globalization.CultureInfo.InvariantCulture,
                " (utility {0:0.000} = {1:0.00}×quality {2:0.000} + {3:0.00}×reliability {4:0.000} + {5:0.00}×speed {6:0.000}; {7}/{8} verified).",
                winner.Utility, QualityWeight, winner.Quality, ReliabilityWeight, winner.Reliability, SpeedWeight, winner.Speed,
                winner.Verified, winner.Attempts);
        return decision;
    }

    /// <summary>Wilson score interval lower bound at 95% confidence.</summary>
    public static double WilsonLowerBound(int successes, int trials)
    {
        if (trials <= 0) return 0;
        const double z = 1.96;
        double p = (double)successes / trials;
        double denominator = 1 + z * z / trials;
        double centre = p + z * z / (2 * trials);
        double margin = z * Math.Sqrt(p * (1 - p) / trials + z * z / (4.0 * trials * trials));
        return Math.Max(0, (centre - margin) / denominator);
    }

    private static double Median(List<double> sorted) =>
        sorted.Count % 2 == 1 ? sorted[sorted.Count / 2] : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2.0;
}
