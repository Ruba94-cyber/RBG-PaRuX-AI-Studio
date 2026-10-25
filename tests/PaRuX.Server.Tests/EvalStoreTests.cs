using PaRuX.Contracts;
using PaRuX.Server.EvalLab;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>vNext Phase 62 - EvalStore CRUD plus the two properties that matter most for a
/// regression lab: GetRun's counts genuinely reflect the individual case results recorded against
/// it, and Compare correctly identifies exactly the cases whose verdict flipped between two runs
/// of the same dataset (and nothing that didn't flip).</summary>
public sealed class EvalStoreTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "parux-eval-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public void CreateDataset_rejects_an_empty_name()
    {
        var store = new EvalStore(_tempDir);
        Assert.Throws<InvalidDataException>(() => store.CreateDataset(new CreateEvalDatasetRequest { Name = " " }));
    }

    [Fact]
    public void CreateCase_rejects_unknown_dataset_or_missing_fields()
    {
        var store = new EvalStore(_tempDir);
        EvalDatasetDto dataset = store.CreateDataset(new CreateEvalDatasetRequest { Name = "Smoke tests" });

        Assert.Throws<InvalidDataException>(() => store.CreateCase(
            "parux-test-unknown-" + Guid.NewGuid().ToString("N"),
            new CreateEvalCaseRequest { Prompt = "Hi", ExpectedBehavior = "Says hello" }));

        Assert.Throws<InvalidDataException>(() => store.CreateCase(dataset.Id,
            new CreateEvalCaseRequest { Prompt = " ", ExpectedBehavior = "Says hello" }));
        Assert.Throws<InvalidDataException>(() => store.CreateCase(dataset.Id,
            new CreateEvalCaseRequest { Prompt = "Hi", ExpectedBehavior = " " }));
    }

    [Fact]
    public void CreateDataset_and_CreateCase_round_trip_through_List()
    {
        var store = new EvalStore(_tempDir);
        EvalDatasetDto dataset = store.CreateDataset(new CreateEvalDatasetRequest { Name = "Greeting checks", Description = "Basic sanity" });
        Assert.Contains(store.ListDatasets(), d => d.Id == dataset.Id);
        Assert.Equal(dataset.Id, store.GetDataset(dataset.Id)!.Id);

        store.CreateCase(dataset.Id, new CreateEvalCaseRequest { Prompt = "Say hi", ExpectedBehavior = "A friendly greeting" });
        store.CreateCase(dataset.Id, new CreateEvalCaseRequest { Prompt = "Say bye", ExpectedBehavior = "A friendly farewell" });

        List<EvalCaseDto> cases = store.ListCases(dataset.Id);
        Assert.Equal(2, cases.Count);
        Assert.All(cases, c => Assert.Equal(dataset.Id, c.DatasetId));
    }

    [Fact]
    public void CreateRun_starts_running_with_zero_counts_and_GetRun_reflects_recorded_case_results()
    {
        var store = new EvalStore(_tempDir);
        EvalDatasetDto dataset = store.CreateDataset(new CreateEvalDatasetRequest { Name = "Dataset" });
        EvalCaseDto caseA = store.CreateCase(dataset.Id, new CreateEvalCaseRequest { Prompt = "A", ExpectedBehavior = "Does A" });
        EvalCaseDto caseB = store.CreateCase(dataset.Id, new CreateEvalCaseRequest { Prompt = "B", ExpectedBehavior = "Does B" });
        EvalCaseDto caseC = store.CreateCase(dataset.Id, new CreateEvalCaseRequest { Prompt = "C", ExpectedBehavior = "Does C" });

        EvalRunDto run = store.CreateRun(dataset.Id);
        Assert.Equal(EvalRunStatus.Running, run.Status);
        Assert.Equal(0, run.PassCount);
        Assert.Equal(0, run.FailCount);
        Assert.Equal(0, run.InconclusiveCount);
        Assert.Null(run.CompletedUtc);

        store.AddCaseResult(run.Id, caseA.Id, "did A", EvalVerdict.Pass, "PASS - did the thing");
        store.AddCaseResult(run.Id, caseB.Id, "did not do B", EvalVerdict.Fail, "FAIL - missed it");
        store.AddCaseResult(run.Id, caseC.Id, "??", EvalVerdict.Inconclusive, "unparseable judge response");

        bool updated = store.UpdateRunStatus(run.Id, EvalRunStatus.Completed, passCount: 1, failCount: 1, inconclusiveCount: 1);
        Assert.True(updated);

        EvalRunDto finished = store.GetRun(run.Id)!;
        Assert.Equal(EvalRunStatus.Completed, finished.Status);
        Assert.Equal(1, finished.PassCount);
        Assert.Equal(1, finished.FailCount);
        Assert.Equal(1, finished.InconclusiveCount);
        Assert.NotNull(finished.CompletedUtc);

        List<EvalCaseResultDto> results = store.ListCaseResults(run.Id);
        Assert.Equal(3, results.Count);
        Assert.Equal(EvalVerdict.Pass, results.Single(r => r.CaseId == caseA.Id).Verdict);
        Assert.Equal(EvalVerdict.Fail, results.Single(r => r.CaseId == caseB.Id).Verdict);
        Assert.Equal(EvalVerdict.Inconclusive, results.Single(r => r.CaseId == caseC.Id).Verdict);
    }

    /// <summary>The core regression-lab property: Compare must surface exactly the cases whose
    /// verdict flipped between two runs, and nothing else.</summary>
    [Fact]
    public void Compare_identifies_only_the_cases_whose_verdict_flipped_between_two_runs()
    {
        var store = new EvalStore(_tempDir);
        EvalDatasetDto dataset = store.CreateDataset(new CreateEvalDatasetRequest { Name = "Regression set" });
        EvalCaseDto staysPassing = store.CreateCase(dataset.Id, new CreateEvalCaseRequest { Prompt = "1", ExpectedBehavior = "1" });
        EvalCaseDto regresses = store.CreateCase(dataset.Id, new CreateEvalCaseRequest { Prompt = "2", ExpectedBehavior = "2" });
        EvalCaseDto improves = store.CreateCase(dataset.Id, new CreateEvalCaseRequest { Prompt = "3", ExpectedBehavior = "3" });

        EvalRunDto previousRun = store.CreateRun(dataset.Id);
        store.AddCaseResult(previousRun.Id, staysPassing.Id, "ok", EvalVerdict.Pass, "PASS");
        store.AddCaseResult(previousRun.Id, regresses.Id, "ok", EvalVerdict.Pass, "PASS");
        store.AddCaseResult(previousRun.Id, improves.Id, "bad", EvalVerdict.Fail, "FAIL");
        store.UpdateRunStatus(previousRun.Id, EvalRunStatus.Completed, 2, 1, 0);

        EvalRunDto currentRun = store.CreateRun(dataset.Id);
        store.AddCaseResult(currentRun.Id, staysPassing.Id, "still ok", EvalVerdict.Pass, "PASS");
        store.AddCaseResult(currentRun.Id, regresses.Id, "now bad", EvalVerdict.Fail, "FAIL");
        store.AddCaseResult(currentRun.Id, improves.Id, "now good", EvalVerdict.Pass, "PASS");
        store.UpdateRunStatus(currentRun.Id, EvalRunStatus.Completed, 2, 1, 0);

        EvalRunComparisonResponse comparison = store.Compare(currentRun.Id, previousRun.Id);

        Assert.Equal(2, comparison.ChangedCases.Count);
        Assert.DoesNotContain(comparison.ChangedCases, c => c.CaseId == staysPassing.Id);

        EvalCaseDiffDto regression = comparison.ChangedCases.Single(c => c.CaseId == regresses.Id);
        Assert.Equal(EvalVerdict.Pass, regression.PreviousVerdict);
        Assert.Equal(EvalVerdict.Fail, regression.CurrentVerdict);

        EvalCaseDiffDto improvement = comparison.ChangedCases.Single(c => c.CaseId == improves.Id);
        Assert.Equal(EvalVerdict.Fail, improvement.PreviousVerdict);
        Assert.Equal(EvalVerdict.Pass, improvement.CurrentVerdict);
    }
}
