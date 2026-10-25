using PaRuX.Contracts;
using PaRuX.Server.Orchestration;

namespace PaRuX.Server.EvalLab;

/// <summary>
/// vNext Phase 62 - the Automatic Eval/Regression Lab's runner (target capability #15). For each
/// case in a dataset: (1) call the model under test to get an actual output, (2) hand that output
/// plus the case's expected behavior to an independent judge call. Step (2) deliberately reuses
/// VerifierService's (Phase 7) exact prompt-and-parse shape - a fixed system prompt asking for
/// exactly PASS or FAIL plus a one-sentence reason, parsed via StartsWith - and the same directive
/// section 120 discipline: an unparseable judge response is never fabricated into a pass or a fail,
/// it is its own "inconclusive" verdict. One case failing (either call throws, or the provider
/// itself reports an error) never aborts the run - it is recorded as verdict "error" and the run
/// moves on to the next case, exactly like AgentWorkerService/VerifierService never letting one
/// crash strand the rest of the pipeline.
/// </summary>
public sealed class EvalRunnerService
{
    private const string JudgeSystemPrompt =
        "You are PaRuX's independent Eval judge. You will be given an EXPECTED BEHAVIOR " +
        "description and an ACTUAL RESPONSE produced by another model. Judge strictly whether the " +
        "ACTUAL RESPONSE satisfies the EXPECTED BEHAVIOR. Reply with exactly one line: the single " +
        "word PASS or the single word FAIL, optionally followed by \" - \" and a short one-sentence " +
        "reason. Never reply with anything else.";

    private const string DefaultSystemPrompt = "You are a helpful assistant.";

    private readonly ILocalCompletionClient _client;
    private readonly EvalStore _store;

    public EvalRunnerService(ILocalCompletionClient client, EvalStore store)
    {
        _client = client;
        _store = store;
    }

    /// <summary>Synchronous, fast: creates the run record immediately so an HTTP endpoint can hand
    /// the caller a run id right away instead of waiting for every case to execute. Call
    /// <see cref="RunCasesAsync"/> afterwards (fire-and-forget from the endpoint, directly awaited
    /// from tests) to actually execute it.</summary>
    public EvalRunDto StartRun(string datasetId) => _store.CreateRun(datasetId);

    /// <summary>Runs every case in the dataset against <paramref name="runId"/>, an already-created
    /// run record (see <see cref="StartRun"/>). Convenience overload for tests/direct callers that
    /// want the whole create-then-run sequence in one awaitable call.</summary>
    public async Task<EvalRunDto> RunAsync(string datasetId, CancellationToken cancellationToken)
    {
        EvalRunDto run = StartRun(datasetId);
        return await RunCasesAsync(run.Id, datasetId, cancellationToken);
    }

    public async Task<EvalRunDto> RunCasesAsync(string runId, string datasetId, CancellationToken cancellationToken)
    {
        EvalDatasetDto? dataset = _store.GetDataset(datasetId);
        if (dataset == null)
        {
            // Catastrophic - the run never got to attempt a single case. Distinct from a run that
            // executed cases and simply had failures/inconclusives; nothing was ever judged here.
            _store.UpdateRunStatus(runId, EvalRunStatus.Error, 0, 0, 0);
            return _store.GetRun(runId)!;
        }

        List<EvalCaseDto> cases = _store.ListCases(datasetId);

        int passCount = 0, failCount = 0, inconclusiveCount = 0, errorCount = 0;
        foreach (EvalCaseDto evalCase in cases)
        {
            if (cancellationToken.IsCancellationRequested) break;
            await RunOneCaseAsync(runId, evalCase, cancellationToken,
                onPass: () => passCount++,
                onFail: () => failCount++,
                onInconclusive: () => inconclusiveCount++,
                onError: () => errorCount++);
        }

        _store.UpdateRunStatus(runId, EvalRunStatus.Completed, passCount, failCount, inconclusiveCount, errorCount);
        return _store.GetRun(runId)!;
    }

    private async Task RunOneCaseAsync(string runId, EvalCaseDto evalCase, CancellationToken cancellationToken,
        Action onPass, Action onFail, Action onInconclusive, Action onError)
    {
        CompletionResult actual;
        try
        {
            actual = await _client.CompleteAsync(evalCase.SystemPrompt ?? DefaultSystemPrompt, evalCase.Prompt, cancellationToken);
        }
        catch (Exception ex)
        {
            // The actual-output call itself threw (a "worker crash" equivalent) - record it and
            // move on to the next case. Never let one case's provider failure kill the whole run.
            _store.AddCaseResult(runId, evalCase.Id, actualOutput: null, verdict: EvalVerdict.Error, verdictReason: ex.Message);
            onError();
            return;
        }

        if (!actual.Success)
        {
            _store.AddCaseResult(runId, evalCase.Id, actualOutput: null, verdict: EvalVerdict.Error, verdictReason: actual.Error);
            onError();
            return;
        }

        string actualOutput = actual.Content ?? string.Empty;
        string judgePrompt = $"EXPECTED BEHAVIOR:\n{evalCase.ExpectedBehavior}\n\nACTUAL RESPONSE:\n{actualOutput}";

        CompletionResult judgment;
        try
        {
            judgment = await _client.CompleteAsync(JudgeSystemPrompt, judgePrompt, cancellationToken);
        }
        catch (Exception ex)
        {
            _store.AddCaseResult(runId, evalCase.Id, actualOutput, verdict: EvalVerdict.Error, verdictReason: ex.Message);
            onError();
            return;
        }

        if (!judgment.Success)
        {
            _store.AddCaseResult(runId, evalCase.Id, actualOutput, verdict: EvalVerdict.Error, verdictReason: judgment.Error);
            onError();
            return;
        }

        string verdictText = (judgment.Content ?? string.Empty).Trim();
        bool pass = verdictText.StartsWith("PASS", StringComparison.OrdinalIgnoreCase);
        bool fail = verdictText.StartsWith("FAIL", StringComparison.OrdinalIgnoreCase);

        if (pass)
        {
            _store.AddCaseResult(runId, evalCase.Id, actualOutput, EvalVerdict.Pass, verdictText);
            onPass();
        }
        else if (fail)
        {
            _store.AddCaseResult(runId, evalCase.Id, actualOutput, EvalVerdict.Fail, verdictText);
            onFail();
        }
        else
        {
            // Directive section 120: never fabricate a PASS (or a FAIL) - the judge didn't follow
            // the protocol, so the only honest verdict is "inconclusive", a distinct third bucket.
            _store.AddCaseResult(runId, evalCase.Id, actualOutput, EvalVerdict.Inconclusive, verdictText);
            onInconclusive();
        }
    }
}
