using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using PaRuX.Contracts;
using PaRuX.Server;
using PaRuX.Server.EvalLab;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>vNext Phase 62 - EvalRunnerService's actual-output/judge pipeline, exercised without a
/// live local model via FakeLocalCompletionClient (the same double AutopilotServiceTests/
/// AgentWorkerServiceTests use). The single most important test here is
/// Unparseable_judge_output_is_recorded_as_inconclusive_never_pass_or_fail - this codebase's own
/// directive section 120 rule ("never fabricate a PASS") applies just as much to eval scoring as it
/// does to VerifierService's task verdicts, and an eval lab that silently defaulted an unparseable
/// judge response to pass (or to fail) would make every regression report untrustworthy.</summary>
public sealed class EvalRunnerServiceTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "parux-evalrunner-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private (EvalRunnerService runner, EvalStore store, FakeLocalCompletionClient client, EvalDatasetDto dataset) Build()
    {
        var client = new FakeLocalCompletionClient();
        var store = new EvalStore(_tempDir);
        var runner = new EvalRunnerService(client, store);
        EvalDatasetDto dataset = store.CreateDataset(new CreateEvalDatasetRequest { Name = "Dataset" });
        return (runner, store, client, dataset);
    }

    [Fact]
    public async Task A_case_whose_judge_says_PASS_is_recorded_with_verdict_pass()
    {
        var (runner, store, client, dataset) = Build();
        store.CreateCase(dataset.Id, new CreateEvalCaseRequest { Prompt = "Say hi", ExpectedBehavior = "A friendly greeting" });
        client.EnqueueOk("Hello there!");           // actual output
        client.EnqueueOk("PASS - greets the user");  // judge verdict

        EvalRunDto run = await runner.RunAsync(dataset.Id, CancellationToken.None);

        Assert.Equal(EvalRunStatus.Completed, run.Status);
        Assert.Equal(1, run.PassCount);
        Assert.Equal(0, run.FailCount);
        Assert.Equal(0, run.InconclusiveCount);

        EvalCaseResultDto result = store.ListCaseResults(run.Id).Single();
        Assert.Equal(EvalVerdict.Pass, result.Verdict);
        Assert.Equal("Hello there!", result.ActualOutput);
    }

    [Fact]
    public async Task A_case_whose_judge_says_FAIL_is_recorded_with_verdict_fail()
    {
        var (runner, store, client, dataset) = Build();
        store.CreateCase(dataset.Id, new CreateEvalCaseRequest { Prompt = "Say hi", ExpectedBehavior = "A friendly greeting" });
        client.EnqueueOk("Go away.");
        client.EnqueueOk("FAIL - hostile, not a greeting");

        EvalRunDto run = await runner.RunAsync(dataset.Id, CancellationToken.None);

        Assert.Equal(0, run.PassCount);
        Assert.Equal(1, run.FailCount);
        Assert.Equal(0, run.InconclusiveCount);
        Assert.Equal(EvalVerdict.Fail, store.ListCaseResults(run.Id).Single().Verdict);
    }

    /// <summary>Directive section 120, applied to eval scoring: an unparseable judge response must
    /// land in its own "inconclusive" bucket - never silently treated as a pass, and never silently
    /// treated as a fail either.</summary>
    [Fact]
    public async Task Unparseable_judge_output_is_recorded_as_inconclusive_never_pass_or_fail()
    {
        var (runner, store, client, dataset) = Build();
        store.CreateCase(dataset.Id, new CreateEvalCaseRequest { Prompt = "Say hi", ExpectedBehavior = "A friendly greeting" });
        client.EnqueueOk("Hello there!");
        client.EnqueueOk("I'm not sure, this is ambiguous."); // does not start with PASS or FAIL

        EvalRunDto run = await runner.RunAsync(dataset.Id, CancellationToken.None);

        Assert.Equal(0, run.PassCount);
        Assert.Equal(0, run.FailCount);
        Assert.Equal(1, run.InconclusiveCount);

        EvalCaseResultDto result = store.ListCaseResults(run.Id).Single();
        Assert.Equal(EvalVerdict.Inconclusive, result.Verdict);
        Assert.NotEqual(EvalVerdict.Pass, result.Verdict);
        Assert.NotEqual(EvalVerdict.Fail, result.Verdict);
    }

    /// <summary>One case's provider failure must not strand or abort the whole run - it is recorded
    /// as verdict "error" and the run continues to (and completes) the remaining cases.</summary>
    [Fact]
    public async Task A_case_whose_actual_output_call_throws_is_recorded_as_error_and_the_run_continues()
    {
        var (runner, store, client, dataset) = Build();
        store.CreateCase(dataset.Id, new CreateEvalCaseRequest { Prompt = "Crash please", ExpectedBehavior = "Anything" });
        store.CreateCase(dataset.Id, new CreateEvalCaseRequest { Prompt = "Say hi", ExpectedBehavior = "A friendly greeting" });

        client.ThrowOnNextCall = new InvalidOperationException("provider crashed");
        client.EnqueueOk("Hello there!"); // second case's actual output
        client.EnqueueOk("PASS - greets the user"); // second case's judge verdict

        EvalRunDto run = await runner.RunAsync(dataset.Id, CancellationToken.None);

        Assert.Equal(EvalRunStatus.Completed, run.Status); // the run itself still finished
        Assert.Equal(1, run.PassCount);
        Assert.Equal(0, run.FailCount);
        Assert.Equal(0, run.InconclusiveCount);
        Assert.Equal(1, run.ErrorCount);

        List<EvalCaseResultDto> results = store.ListCaseResults(run.Id);
        Assert.Equal(2, results.Count);
        Assert.Contains(results, r => r.Verdict == EvalVerdict.Error && r.ActualOutput == null);
        Assert.Contains(results, r => r.Verdict == EvalVerdict.Pass);
    }

    /// <summary>Real ambiguity this closes (2026-08-27): before ErrorCount existed, an all-errored
    /// run (e.g. no model reachable) and a genuinely empty dataset both reported
    /// PassCount=FailCount=InconclusiveCount=0, indistinguishable from each other. Now they aren't.</summary>
    [Fact]
    public async Task An_all_errored_run_is_distinguishable_from_an_empty_dataset_via_ErrorCount()
    {
        var (runner, store, client, dataset) = Build();
        store.CreateCase(dataset.Id, new CreateEvalCaseRequest { Prompt = "Crash", ExpectedBehavior = "Anything" });
        client.EnqueueFailure("no model loaded");

        EvalRunDto allErrored = await runner.RunAsync(dataset.Id, CancellationToken.None);
        Assert.Equal(0, allErrored.PassCount + allErrored.FailCount + allErrored.InconclusiveCount);
        Assert.Equal(1, allErrored.ErrorCount);

        EvalDatasetDto emptyDataset = store.CreateDataset(new CreateEvalDatasetRequest { Name = "Empty" });
        EvalRunDto emptyRun = await runner.RunAsync(emptyDataset.Id, CancellationToken.None);
        Assert.Equal(0, emptyRun.PassCount + emptyRun.FailCount + emptyRun.InconclusiveCount);
        Assert.Equal(0, emptyRun.ErrorCount); // the real difference: no cases were even attempted
    }

    /// <summary>A provider that returns a Failure result (rather than throwing) must be treated the
    /// same way as a thrown exception - recorded as "error", run continues.</summary>
    [Fact]
    public async Task A_case_whose_actual_output_call_returns_a_failure_result_is_recorded_as_error()
    {
        var (runner, store, client, dataset) = Build();
        store.CreateCase(dataset.Id, new CreateEvalCaseRequest { Prompt = "Say hi", ExpectedBehavior = "A friendly greeting" });
        client.EnqueueFailure("model not loaded");

        EvalRunDto run = await runner.RunAsync(dataset.Id, CancellationToken.None);

        Assert.Equal(EvalRunStatus.Completed, run.Status);
        EvalCaseResultDto result = store.ListCaseResults(run.Id).Single();
        Assert.Equal(EvalVerdict.Error, result.Verdict);
        Assert.Equal("model not loaded", result.VerdictReason);
    }

    /// <summary>Nothing to attempt ever ran (the dataset itself doesn't exist) - the run must land
    /// on "error" status rather than being silently reported as a normal completed run with zero
    /// cases.</summary>
    [Fact]
    public async Task RunAsync_marks_the_run_as_error_when_the_dataset_does_not_exist()
    {
        var client = new FakeLocalCompletionClient();
        var store = new EvalStore(_tempDir);
        var runner = new EvalRunnerService(client, store);

        EvalRunDto run = await runner.RunAsync("parux-test-unknown-" + Guid.NewGuid().ToString("N"), CancellationToken.None);

        Assert.Equal(EvalRunStatus.Error, run.Status);
        Assert.Empty(store.ListCaseResults(run.Id));
        Assert.Empty(client.Calls); // never even attempted to call the model
    }

    /// <summary>After a multi-case run completes, EvalStore's run record's counts must exactly
    /// match the individual case results actually recorded - the property GetRun's callers rely on
    /// to trust the summary without re-reading every result.</summary>
    [Fact]
    public async Task After_a_run_completes_the_stored_run_record_counts_match_the_individual_case_results()
    {
        var (runner, store, client, dataset) = Build();
        store.CreateCase(dataset.Id, new CreateEvalCaseRequest { Prompt = "1", ExpectedBehavior = "one" });
        store.CreateCase(dataset.Id, new CreateEvalCaseRequest { Prompt = "2", ExpectedBehavior = "two" });
        store.CreateCase(dataset.Id, new CreateEvalCaseRequest { Prompt = "3", ExpectedBehavior = "three" });

        client.EnqueueOk("out1"); client.EnqueueOk("PASS");
        client.EnqueueOk("out2"); client.EnqueueOk("FAIL - wrong");
        client.EnqueueOk("out3"); client.EnqueueOk("who knows");

        EvalRunDto run = await runner.RunAsync(dataset.Id, CancellationToken.None);

        EvalRunDto stored = store.GetRun(run.Id)!;
        List<EvalCaseResultDto> results = store.ListCaseResults(run.Id);

        Assert.Equal(results.Count(r => r.Verdict == EvalVerdict.Pass), stored.PassCount);
        Assert.Equal(results.Count(r => r.Verdict == EvalVerdict.Fail), stored.FailCount);
        Assert.Equal(results.Count(r => r.Verdict == EvalVerdict.Inconclusive), stored.InconclusiveCount);
        Assert.Equal(1, stored.PassCount);
        Assert.Equal(1, stored.FailCount);
        Assert.Equal(1, stored.InconclusiveCount);
    }
}

