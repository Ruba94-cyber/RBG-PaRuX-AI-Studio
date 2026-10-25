using System;

namespace PaRuX.Services
{
    public static class CodingAgentProtocolRetryPolicy
    {
        public const int MaximumProtocolFailures = 3;

        public static bool ShouldRetry(int failureCount) => failureCount < MaximumProtocolFailures;

        public static string CorrectionInstruction(int failureCount, string reason)
        {
            if (!ShouldRetry(failureCount))
                throw new ArgumentOutOfRangeException(nameof(failureCount), "No protocol retry remains.");

            string detail = string.IsNullOrWhiteSpace(reason)
                ? "the previous response did not contain one valid action object"
                : reason.Trim();

            return failureCount switch
            {
                1 => "Retry 1 of 2: " + detail
                    + ". Return exactly one action object matching the advertised JSON Schema. Preserve your intended action and values; add no prose.",
                2 => "Final retry: " + detail
                    + ". Return exactly one action object matching the advertised JSON Schema. Preserve your intended action and values; add no prose.",
                _ => throw new ArgumentOutOfRangeException(nameof(failureCount), "Protocol failure count must be positive.")
            };
        }

        public static string StopReason(int failureCount) =>
            "Stopped after " + Math.Max(0, failureCount) + " invalid action responses; no action was executed for the last response.";
    }
}
