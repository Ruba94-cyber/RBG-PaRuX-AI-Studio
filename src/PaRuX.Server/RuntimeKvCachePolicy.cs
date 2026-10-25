using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>
/// Output-quality guard for KV-cache precision. A quantised KV cache is lossy: it can change the greedy text.
/// The calibration compares each candidate's fixed-prompt greedy output with the reference (highest precision that
/// ran) and rejects the candidate when the text diverges early. The measure is the share of the output that is
/// identical before the first differing character (1.0 = identical output).
/// </summary>
internal static class KvQualityGuard
{
    /// <summary>How many characters of the greedy calibration answer are kept for the comparison.</summary>
    internal const int SampleChars = 600;
    /// <summary>q8_0 / q5_0 must keep at least this share of the reference text identical before diverging.</summary>
    internal const double MinimumPrefixRatioModerate = 0.85;
    /// <summary>q4_0 is the lossiest option, so it is held to a stricter bar.</summary>
    internal const double MinimumPrefixRatioAggressive = 0.95;

    internal static string? Sample(params string?[] parts)
    {
        string joined = string.Concat(parts.Select(p => p ?? string.Empty));
        if (joined.Length == 0) return null;
        return joined.Length <= SampleChars ? joined : joined[..SampleChars];
    }

    /// <summary>Common-prefix length divided by the longer text's length. Unknown (missing) text is 0, never a pass.</summary>
    internal static double PrefixRatio(string? reference, string? candidate)
    {
        if (string.IsNullOrEmpty(reference) || string.IsNullOrEmpty(candidate)) return 0;
        int max = Math.Min(reference.Length, candidate.Length);
        int common = 0;
        while (common < max && reference[common] == candidate[common]) common++;
        return (double)common / Math.Max(reference.Length, candidate.Length);
    }

    internal static double MinimumPrefixRatio(string cacheType) =>
        KvCachePolicy.Aggressiveness(cacheType) >= 3 ? MinimumPrefixRatioAggressive : MinimumPrefixRatioModerate;

    internal static bool Passes(RuntimeOptimizationServerResult reference, RuntimeOptimizationServerResult candidate)
    {
        if (reference.OutputDigest != null && reference.OutputDigest == candidate.OutputDigest) return true;
        return PrefixRatio(reference.OutputSample, candidate.OutputSample) >= MinimumPrefixRatio(candidate.Profile.CacheTypeK);
    }
}

internal sealed record KvChoice(RuntimeOptimizationServerResult Winner, string Reason);

/// <summary>
/// Which KV-cache precision a (model, hardware, runtime) fingerprint keeps. Rule: full precision (f16) is the default.
/// A quantised cache is kept only when (a) the context would not fit with f16 KV next to the weights, or (b) it measures
/// faster by a stated margin, and in both cases its greedy output must stay within the <see cref="KvQualityGuard"/>
/// tolerance of the reference and prefill/TTFT must not regress.
/// </summary>
internal static class KvCachePolicy
{
    internal const double MinimumSpeedGain = 1.05;
    internal const double MinimumSpeedGainAggressive = 1.08;
    internal const double MaximumTtftRegression = 1.15;
    internal const double MinimumPromptRatio = 0.90;

    /// <summary>1 = f16/bf16, 2 = q8_0/q5_0/q5_1, 3 = q4_0 and below.</summary>
    internal static int Aggressiveness(string? cacheType)
    {
        string t = (cacheType ?? string.Empty).Trim().ToLowerInvariant();
        if (t is "f16" or "bf16" or "f32" or "") return 1;
        if (t.StartsWith("q8", StringComparison.Ordinal) || t.StartsWith("q5", StringComparison.Ordinal)) return 2;
        return 3;
    }

    /// <summary>
    /// Estimate of whether an f16 KV cache for the requested total context fits beside the weights. An estimate only: it
    /// decides whether the quantised options may be kept without a speed gain; speed is always measured, never estimated.
    /// Unknown geometry returns false (full precision stays the default).
    /// </summary>
    internal static bool ContextNeedsQuantisedKv(
        PaRuX.Intelligence.GgufModelInfo? model, long totalContext, long modelBytes, long vramBytes)
    {
        if (model is null || vramBytes <= 0 || totalContext <= 0 || modelBytes <= 0) return false;
        if (model.BlockCount is not > 0 || model.KeyValueHeadCount is not > 0) return false;
        long headDim = (model.KeyLength ?? 0) + (model.ValueLength ?? 0);
        if (headDim <= 0)
        {
            if (model.EmbeddingLength is not > 0 || model.AttentionHeadCount is not > 0) return false;
            headDim = 2L * model.EmbeddingLength.Value / model.AttentionHeadCount.Value;
        }
        long interval = Math.Max(1, model.FullAttentionInterval ?? 1);
        long attentionLayers = Math.Max(1, model.BlockCount.Value / interval);
        double kvBytes = (double)attentionLayers * model.KeyValueHeadCount.Value * headDim * totalContext * 2.0; // f16
        const double overhead = 1.5 * 1024 * 1024 * 1024; // compute buffers + desktop headroom
        return modelBytes + kvBytes + overhead > vramBytes;
    }

