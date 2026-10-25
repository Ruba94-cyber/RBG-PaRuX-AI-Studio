using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using PaRuX.Contracts;
using PaRuX.Server.ArtifactWorkspace;

namespace PaRuX.Server.Orchestration;

/// <summary>
/// Independent completion verifier. A model PASS is necessary but no longer always sufficient:
/// explicit deterministic FinishConditions are checked after the model verdict. Goal Mode V2 tasks
/// are stricter still: every independent pass must evaluate every acceptance criterion from the
/// exact immutable task-bound version/hash, and those evaluations are persisted append-only before
/// completion is possible. Repair is bounded and includes the real desktop coding role, whose
/// worker re-enters the same worktree. Execution Profiles control independent verification passes.
/// </summary>
public sealed class VerifierService
{
    private const string VerifierSystemPrompt =
        "You are PaRuX's independent Verifier agent. You will be given a GOAL and a RESULT " +
        "produced by another agent. Judge strictly whether the RESULT actually and substantively " +
        "addresses the GOAL. Return only the requested structured verdict. PASS means the result " +
        "substantively addresses the goal; FAIL means it does not. For coding work, inspect the " +
        "recorded diff/build/test evidence rather than trusting the producer's summary. " +
        // Live suite: three verified results edited files the goal never asked for, including the
        // fixture's own verifier program, and each was passed.
        "For coding work also check scope: FAIL when the diff changes tests, verifiers, harness or fixture " +
        "programs that check the result, unless the GOAL explicitly asks for that; FAIL when it changes a " +
        "different file or component than the one the GOAL names (for example a same-named function in another " +
        "language or module) instead of fixing the named one. Keep reason concise and factual.";

    private const string GoalVerifierSystemPrompt =
        "You are PaRuX's independent Goal Mode V2 verifier. Evaluate the recorded RESULT against " +
        "each immutable acceptance criterion independently. The criterion text is a requirement, " +
        "not evidence that it was satisfied. Use only evidence present in RESULT. For coding work, " +
        "diff/build/test evidence outranks the producer's summary. Return exactly one entry for every " +
        "criterion id supplied, preserve each id exactly, and keep evidence concise and factual.";

    private static readonly LocalStructuredOutputRequest VerifierStructuredOutput = new(
        "parux_verifier_verdict",
        "{\"type\":\"object\",\"properties\":{\"verdict\":{\"type\":\"string\",\"enum\":[\"PASS\",\"FAIL\"]},\"reason\":{\"type\":\"string\",\"maxLength\":500}},\"required\":[\"verdict\",\"reason\"],\"additionalProperties\":false}",
        Strict: true);

    private static readonly LocalStructuredOutputRequest GoalVerifierStructuredOutput = new(
        "parux_goal_v2_acceptance_verdict",
        "{\"type\":\"object\",\"properties\":{\"criteria\":{\"type\":\"array\",\"minItems\":1,\"maxItems\":64,\"items\":{\"type\":\"object\",\"properties\":{\"criterionId\":{\"type\":\"string\",\"maxLength\":64},\"satisfied\":{\"type\":\"boolean\"},\"evidence\":{\"type\":\"string\",\"minLength\":1,\"maxLength\":2000}},\"required\":[\"criterionId\",\"satisfied\",\"evidence\"],\"additionalProperties\":false}}},\"required\":[\"criteria\"],\"additionalProperties\":false}",
        Strict: true);

    private readonly TaskStore _tasks;
    private readonly ILocalCompletionClient _client;
    private readonly ArtifactStore _artifacts;
    private readonly Func<string?, ExecutionProfileStatus> _executionProfiles;
    private readonly GoalModeV2Store _goals;
    private readonly VisualContextV2Store _visualSnapshots;
    private readonly ConcurrentDictionary<string, byte> _inFlight = new();

