namespace PaRuX.Contracts;

/// <summary>
/// vNext Phase 62 - the Automatic Eval/Regression Lab (target capability #15 in
/// PARUX_LEGACY_VNEXT_ARCHITECTURE.md's classification table, previously "MISSING - No eval-dataset/
/// comparison infrastructure found"). A dataset is a named collection of eval cases (prompt +
/// expected behavior); a run executes every case in a dataset against the local model and asks an
/// independent judge call to score each result - the exact same "ask the model to judge, parse
/// strictly, never fabricate a verdict" discipline VerifierService (Phase 7, directive section 120)
/// already established for task completion, reused here for regression scoring instead.
///
/// Compare (EvalStore.Compare) is what makes this a *regression* lab and not just a one-shot eval
/// runner: it diffs two runs of the same dataset case-by-case so a genuine behavior regression
/// (a case that used to PASS and now FAILs) is visible without a human re-reading every transcript.
/// </summary>
public static class EvalRunStatus
{
    public const string Running = "running";
    public const string Completed = "completed";
    public const string Error = "error";
}

/// <summary>The four possible outcomes for one case in one run. "Inconclusive" and "Error" are
/// deliberately distinct buckets, not aliases for Fail: Inconclusive means the judge responded but
/// its response did not clearly parse as PASS or FAIL (directive section 120 - never fabricate a
/// verdict either way); Error means the run never got as far as asking the judge at all (the
/// actual-output call itself failed or threw).</summary>
public static class EvalVerdict
{
    public const string Pass = "pass";
    public const string Fail = "fail";
    public const string Inconclusive = "inconclusive";
    public const string Error = "error";
}

public sealed class EvalDatasetDto
{
    public required string Id { get; set; }
    public required string Name { get; set; }
    public string? Description { get; set; }
    public required DateTime CreatedUtc { get; set; }
}

public sealed class EvalCaseDto
{
    public required string Id { get; set; }
    public required string DatasetId { get; set; }
    public required string Prompt { get; set; }

    /// <summary>Free-text description of what a correct response looks like - the judge call's
    /// yardstick, exactly analogous to the GOAL VerifierService compares a task RESULT against.</summary>
    public required string ExpectedBehavior { get; set; }

    /// <summary>Overrides the default "You are a helpful assistant." system prompt when a case
    /// needs to exercise a specific persona/instruction-following scenario.</summary>
    public string? SystemPrompt { get; set; }
    public required DateTime CreatedUtc { get; set; }
}

public sealed class EvalRunDto
{
    public required string Id { get; set; }
    public required string DatasetId { get; set; }

    /// <summary>One of EvalRunStatus - "running" until every case has been attempted (or the
    /// dataset turned out not to exist at all), then "completed" or "error".</summary>
    public required string Status { get; set; }
    public required int PassCount { get; set; }
    public required int FailCount { get; set; }
    public required int InconclusiveCount { get; set; }
    /// <summary>Cases whose EvalVerdict.Error result (the actual-output or judge call itself
    /// failed/threw, so nothing was ever judged) - a real bucket added 2026-08-27 so an
    /// all-errored run (e.g. no model reachable) is no longer indistinguishable from a genuinely
    /// empty dataset, both of which previously reported 0/0/0. See EvalRunnerService.RunCasesAsync.</summary>
    public int ErrorCount { get; set; }
    public required DateTime StartedUtc { get; set; }
    public DateTime? CompletedUtc { get; set; }
}

public sealed class EvalCaseResultDto
{
    public required string Id { get; set; }
    public required string RunId { get; set; }
    public required string CaseId { get; set; }

    /// <summary>What the model under test actually produced. Null when the actual-output call
    /// itself failed/threw (Verdict is "error" in that case - there was never anything to judge).</summary>
    public string? ActualOutput { get; set; }

    /// <summary>One of EvalVerdict.</summary>
    public required string Verdict { get; set; }

    /// <summary>The judge's one-sentence reason, or the failure/exception message when Verdict is
    /// "error" - kept around so a human reviewing a run doesn't have to re-run it to see why.</summary>
    public string? VerdictReason { get; set; }
    public required DateTime CreatedUtc { get; set; }
}

/// <summary>Body for POST /api/v1/eval/datasets/{id}/runs. When Role is set (2026-08-27, Smart
/// Router return-to-close pass), routes through SmartRouterService first so the run scores
/// against a specific role directly - without going through AutoLab's experiment wrapper, using
/// exactly the JIT-load pattern AutoLabService.CreateComparisonSweepAsync already proved. Null/
/// omitted (the original behavior): runs against whatever is already loaded, unchanged.</summary>
public sealed class StartEvalRunRequest
{
    public string? Role { get; set; }
}

public sealed class CreateEvalDatasetRequest
{
    public required string Name { get; set; }
    public string? Description { get; set; }
}

public sealed class CreateEvalCaseRequest
{
    public required string Prompt { get; set; }
    public required string ExpectedBehavior { get; set; }
    public string? SystemPrompt { get; set; }
}

public sealed class EvalDatasetListResponse
{
    public required List<EvalDatasetDto> Datasets { get; set; }
}

public sealed class EvalCaseListResponse
{
    public required List<EvalCaseDto> Cases { get; set; }
}

public sealed class EvalCaseResultListResponse
{
    public required List<EvalCaseResultDto> Results { get; set; }
}

/// <summary>One case whose verdict differs between two runs of the same dataset - the actual
/// "regression" signal Compare surfaces. PreviousVerdict/CurrentVerdict are always different
/// (that's the filter Compare applies); either side can be null when the case has no result at
/// all in that run (e.g. it was added to the dataset after the older run executed).</summary>
public sealed class EvalCaseDiffDto
{
    public required string CaseId { get; set; }
    public string? PreviousVerdict { get; set; }
    public string? CurrentVerdict { get; set; }
}

public sealed class EvalRunComparisonResponse
{
    public required string RunId { get; set; }
    public required string PreviousRunId { get; set; }
    public required List<EvalCaseDiffDto> ChangedCases { get; set; }
}