/// <summary>HTTP-level coverage for POST /api/v1/eval/datasets/{id}/runs. The no-Role path (the
/// endpoint's original behavior) is safe to call for real: it only ever reaches a plain HTTP
/// chat-completion call (LlamaServerCompletionClient.CompleteAsync), which gracefully returns a
/// Failure result when nothing is listening on 127.0.0.1:1234 rather than throwing or mutating
/// any runtime state - a fundamentally different risk profile from the role-based endpoints this
/// project's other test files (RuntimeManagerTests/SmartRouterTests/CapabilityProfilerTests)
/// deliberately never call hermetically, which can reach real process-control code. The Role path
/// on this same endpoint DOES route through SmartRouterService and inherits that same hazard, so
/// it is deliberately not exercised here either - covered by live verification instead.</summary>
public sealed class EvalRunEndpointTests : IClassFixture<ApiSecurityTests.PaRuXFactory>
{
    private readonly ApiSecurityTests.PaRuXFactory _factory;

    public EvalRunEndpointTests(ApiSecurityTests.PaRuXFactory factory) => _factory = factory;

    [Fact]
    public async Task Run_endpoint_rejects_missing_token()
    {
        using HttpClient client = _factory.CreateClient();
        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v1/eval/datasets/" + Guid.NewGuid().ToString("N") + "/runs", new StartEvalRunRequest());
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Run_endpoint_with_no_role_starts_a_run_exactly_like_the_original_behavior()
    {
        using HttpClient client = _factory.CreateClient();
        ServerState state = _factory.Services.GetRequiredService<ServerState>();
        client.DefaultRequestHeaders.Add("X-PaRuX-Token", state.Token);

        HttpResponseMessage createDataset = await client.PostAsJsonAsync("/api/v1/eval/datasets",
            new CreateEvalDatasetRequest { Name = "HTTP endpoint test dataset" });
        EvalDatasetDto? dataset = await createDataset.Content.ReadFromJsonAsync<EvalDatasetDto>();
        Assert.NotNull(dataset);

        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v1/eval/datasets/" + dataset!.Id + "/runs", new StartEvalRunRequest());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        EvalRunDto? run = await response.Content.ReadFromJsonAsync<EvalRunDto>();
        Assert.NotNull(run);
        Assert.Equal(dataset.Id, run!.DatasetId);
    }
}