    public VerifierService(TaskStore tasks, ILocalCompletionClient client, ArtifactStore artifacts)
        : this(tasks, client, artifacts, modelKey => ExecutionProfileStore.Current.Get(modelKey), GoalModeV2Store.Current)
    {
    }

    internal VerifierService(
        TaskStore tasks,
        ILocalCompletionClient client,
        ArtifactStore artifacts,
        Func<string?, ExecutionProfileStatus> executionProfiles,
        GoalModeV2Store? goals = null,
        VisualContextV2Store? visualSnapshots = null)
    {
        _tasks = tasks;
        _client = client;
        _artifacts = artifacts;
        _executionProfiles = executionProfiles ?? throw new ArgumentNullException(nameof(executionProfiles));
        _goals = goals ?? GoalModeV2Store.Current;
        _visualSnapshots = visualSnapshots ?? VisualContextV2Store.Current;
    }

    public List<string> RunVerifierPass(CancellationToken cancellationToken)
    {
        var started = new List<string>();
        foreach (AgentTaskDto task in _tasks.ListByStatus(AgentTaskStatus.Verifying))
        {
            if (!_inFlight.TryAdd(task.Id, 0)) continue;
            started.Add(task.Id);
            _ = ReviewAsync(task, cancellationToken)
                .ContinueWith(completedTask => _inFlight.TryRemove(task.Id, out byte _), TaskScheduler.Default);
        }
        return started;
    }

