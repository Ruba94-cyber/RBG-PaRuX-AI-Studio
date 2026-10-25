using System;

namespace PaRuX.Services
{
    /// <summary>
    /// Soft high-water mark inspired by Jan's preflight compaction strategy
    /// (janhq/jan, Apache-2.0); this is an independently authored policy for
    /// PaRuX's existing lossless checkpoint and exact rendered-token path.
    /// Never claim that estimated/provider-unknown counts are exact.
    /// </summary>
    public static class CodingAgentProactiveContextPolicy
    {
        // Start reducing only replayed evidence before the physical window
        // is exhausted; leave a full output/reasoning reserve at all times.
        public const int HighWaterPercent = 85;
        public const int MinimumSavingCharacters = 256;

        public static bool ShouldTrim(
            int? renderedInputTokens, int observedContextTokens,
            int outputReserveTokens, int replayedObservationCount)
        {
            if (!renderedInputTokens.HasValue || renderedInputTokens.Value <= 0
                || observedContextTokens <= 0 || replayedObservationCount < 3)
                return false;
            long usable = (long)observedContextTokens - Math.Max(0, outputReserveTokens);
            if (usable <= 0) return false;
            // Use integer arithmetic to avoid floating point edge behaviour.
            return renderedInputTokens.Value * 100L >= usable * HighWaterPercent
                && renderedInputTokens.Value <= usable;
        }

        /// <summary>
        /// A first, cheap reduction: cut the budget for replayed observations
        /// without touching repository evidence, immutable task goals, tool
        /// schema, verification state, or the durable checkpoint.
        /// </summary>
        public static int SuggestedRecentEvidenceCharacters(int originalCharacters)
        {
            if (originalCharacters <= 0) return 0;
            return Math.Min(2_500, Math.Max(0, originalCharacters / 2));
        }

        public static bool HasUsefulSaving(int originalLength, int candidateLength) =>
            candidateLength >= 0
            && (long)candidateLength + MinimumSavingCharacters <= originalLength;
    }
}
