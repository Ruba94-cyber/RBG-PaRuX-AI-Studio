using System;
using System.Text.RegularExpressions;

namespace PaRuX.Services
{
    /// <summary>
    /// Reject only explicit unsupported test-success claims. A natural-language
    /// completion summary is not executable test evidence. This deliberately
    /// does not block ordinary "done" or honest "tests not run" statements.
    /// Inspired by independent completion/evidence separation, implemented
    /// entirely for PaRuX without importing third-party source code.
    /// </summary>
    public static class CodingCompletionClaimPolicy
    {
        private static readonly Regex PositiveTestClaim = new(
            @"\b(?:(?:all|the|unit|integration|automated)\s+){0,3}(?:tests?|checks?|test\s+suite|verification)\s+(?:(?:have|has|are|were|is)\s+)?(?:all\s+)?(?:passed|passing|green|successful|succeeded)\b|\b\d+\s+(?:unit\s+|integration\s+)?tests?\s+passed\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant |
            RegexOptions.Compiled,
            TimeSpan.FromMilliseconds(100));

        public static bool RequiresCorrection(string? summary, bool verifiedByCommand)
        {
            if (verifiedByCommand || string.IsNullOrWhiteSpace(summary))
                return false;
            foreach (string clause in summary.Split(new[] { '.', '!', '?', ';', '\n' },
                StringSplitOptions.RemoveEmptyEntries))
            {
                string statement = clause.Trim();
                if (statement.StartsWith("if ", StringComparison.OrdinalIgnoreCase)
                    || statement.StartsWith("when ", StringComparison.OrdinalIgnoreCase)
                    || statement.StartsWith("example:", StringComparison.OrdinalIgnoreCase)
                    || statement.StartsWith("not ", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!PositiveTestClaim.IsMatch(statement)) continue;
                // Transparent negative reports must never be treated as a false
                // claim of success merely because they mention the word passed.
                if (Regex.IsMatch(statement,
                        @"\b(?:not|never|haven't|hasn't|didn't|without)\b.{0,24}\bpass(?:ed|ing)?\b",
                        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                    continue;
                return true;
            }
            return false;
        }
    }
}
