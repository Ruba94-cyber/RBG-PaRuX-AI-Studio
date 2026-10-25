using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace PaRuX.Server;

/// <summary>
/// Deterministic, local-only benchmark harness for PaRuX working on PaRuX. The harness does not
/// know or require a cloud provider: callers supply a local task executor and the harness applies
/// machine-checkable finish conditions, captures bounded evidence, and emits JSON/Markdown reports.
/// Runtime performance claims still require the Windows/GPU evidence gates in PARUX_EXECUTION_PLAN.
/// </summary>
public static class SelfHostingBenchmark
{
    public sealed record BenchmarkCase(
        string Id,
        string Goal,
        IReadOnlyList<string> RequiredEvidence,
        IReadOnlyList<string> RequiredResultFragments,
        int MaximumRepairs = 2);

    public sealed record AttemptResult(
        string Result,
        IReadOnlyList<string> Evidence,
        int Repairs,
        bool Cancelled = false,
        string? Failure = null);

    public sealed record CaseResult(
        string Id,
        bool Passed,
        long DurationMilliseconds,
        int Repairs,
        int RequiredEvidenceCount,
        int MatchedEvidenceCount,
        int RequiredResultFragmentCount,
        int MatchedResultFragmentCount,
        string Failure,
        IReadOnlyList<string> MissingEvidence,
        IReadOnlyList<string> MissingResultFragments);

    public sealed record Report(
        DateTimeOffset StartedUtc,
        DateTimeOffset CompletedUtc,
        int TotalCases,
        int PassedCases,
        int FailedCases,
        double PassRate,
        double AverageDurationMilliseconds,
        double AverageRepairs,
        IReadOnlyList<CaseResult> Cases);

    public static async Task<Report> RunAsync(
        IEnumerable<BenchmarkCase> cases,
        Func<BenchmarkCase, CancellationToken, Task<AttemptResult>> executor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cases);
        ArgumentNullException.ThrowIfNull(executor);
        BenchmarkCase[] materialized = cases.ToArray();
        if (materialized.Length == 0) throw new InvalidDataException("At least one self-hosting benchmark case is required.");
        ValidateCases(materialized);

        DateTimeOffset startedUtc = DateTimeOffset.UtcNow;
        var results = new List<CaseResult>(materialized.Length);
        foreach (BenchmarkCase item in materialized)
        {
            cancellationToken.ThrowIfCancellationRequested();
            long started = Stopwatch.GetTimestamp();
            AttemptResult attempt;
            try
            {
                attempt = await executor(item, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                attempt = new AttemptResult(string.Empty, Array.Empty<string>(), 0, Failure: Bound(ex.Message, 500));
            }

            results.Add(Evaluate(item, attempt,
                (long)Math.Max(0d, Stopwatch.GetElapsedTime(started).TotalMilliseconds)));
        }

        DateTimeOffset completedUtc = DateTimeOffset.UtcNow;
        int passed = results.Count(result => result.Passed);
        return new Report(
            startedUtc,
            completedUtc,
            results.Count,
            passed,
            results.Count - passed,
            passed / (double)results.Count,
            results.Average(result => (double)result.DurationMilliseconds),
            results.Average(result => (double)result.Repairs),
            results);
    }

    public static CaseResult Evaluate(BenchmarkCase item, AttemptResult attempt, long durationMilliseconds = 0)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(attempt);
        string[] evidence = (attempt.Evidence ?? Array.Empty<string>())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .ToArray();
        string result = attempt.Result ?? string.Empty;

