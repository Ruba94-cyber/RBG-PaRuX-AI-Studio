using System;
using System.Collections.Generic;
using System.Linq;
using PaRuX.Contracts;

namespace PaRuX.Services
{
    /// <summary>
    /// Pure decisions for automatically widening the coding model's context window when a prompt
    /// cannot fit. The I/O (reading options, storing the preference, reloading) lives in
    /// <see cref="CodingContextRemedy"/>.
    /// </summary>
    public static class CodingContextRemedyPolicy
    {
        public const long MinimumUsefulTokens = 16_384;

        public static long NeededTokens(long currentTokens) =>
            Math.Max(MinimumUsefulTokens, Math.Max(0, currentTokens) * 2);

        /// <summary>
        /// The cheapest option that reaches the target: never beyond the trained window (RoPE
        /// scaling), VRAM before system RAM, then the shortest sufficient length.
        /// </summary>
        public static ModelContextOption? ChooseOption(
            IEnumerable<ModelContextOption>? options, long currentTokens, long neededTokens)
        {
            if (options == null) return null;
            return options
                .Where(option => option.ContextLength >= neededTokens
                    && option.ContextLength > currentTokens
                    && option.RopeScale <= 1.0)
                .OrderBy(option => option.UsesSystemRam)
                .ThenBy(option => option.ContextLength)
                .FirstOrDefault();
        }
    }
}
