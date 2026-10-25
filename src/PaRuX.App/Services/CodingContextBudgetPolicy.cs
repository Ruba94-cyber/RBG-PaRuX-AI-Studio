using System;

namespace PaRuX.Services
{
    /// <summary>
    /// Pure conversion from an observed model context window to the repository-evidence character
    /// budget used by CodingContextCompiler. Kept I/O-free so the safety math is hermetically
    /// testable without pulling desktop networking/runtime dependencies into server tests.
    ///
    /// Large contexts are useful only if PaRuX can spend some of them on better repository evidence.
    /// The old 30k-character cap made 64k/128k+ models behave almost like a 16k model on coding
    /// tasks. The cap now follows the compiler's own hard ceiling while retaining a large reserve
    /// for task instructions, observations, tool protocol and the next model response.
    /// </summary>
    public static class CodingContextBudgetPolicy
    {
        public const int UnknownContextFallbackCharacters = CodingContextCompiler.DefaultMaxChars;
        public const int MaximumContextCharacters = CodingContextCompiler.MaximumMaxChars;

        public sealed record Budget(int ContextTokens, int MaxCharacters, bool Observed);

        public static Budget ForContextTokens(int contextTokens)
        {
            if (contextTokens <= 0)
                return new Budget(0, UnknownContextFallbackCharacters, false);

            // Reserve at least 25% of n_ctx (and up to 24k tokens) for everything that is not the
            // repository packet. The packet itself receives 38% of what remains. This is purposely
            // conservative: a larger context should improve evidence quality, not invite overflow.
            int reserveTokens = Math.Clamp(contextTokens / 4, 2_048, 24_576);
            int usableTokens = Math.Max(2_048, contextTokens - reserveTokens);
            long requestedCharacters = (long)Math.Floor(usableTokens * 0.38 * 4.0);
            int maxCharacters = (int)Math.Clamp(requestedCharacters, 6_000L, MaximumContextCharacters);
            return new Budget(contextTokens, maxCharacters, true);
        }
    }
}