        string[] missingEvidence = item.RequiredEvidence
            .Where(required => !evidence.Any(actual => actual.Contains(required, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        string[] missingFragments = item.RequiredResultFragments
            .Where(required => !result.Contains(required, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        string failure = Bound(attempt.Failure, 500);
        if (attempt.Cancelled) failure = "cancelled";
        else if (attempt.Repairs > item.MaximumRepairs) failure = "repair budget exceeded";
        else if (failure.Length == 0 && missingEvidence.Length > 0) failure = "required evidence missing";
        else if (failure.Length == 0 && missingFragments.Length > 0) failure = "finish condition not satisfied";

        bool passed = !attempt.Cancelled
            && attempt.Repairs <= item.MaximumRepairs
            && failure.Length == 0
            && missingEvidence.Length == 0
            && missingFragments.Length == 0;

        return new CaseResult(
            item.Id,
            passed,
            Math.Max(0, durationMilliseconds),
            Math.Max(0, attempt.Repairs),
            item.RequiredEvidence.Count,
            item.RequiredEvidence.Count - missingEvidence.Length,
            item.RequiredResultFragments.Count,
            item.RequiredResultFragments.Count - missingFragments.Length,
            failure,
            missingEvidence,
            missingFragments);
    }

    public static async Task WriteReportAsync(Report report, string outputDirectory, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (string.IsNullOrWhiteSpace(outputDirectory)) throw new InvalidDataException("A benchmark output directory is required.");
        Directory.CreateDirectory(outputDirectory);
        string jsonPath = Path.Combine(outputDirectory, "parux-self-hosting-benchmark.json");
        string markdownPath = Path.Combine(outputDirectory, "parux-self-hosting-benchmark.md");
        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
        await File.WriteAllTextAsync(jsonPath, JsonSerializer.Serialize(report, jsonOptions), cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(markdownPath, ToMarkdown(report), cancellationToken).ConfigureAwait(false);
    }

    public static string ToMarkdown(Report report)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# PaRuX self-hosting benchmark");
        sb.AppendLine();
        sb.AppendLine($"- Cases: {report.TotalCases}");
        sb.AppendLine($"- Passed: {report.PassedCases}");
        sb.AppendLine($"- Failed: {report.FailedCases}");
        sb.AppendLine(string.Format(
            System.Globalization.CultureInfo.InvariantCulture, "- Pass rate: {0:P1}", report.PassRate));
        sb.AppendLine($"- Average duration: {report.AverageDurationMilliseconds:0} ms");
        sb.AppendLine($"- Average repairs: {report.AverageRepairs:0.00}");
        sb.AppendLine();
        sb.AppendLine("| Case | Passed | Repairs | Evidence | Finish conditions | Failure |");
        sb.AppendLine("|---|---:|---:|---:|---:|---|");
        foreach (CaseResult item in report.Cases)
        {
            sb.Append('|').Append(Escape(item.Id)).Append('|')
                .Append(item.Passed ? "yes" : "no").Append('|')
                .Append(item.Repairs).Append('|')
                .Append(item.MatchedEvidenceCount).Append('/').Append(item.RequiredEvidenceCount).Append('|')
                .Append(item.MatchedResultFragmentCount).Append('/').Append(item.RequiredResultFragmentCount).Append('|')
                .Append(Escape(item.Failure)).AppendLine("|");
        }
        return sb.ToString();
    }

    private static void ValidateCases(IEnumerable<BenchmarkCase> cases)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (BenchmarkCase item in cases)
        {
            if (string.IsNullOrWhiteSpace(item.Id) || item.Id.Length > 120)
                throw new InvalidDataException("Every benchmark case needs a bounded id.");
            if (!ids.Add(item.Id)) throw new InvalidDataException("Benchmark case ids must be unique.");
            if (string.IsNullOrWhiteSpace(item.Goal) || item.Goal.Length > 20_000)
                throw new InvalidDataException("Every benchmark case needs a bounded goal.");
            if (item.RequiredEvidence.Count > 64 || item.RequiredResultFragments.Count > 64)
                throw new InvalidDataException("Benchmark finish conditions are too large.");
            if (item.MaximumRepairs is < 0 or > 8)
                throw new InvalidDataException("maximumRepairs must be between 0 and 8.");
        }
    }

    private static string Bound(string? value, int max)
    {
        string normalized = string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
        return normalized.Length <= max ? normalized : normalized[..max];
    }

    private static string Escape(string value) => (value ?? string.Empty).Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");
}