    internal static KvChoice Choose(
        IReadOnlyList<RuntimeOptimizationServerResult> raced,
        RuntimeOptimizationServerResult baseline,
        string workload,
        bool contextNeedsQuantisedKv,
        bool fullPrecisionFailed)
    {
        List<RuntimeOptimizationServerResult> good = raced
            .Where(c => c.Status == "completed" && c.DecodeTokensPerSecond > 0 && c.PromptTokensPerSecond > 0
                && c.TimeToFirstTokenMilliseconds > 0)
            .ToList();
        if (!good.Contains(baseline) && baseline.Status == "completed") good.Add(baseline);
        if (good.Count == 0) return new KvChoice(baseline, "no KV candidate completed; baseline kept");

        RuntimeOptimizationServerResult reference = good
            .OrderBy(c => Aggressiveness(c.Profile.CacheTypeK))
            .ThenByDescending(c => c.Score ?? 0).First();
        bool needsVram = contextNeedsQuantisedKv || fullPrecisionFailed;
        bool longContext = RuntimePerformanceTuner.NormalizeWorkload(workload) == "long-context";

        RuntimeOptimizationServerResult best = reference;
        bool replacedByNeed = false;
        string reason = Describe(reference) + " kept: no quantised KV cache was both faster by "
            + ((MinimumSpeedGain - 1) * 100).ToString("0") + "% and within the output-quality tolerance";
        foreach (RuntimeOptimizationServerResult candidate in good
            .Where(c => Aggressiveness(c.Profile.CacheTypeK) > Aggressiveness(reference.Profile.CacheTypeK))
            .OrderBy(c => Aggressiveness(c.Profile.CacheTypeK)).ThenByDescending(c => c.DecodeTokensPerSecond ?? 0))
        {
            int level = Aggressiveness(candidate.Profile.CacheTypeK);
            if (candidate.ThermalRegressionSuspected && !reference.ThermalRegressionSuspected) continue;
            if (!KvQualityGuard.Passes(reference, candidate)) { reason += $" ({Describe(candidate)} rejected: output diverges beyond tolerance)"; continue; }
            if ((candidate.TimeToFirstTokenMilliseconds ?? double.MaxValue) > (reference.TimeToFirstTokenMilliseconds ?? 0) * MaximumTtftRegression) continue;
            if ((candidate.PromptTokensPerSecond ?? 0) < (reference.PromptTokensPerSecond ?? 0) * MinimumPromptRatio) continue;
            if (level >= 3 && longContext && !needsVram) continue;

            if (ReferenceEquals(best, reference) && needsVram && !replacedByNeed && Aggressiveness(reference.Profile.CacheTypeK) == 1)
            {
                best = candidate;
                replacedByNeed = true;
                reason = Describe(candidate) + " kept: the context does not fit with a full-precision KV cache on this GPU, output within tolerance";
                continue;
            }
            double margin = level >= 3 ? MinimumSpeedGainAggressive : MinimumSpeedGain;
            if ((candidate.DecodeTokensPerSecond ?? 0) >= (best.DecodeTokensPerSecond ?? 0) * margin)
            {
                reason = Describe(candidate) + " kept: measured " + (((candidate.DecodeTokensPerSecond ?? 0) / Math.Max(1e-9, best.DecodeTokensPerSecond ?? 1) - 1) * 100).ToString("0.0")
                    + "% faster decode than " + Describe(best) + ", output within tolerance";
                best = candidate;
            }
        }
        return new KvChoice(best, reason);
    }

    private static string Describe(RuntimeOptimizationServerResult r) => (string.IsNullOrWhiteSpace(r.Profile.CacheTypeK) ? "f16" : r.Profile.CacheTypeK) + " KV";
}
