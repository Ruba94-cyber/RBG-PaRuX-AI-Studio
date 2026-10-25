using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class SelfHostingBenchmarkTests
{
    [Fact]
    public void Evaluate_requires_grounded_evidence_finish_conditions_and_repair_budget()
    {
        var testCase = new SelfHostingBenchmark.BenchmarkCase(
            "coding-fix",
            "Fix the parser regression and report evidence.",
            new[] { "Parser.cs", "ParserTests" },
            new[] { "fixed", "verified" },
            MaximumRepairs: 1);

        var pass = SelfHostingBenchmark.Evaluate(testCase,
            new SelfHostingBenchmark.AttemptResult(
                "Parser fixed and verified.",
                new[] { "Changed Parser.cs", "ParserTests added" },
                Repairs: 1), 42);
        Assert.True(pass.Passed);
        Assert.Empty(pass.MissingEvidence);
        Assert.Empty(pass.MissingResultFragments);

        var fail = SelfHostingBenchmark.Evaluate(testCase,
            new SelfHostingBenchmark.AttemptResult(
                "Parser fixed.",
                new[] { "Changed Parser.cs" },
                Repairs: 2), 42);
        Assert.False(fail.Passed);
        Assert.Equal("repair budget exceeded", fail.Failure);
        Assert.Contains("ParserTests", fail.MissingEvidence);
        Assert.Contains("verified", fail.MissingResultFragments);
    }

    [Fact]
    public async Task Run_and_write_report_produce_machine_and_human_readable_evidence()
    {
        var cases = new[]
        {
            new SelfHostingBenchmark.BenchmarkCase(
                "case-a", "Do A", new[] { "evidence-a" }, new[] { "done" }, 2),
            new SelfHostingBenchmark.BenchmarkCase(
                "case-b", "Do B", Array.Empty<string>(), new[] { "complete" }, 2)
        };

        SelfHostingBenchmark.Report report = await SelfHostingBenchmark.RunAsync(
            cases,
            (item, _) => Task.FromResult(item.Id == "case-a"
                ? new SelfHostingBenchmark.AttemptResult("done", new[] { "evidence-a" }, 0)
                : new SelfHostingBenchmark.AttemptResult("not yet", Array.Empty<string>(), 0)),
            CancellationToken.None);

        Assert.Equal(2, report.TotalCases);
        Assert.Equal(1, report.PassedCases);
        Assert.Equal(1, report.FailedCases);
        Assert.Equal(0.5d, report.PassRate, 3);

        string root = Path.Combine(Path.GetTempPath(), "parux-self-hosting-" + Guid.NewGuid().ToString("N"));
        try
        {
            await SelfHostingBenchmark.WriteReportAsync(report, root, CancellationToken.None);
            Assert.True(File.Exists(Path.Combine(root, "parux-self-hosting-benchmark.json")));
            string markdown = await File.ReadAllTextAsync(Path.Combine(root, "parux-self-hosting-benchmark.md"));
            Assert.Contains("case-a", markdown);
            Assert.Contains("case-b", markdown);
            Assert.Contains("Pass rate", markdown);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }
}
