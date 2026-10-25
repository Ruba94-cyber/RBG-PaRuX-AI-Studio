using System.Collections.Generic;
using System.Globalization;
using PaRuX.Contracts;

namespace PaRuX;

internal static class CandidateEvaluationStatusText
{
    internal static string FormatMetrics(CandidateEvaluationJobStatus status)
    {
        var metrics = new List<string>();
        if (status.TimeToFirstTokenMilliseconds is double ttft && double.IsFinite(ttft) && ttft > 0)
            metrics.Add("TTFT " + ttft.ToString("F0", CultureInfo.InvariantCulture) + " ms");
        if (status.DecodeTokensPerSecond is double decode && double.IsFinite(decode) && decode > 0)
            metrics.Add("measured decode " + decode.ToString("F1", CultureInfo.InvariantCulture) + " tok/s");
        if (status.CompletionMilliseconds is double completion && double.IsFinite(completion) && completion > 0)
            metrics.Add(status.CompletionLatencyIsLowerBound == true
                // A capped sample stopped at the answer-token bound: the true completion time is longer.
                ? "completion ≥ " + completion.ToString("F0", CultureInfo.InvariantCulture) + " ms (capped answer, lower bound)"
                : "completion " + completion.ToString("F0", CultureInfo.InvariantCulture) + " ms");
        return metrics.Count == 0 ? string.Empty : " · " + string.Join(" · ", metrics);
    }

    /// <summary>Which quality checks failed in this run (ids only), so run-to-run variance is visible.</summary>
    internal static string FormatFailedChecks(CandidateEvaluationJobStatus status) =>
        status.QualityFailedCaseIds is { Count: > 0 } ids ? " · failed checks: " + string.Join(", ", ids) : string.Empty;

    /// <summary>Plain launch-scope line: actual AUTO launch identity, or why evidence has no AUTO scope.</summary>
    internal static string FormatLaunch(CandidateEvaluationJobStatus status)
    {
        if (!string.IsNullOrEmpty(status.EvaluationLaunchRefusal))
            return " · not measured under the actual AUTO launch: " + CandidateReasonText.Explain(status.EvaluationLaunchRefusal);
        if (string.IsNullOrEmpty(status.EvaluationLaunchFingerprint)) return string.Empty;
        string shortId = status.EvaluationLaunchFingerprint.Length > 19
            ? status.EvaluationLaunchFingerprint[..19] : status.EvaluationLaunchFingerprint;
        return " · measured under the actual AUTO launch " + shortId
            + (string.IsNullOrEmpty(status.RequestParity) ? string.Empty : " with " + status.RequestParity);
    }
}