    public async Task ReviewAsync(AgentTaskDto task, CancellationToken cancellationToken)
    {
        // Defense in depth: the desktop worker already gates pending tests,
        // but server verification must reject a bypassed/stale/malformed coding
        // result BEFORE it lets an LLM verdict or finish condition mark PASS.
        CodingResultVerifierGate.Decision codingEvidence =
            CodingResultVerifierGate.Evaluate(task.Role, task.ResultJson, task.Id);
        if (!codingEvidence.Allowed)
        {
            string feedback = "Coding verification evidence rejected: " + codingEvidence.Reason;
            _tasks.AppendLog(task.Id, feedback);
            if (!TryStartRepair(task, feedback))
                _tasks.UpdateStatus(task.Id, new UpdateAgentTaskStatusRequest
                {
                    Status = AgentTaskStatus.Failed,
                    Stage = "Coding verification evidence rejected",
                    Error = feedback,
                    VerificationStatus = "fail"
                });
            return;
        }

        UiVerificationEvidenceGate.Decision uiEvidence =
            UiVerificationEvidenceGate.Evaluate(task.Role, task.ResultJson);
        if (!uiEvidence.Allowed)
        {
            string feedback = "UI verification evidence rejected: " + uiEvidence.Reason;
            _tasks.AppendLog(task.Id, feedback);
            if (!TryStartRepair(task, feedback))
                _tasks.UpdateStatus(task.Id, new UpdateAgentTaskStatusRequest
                {
                    Status = AgentTaskStatus.Failed,
                    Stage = "UI verification evidence rejected",
                    Error = feedback,
                    VerificationStatus = "fail"
                });
            return;
        }

        if (string.Equals(task.Role, "ui-verification", StringComparison.OrdinalIgnoreCase))
        {
            UiVerificationEvidenceGate.Decision retained;
            try
            {
                string snapshotId = UiVerificationEvidenceGate.ReadSnapshotId(task.ResultJson!);
                retained = UiVerificationEvidenceGate.ValidatePersistedSnapshot(
                    task.Id, snapshotId, _visualSnapshots.GetSnapshot(snapshotId));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Do not expose database paths, screenshot content or exception
                // details in task messages. A missing/damaged store means the
                // requested visual evidence was not independently available.
                retained = new UiVerificationEvidenceGate.Decision(false,
                    "UI snapshot could not be retrieved from the authoritative visual evidence store.");
            }

            if (!retained.Allowed)
            {
                string feedback = "UI verification snapshot rejected: " + retained.Reason;
                _tasks.AppendLog(task.Id, feedback);
                if (!TryStartRepair(task, feedback))
                    _tasks.UpdateStatus(task.Id, new UpdateAgentTaskStatusRequest
                    {
                        Status = AgentTaskStatus.Failed,
                        Stage = "UI verification snapshot rejected",
                        Error = feedback,
                        VerificationStatus = "fail"
                    });
                return;
            }
        }

        string resultText = string.IsNullOrWhiteSpace(task.ResultJson) ? "(no result recorded)" : task.ResultJson;
        string prompt = $"GOAL:\n{task.Goal}\n\nRESULT:\n{resultText}";

        // Evidence gate (coding tasks): a model reading a diff cannot show that code builds or that
        // tests pass. Only executed checks with exit codes can; a failing one is a failure no matter
        // what the model thinks. Having none, or exit-0 checks without the worker's accepted-command
        // claim, caps the outcome at "unverified".
        CodingCheckGate? gate = CodingExecutedChecks.AppliesTo(task.Role)
            ? CodingExecutedChecks.EvaluateOutcome(task.ResultJson) : null;
        if (gate != null)
            _tasks.AppendLog(task.Id, "Executed-check gate: " + gate.Verdict + " - " + gate.Reason + ".");
        if (gate is { Verdict: CodingCheckVerdict.Fail })
        {
            string checkFailure = "An executed check failed: " + gate.Reason + ". A model's opinion cannot override a failing command.";
            if (!TryStartRepair(task, checkFailure))
            {
                _tasks.UpdateStatus(task.Id, new UpdateAgentTaskStatusRequest
                {
                    Status = AgentTaskStatus.Failed,
                    Stage = "Verification failed: executed check exited non-zero",
                    Error = checkFailure,
                    VerificationStatus = "fail",
                    ResultJson = CodingExecutedChecks.WithAdvisory(task.ResultJson, Array.Empty<string>(), gate)
                });
            }
            return;
        }

        GoalSpecificationVersionDto? goalSpecification;
        try
        {
            goalSpecification = _goals.GetBoundSpecification(task.Id);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            FailVerificationIntegrity(task.Id, "Goal Mode V2 binding/specification integrity failed before verification: " + ex.Message);
            return;
        }

        string? goalPrompt = null;
        if (goalSpecification != null)
        {
            goalPrompt = BuildGoalVerificationPrompt(goalSpecification, resultText);
            _tasks.AppendLog(task.Id,
                $"Goal Mode V2 verifier bound to {goalSpecification.GoalId} v{goalSpecification.Version} sha256={goalSpecification.ContentSha256}; " +
                $"{goalSpecification.AcceptanceCriteria.Count} immutable acceptance criterion/criteria must be evaluated on every pass.");
        }

        int requiredPasses;
        string verificationProfile;
        try
        {
            string? modelKey = TryExtractExecutionModelKey(task.ResultJson);
            ExecutionProfileStatus status = _executionProfiles(modelKey);
            requiredPasses = Math.Clamp(status.Effective.Budget.VerificationPasses, 1, 5);
            verificationProfile = status.ActiveQualityProfile;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            _tasks.AppendLog(task.Id, "Verifier could not validate Execution Profile: " + ex.Message);
            _tasks.UpdateStatus(task.Id, new UpdateAgentTaskStatusRequest
            {
                Status = AgentTaskStatus.Failed,
                Stage = "Verification policy invalid",
                Error = "Execution Profile could not be validated before verification: " + ex.Message,
                VerificationStatus = "error"
            });
            return;
        }

        _tasks.AppendLog(task.Id,
            $"Execution Profile '{verificationProfile}' requires {requiredPasses} independent verification pass(es); all must PASS.");

        var passedVerdicts = new List<string>();
        for (int passIndex = 1; passIndex <= requiredPasses; passIndex++)
        {
            if (goalSpecification != null)
            {
                CompletionResult goalResult;
                try
                {
                    goalResult = await _client.CompleteStructuredAsync(
                        GoalVerifierSystemPrompt,
                        goalPrompt!,
                        GoalVerifierStructuredOutput,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex)
                {
                    FailVerificationRun(task.Id, passIndex, requiredPasses, "Goal verifier crashed", ex.Message);
                    return;
                }

                if (!goalResult.Success)
                {
                    FailVerificationRun(task.Id, passIndex, requiredPasses, "Goal verification failed to run", goalResult.Error ?? "Unknown verifier error");
                    return;
                }

                string raw = (goalResult.Content ?? string.Empty).Trim();
                if (!TryParseGoalCriteriaVerdict(raw, goalSpecification, out List<GoalAcceptanceCriterionVerificationDto> criteria, out string parseError))
                {
                    _tasks.AppendLog(task.Id,
                        $"Goal Mode V2 verifier pass {passIndex}/{requiredPasses} was structurally inconclusive: {parseError}");
                    _tasks.UpdateStatus(task.Id, new UpdateAgentTaskStatusRequest
                    {
                        Status = AgentTaskStatus.Blocked,
                        Stage = "Goal acceptance verification inconclusive",
                        Error = parseError,
                        VerificationStatus = "inconclusive"
                    });
                    return;
                }

                GoalVerificationRecordDto record;
                try
                {
                    record = _goals.RecordVerification(task.Id, passIndex, criteria);
                }
                catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException)
                {
                    FailVerificationIntegrity(task.Id, "Goal Mode V2 verifier evidence could not be bound durably: " + ex.Message);
                    return;
                }

                List<GoalAcceptanceCriterionVerificationDto> failedRequired = record.Criteria
                    .Where(item => !item.Satisfied && goalSpecification.AcceptanceCriteria.Any(
                        criterion => criterion.Required && string.Equals(criterion.Id, item.CriterionId, StringComparison.Ordinal)))
                    .ToList();
                string compact = RenderGoalCriteria(record.Criteria);
                _tasks.AppendLog(task.Id,
                    $"Goal Mode V2 verifier pass {passIndex}/{requiredPasses} recorded as {record.Id}: " + compact);

                if (!record.AllRequiredSatisfied)
                {
                    string failureDetails = string.Join("; ", failedRequired.Select(item =>
                        item.CriterionId + ": " + Bound(item.Evidence, 500)));
                    string feedback = $"Goal Mode V2 acceptance verification pass {passIndex}/{requiredPasses} failed required criterion/criteria: {failureDetails}";
                    bool repairStarted = TryStartRepair(task, feedback);
                    if (!repairStarted)
                    {
                        _tasks.UpdateStatus(task.Id, new UpdateAgentTaskStatusRequest
                        {
                            Status = AgentTaskStatus.Failed,
                            Stage = "Goal acceptance criteria not satisfied",
                            Error = feedback,
                            VerificationStatus = "fail"
                        });
                    }
                    return;
                }

                passedVerdicts.Add("Goal criteria PASS - " + compact);
                continue;
            }

            CompletionResult result;
            try
            {
                result = await _client.CompleteStructuredAsync(
                    VerifierSystemPrompt,
                    prompt,
                    VerifierStructuredOutput,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                _tasks.AppendLog(task.Id, $"Verifier pass {passIndex}/{requiredPasses} crashed: " + ex.Message);
                _tasks.UpdateStatus(task.Id, new UpdateAgentTaskStatusRequest
                {
                    Status = AgentTaskStatus.Failed,
                    Stage = "Verification crashed",
                    Error = ex.Message,
                    VerificationStatus = "error"
                });
                return;
            }

            if (!result.Success)
            {
                _tasks.AppendLog(task.Id, $"Verifier pass {passIndex}/{requiredPasses} could not run: " + result.Error);
                _tasks.UpdateStatus(task.Id, new UpdateAgentTaskStatusRequest
                {
                    Status = AgentTaskStatus.Failed,
                    Stage = "Verification failed to run",
                    Error = result.Error,
                    VerificationStatus = "error"
                });
                return;
            }

            string rawVerdict = (result.Content ?? string.Empty).Trim();
            bool pass;
            bool fail;
            string verdict;
            bool parsed = result.StructuredOutputApplied
                ? TryParseStructuredVerdict(rawVerdict, out pass, out fail, out verdict)
                : TryParseLegacyVerdict(rawVerdict, out pass, out fail, out verdict);

            _tasks.AppendLog(task.Id,
                $"Verifier pass {passIndex}/{requiredPasses}" +
                (result.StructuredOutputApplied ? " (schema-constrained): " : " (parser fallback): ")
                + (parsed ? verdict : rawVerdict));

            if (!parsed)
            {
                _tasks.AppendLog(task.Id,
                    $"Verifier pass {passIndex}/{requiredPasses} did not parse as PASS/FAIL - marking blocked for manual review.");
                _tasks.UpdateStatus(task.Id, new UpdateAgentTaskStatusRequest
                {
                    Status = AgentTaskStatus.Blocked,
                    Stage = "Verifier response unparseable",
                    VerificationStatus = "inconclusive"
                });
                return;
            }

            if (fail)
            {
                string feedback = requiredPasses == 1
                    ? verdict
                    : $"Verification pass {passIndex}/{requiredPasses} failed: {verdict}";
                bool repairStarted = TryStartRepair(task, feedback);
                if (!repairStarted)
                {
                    _tasks.UpdateStatus(task.Id, new UpdateAgentTaskStatusRequest
                    {
                        Status = AgentTaskStatus.Failed,
                        Stage = "Verification failed",
                        Error = feedback,
                        VerificationStatus = "fail"
                    });
                }
                return;
            }

            if (pass) passedVerdicts.Add(verdict);
        }

        if (passedVerdicts.Count != requiredPasses)
        {
            _tasks.UpdateStatus(task.Id, new UpdateAgentTaskStatusRequest
            {
                Status = AgentTaskStatus.Blocked,
                Stage = "Verification incomplete",
                VerificationStatus = "inconclusive"
            });
            return;
        }

        AgentFinishConditionEvaluator.Evaluation finish = AgentFinishConditionEvaluator.Evaluate(
            task.FinishCondition, task.ResultJson);
        _tasks.AppendLog(task.Id, "Finish condition: " + finish.Reason);
        if (!finish.Satisfied)
        {
            string conditionFailure = "Independent verification passed, but the machine-checkable finish condition was not satisfied: " + finish.Reason;
            bool repairStarted = TryStartRepair(task, conditionFailure);
            if (!repairStarted)
            {
                _tasks.UpdateStatus(task.Id, new UpdateAgentTaskStatusRequest
                {
                    Status = AgentTaskStatus.Failed,
                    Stage = "Finish condition not satisfied",
                    Error = conditionFailure,
                    VerificationStatus = gate is { Verdict: CodingCheckVerdict.Unverified } ? "unverified" : "pass",
                    FinishConditionSatisfied = false
                });
            }
            return;
        }

        string verifiedStage = goalSpecification != null
            ? $"Goal Mode V2 verified ({requiredPasses} pass{(requiredPasses == 1 ? string.Empty : "es")}, {goalSpecification.AcceptanceCriteria.Count} criteria)"
            : requiredPasses == 1 ? "Verified" : $"Verified ({requiredPasses} passes)";
        if (task.FinishCondition != null) verifiedStage += " + finish condition satisfied";

        bool unverified = gate is { Verdict: CodingCheckVerdict.Unverified };
        // Exit-0 checks without an accepted command are unverified too, but the stage says why.
        string unverifiedStage = gate is { Checks.Count: > 0 }
            ? CodingExecutedChecks.UnverifiedWithoutAcceptedCommandStage
            : CodingExecutedChecks.UnverifiedStage;
        if (gate is { Verdict: CodingCheckVerdict.Pass })
            verifiedStage += " by " + gate.Checks.Count + " executed check(s)";
        _tasks.UpdateStatus(task.Id, new UpdateAgentTaskStatusRequest
        {
            Status = AgentTaskStatus.Completed,
            Stage = unverified ? unverifiedStage : verifiedStage,
            ProgressPercent = 100,
            VerificationStatus = unverified ? "unverified" : "pass",
            ResultJson = gate == null ? null : CodingExecutedChecks.WithAdvisory(task.ResultJson, passedVerdicts, gate),
            FinishConditionSatisfied = task.FinishCondition == null ? null : true
        });
        RecordArtifact(task);
        RecordParentFinding(task);
    }

    private bool TryStartRepair(AgentTaskDto task, string feedback)
    {
        bool repairStarted = task.MaxRepairAttempts > task.RepairAttemptsUsed
            && IsRepairCapableRole(task.Role)
            && _tasks.TryBeginRepair(task.Id, feedback);
        if (repairStarted)
        {
            _tasks.AppendLog(task.Id,
                $"Starting bounded repair attempt {task.RepairAttemptsUsed + 1}/{task.MaxRepairAttempts}: {feedback}");
        }
        return repairStarted;
    }

    private static bool IsRepairCapableRole(string? role) =>
        AgentWorkerService.CanExecute(role)
        || string.Equals(role, "coding", StringComparison.Ordinal);

    private void FailVerificationIntegrity(string taskId, string error)
    {
        _tasks.AppendLog(taskId, error);
        _tasks.UpdateStatus(taskId, new UpdateAgentTaskStatusRequest
        {
            Status = AgentTaskStatus.Failed,
            Stage = "Verification integrity failure",
            Error = error,
            VerificationStatus = "error"
        });
    }

    private void FailVerificationRun(string taskId, int passIndex, int requiredPasses, string stage, string error)
    {
        _tasks.AppendLog(taskId, $"Verifier pass {passIndex}/{requiredPasses} could not complete: {error}");
        _tasks.UpdateStatus(taskId, new UpdateAgentTaskStatusRequest
        {
            Status = AgentTaskStatus.Failed,
            Stage = stage,
            Error = error,
            VerificationStatus = "error"
        });
    }

    private static string BuildGoalVerificationPrompt(GoalSpecificationVersionDto specification, string resultText)
    {
        var prompt = new StringBuilder();
        prompt.AppendLine("IMMUTABLE GOAL SPECIFICATION (integrity already verified by Core):");
        prompt.Append("goalId: ").AppendLine(specification.GoalId);
        prompt.Append("version: ").AppendLine(specification.Version.ToString(System.Globalization.CultureInfo.InvariantCulture));
        prompt.Append("sha256: ").AppendLine(specification.ContentSha256);
        prompt.AppendLine("GOAL:");
        prompt.AppendLine(specification.Goal);
        prompt.AppendLine("ACCEPTANCE CRITERIA:");
        foreach (GoalAcceptanceCriterionDto criterion in specification.AcceptanceCriteria.OrderBy(x => x.Order))
        {
            prompt.Append(criterion.Id).Append(criterion.Required ? " [REQUIRED]: " : " [OPTIONAL]: ")
                .AppendLine(criterion.Text);
        }
        prompt.AppendLine();
        prompt.AppendLine("RESULT/EVIDENCE TO VERIFY:");
        prompt.AppendLine(resultText);
        prompt.AppendLine();
        prompt.AppendLine("For every criterion above, return criterionId exactly, satisfied=true/false, and concise evidence from RESULT. Missing evidence means satisfied=false.");
        return prompt.ToString();
    }

    internal static bool TryParseGoalCriteriaVerdict(
        string value,
        GoalSpecificationVersionDto specification,
        out List<GoalAcceptanceCriterionVerificationDto> criteria,
        out string error)
    {
        criteria = new List<GoalAcceptanceCriterionVerificationDto>();
        error = string.Empty;
        if (specification == null)
        {
            error = "Goal specification is missing.";
            return false;
        }

        Dictionary<string, GoalAcceptanceCriterionDto> expected = specification.AcceptanceCriteria
            .ToDictionary(x => x.Id, StringComparer.Ordinal);
        try
        {
            using JsonDocument doc = JsonDocument.Parse(value);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("criteria", out JsonElement criteriaNode)
                || criteriaNode.ValueKind != JsonValueKind.Array)
            {
                error = "Verifier response must contain a criteria array.";
                return false;
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonElement item in criteriaNode.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object
                    || !item.TryGetProperty("criterionId", out JsonElement idNode)
                    || idNode.ValueKind != JsonValueKind.String
                    || !item.TryGetProperty("satisfied", out JsonElement satisfiedNode)
                    || satisfiedNode.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
                    || !item.TryGetProperty("evidence", out JsonElement evidenceNode)
                    || evidenceNode.ValueKind != JsonValueKind.String)
                {
                    error = "Every criterion verdict must contain criterionId, boolean satisfied and evidence.";
                    return false;
                }

                string criterionId = (idNode.GetString() ?? string.Empty).Trim();
                string evidence = (evidenceNode.GetString() ?? string.Empty).Trim();
                if (!expected.ContainsKey(criterionId))
                {
                    error = "Verifier returned unknown criterion id: " + criterionId;
                    return false;
                }
                if (!seen.Add(criterionId))
                {
                    error = "Verifier returned duplicate criterion id: " + criterionId;
                    return false;
                }
                if (evidence.Length is < 1 or > 2_000)
                {
                    error = "Verifier evidence for " + criterionId + " must contain 1-2,000 characters.";
                    return false;
                }
                criteria.Add(new GoalAcceptanceCriterionVerificationDto
                {
                    CriterionId = criterionId,
                    Satisfied = satisfiedNode.GetBoolean(),
                    Evidence = evidence
                });
            }

            if (criteria.Count != expected.Count)
            {
                List<string> missing = expected.Keys.Where(id => !seen.Contains(id)).OrderBy(id => id).ToList();
                error = "Verifier did not cover every immutable acceptance criterion. Missing: " +
                    (missing.Count == 0 ? "unknown" : string.Join(", ", missing));
                return false;
            }
            criteria = criteria.OrderBy(item => expected[item.CriterionId].Order).ToList();
            return true;
        }
        catch (JsonException ex)
        {
            error = "Verifier response was not valid JSON: " + ex.Message;
            return false;
        }
    }

    private static string RenderGoalCriteria(IEnumerable<GoalAcceptanceCriterionVerificationDto> criteria) =>
        string.Join(", ", criteria.Select(item => item.CriterionId + "=" + (item.Satisfied ? "PASS" : "FAIL")));

    private static string Bound(string? value, int maximum)
    {
        string text = (value ?? string.Empty).Trim();
        if (maximum <= 0) return string.Empty;
        return text.Length <= maximum ? text : text[..maximum] + "…";
    }

    internal static string? TryExtractExecutionModelKey(string? resultJson)
    {
        if (string.IsNullOrWhiteSpace(resultJson)) return null;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(resultJson);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("executionModelKey", out JsonElement node)
                && node.ValueKind == JsonValueKind.String)
            {
                string value = node.GetString()?.Trim() ?? string.Empty;
                return value.Length == 0 ? null : value;
            }
        }
        catch (JsonException) { }
        return null;
    }

    internal static bool TryParseStructuredVerdict(
        string value,
        out bool pass,
        out bool fail,
        out string rendered)
    {
        pass = false;
        fail = false;
        rendered = value;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(value);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("verdict", out JsonElement verdictNode)
                || verdictNode.ValueKind != JsonValueKind.String)
                return false;

            string verdict = verdictNode.GetString()?.Trim() ?? string.Empty;
            pass = string.Equals(verdict, "PASS", StringComparison.OrdinalIgnoreCase);
            fail = string.Equals(verdict, "FAIL", StringComparison.OrdinalIgnoreCase);
            if (!pass && !fail) return false;

            string reason = root.TryGetProperty("reason", out JsonElement reasonNode)
                && reasonNode.ValueKind == JsonValueKind.String
                ? (reasonNode.GetString() ?? string.Empty).Trim()
                : string.Empty;
            if (reason.Length > 500) reason = reason[..500];
            rendered = (pass ? "PASS" : "FAIL") + (reason.Length == 0 ? string.Empty : " - " + reason);
            return true;
        }
        catch (JsonException) { return false; }
    }

    internal static bool TryParseLegacyVerdict(
        string value,
        out bool pass,
        out bool fail,
        out string rendered)
    {
        rendered = (value ?? string.Empty).Trim();
        pass = rendered.StartsWith("PASS", StringComparison.OrdinalIgnoreCase);
        fail = rendered.StartsWith("FAIL", StringComparison.OrdinalIgnoreCase);
        return pass || fail;
    }

    private void RecordArtifact(AgentTaskDto task)
    {
        try
        {
            string content = ExtractResultText(task.ResultJson) ?? "(no result recorded)";
            string title = task.Goal.Length > 120 ? task.Goal[..120] + "..." : task.Goal;
            ArtifactDto artifact = _artifacts.Create(new CreateArtifactRequest
            {
                ArtifactType = "agent-task-result",
                Title = title,
                ContentRef = content,
                CreatedByTaskId = task.Id,
                Source = "orchestrator:" + (task.Role ?? "unknown")
            });
            _artifacts.SetStatus(artifact.Id, ArtifactStore.StatusFinal);
            // Artifact archival is not the same as verification: coding results can complete with
            // an unverified status when the independent model verdict passes but no accepted check ran.
            _tasks.AppendLog(task.Id, "Recorded task result as artifact " + artifact.Id);
        }
        catch (Exception ex)
        {
            _tasks.AppendLog(task.Id, "Artifact recording failed (task remains completed): " + ex.Message);
        }
    }

    private void RecordParentFinding(AgentTaskDto task)
    {
        if (string.IsNullOrWhiteSpace(task.ParentId)) return;
        try
        {
            string finding = ExtractResultText(task.ResultJson) ?? "(no textual finding)";
            finding = finding.Trim();
            if (finding.Length > 1_500) finding = finding[..1_500] + "…";
            _tasks.AppendLog(task.ParentId,
                $"Verified child finding [{task.Id}, {task.Role ?? "unassigned"}]: {finding}");
        }
        catch
        {
            // Parent handoff is additive; a verified child must not be invalidated if the parent
            // was concurrently deleted/imported or its log became temporarily unavailable.
        }
    }

    private static string? ExtractResultText(string? resultJson)
    {
        if (string.IsNullOrWhiteSpace(resultJson)) return null;
        try
        {
            using JsonDocument document = JsonDocument.Parse(resultJson);
            JsonElement root = document.RootElement;
            if (root.TryGetProperty("response", out JsonElement response) && response.ValueKind == JsonValueKind.String)
                return response.GetString();

            // Coding results are structured evidence. Store a compact readable artifact while the
            // full JSON/diff remains in TaskStore and the worktree remains reviewable.
            if (root.TryGetProperty("summary", out JsonElement summary) && summary.ValueKind == JsonValueKind.String)
            {
                var text = new StringBuilder(summary.GetString() ?? string.Empty);
                if (root.TryGetProperty("diff", out JsonElement diff) && diff.ValueKind == JsonValueKind.String)
                    text.Append("\n\nDiff evidence:\n").Append(diff.GetString());
                return text.ToString();
            }
        }
        catch (JsonException) { }
        return resultJson;
    }
}
