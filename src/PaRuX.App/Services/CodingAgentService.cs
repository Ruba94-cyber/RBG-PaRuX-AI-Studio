using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using PaRuX.Contracts;
using PaRuX.Models;

namespace PaRuX.Services
{
    public sealed class CodingAgentResult
    {
        public bool GoalReported { get; set; }
        public bool InfrastructureBlocked { get; set; }
        /// <summary>Specific reason the attempt stopped (see <see cref="CodingAgentStopReason"/>); empty when not applicable.</summary>
        public string StopKind { get; set; } = CodingAgentStopReason.None;
        /// <summary>
        /// A paused attempt still needs deterministic verification or a trusted completion
        /// authority. It must not be handed to the verifier as a candidate result.
        /// </summary>
        public bool VerificationPending { get; set; }
        /// <summary>The goal was reported after an accepted verification command succeeded in this run.</summary>
        public bool VerifiedByCommand { get; set; }
        public string Summary { get; set; } = string.Empty;
        public int StepsTaken { get; set; }
        public bool Cancelled { get; set; }
        public bool WaitingForDelegatedChild { get; set; }
        public string? DelegatedChildTaskId { get; set; }
        public List<string> DelegatedChildTaskIds { get; set; } = new();
        /// <summary>Accepted verification commands actually executed since the last testable change.</summary>
        public List<CodingExecutedCheckRecord> ExecutedChecks { get; set; } = new();
        public bool WaitingForUserInput { get; set; }
        public string? UserQuestionId { get; set; }
    }

    internal sealed record CodingExecutionOutcome(
        string Description,
        string Observation,
        bool MutatedTestableCode = false,
        bool VerificationAttempted = false,
        bool VerificationSucceeded = false,
        bool MadeMaterialProgress = false,
        bool InfrastructureBlocked = false,
        bool BrowserActionSucceeded = false,
        string? DelegatedChildTaskId = null,
        IReadOnlyList<string>? DelegatedChildTaskIds = null,
        string? UserQuestionId = null,
        IReadOnlyList<string>? ContinuedChildTaskIds = null,
        int? CommandExitCode = null,
        bool WorkspaceMutated = false);

    /// <summary>
    /// Local coding agent with durable execution checkpoints. The checkpoint stores real tool
    /// evidence and verification state, never hidden model reasoning. Ordinary shell commands are
    /// fail-closed through a no-network Windows AppContainer. Package restore is a distinct action:
    /// the model can select only a discovered ecosystem/repository alias, while PaRuX Core task
    /// capabilities plus the global Internet switch decide whether the deterministic restore may
    /// receive a temporary InternetClient capability. Browser actions use PaRuX's dedicated private
    /// CDP profile. Large tool evidence is persisted in CodingAgentArtifactStore instead of being
    /// silently destroyed by prompt truncation. Durable orchestrator coding tasks may self-delegate
    /// bounded children or pause for explicit user input through typed Core bridges; the model never
    /// receives Core credentials or workstation authority paths.
    /// </summary>
    public static class CodingAgentService
    {
        public const int MaxSteps = 10_000;
        private const int MaxObservationChars = 4_000;
        private const int MaxLogEntriesInPrompt = 12;
        /// <summary>Floor for replayed execution evidence - what the budget was before it
        /// started scaling with the context window. No configuration gets less recall than this.</summary>
        private const int MinimumRecentEvidenceCharsInPrompt = 2_500;
        /// <summary>
        /// Mirrors the reasoning budget RuntimeLaunchOptimizer.RecommendReasoning gives a coding
        /// launch. It lives here because the prompt budget has to withhold that many tokens for
        /// hidden reasoning, and the App cannot read the server's launch profile mid-task.
        /// Measured consequence of ignoring it: empty actions at n_ctx 8,192.
        /// </summary>
        private const int CodingAgentReasoningBudget = 4_096;

        private const int MaxProtocolFailures = CodingAgentProtocolRetryPolicy.MaximumProtocolFailures;
        private const int ProgressWarningSteps = 30;
        private const int ProgressStopSteps = 60;
        private const long MaxWholeFileReadBytes = 4L * 1024 * 1024;
        private const int MaxDirectoryEntries = 2_000;
        private static readonly CodingTaskRunLease ActiveRunLease = new();

        static CodingAgentService()
        {
            CodingAgentFeatureFlags.BatchReadSetting = () => SettingsStore.Current.Data.CodingBatchReadEnabled;
            CodingAgentFeatureFlags.ParentContinuationSetting = () => SettingsStore.Current.Data.CodingParentContinuationEnabled;
        }

        /// <summary>
        /// The action protocol, as a JSON Schema the local runtime compiles into a sampling grammar.
        ///
        /// It deliberately carries no maxLength bounds. llama.cpp turns a string length bound into
        /// repetition rules, and the bundled build rejects the result outright for some values -
        /// "question" at maxLength 2000 reproducibly returned HTTP 400 "failed to initialize
        /// samplers: failed to parse grammar", while 240 and 4096 compiled, so the bound could not
        /// even be lowered into a safe range with any confidence. With the bound present no coding
        /// step could ever run: every request failed before a token was sampled.
        ///
        /// Nothing is lost by omitting them. TryValidateAction enforces every one of these limits on
        /// the response itself, and it - not the sampler - is what the rest of the agent trusts.
        /// </summary>
        private const string ActionSchema = CodingAgentPromptBuilder.ActionSchema;

        /// <summary>
        /// The schema this run advertises. Disabled capabilities are removed from the action enum so
        /// the sampler cannot produce them (the validator rejects them as well).
        /// </summary>
        internal static string ActionSchemaFor(bool batchRead, bool parentContinuation, bool mcpToolsAvailable = false) =>
            CodingAgentPromptBuilder.ActionSchemaFor(batchRead, parentContinuation, mcpToolsAvailable);

        public static async Task<CodingAgentResult> RunAsync(
            string workspaceRoot,
            string task,
            Action<string> onStep,
            CancellationToken ct,
            string? sessionId = null,
            int repairAttempt = 0,
            string? permissionMode = null)
        {
            if (GamingManager.Current.IsActive)
                throw new InvalidOperationException("Gaming Mode is active. Coding agents are suspended.");
            if (string.IsNullOrWhiteSpace(workspaceRoot) || !Directory.Exists(workspaceRoot))
                throw new InvalidOperationException("The workspace folder no longer exists.");
            if (string.IsNullOrWhiteSpace(task))
                throw new InvalidOperationException("A coding task is required.");

            string root = Path.GetFullPath(workspaceRoot);
            string resolvedSessionId = CodingAgentCheckpointStore.ResolveSessionId(root, task, sessionId);
            using IDisposable executionLease = ActiveRunLease.Enter(resolvedSessionId);
            string? orchestratorTaskId = string.IsNullOrWhiteSpace(sessionId) ? null : resolvedSessionId;
            string requestedPermissionMode = CodingAgentPermissionMode.Normalize(permissionMode);
            // One ledger scope per coding task, so durable memory never bleeds between
            // unrelated tasks and a resumed run picks its own history back up.
            string memoryScopeId = orchestratorTaskId ?? resolvedSessionId;
            CodingAgentCheckpoint? restored = CodingAgentCheckpointStore.Load(resolvedSessionId, root, task);
            CodingAgentCheckpoint checkpoint = restored ?? new CodingAgentCheckpoint
            {
                SessionId = resolvedSessionId,
                WorkspaceRoot = root,
                Task = task.Trim(),
                PermissionMode = requestedPermissionMode,
                VerificationSatisfied = false
            };
            string effectivePermissionMode = restored == null
                ? requestedPermissionMode
                : CodingAgentPermissionMode.Normalize(restored.PermissionMode);
            checkpoint.PermissionMode = effectivePermissionMode;

            // An OS/process crash may land AFTER a side-effecting tool returned but
            // BEFORE its observation reached disk. Never blindly replay that tool.
            // Persist uncertainty as execution evidence before starting a new inference.
            string uncertainDispatch = CodingAgentDispatchJournal.ReconcileInterruptedAction(checkpoint);
            if (uncertainDispatch.Length > 0)
            {
                checkpoint.Observations.Add(uncertainDispatch);
                CodingAgentCheckpointStore.Save(checkpoint);
                onStep?.Invoke("Recovered an interrupted tool dispatch; workspace state and verification require re-inspection.");
            }

            if (CodingAgentCheckpointStore.BeginRepairAttempt(checkpoint, repairAttempt))
            {
                checkpoint.Observations.Add("A new verifier repair attempt began. The previous no-progress count was reset; inspect the preserved diff and evidence before choosing a different action.");
                CodingAgentCheckpointStore.Save(checkpoint);
                onStep?.Invoke("Verifier repair attempt " + repairAttempt + ": reset the progress watchdog while preserving the coding checkpoint.");
            }

            var grantedCapabilities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool coreVerifierTaskAvailable = false;
            if (orchestratorTaskId != null)
            {
                try
                {
                    AgentTaskDto authority = await AgentOrchestratorClient.Current.GetAsync(orchestratorTaskId, ct).ConfigureAwait(false);
                    AgentWorkspaceAssignmentDto? assignment = await AgentOrchestratorClient.Current
                        .TryGetWorkspaceAssignmentAsync(orchestratorTaskId, ct).ConfigureAwait(false);
                    AgentDesktopWorktreeDto? desktopWorktree = await AgentOrchestratorClient.Current
                        .TryGetDesktopWorktreeAsync(orchestratorTaskId, ct).ConfigureAwait(false);
                    coreVerifierTaskAvailable = CodingAgentCompletionAuthority.IsBoundCoreCodingTask(
                        authority, assignment, desktopWorktree, orchestratorTaskId, root);
                    if (coreVerifierTaskAvailable)
                        grantedCapabilities.UnionWith(authority.GrantedCapabilities);
                    else
                        onStep?.Invoke("Core coding-task identity and workspace assignment could not be confirmed; no verifier handoff authority was granted.");
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception)
                {
                    onStep?.Invoke("Core coding-task identity and workspace assignment could not be confirmed; no verifier handoff authority was granted.");
                }
            }
            bool canSafeRestore = grantedCapabilities.Contains(CodingPackageRestoreDiscovery.SafeRestoreCapability);
            bool canScriptedRestore = grantedCapabilities.Contains(CodingPackageRestoreDiscovery.ScriptedRestoreCapability);

            IReadOnlyList<CodingAgentMcpToolDefinition> mcpTools = Array.Empty<CodingAgentMcpToolDefinition>();
            if (orchestratorTaskId != null
                && grantedCapabilities.Contains(CodingAgentMcpToolPolicy.RequiredCapability))
            {
                try
                {
                    IReadOnlyList<CodingAgentMcpToolDefinition> discovered = await CodingAgentMcpToolClient.Current
                        .ListToolsAsync(orchestratorTaskId, ct).ConfigureAwait(false);
                    mcpTools = CodingAgentMcpToolPolicy.FilterAvailable(discovered);
                    if (mcpTools.Count > 0)
                        onStep?.Invoke("Core exposed " + mcpTools.Count + " explicitly granted external MCP tool(s) to this coding task.");
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch
                {
                    // External tool discovery is optional; failure never widens another authority path.
                    onStep?.Invoke("Core MCP tool discovery failed; this coding run has no external MCP tools.");
                }
            }

            bool batchReadEnabled = CodingAgentFeatureFlags.BatchReadEnabled;
            bool continuationEnabled = CodingAgentFeatureFlags.ParentContinuationEnabled && orchestratorTaskId != null;
            string actionSchema = ActionSchemaFor(batchReadEnabled, continuationEnabled, mcpTools.Count > 0);
            onStep?.Invoke(CodingAgentFeatureFlags.Describe());
            int childHoldRejections = 0;

            var log = new List<string>(checkpoint.Observations);
            if (orchestratorTaskId != null)
            {
                try
                {
                    AgentUserQuestionStateDto userInput = await AgentOrchestratorClient.Current
                        .GetUserQuestionStateAsync(orchestratorTaskId, ct).ConfigureAwait(false);
                    List<CodingAgentCheckpointStore.UserAnswerExchange> exchanges = userInput.RecentExchanges
                        .TakeLast(6)
                        .Select(exchange => new CodingAgentCheckpointStore.UserAnswerExchange(
                            exchange.QuestionId, exchange.Question, exchange.Answer))
                        .ToList();
                    foreach (CodingAgentCheckpointStore.UserAnswerExchange exchange in exchanges)
                    {
                        CodingAgentCommandApproval.ConsumeAnswer(
                            checkpoint, exchange.QuestionId, exchange.Answer);
                        CodingAgentFileApproval.ConsumeAnswer(
                            checkpoint, exchange.QuestionId, exchange.Answer);
                    }
                    // Consumption and the watchdog reset are decided in CodingAgentCheckpointStore.ConsumeUserAnswers,
                    // which the unit tests cover. This call only supplies the recent exchanges.
                    CodingAgentCheckpointStore.ConsumeUserAnswers(
                        checkpoint, exchanges, log);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    onStep?.Invoke("Could not read optional durable user-input history from Core: " + Truncate(ex.Message, 300));
                }
            }

            int protocolFailures = 0;
            bool compactNextProtocolRetry = false;
            bool structuredFallbackReported = false;
            bool contextBudgetReported = false;
            bool contextTooSmallReported = false;
            bool contextRaiseAttempted = false;
            bool exactTokenProbeReported = false;
            int consecutiveNarrationResponses = 0;
            bool verificationRequired = checkpoint.VerificationRequired;
            bool verificationSatisfied = checkpoint.VerificationSatisfied;
            int consecutiveNonProgress = Math.Max(0, checkpoint.ConsecutiveNonProgressSteps);
            var repeatGuard = new CodingAgentRepeatGuard();
            int suppressedRepeats = 0;
            // Rejected completions since the last testable mutation or verification attempt. A
            // successful non-verification command counts as progress, so without this bound a
            // model that repeats it and keeps reporting done never pauses.
            int unverifiedCompletionRejections = 0;
            // Identical successful commands with no change in between cannot produce new evidence.
            var successfulCommandsSinceChange = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int identicalSuccessRepeats = 0;
            // An accepted, deterministic verification command succeeded after the last testable
            // mutation in this run. Core counts only such completions as model-quality evidence.
            bool verifiedByCommand = checkpoint.VerifiedByCommand;

            if (restored != null && restored.StepsTaken > 0)
                onStep?.Invoke("Resumed coding session " + resolvedSessionId + " at step "
                    + (restored.StepsTaken + 1) + " from durable local execution evidence.");

            var phaseTimer = new CodingAgentPhaseTimer();
            void FlushStepTiming()
            {
                string timing = phaseTimer.EndStep();
                if (timing.Length > 0) onStep?.Invoke(timing);
            }

            void SaveCheckpoint(int stepsTaken)
            {
                long checkpointStart = System.Diagnostics.Stopwatch.GetTimestamp();
                try { SaveCheckpointCore(stepsTaken); }
                finally { phaseTimer.Add(CodingAgentPhase.Checkpoint, System.Diagnostics.Stopwatch.GetTimestamp() - checkpointStart); }
            }

            void SaveCheckpointCore(int stepsTaken)
            {
                checkpoint.StepsTaken = stepsTaken;
                checkpoint.ConsecutiveNonProgressSteps = consecutiveNonProgress;
                checkpoint.VerificationRequired = verificationRequired;
                checkpoint.VerificationSatisfied = verificationSatisfied;
                checkpoint.VerifiedByCommand = verifiedByCommand;
                checkpoint.Observations = new List<string>(log);
                CodingAgentCheckpointStore.Save(checkpoint);
            }

            // Every result that can reach the verifier carries the executed checks: the verifier gates
            // "pass" on real exit codes, so a paused attempt must not lose them.
            CodingAgentResult Finish(CodingAgentResult result)
            {
                result.ExecutedChecks = checkpoint.ExecutedChecks.Select(check => new CodingExecutedCheckRecord
                    { Command = check.Command, ExitCode = check.ExitCode, Scope = check.Scope }).ToList();
                return result;
            }

            try
            {
                int firstStep = Math.Max(1, checkpoint.StepsTaken + 1);
                for (int step = firstStep; step <= MaxSteps; step++)
                {
                    ct.ThrowIfCancellationRequested();
                    FlushStepTiming();
                    phaseTimer.BeginStep(step);

                    long contextStart = System.Diagnostics.Stopwatch.GetTimestamp();
                    CodingContextBudgetResolver.Budget contextBudget = await CodingContextBudgetResolver
                        .ResolveAsync(ct).ConfigureAwait(false);
                    // Step 1 observes whichever model is resident (often the chat model) before Core
                    // swaps in the coding model for the first request, which logged a misleading
                    // "8192 tokens / context too small". Report once the coding model has answered.
                    bool budgetReflectsCodingModel = step > firstStep;
                    if (!contextBudgetReported && budgetReflectsCodingModel)
                    {
                        contextBudgetReported = true;
                        onStep?.Invoke(contextBudget.Observed
                            ? "Observed active model context: " + contextBudget.ContextTokens + " tokens; repository evidence budget " + contextBudget.MaxCharacters + " characters."
                            : "Active model context is unknown; using conservative repository evidence budget " + contextBudget.MaxCharacters + " characters without inventing n_ctx.");
                    }

                    int promptContextTokens = CodingAgentPromptBudgetPolicy.InitialPromptContextTokens(
                        contextBudget.ContextTokens, budgetReflectsCodingModel);
                    int promptMaximumCharacters = promptContextTokens == contextBudget.ContextTokens
                        ? contextBudget.MaxCharacters
                        : CodingContextBudgetPolicy.ForContextTokens(promptContextTokens).MaxCharacters;
                    string systemPrompt = BuildSystemPrompt(root, orchestratorTaskId != null, canSafeRestore, canScriptedRestore,
                        batchReadEnabled, continuationEnabled, promptContextTokens, mcpTools);
                    // Working memory scales with the window instead of staying at the old fixed
                    // floor, so a larger physical context actually buys recall and not just more
                    // fresh repository text. See CodingAgentPromptBudgetPolicy for the measurement.
                    // The coding runtime launches with a reasoning budget, and hidden
                    // reasoning consumes the same window as the answer. Reserving only the
                    // default overfilled the context and the model returned empty actions.
                    int generationReserve = CodingAgentPromptBudgetPolicy.GenerationReserveTokens(
                        CodingAgentReasoningBudget);
                    int fixedPromptCharacters = systemPrompt.Length + actionSchema.Length;
                    int recentEvidenceCharacters = CodingAgentPromptBudgetPolicy.RecentEvidenceCharacters(
                        promptContextTokens, MinimumRecentEvidenceCharsInPrompt,
                        fixedPromptCharacters, task.Length, generationReserve);
                    int repositoryCharacters = CodingAgentPromptBudgetPolicy.RepositoryCharacters(
                        promptContextTokens, promptMaximumCharacters,
                        task.Length, fixedPromptCharacters, recentEvidenceCharacters, generationReserve);
                    bool compactProtocolRetry = compactNextProtocolRetry;
                    compactNextProtocolRetry = false;
                    if (compactProtocolRetry)
                    {
                        recentEvidenceCharacters = Math.Min(
                            recentEvidenceCharacters,
                            Math.Max(CodingAgentPromptBudgetPolicy.SmallWindowMinimumRecentEvidenceCharacters,
                                recentEvidenceCharacters / 2));
                        repositoryCharacters = Math.Max(
                            CodingAgentPromptBudgetPolicy.SmallWindowMinimumRepositoryCharacters,
                            repositoryCharacters / 2);
                        onStep?.Invoke("Retrying the empty model response once with compacted repository and recent-evidence context.");
                    }
                    if (!contextTooSmallReported && budgetReflectsCodingModel && CodingAgentPromptBudgetPolicy.IsContextTooSmall(
                            promptContextTokens, task.Length, fixedPromptCharacters,
                            recentEvidenceCharacters, generationReserve))
                    {
                        contextTooSmallReported = true;
                        onStep?.Invoke("Context too small for this agent: " + contextBudget.ContextTokens
                            + " tokens cannot hold the protocol (" + fixedPromptCharacters
                            + " chars), the evidence and a " + generationReserve
                            + "-token generation reserve. Raise this model's physical context window in "
                            + "Runtime Manager, or lower the coding reasoning effort; answers may come back empty.");
                    }
                    string compiledContext = await CodingContextCompiler
                        .CompileAsync(root, task, ct, maxChars: repositoryCharacters, priorObservations: log)
                        .ConfigureAwait(false);
                    string restoreHint = BuildRestoreHint(root, grantedCapabilities);
                    // Half the working-memory budget goes to durable task memory, half to the
                    // verbatim tail. The ledger holds what is older than the tail, which is
                    // exactly the evidence the agent used to lose and re-derive by re-reading.
                    int durableMemoryCharacters = recentEvidenceCharacters / 2;
                    string durableMemory = await CodingTaskMemoryClient.Current
                        .RecallAsync(memoryScopeId, durableMemoryCharacters, ct).ConfigureAwait(false);
                    string userPrompt = BuildPrompt(
                        task, log, compiledContext, restoreHint,
                        checkpoint.Plan, verificationRequired, verificationSatisfied,
                        checkpoint.StepsTaken > 0,
                        recentEvidenceCharacters - durableMemory.Length, durableMemory,
                        permissionMode: effectivePermissionMode);
                    // The real managed llama tokenizer counts the exact structured chat
                    // request, including its template and JSON schema, before inference.
                    // A successful Core probe first resolves/loads the coding model; re-read
                    // /props afterwards, because the previous resident Chat model can have
                    // a different per-slot context. External/unsupported providers fall back
                    // to the existing conservative character estimate.
                    bool exactProbeRefreshedContext = false;
                    int? lastExactRenderedInputTokens = null;
                    async Task<bool> FitsPreparedPromptAsync(string candidate)
                    {
                        int? exactTokens = await CodingRenderedTokenProbeClient.TryCountAsync(
                            systemPrompt, candidate, "parux_coding_action", actionSchema, ct)
                            .ConfigureAwait(false);
                        if (exactTokens.HasValue)
                        {
                            lastExactRenderedInputTokens = exactTokens.Value;
                            if (!exactProbeRefreshedContext)
                            {
                                CodingContextBudgetResolver.Invalidate();
                                contextBudget = await CodingContextBudgetResolver.ResolveAsync(ct)
                                    .ConfigureAwait(false);
                                exactProbeRefreshedContext = true;
                                budgetReflectsCodingModel = true;
                            }
                            if (contextBudget.Observed)
                            {
                                if (!exactTokenProbeReported)
                                {
                                    exactTokenProbeReported = true;
                                    onStep?.Invoke("Coding context: exact rendered prompt uses "
                                        + exactTokens.Value + " tokens; observed model slot has "
                                        + contextBudget.ContextTokens + " tokens; "
                                        + generationReserve + " tokens reserved for reasoning and action.");
                                }
                                return CodingExactPromptAdmissionPolicy.Fits(
                                    exactTokens.Value, contextBudget.ContextTokens, generationReserve);
                            }
                        }
                        return !budgetReflectsCodingModel || !contextBudget.Observed
                            || CodingAgentPromptBudgetPolicy.FitsFinalPrompt(
                                contextBudget.ContextTokens, generationReserve,
                                systemPrompt.Length, actionSchema.Length, candidate.Length);
                    }

                    // One exact preflight also supports proactive, lossless
                    // compaction at 85% of the usable window, following Jan's
                    // high-watermark idea. Shrink ONLY replayed tool observations:
                    // the full data remains in the persisted checkpoint/artifacts.
                    int contextTokensBeforeExactProbe = contextBudget.ContextTokens;
                    bool originalFits = await FitsPreparedPromptAsync(userPrompt)
                        .ConfigureAwait(false);
                    if (exactProbeRefreshedContext
                        && contextBudget.ContextTokens != contextTokensBeforeExactProbe)
                    {
                        // The token-count preflight resolves the selected coding-role model. The
                        // earlier /props result can describe the resident chat model, so rebuild
                        // every context-dependent projection before the first coding inference.
                        systemPrompt = BuildSystemPrompt(root, orchestratorTaskId != null,
                            canSafeRestore, canScriptedRestore, batchReadEnabled,
                            continuationEnabled, contextBudget.ContextTokens, mcpTools);
                        promptContextTokens = contextBudget.ContextTokens;
                        promptMaximumCharacters = contextBudget.MaxCharacters;
                        fixedPromptCharacters = systemPrompt.Length + actionSchema.Length;
                        recentEvidenceCharacters = CodingAgentPromptBudgetPolicy.RecentEvidenceCharacters(
                            contextBudget.ContextTokens, MinimumRecentEvidenceCharsInPrompt,
                            fixedPromptCharacters, task.Length, generationReserve);
                        repositoryCharacters = CodingAgentPromptBudgetPolicy.RepositoryCharacters(
                            contextBudget.ContextTokens, contextBudget.MaxCharacters,
                            task.Length, fixedPromptCharacters, recentEvidenceCharacters, generationReserve);
                        compiledContext = await CodingContextCompiler.CompileAsync(
                            root, task, ct, maxChars: repositoryCharacters, priorObservations: log)
                            .ConfigureAwait(false);
                        durableMemoryCharacters = recentEvidenceCharacters / 2;
                        durableMemory = await CodingTaskMemoryClient.Current
                            .RecallAsync(memoryScopeId, durableMemoryCharacters, ct).ConfigureAwait(false);
                        userPrompt = BuildPrompt(
                            task, log, compiledContext, restoreHint,
                            checkpoint.Plan, verificationRequired, verificationSatisfied,
                            checkpoint.StepsTaken > 0,
                            recentEvidenceCharacters - durableMemory.Length, durableMemory,
                            permissionMode: effectivePermissionMode);
                        originalFits = await FitsPreparedPromptAsync(userPrompt).ConfigureAwait(false);
                        onStep?.Invoke("Coding context changed after the selected role model loaded; rebuilt the prompt and evidence budgets for the "
                            + contextBudget.ContextTokens + "-token coding window.");
                    }
                    if (originalFits && CodingAgentProactiveContextPolicy.ShouldTrim(
                            lastExactRenderedInputTokens, contextBudget.ContextTokens,
                            generationReserve, log.Count))
                    {
                        int compactReplay = CodingAgentProactiveContextPolicy
                            .SuggestedRecentEvidenceCharacters(recentEvidenceCharacters);
                        int compactMemoryLength = Math.Min(durableMemory.Length, compactReplay / 2);
                        string proposed = BuildPrompt(
                            task, log, compiledContext, restoreHint,
                            checkpoint.Plan, verificationRequired, verificationSatisfied,
                            checkpoint.StepsTaken > 0,
                            compactReplay - compactMemoryLength,
                            durableMemory[..compactMemoryLength],
                            CodingAgentRecoveryDigest.Build(checkpoint, log, 900),
                            permissionMode: effectivePermissionMode);
                        if (CodingAgentProactiveContextPolicy.HasUsefulSaving(
                                userPrompt.Length, proposed.Length)
                            && await FitsPreparedPromptAsync(proposed).ConfigureAwait(false))
                        {
                            userPrompt = proposed;
                            onStep?.Invoke("Step " + step + ": proactive context compaction "
                                + "preserved full checkpoint/artifacts while reducing replay by "
                                + (recentEvidenceCharacters - compactReplay)
                                + " budget characters before model inference.");
                        }
                    }

                    // If either actual tokenizer or conservative estimate says the
                    // fully assembled request cannot fit, compact ONLY reconstructible
                    // evidence. Task, tool schema and durable checkpoint remain intact.
                    if (!originalFits)
                    {
                        bool recovered = false;
                        for (int pass = 0; pass < 4; pass++)
                        {
                            var reduced = CodingAgentPromptBudgetPolicy.RecoveryBudgets(
                                pass, recentEvidenceCharacters, repositoryCharacters);
                            if (reduced.RepositoryCharacters != repositoryCharacters)
                            {
                                compiledContext = await CodingContextCompiler.CompileAsync(
                                    root, task, ct, maxChars: reduced.RepositoryCharacters,
                                    priorObservations: log).ConfigureAwait(false);
                                repositoryCharacters = reduced.RepositoryCharacters;
                            }

                            // The original observations and Virtual Context ledger are untouched.
                            // These are only bounded projections for the current model turn.
                            int memoryCharacters = Math.Min(
                                durableMemory.Length, reduced.RecentCharacters / 2);
                            string compactMemory = durableMemory[..memoryCharacters];
                            userPrompt = BuildPrompt(
                                task, log, compiledContext, restoreHint,
                                checkpoint.Plan, verificationRequired, verificationSatisfied,
                                checkpoint.StepsTaken > 0,
                                reduced.RecentCharacters - memoryCharacters, compactMemory,
                                CodingAgentRecoveryDigest.Build(
                                    checkpoint, log, pass <= 1 ? 900 : pass == 2 ? 600 : 350),
                                permissionMode: effectivePermissionMode);

                            if (!await FitsPreparedPromptAsync(userPrompt).ConfigureAwait(false))
                                continue;

                            onStep?.Invoke("Step " + step + ": context recovery pass " + (pass + 1)
                                + " fitted the request to the observed " + contextBudget.ContextTokens
                                + "-token window; all execution evidence remains in the checkpoint.");
                            recovered = true;
                            break;
                        }

                        if (!recovered && !contextRaiseAttempted)
                        {
                            // Failing is the last resort: widen the model window once (cheapest launchable
                            // configuration), reload, and retry this very step with the larger window.
                            contextRaiseAttempted = true;
                            onStep?.Invoke("Step " + step + ": the " + contextBudget.ContextTokens
                                + "-token context cannot hold this request even after compaction; trying to raise it automatically.");
                            CodingContextRemedy.Outcome remedy = await CodingContextRemedy
                                .TryRaiseAsync(contextBudget.ContextTokens, ct).ConfigureAwait(false);
                            onStep?.Invoke(remedy.Message);
                            if (remedy.Raised)
                            {
                                step--;
                                continue;
                            }
                        }

                        if (!recovered)
                        {
                            // Keep the previous completed step, so resume does not skip a model
                            // action that never ran. Never present this as a completed code task.
                            SaveCheckpoint(step - 1);
                            string reason = "Coding paused before inference: the selected model's "
                                + contextBudget.ContextTokens + "-token context cannot hold the "
                                + "task, action protocol and generation reserve, even after bounded "
                                + "evidence compaction and an automatic context increase attempt. "
                                + "The checkpoint and artifacts are preserved. "
                                + "Use a larger usable context or reduce/split the task.";
                            onStep?.Invoke(reason);
                            return Finish(new CodingAgentResult
                            {
                                StepsTaken = step - 1,
                                InfrastructureBlocked = true,
                                StopKind = CodingAgentStopReason.ContextTooSmall,
                                VerificationPending = verificationRequired && !verificationSatisfied,
                                Summary = reason
                            });
                        }
                    }

                    var history = new List<ChatMessage> { new ChatMessage { Role = "user", Content = userPrompt } };
                    phaseTimer.Add(CodingAgentPhase.Context, System.Diagnostics.Stopwatch.GetTimestamp() - contextStart);

                    var buffer = new StringBuilder();
                    long inferenceStart = System.Diagnostics.Stopwatch.GetTimestamp();
                    long firstTokenAt = 0;
                    StructuredChatOutcome structured;
                    try
                    {
                        structured = await StructuredChatClient.Current.StreamAsync(
                            history,
                            systemPrompt,
                            "parux_coding_action",
                            actionSchema,
                            fragment =>
                            {
                                if (firstTokenAt == 0) firstTokenAt = System.Diagnostics.Stopwatch.GetTimestamp();
                                buffer.Append(fragment);
                            },
                            ct,
                            modelSelection: "parux-role:coding").ConfigureAwait(false);
                    }
                    catch (LocalAiException ex) when (
                        CodingAgentPromptBudgetPolicy.IsProviderContextOverflow(ex.Message))
                    {
                        // A provider may count tokens differently from the conservative
                        // character estimate, or the initial turn may have observed the
                        // previous resident model before the coding role was loaded.
                        // No action has executed yet. Make ONE smaller request, preserving
                        // both repository state and the exact completed-step checkpoint.
                        onStep?.Invoke("Step " + step + ": provider rejected context; retrying once "
                            + "with minimal, reconstructible evidence. Prior observations are preserved.");
                        // The first inference may have swapped the resident model from Chat
                        // to Coding. A pre-request /props cache can then describe the wrong
                        // window, so refresh it *after* the provider's overflow response.
                        CodingContextBudgetResolver.Invalidate();
                        contextBudget = await CodingContextBudgetResolver.ResolveAsync(ct)
                            .ConfigureAwait(false);
                        compiledContext = await CodingContextCompiler.CompileAsync(
                            root, task, ct,
                            maxChars: CodingAgentPromptBudgetPolicy.MinimumRepositoryCharacters,
                            priorObservations: null).ConfigureAwait(false);
                        userPrompt = BuildPrompt(
                            task, log, compiledContext, restoreHint,
                            checkpoint.Plan, verificationRequired, verificationSatisfied,
                            checkpoint.StepsTaken > 0, 0, string.Empty,
                            CodingAgentRecoveryDigest.Build(checkpoint, log, 500),
                            permissionMode: effectivePermissionMode);
                        // Even on the provider-overflow retry, use the same
                        // actual model-rendered token admission as the initial
                        // request. A chars-only check can underprice dense code
                        // or reject a valid small retry unnecessarily.
                        if (!await FitsPreparedPromptAsync(userPrompt).ConfigureAwait(false))
                        {
                            SaveCheckpoint(step - 1);
                            string reason = "Coding paused: the provider reported a context overflow, "
                                + "and the minimal retry cannot fit the observed context window. "
                                + "No model action was executed; checkpoint and artifacts are preserved.";
                            onStep?.Invoke(reason);
                            return Finish(new CodingAgentResult
                            {
                                InfrastructureBlocked = true,
                                StopKind = CodingAgentStopReason.ContextTooSmall,
                                StepsTaken = step - 1,
                                VerificationPending = verificationRequired && !verificationSatisfied,
                                Summary = reason
                            });
                        }

                        buffer.Clear();
                        try
                        {
                            var retryHistory = new List<ChatMessage> {
                                new ChatMessage { Role = "user", Content = userPrompt }
                            };
                            structured = await StructuredChatClient.Current.StreamAsync(
                                retryHistory, systemPrompt, "parux_coding_action", actionSchema,
                                fragment => buffer.Append(fragment), ct,
                                modelSelection: "parux-role:coding").ConfigureAwait(false);
                        }
                        catch (LocalAiException retryException) when (
                            CodingAgentPromptBudgetPolicy.IsProviderContextOverflow(retryException.Message))
                        {
                            // Repeated provider overflow is not a model action, and is not
                            // a reason to spend 10,000 coding steps retrying the same input.
                            SaveCheckpoint(step - 1);
                            string reason = "Coding paused after a second provider context overflow. "
                                + "Select a model with a larger usable context or split the task; "
                                + "the last completed step and all evidence are preserved.";
                            onStep?.Invoke(reason);
                            return Finish(new CodingAgentResult
                            {
                                InfrastructureBlocked = true,
                                StopKind = CodingAgentStopReason.ContextTooSmall,
                                StepsTaken = step - 1,
                                VerificationPending = verificationRequired && !verificationSatisfied,
                                Summary = reason
                            });
                        }
                    }
                    finally
                    {
                        long inferenceEnd = System.Diagnostics.Stopwatch.GetTimestamp();
                        long firstToken = firstTokenAt == 0 ? inferenceEnd : firstTokenAt;
                        phaseTimer.Add(CodingAgentPhase.FirstToken, firstToken - inferenceStart);
                        phaseTimer.Add(CodingAgentPhase.Generate, inferenceEnd - firstToken);
                    }

                    // The first coding request can swap the resident model away from the
                    // previous Chat model. A /props result cached before that swap may
                    // describe the wrong context window for subsequent coding steps.
                    // Force one fresh runtime probe on the next step, rather than
                    // trusting the obsolete 30-second cache.
                    if (step == firstStep)
                        CodingContextBudgetResolver.Invalidate();

                    if (structured.StructuredOutputApplied == false && !structuredFallbackReported)
                    {
                        structuredFallbackReported = true;
                        string reason = string.IsNullOrWhiteSpace(structured.FallbackReason)
                            ? "the selected local provider did not advertise schema-constrained output"
                            : Truncate(structured.FallbackReason, 300);
                        onStep?.Invoke("Structured-output fallback active: " + reason
                            + ". PaRuX will validate each JSON action locally.");
                    }

                    CodingAgentAction? action = TryParseAction(buffer.ToString(), out string parseError);
                    if (!TryValidateAction(action, out string validationError))
                    {
                        consecutiveNonProgress++;
                        if (action == null && !string.IsNullOrWhiteSpace(parseError))
                            validationError = parseError;

                        // Record what the model actually said. Without it a protocol failure is
                        // undiagnosable after the fact - the durable evidence held nine identical
                        // "missing action" lines and no way to tell an empty response from a
                        // well-formed answer in the wrong shape. It also gives the model its own
                        // previous output back, which is what it needs in order to correct it.
                        string rawResponse = Truncate(buffer.ToString().Trim(), 1_200);
                        bool narrationOnly = action == null
                            && CodingAgentNarrationStallPolicy.IsNarrationOnly(buffer.ToString());
                        bool retryProtocol;
                        string correction;
                        if (narrationOnly)
                        {
                            consecutiveNarrationResponses++;
                            // Prose does not clear the invalid-action count: alternating prose and invalid JSON must still spend the protocol budget.
                            retryProtocol = !CodingAgentNarrationStallPolicy.ShouldStop(consecutiveNarrationResponses);
                            correction = retryProtocol
                                ? CodingAgentNarrationStallPolicy.NudgeFor(consecutiveNarrationResponses)
                                    ?? "Return exactly one JSON object with an action field. Do not send prose without an action."
                                : "Stopped after " + consecutiveNarrationResponses
                                    + " consecutive prose-only responses; no action was executed and the checkpoint was preserved.";
                        }
                        else
                        {
                            // Invalid JSON does not clear the prose count; only a valid action resets both (below).
                            protocolFailures++;
                            retryProtocol = CodingAgentProtocolRetryPolicy.ShouldRetry(protocolFailures);
                            correction = retryProtocol
                                ? CodingAgentProtocolRetryPolicy.CorrectionInstruction(protocolFailures, validationError)
                                : CodingAgentProtocolRetryPolicy.StopReason(protocolFailures);
                            compactNextProtocolRetry = retryProtocol
                                && protocolFailures == 1
                                && rawResponse.Length == 0;
                        }
                        log.Add("Protocol correction required: " + validationError
                            + " Raw response: " + (rawResponse.Length == 0 ? "(empty)" : rawResponse)
                            + " " + correction);
                        onStep?.Invoke("Step " + step + ": invalid action protocol — " + validationError
                            + (rawResponse.Length == 0 ? " (model returned an empty response)" : string.Empty));
                        SaveCheckpoint(step);
                        if (!retryProtocol)
                        {
                            return Finish(new CodingAgentResult
                            {
                                StepsTaken = step,
                                VerificationPending = verificationRequired && !verificationSatisfied,
                                Summary = (narrationOnly
                                    ? "Paused after " + consecutiveNarrationResponses + " consecutive prose-only responses."
                                    : CodingAgentProtocolRetryPolicy.StopReason(protocolFailures))
                                    + " No action was executed; the coding checkpoint was preserved for resume."
                            });
                        }
                        continue;
                    }
                    consecutiveNarrationResponses = 0;
                    protocolFailures = 0;

                    if (string.Equals(action.Action, "update_plan", StringComparison.OrdinalIgnoreCase))
                    {
                        string nextPlan = (string.IsNullOrWhiteSpace(action.Content) ? action.Summary : action.Content).Trim();
                        bool changed = !string.Equals(nextPlan, checkpoint.Plan, StringComparison.Ordinal);
                        checkpoint.Plan = nextPlan;
                        // A plan is durable coordination evidence, but it is not repository progress.
                        // Counting it as progress lets a model rewrite its plan forever without editing.
                        consecutiveNonProgress++;
                        log.Add(changed
                            ? "Implementation plan updated:\n" + Truncate(nextPlan, 2_000)
                            : "Plan update made no change. Execute the next concrete step instead of restating the plan.");
                        if (changed)
                            await CodingTaskMemoryClient.Current.RememberAsync(
                                memoryScopeId, VirtualContextLedgerKind.Decision,
                                "Implementation plan: " + Truncate(nextPlan, 2_000),
                                "step-" + step + ":update_plan", ct).ConfigureAwait(false);
                        onStep?.Invoke("Step " + step + ": implementation plan " + (changed ? "updated" : "unchanged") + ".");
                        SaveCheckpoint(step);
                        continue;
                    }

                    if (string.Equals(action.Action, "collect_children", StringComparison.OrdinalIgnoreCase))
                    {
                        consecutiveNonProgress++;
                        if (!continuationEnabled || checkpoint.ContinuedChildTaskIds.Count == 0)
                        {
                            log.Add(continuationEnabled
                                ? "collect_children: no continued child exists. Delegate read-only children with continueParent=true first."
                                : "collect_children is unavailable in this session.");
                            onStep?.Invoke("Step " + step + ": collect_children had nothing to collect.");
                            SaveCheckpoint(step);
                            continue;
                        }

                        IReadOnlyDictionary<string, AgentTaskDto?> childStates = await phaseTimer.MeasureAsync(
                            CodingAgentPhase.Coordination,
                            () => FetchChildStatesAsync(checkpoint.ContinuedChildTaskIds, ct)).ConfigureAwait(false);
                        IReadOnlyList<string> terminalNow = CodingAgentContinuation.TerminalIds(checkpoint.ContinuedChildTaskIds, childStates);
                        IReadOnlyList<string> liveNow = CodingAgentContinuation.LiveIds(checkpoint.ContinuedChildTaskIds, childStates);
                        checkpoint.CollectedChildTaskIds = checkpoint.CollectedChildTaskIds
                            .Concat(terminalNow).Distinct(StringComparer.Ordinal).ToList();
                        string snapshot = CodingAgentContinuation.RenderSnapshot(checkpoint.ContinuedChildTaskIds, childStates);
                        log.Add("collect_children: " + terminalNow.Count + " terminal, " + liveNow.Count + " still running. "
                            + CodingAgentContinuation.UntrustedEvidenceNotice + "\n" + snapshot
                            + (liveNow.Count > 0
                                ? "\nContinue independent work; reporting done pauses this task behind the running child task(s) and resumes it with their results."
                                : string.Empty));
                        onStep?.Invoke("Step " + step + ": collect_children — " + terminalNow.Count + " terminal, " + liveNow.Count + " running.");
                        SaveCheckpoint(step);
                        continue;
                    }

                    if (string.Equals(action.Action, "done", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!CodingBrowserPostconditionPolicy.MayReportDone(checkpoint))
                        {
                            consecutiveNonProgress++;
                            unverifiedCompletionRejections++;
                            log.Add("Completion blocked: a browser click, type or evaluation has no subsequent successful "
                                + "browser_assert with a concrete text_equals or value_equals expected outcome. "
                                + "A trivial exists/visible assertion is not proof that an interaction succeeded; "
                                + "a CDP action-delivery success is not a functional PASS.");
                            SaveCheckpoint(step);
                            if (CodingVerificationCommandPolicy.ShouldPauseAfterUnverifiedCompletions(
                                    unverifiedCompletionRejections))
                                return Finish(new CodingAgentResult
                                {
                                    StepsTaken = step,
                                    VerificationPending = true,
                                    Summary = "Browser action postcondition is pending; checkpoint preserved for verified resume."
                                });
                            continue;
                        }
                        if (verificationRequired && !verificationSatisfied)
                        {
                            consecutiveNonProgress++;
                            unverifiedCompletionRejections++;
                            log.Add(CodingVerificationDiscovery.BuildUnverifiedCompletionRejection(root));
                            onStep?.Invoke("Step " + step + ": completion blocked until changed code is verified.");
                            SaveCheckpoint(step);
                            if (CodingVerificationCommandPolicy.ShouldPauseAfterUnverifiedCompletions(unverifiedCompletionRejections))
                            {
                                return Finish(new CodingAgentResult
                                {
                                    StepsTaken = step,
                                    VerificationPending = true,
                                    Summary = "Paused after " + unverifiedCompletionRejections
                                        + " completion attempts without running an accepted verification command. The checkpoint was preserved for review/resume."
                                });
                            }
                            continue;
                        }

                        // The agent may run another command, a build tool may rewrite
                        // tracked files, or a separate app may edit the workspace
                        // *after* the accepted verification command. The command
                        // receipt is bound to the exact Git state it certified.
                        if (verificationRequired && verificationSatisfied)
                        {
                            string? actualFingerprint =
                                await CodingWorkspaceVerificationFingerprint.CaptureAsync(root, ct)
                                    .ConfigureAwait(false);
                            if (string.IsNullOrWhiteSpace(checkpoint.VerifiedWorkspaceFingerprint)
                                || !string.Equals(
                                    actualFingerprint, checkpoint.VerifiedWorkspaceFingerprint,
                                    StringComparison.Ordinal))
                            {
                                CodingAgentCheckpointStore.InvalidateVerificationAfterMutation(checkpoint);
                                verificationRequired = true;
                                verificationSatisfied = false;
                                verifiedByCommand = false;
                                consecutiveNonProgress++;
                                unverifiedCompletionRejections++;
                                log.Add("Completion blocked: workspace Git state changed since the last accepted verification command, "
                                    + "or its fingerprint could not be proved. Re-run the discovered build/test gate on the current files.");
                                SaveCheckpoint(step);
                                if (CodingVerificationCommandPolicy.ShouldPauseAfterUnverifiedCompletions(
                                        unverifiedCompletionRejections))
                                    return Finish(new CodingAgentResult
                                    {
                                        StepsTaken = step,
                                        VerificationPending = true,
                                        Summary = "Paused because verified workspace evidence was stale or unavailable. "
                                            + "The checkpoint and worktree are preserved."
                                    });
                                continue;
                            }
                        }

                        // A continuing parent never finishes over an outstanding read-only child: a live
                        // child pauses it through Core (the ordinary delegation Waiting stage, resumed
                        // with bounded child evidence), and a finished-but-unseen child is shown first.
                        if (continuationEnabled && (checkpoint.ContinuationUsed || checkpoint.ContinuedChildTaskIds.Count > 0))
                        {
                            // Core, not this client, decides whether any direct child is still live: the
                            // client keeps at most six ids and a lookup gap must never let the task finish
                            // over an outstanding child. Core pauses the parent atomically, or reports none.
                            AwaitDelegatedChildrenResponse? hold = null;
                            string holdFailure = string.Empty;
                            try
                            {
                                hold = await phaseTimer.MeasureAsync(
                                    CodingAgentPhase.Coordination,
                                    () => AgentOrchestratorClient.Current.AwaitDelegatedChildrenAsync(orchestratorTaskId!, ct)).ConfigureAwait(false);
                            }
                            catch (OperationCanceledException) { throw; }
                            catch (Exception ex) when (ex is InvalidDataException or LocalAiException or IOException or UnauthorizedAccessException or System.Net.Http.HttpRequestException)
                            {
                                holdFailure = Truncate(ex.Message, 400);
                            }

                            if (hold is { Waiting: true })
                            {
                                // The worker injects every terminal child's evidence into the goal on resume.
                                checkpoint.CollectedChildTaskIds = checkpoint.ContinuedChildTaskIds.ToList();
                                onStep?.Invoke("Step " + step + ": done held; parent paused behind " + hold.LiveChildTaskIds.Count
                                    + " running read-only child task(s). Worktree/checkpoint preserved.");
                                SaveCheckpoint(step);
                                return Finish(new CodingAgentResult
                                {
                                    StepsTaken = step,
                                    WaitingForDelegatedChild = true,
                                    DelegatedChildTaskId = hold.LiveChildTaskIds.FirstOrDefault(),
                                    DelegatedChildTaskIds = hold.LiveChildTaskIds.ToList(),
                                    Summary = "Paused while " + hold.LiveChildTaskIds.Count + " read-only child task(s) finish."
                                });
                            }

                            if (hold == null)
                            {
                                childHoldRejections++;
                                consecutiveNonProgress++;
                                log.Add("Completion held: Core could not confirm that no read-only child task is running (" + holdFailure
                                    + "). Report done again once Core is reachable.");
                                onStep?.Invoke("Step " + step + ": completion held; Core did not confirm that child tasks are finished.");
                                SaveCheckpoint(step);
                                if (childHoldRejections >= 5)
                                    return Finish(new CodingAgentResult
                                    {
                                        StepsTaken = step,
                                        VerificationPending = verificationRequired && !verificationSatisfied,
                                        Summary = "Paused: Core never confirmed that the read-only child tasks were finished. The checkpoint was preserved."
                                    });
                                continue;
                            }

                            // Core reports no live child. Finished-but-unseen results are shown once first.
                            IReadOnlyDictionary<string, AgentTaskDto?> doneStates = await phaseTimer.MeasureAsync(
                                CodingAgentPhase.Coordination,
                                () => FetchChildStatesAsync(checkpoint.ContinuedChildTaskIds, ct)).ConfigureAwait(false);
                            IReadOnlyList<string> unseen = CodingAgentContinuation.TerminalIds(checkpoint.ContinuedChildTaskIds, doneStates)
                                .Where(id => !checkpoint.CollectedChildTaskIds.Contains(id, StringComparer.Ordinal)).ToList();
                            if (unseen.Count > 0)
                            {
                                checkpoint.CollectedChildTaskIds = checkpoint.CollectedChildTaskIds
                                    .Concat(unseen).Distinct(StringComparer.Ordinal).ToList();
                                consecutiveNonProgress++;
                                log.Add("Completion held once so you can read the finished child task result(s) before finishing. "
                                    + CodingAgentContinuation.UntrustedEvidenceNotice + "\n"
                                    + CodingAgentContinuation.RenderSnapshot(checkpoint.ContinuedChildTaskIds, doneStates)
                                    + "\nIncorporate anything relevant, then report done.");
                                onStep?.Invoke("Step " + step + ": done held to present " + unseen.Count + " finished child result(s).");
                                SaveCheckpoint(step);
                                continue;
                            }
                        }

                        string summary = string.IsNullOrWhiteSpace(action.Summary)
                            ? "PaRuX reported the task complete."
                            : action.Summary;
                        if (CodingCompletionClaimPolicy.RequiresCorrection(summary, verifiedByCommand))
                        {
                            consecutiveNonProgress++;
                            unverifiedCompletionRejections++;
                            log.Add("Completion refused: the summary claims successful tests "
                                + "but no accepted test command success is recorded after the "
                                + "most recent code change. Run appropriate verification or "
                                + "report 'tests not run' rather than inventing results.");
                            onStep?.Invoke("Step " + step + ": unsupported test-success claim rejected.");
                            SaveCheckpoint(step);
                            if (CodingVerificationCommandPolicy.ShouldPauseAfterUnverifiedCompletions(
                                    unverifiedCompletionRejections))
                                return Finish(new CodingAgentResult
                                {
                                    StepsTaken = step,
                                    VerificationPending = true,
                                    Summary = "Paused after repeated unsupported test-success claims. "
                                        + "The actual repository state and checkpoint are preserved."
                                });
                            continue;
                        }
                        onStep?.Invoke("Step " + step + ": done — " + Truncate(summary, 300));
                        if (!coreVerifierTaskAvailable)
                        {
                            // A session ID alone is not Core authority. Direct /code runs and stale,
                            // mismatched, or unassigned session IDs have no durable Core task to
                            // carry an independent verifier decision. The checkpoint's worker-
                            // reported command fingerprint is not an authenticated receipt.
                            log.Add("Completion held: no current Core verifier task is bound to this session and workspace. "
                                + "The worker-reported command fingerprint is not an authenticated receipt. "
                                + "The coding checkpoint and worktree were preserved.");
                            onStep?.Invoke("Step " + step + ": completion held; no current Core verifier task is bound to this session and workspace.");
                            SaveCheckpoint(step);
                            return Finish(new CodingAgentResult
                            {
                                StepsTaken = step,
                                VerificationPending = true,
                                Summary = "Completion is pending an independent Core verifier decision or an authenticated workspace-bound receipt. "
                                    + "The checkpoint and worktree were preserved for resume."
                            });
                        }
                        if (orchestratorTaskId != null)
                        {
                            // The Core independent verifier has not approved
                            // this code yet. Preserve the exact command/step
                            // evidence for review, repair and crash recovery.
                            // A direct chat run has no durable Core handoff.
                            log.Add("Coding implementation reported complete; independent Core verifier decision still pending.");
                            SaveCheckpoint(step);
                        }
                        else
                        {
                            CodingAgentCheckpointStore.Clear(resolvedSessionId);
                        }
                        return Finish(new CodingAgentResult
                        {
                            GoalReported = true, VerifiedByCommand = verifiedByCommand, StepsTaken = step, Summary = summary
                        });
                    }

                    string? repeatKey = ReadOnlyActionKey(action);
                    string priorEvidence = string.Empty;
                    CodingAgentRepeatDecision repeatDecision = repeatKey == null
                        ? CodingAgentRepeatDecision.Allow
                        : repeatGuard.Evaluate(repeatKey, out priorEvidence);
                    if (repeatDecision == CodingAgentRepeatDecision.Steer)
                    {
                        suppressedRepeats++;
                        consecutiveNonProgress++;
                        log.Add("Repeated read-only action needs steering: " + action.Action + " was not executed. Its earlier result was: "
                            + Truncate(priorEvidence, 900)
                            + " Use this evidence to take a different concrete action, or report a specific blocker.");
                        onStep?.Invoke("Step " + step + ": repeated " + action.Action + " steered; no tool call made.");
                        SaveCheckpoint(step);
                        continue;
                    }
                    if (repeatDecision == CodingAgentRepeatDecision.Stop)
                    {
                        consecutiveNonProgress++;
                        log.Add("Repeated read-only action halted: " + action.Action + " was requested a third time in workspace epoch "
                            + repeatGuard.Epoch + ". No tool call was made; its earlier result was: " + Truncate(priorEvidence, 900));
                        onStep?.Invoke("Step " + step + ": third identical " + action.Action + " halted the coding run.");
                        SaveCheckpoint(step);
                        return Finish(new CodingAgentResult
                        {
                            StepsTaken = step,
                            StopKind = CodingAgentStopReason.RepeatedAction,
                            VerificationPending = verificationRequired && !verificationSatisfied,
                            Summary = "Stopped after a third identical read-only action in workspace epoch "
                                + repeatGuard.Epoch + ". The checkpoint and workspace were preserved."
                        });
                    }
                    if (repeatKey != null && consecutiveNonProgress >= ProgressWarningSteps)
                    {
                        suppressedRepeats++;
                        consecutiveNonProgress++;
                        log.Add("Exploration budget exhausted after " + ProgressWarningSteps
                            + " actions without a mutation or successful command. Read-only action "
                            + action.Action + " was not executed. Use the evidence already collected to make a grounded change, run a relevant command, ask the user only if truly blocked, or finish with a concrete blocker.");
                        onStep?.Invoke("Step " + step + ": exploration budget exhausted; " + action.Action + " blocked.");
                        SaveCheckpoint(step);
                        if (suppressedRepeats >= 8 || consecutiveNonProgress >= ProgressStopSteps)
                            return Finish(new CodingAgentResult { StepsTaken = step,
                                StopKind = CodingAgentStopReason.NoProgress,
                                VerificationPending = verificationRequired && !verificationSatisfied,
                                Summary = "Paused after the exploration budget was exhausted without a code change. The checkpoint and repository are preserved for diagnosis." });
                        continue;
                    }

                    bool refusedByPlan = CodingAgentPermissionPolicy.IsRefusedInPlanMode(
                        effectivePermissionMode, action.Action, action.Command);
                    string normalizedActionName = action.Action.Trim().ToLowerInvariant();
                    bool hasExactCommandApproval = effectivePermissionMode == CodingAgentPermissionMode.Ask
                        && normalizedActionName == "run_command"
                        && CodingAgentPermissionPolicy.IsExactCommandApproval(
                            checkpoint.ApprovedCommandText, action.Command);
                    bool askForFileApproval = effectivePermissionMode == CodingAgentPermissionMode.Ask
                        && CodingAgentPermissionPolicy.IsWorkspaceMutationAction(action.Action);
                    string? fileApprovalKey = askForFileApproval
                        ? CodingAgentPermissionPolicy.FileMutationApprovalKey(
                            action.Action, action.Path, action.NewPath, action.Content, action.OldText, action.NewText)
                        : null;
                    bool hasExactFileApproval = askForFileApproval && fileApprovalKey != null
                        && CodingAgentFileApproval.IsApproved(checkpoint, fileApprovalKey);
                    if (hasExactCommandApproval)
                        CodingAgentCommandApproval.ConsumeApproval(checkpoint);
                    if (hasExactFileApproval)
                    {
                        CodingAgentFileApproval.ConsumeApproval(checkpoint);
                        SaveCheckpoint(step);
                    }

                    bool waitingForFileApproval = askForFileApproval && !hasExactFileApproval;

                    // Record the intent synchronously to durable disk BEFORE dispatching
                    // any tool. After a process interruption, resume at the NEXT step
                    // with an explicit uncertainty receipt instead of repeating a
                    // command that may already have mutated the repository.
                    // Reads and search have no external mutation and already use the
                    // repeat guard. Avoid doubling large checkpoint disk writes for
                    // those high-frequency observations. Unknown or potentially
                    // mutating actions always receive a write-ahead receipt.
                    bool trackedDispatch = repeatKey == null && !refusedByPlan && !waitingForFileApproval;
                    if (trackedDispatch)
                    {
                        CodingAgentDispatchJournal.Begin(checkpoint, step, action.Action);
                        checkpoint.Observations = new List<string>(log);
                        CodingAgentCheckpointStore.Save(checkpoint);
                    }
                    CodingExecutionOutcome outcome;
                    if (refusedByPlan)
                    {
                        outcome = new CodingExecutionOutcome(
                            action.Action + " (refused in Plan mode)",
                            "Plan mode is read-only. This action is blocked before execution; switch to Ask or Bypass mode to allow it.");
                    }
                    else if (waitingForFileApproval)
                    {
                        string? preview = CodingAgentPermissionPolicy.FileMutationApprovalPreview(
                            action.Action, action.Path, action.NewPath, action.Content, action.OldText, action.NewText);
                        if (string.IsNullOrWhiteSpace(orchestratorTaskId))
                        {
                            outcome = new CodingExecutionOutcome(
                                action.Action + " (approval unavailable)",
                                "Ask mode requires a durable orchestrator task to approve staged file changes. No file was changed.");
                        }
                        else if (preview == null || fileApprovalKey == null)
                        {
                            outcome = new CodingExecutionOutcome(
                                action.Action + " (approval preview too large)",
                                "Ask mode cannot show this complete file change for approval within the safe preview limit. No file was changed; split the change into smaller edits.");
                        }
                        else
                        {
                            AgentUserQuestionDto question = await AgentOrchestratorClient.Current.AskUserQuestionAsync(
                                orchestratorTaskId,
                                new AskAgentUserQuestionRequest
                                {
                                    Question = "Approve applying this exact staged file change once?\n\n" + preview,
                                    Options = new List<string>
                                    {
                                        CodingAgentPermissionPolicy.ApproveExactFileChangeOnce,
                                        CodingAgentPermissionPolicy.DeclineCommand
                                    },
                                    AllowOther = false
                                },
                                ct).ConfigureAwait(false);
                            CodingAgentFileApproval.Stage(checkpoint, question.Id, fileApprovalKey);
                            outcome = new CodingExecutionOutcome(
                                action.Action + " paused for exact file-change approval -> " + question.Id,
                                "The complete file change is pending explicit approval. It has not been applied; approval is bound to this exact action payload and one execution.",
                                MadeMaterialProgress: true,
                                UserQuestionId: question.Id);
                        }
                    }
                    else
                    {
                        outcome = await phaseTimer.MeasureAsync(
                            CodingAgentPhaseTimer.PhaseForAction(action.Action),
                            () => ExecuteAsync(root, resolvedSessionId, orchestratorTaskId, task,
                                action, effectivePermissionMode, hasExactCommandApproval, checkpoint, mcpTools, ct)).ConfigureAwait(false);
                    }
                    if (repeatKey != null)
                        repeatGuard.Record(repeatKey, outcome.Observation);
                    bool workspaceMutation = outcome.WorkspaceMutated
                        || outcome.MutatedTestableCode
                        || (outcome.MadeMaterialProgress
                            && CodingAgentPermissionPolicy.IsWorkspaceMutationAction(action.Action));
                    if (workspaceMutation)
                    {
                        repeatGuard.AdvanceEpoch();
                        suppressedRepeats = 0;
                    }
                    else if (outcome.MadeMaterialProgress)
                        suppressedRepeats = 0;
                    onStep?.Invoke("Step " + step + ": " + outcome.Description);
                    string observation = outcome.Description + (string.IsNullOrEmpty(outcome.Observation)
                        ? string.Empty : "\n" + outcome.Observation);
                    log.Add(observation);
                    if (outcome.ContinuedChildTaskIds is { Count: > 0 })
                    {
                        checkpoint.ContinuationUsed = true;
                        checkpoint.ContinuedChildTaskIds = CodingAgentContinuation.MergeContinuedIds(
                            checkpoint.ContinuedChildTaskIds, outcome.ContinuedChildTaskIds);
                        onStep?.Invoke("Parent continues while " + outcome.ContinuedChildTaskIds.Count
                            + " read-only child task(s) run: " + string.Join(", ", outcome.ContinuedChildTaskIds) + ".");
                    }
                    // Durable task memory: the prompt can only replay a bounded slice of this
                    // log, so what the agent learned early is gone by the time it needs it and
                    // it reads the same file again. The ledger carries it past the physical
                    // window. Best-effort by design - reduced recall must never fail a task.
                    // Recorded as a constraint, not a note: the ledger prompt projection only
                    // surfaces contradictions, constraints and decisions, so a note would be
                    // stored and never shown to the model - memory that changes nothing.
                    await CodingTaskMemoryClient.Current.RememberAsync(
                        memoryScopeId, VirtualContextLedgerKind.Constraint, observation,
                        "step-" + step + ":" + action.Action, ct).ConfigureAwait(false);

                    CodingBrowserPostconditionPolicy.Apply(
                        checkpoint, action.Action, outcome.BrowserActionSucceeded,
                        action.Target, action.Text);

                    if (outcome.MutatedTestableCode)
                    {
                        // Evidence from before the change says nothing about the changed code, and a
                        // code edit makes prior test receipts stale even for satisfied per-repository scopes.
                        checkpoint.ExecutedChecks.Clear();
                        CodingAgentCheckpointStore.InvalidateVerificationAfterMutation(checkpoint);
                        verifiedByCommand = false;
                        verificationSatisfied = false;
                        verificationRequired = true;
                        // With no discoverable build/test command, changed JavaScript can still be required
                        // to parse (node --check). When nothing at all can verify the edit, the task may finish
                        // but Core records it as "unverified": it is never shown as verified nor admitted to apply.
                        if (CodingVerificationDiscovery.Discover(root).Count == 0
                            && !await HasChangedScriptAsync(root, ct).ConfigureAwait(false))
                        {
                            checkpoint.VerificationRequiredScopes.Clear();
                            verificationRequired = false;
                            verificationSatisfied = true;
                        }
                    }
                    if (outcome.VerificationAttempted && string.Equals(action.Action, "run_command", StringComparison.OrdinalIgnoreCase))
                    {
                        if (outcome.CommandExitCode.HasValue)
                            RecordExecutedCheck(checkpoint, action.Command ?? string.Empty, outcome.CommandExitCode.Value);
                        else
                            RecordExecutedCheck(checkpoint, action.Command ?? string.Empty, outcome.Description);
                    }
                    if (outcome.VerificationAttempted && verificationRequired)
                    {
                        bool acceptedForCurrentWorkspace = outcome.VerificationSucceeded;
                        if (acceptedForCurrentWorkspace)
                        {
                            checkpoint.VerifiedWorkspaceFingerprint =
                                await CodingWorkspaceVerificationFingerprint.CaptureAsync(root, ct)
                                    .ConfigureAwait(false);
                            acceptedForCurrentWorkspace =
                                !string.IsNullOrWhiteSpace(checkpoint.VerifiedWorkspaceFingerprint);
                            if (!acceptedForCurrentWorkspace)
                                log.Add("The verification command passed, but a complete Git-worktree fingerprint "
                                    + "was unavailable. This command cannot certify the current workspace.");
                        }
                        CodingAgentCheckpointStore.RecordVerificationCommandOutcome(
                            checkpoint, acceptedForCurrentWorkspace);
                        verificationSatisfied = acceptedForCurrentWorkspace;
                        verifiedByCommand = acceptedForCurrentWorkspace;
                    }
                    if (outcome.MutatedTestableCode || outcome.VerificationAttempted)
                        unverifiedCompletionRejections = 0;

                    bool repeatedSuccess = false;
                    if (outcome.MutatedTestableCode)
                    {
                        successfulCommandsSinceChange.Clear();
                        identicalSuccessRepeats = 0;
                    }
                    if (string.Equals(action.Action, "run_command", StringComparison.OrdinalIgnoreCase) && outcome.MadeMaterialProgress)
                    {
                        string commandKey = CodingVerificationCommandPolicy.Normalize(action.Command ?? string.Empty);
                        // Any command that already succeeded since the last change (including an
                        // alternating build/test pair) cannot produce new evidence.
                        repeatedSuccess = !successfulCommandsSinceChange.Add(commandKey);
                        if (repeatedSuccess) identicalSuccessRepeats++;
                    }
                    else if (outcome.MadeMaterialProgress)
                    {
                        successfulCommandsSinceChange.Clear();
                        identicalSuccessRepeats = 0;
                    }
                    if (repeatedSuccess)
                        log.Add("This exact command already succeeded and nothing changed since, so its result cannot change. "
                            + (verificationRequired && !verificationSatisfied
                                ? CodingVerificationDiscovery.BuildUnverifiedCompletionRejection(root).Replace("Completion was rejected: ", string.Empty, StringComparison.Ordinal)
                                : "If the goal is met, report done now; otherwise make the next concrete change."));

                    consecutiveNonProgress = outcome.MadeMaterialProgress && !repeatedSuccess ? 0 : consecutiveNonProgress + 1;
                    if (trackedDispatch)
                        CodingAgentDispatchJournal.Complete(checkpoint, step);
                    SaveCheckpoint(step);
                    if (CodingVerificationCommandPolicy.ShouldPauseAfterRepeatedSuccess(identicalSuccessRepeats))
                    {
                        return Finish(new CodingAgentResult
                        {
                            StepsTaken = step,
                            VerificationPending = verificationRequired && !verificationSatisfied,
                            Summary = "Paused after the same successful command was repeated " + identicalSuccessRepeats
                                + " times with no change in between. The checkpoint was preserved for review/resume."
                        });
                    }

                    if (outcome.InfrastructureBlocked)
                    {
                        onStep?.Invoke("Coding sandbox unavailable; task stopped after the failed command. The checkpoint and worktree are preserved for diagnosis.");
                        return Finish(new CodingAgentResult
                        {
                            StepsTaken = step,
                            InfrastructureBlocked = true,
                            StopKind = CodingAgentStopReason.Sandbox,
                            Summary = "Coding sandbox unavailable before command execution: "
                                + Truncate(outcome.Observation, 500)
                                + ". The checkpoint and worktree are preserved for diagnosis."
                        });
                    }

                    if (!string.IsNullOrWhiteSpace(outcome.UserQuestionId))
                    {
                        onStep?.Invoke("Coding task paused for durable user input " + outcome.UserQuestionId + ". Worktree/checkpoint state is preserved.");
                        return Finish(new CodingAgentResult
                        {
                            StepsTaken = step,
                            WaitingForUserInput = true,
                            VerificationPending = verificationRequired && !verificationSatisfied,
                            UserQuestionId = outcome.UserQuestionId,
                            Summary = "Paused for user input (question " + outcome.UserQuestionId + ")."
                        });
                    }

                    IReadOnlyList<string> delegatedIds = outcome.DelegatedChildTaskIds
                        ?? (string.IsNullOrWhiteSpace(outcome.DelegatedChildTaskId)
                            ? Array.Empty<string>()
                            : new[] { outcome.DelegatedChildTaskId! });
                    if (delegatedIds.Count > 0)
                    {
                        // The worker injects every terminal child's evidence on resume, including
                        // earlier continued children, so none of them needs a separate collect.
                        checkpoint.CollectedChildTaskIds = checkpoint.ContinuedChildTaskIds.ToList();
                        SaveCheckpoint(step);
                        onStep?.Invoke("Coding parent paused for " + delegatedIds.Count + " delegated child task(s): "
                            + string.Join(", ", delegatedIds) + ". Durable worktree/checkpoint state is preserved.");
                        return Finish(new CodingAgentResult
                        {
                            StepsTaken = step,
                            WaitingForDelegatedChild = true,
                            VerificationPending = verificationRequired && !verificationSatisfied,
                            DelegatedChildTaskId = delegatedIds[0],
                            DelegatedChildTaskIds = delegatedIds.ToList(),
                            Summary = "Paused while " + delegatedIds.Count + " delegated child task(s) run."
                        });
                    }

                    if (consecutiveNonProgress == ProgressWarningSteps)
                    {
                        log.Add("Progress watchdog: " + ProgressWarningSteps
                            + " consecutive non-mutating/non-successful actions occurred. Reassess the goal, repository evidence, current diff and blockers; do not repeat searches already answered.");
                        onStep?.Invoke("Progress watchdog: asking the coding model to reassess instead of looping.");
                        SaveCheckpoint(step);
                    }

                    if (consecutiveNonProgress >= ProgressStopSteps)
                    {
                        return Finish(new CodingAgentResult
                        {
                            StepsTaken = step,
                            VerificationPending = verificationRequired && !verificationSatisfied,
                            Summary = "Paused by the progress watchdog after " + ProgressStopSteps
                                + " consecutive steps without material progress. The checkpoint was preserved for review/resume."
                        });
                    }
                }

                SaveCheckpoint(MaxSteps);
                onStep?.Invoke("Reached the runaway safety ceiling. Execution evidence was checkpointed instead of discarded.");
                return Finish(new CodingAgentResult
                {
                    StepsTaken = MaxSteps,
                    VerificationPending = verificationRequired && !verificationSatisfied,
                    Summary = "Paused at the runaway safety ceiling; durable state is preserved for resume."
                });
            }
            catch (OperationCanceledException)
            {
                SaveCheckpoint(Math.Max(checkpoint.StepsTaken, 0));
                // STOP covers the whole task tree: a continued read-only child must not outlive the
                // parent that asked for its answer.
                await CancelLiveContinuedChildrenAsync(checkpoint.ContinuedChildTaskIds, onStep).ConfigureAwait(false);
                return Finish(new CodingAgentResult
                {
                    Cancelled = true,
                    StepsTaken = checkpoint.StepsTaken,
                    Summary = "Stopped. The coding checkpoint was preserved."
                });
            }
            catch
            {
                SaveCheckpoint(Math.Max(checkpoint.StepsTaken, 0));
                throw;
            }
            finally
            {
                // Timing is evidence, never a failure source: a logging fault must not mask the outcome.
                try
                {
                    FlushStepTiming();
                    onStep?.Invoke(phaseTimer.RenderRunTotal());
                }
                catch (Exception) { }
                // The browser is a process with cookies, DOM and navigation state.
                // A different coding task must never inherit it on completion,
                // cancellation, pause or verifier-repair handoff.
                await BrowserDevToolsService.Current.ReleaseTaskAsync(resolvedSessionId)
                    .ConfigureAwait(false);
            }
        }

        private static string BuildPrompt(
            string task,
            List<string> log,
            string compiledContext,
            string restoreHint,
            string plan,
            bool verificationRequired,
            bool verificationSatisfied,
            bool resumed,
            int recentEvidenceCharacters,
            string durableMemory,
            string recoveryDigest = "",
            string permissionMode = CodingAgentPermissionMode.Bypass)
        {
            var sb = new StringBuilder();
            sb.Append("Task: ").Append(task).Append("\n\n");
            string normalizedPermissionMode = CodingAgentPermissionMode.Normalize(permissionMode);
            if (normalizedPermissionMode == CodingAgentPermissionMode.Plan)
                sb.Append("CODING MODE: PLAN (READ ONLY). File writes, package restore, browser mutations, and shell commands that are not mechanically classified as safe read-only commands are refused before execution. Read-only shell commands must be a single command with no chaining, substitution, or redirection.\n\n");
            else if (normalizedPermissionMode == CodingAgentPermissionMode.Ask)
                sb.Append("CODING MODE: ASK. Every arbitrary shell command and file mutation must be explicitly approved by the user for its exact content before it can run.\n\n");
            if (resumed)
                sb.Append("This execution may have resumed after an interruption. Treat repository state, git diff and persisted observations as authoritative; do not redo completed work merely because inference context is fresh.\n\n");
            if (!string.IsNullOrWhiteSpace(compiledContext))
                sb.Append(compiledContext).Append("\n\n");
            if (!string.IsNullOrWhiteSpace(restoreHint))
                sb.Append(restoreHint).Append("\n\n");
            if (!string.IsNullOrWhiteSpace(plan))
                sb.Append("Current implementation plan (durable; update it when evidence changes the approach):\n")
                    .Append(plan).Append("\n\n");

            if (!string.IsNullOrWhiteSpace(durableMemory))
                sb.Append("Durable task memory (Virtual Context - facts you already established in earlier steps; do not re-derive them by repeating tool calls):\n")
                    .Append(durableMemory).Append("\n\n");

            if (!string.IsNullOrWhiteSpace(recoveryDigest))
                sb.Append(recoveryDigest).Append("\n\n");

            IReadOnlyList<string> recent = CodingAgentObservationPolicy.Recent(
                log, MaxLogEntriesInPrompt, Math.Max(0, recentEvidenceCharacters));
            int omitted = log.Count - recent.Count;
            if (omitted > 0)
                sb.Append('(').Append(omitted).Append(" earlier observation(s) are retained in the durable checkpoint; only the newest are expanded here.)\n\n");
            sb.Append("Recent execution evidence (").Append(log.Count).Append(" total observation(s)):\n");
            sb.Append(log.Count == 0 ? "(none yet)" : string.Join("\n---\n", recent));
            if (verificationRequired && !verificationSatisfied)
                sb.Append("\n\nCompletion gate: testable code changed and no successful discovered verification command has been observed since that change.");
            sb.Append("\n\nLarge tool/file evidence is never intentionally discarded: observations may contain an artifactId. Use read_artifact/search_artifact/list_artifacts instead of repeating expensive commands or guessing from a truncated preview.");
            sb.Append("\n\nUse repository and execution evidence before guessing. What is the next single JSON action?");
            return sb.ToString();
        }

        private static string BuildRestoreHint(string root, IReadOnlyCollection<string> grantedCapabilities)
        {
            if (grantedCapabilities.Count == 0) return string.Empty;
            IReadOnlyList<CodingPackageRestoreDiscovery.RestoreCommand> discovered = CodingPackageRestoreDiscovery.Discover(root)
                .Where(item => grantedCapabilities.Contains(item.RequiredCapability, StringComparer.OrdinalIgnoreCase))
                .Take(16)
                .ToList();
            if (discovered.Count == 0) return string.Empty;
            return "PACKAGE RESTORE AUTHORITY (deterministic; command text is controlled by PaRuX):\n"
                + string.Join("\n", discovered.Select(item => "- path=" + item.RelativeRoot
                    + "; ecosystem=" + item.Ecosystem
                    + "; capability=" + item.RequiredCapability
                    + (item.MayExecuteProjectCode ? "; project-code-may-run" : "; lifecycle/build scripts disabled or fetch-only")));
        }


        /// <summary>Records the exact pre/post state of a file the agent writes so a failed verification can revert only its own changes.</summary>
        private static void TrackOwnWrite(string? orchestratorTaskId, string root, string? relativePath, bool before)
        {
            if (orchestratorTaskId == null || string.IsNullOrWhiteSpace(relativePath)) return;
            try
            {
                if (before) CodingTaskChangeTracker.Current.BeforeWrite(orchestratorTaskId, root, relativePath);
                else CodingTaskChangeTracker.Current.AfterWrite(orchestratorTaskId, root, relativePath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }

        private static async Task<CodingExecutionOutcome> ExecuteAsync(
            string root, string sessionId, string? orchestratorTaskId, string task,
            CodingAgentAction action, string permissionMode, bool hasExactCommandApproval,
            CodingAgentCheckpoint checkpoint, IReadOnlyList<CodingAgentMcpToolDefinition> mcpTools,
            CancellationToken ct)
        {
            if (CodingAgentPermissionPolicy.IsRefusedInPlanMode(permissionMode, action.Action, action.Command))
                return Outcome(action.Action + " (refused in Plan mode)",
                    "Plan mode is read-only. This action is blocked before execution; switch to Ask or Bypass mode to allow it.");

            if ((action.Action ?? string.Empty).StartsWith("browser_", StringComparison.OrdinalIgnoreCase)
                && !BrowserDevToolsService.Current.TryClaimTask(sessionId))
                return Outcome("browser tool (task isolation)", 
                    "Dedicated developer browser is in use by another coding task or has not shut down safely. "
                    + "No navigation, DOM inspection or browser action was executed.");

            switch ((action.Action ?? string.Empty).ToLowerInvariant())
            {
                case "mcp_tool_call":
                {
                    if (orchestratorTaskId == null)
                        return Outcome("mcp_tool_call (refused)",
                            "External MCP tools require a durable Core coding task with an explicit mcp.external grant.");
                    if (!CodingAgentMcpToolPolicy.TryValidateCall(
                            mcpTools, action.McpServerId, action.McpToolName, action.McpArguments, out string error))
                        return Outcome("mcp_tool_call (refused)", error);

                    string result = await CodingAgentMcpToolClient.Current.CallAsync(
                        orchestratorTaskId, action.McpServerId, action.McpToolName,
                        action.McpArguments!.Value, ct).ConfigureAwait(false);
                    return new CodingExecutionOutcome(
                        "mcp_tool_call " + action.McpToolName + " via the selected external server",
                        CodingAgentMcpToolPolicy.UntrustedResult(result),
                        MadeMaterialProgress: true,
                        WorkspaceMutated: true);
                }
                case "read_file":
                {
                    string full = ResolveSafePath(root, action.Path);
                    if (!File.Exists(full)) return Outcome("read_file \"" + action.Path + "\"", "(file does not exist)");
                    if (new FileInfo(full).Length > MaxWholeFileReadBytes)
                        return Outcome("read_file \"" + action.Path + "\"",
                            "(file exceeds the bounded whole-file read limit; use read_file_range to inspect it incrementally)");
                    string content = await File.ReadAllTextAsync(full, ct).ConfigureAwait(false);
                    if (content.Length > MaxObservationChars)
                    {
                        string relevant = CodingAgentTools.ReadTaskRelevantFileLines(full, task);
                        CodingAgentArtifactStore.ArtifactMetadata artifact = CodingAgentArtifactStore.SaveText(
                            sessionId, "workspace-file", action.Path, content);
                        string preview = string.IsNullOrWhiteSpace(relevant)
                            ? Truncate(content, MaxObservationChars)
                            : "Task-relevant numbered excerpts:\n" + Truncate(relevant, MaxObservationChars - 500);
                        return Outcome("read_file \"" + action.Path + "\"", preview
                            + "\n[FULL FILE artifactId=" + artifact.Id + "; chars=" + artifact.Characters
                            + "; lines=" + artifact.Lines + "; sha256=" + artifact.Sha256 + "]");
                    }
                    return Outcome("read_file \"" + action.Path + "\"", PersistEvidence(sessionId, "workspace-file", action.Path, content));
                }
                case "read_file_range":
                {
                    string full = ResolveSafePath(root, action.Path);
                    if (!File.Exists(full)) return Outcome("read_file_range \"" + action.Path + "\"", "(file does not exist)");
                    int offset = Math.Max(0, action.Offset ?? 0);
                    int length = action.Length ?? CodingAgentArtifactStore.DefaultReadCharacters;
                    CodingAgentArtifactStore.ReadResult? page = CodingAgentArtifactStore.ReadFileRange(full, offset, length);
                    return page == null
                        ? Outcome("read_file_range \"" + action.Path + "\"", "(could not read file)")
                        : Outcome("read_file_range \"" + action.Path + "\" @" + page.Offset + " +" + page.ReturnedCharacters,
                            RenderPage(page));
                }
                case "read_file_lines":
                {
                    string full = ResolveSafePath(root, action.Path);
                    if (!File.Exists(full)) return Outcome("read_file_lines \"" + action.Path + "\"", "(file does not exist)");
                    int firstLine = Math.Max(1, action.Line ?? 1);
                    int count = Math.Clamp(action.Count ?? 60, 1, 100);
                    string rendered = CodingAgentTools.ReadNumberedFileLines(full, firstLine, count);
                    return Outcome("read_file_lines \"" + action.Path + "\" line " + firstLine + " +" + count,
                        PersistEvidence(sessionId, "workspace-file-lines", action.Path, rendered.Length == 0 ? "(no lines at this position)" : rendered));
                }
                case "batch_read":
                {
                    List<CodingBatchReadItem> requests = action.Requests ?? new List<CodingBatchReadItem>();
                    IReadOnlyList<CodingBatchReadResult> batch = await CodingAgentReadBatch.ExecuteAsync(
                        requests,
                        async (item, token) =>
                        {
                            var inner = new CodingAgentAction
                            {
                                Action = item.Action, Path = item.Path, Pattern = item.Pattern, Regex = item.Regex,
                                Line = item.Line, Count = item.Count, Offset = item.Offset, Length = item.Length
                            };
                            CodingExecutionOutcome one = await ExecuteAsync(root, sessionId, orchestratorTaskId,
                                task, inner, permissionMode, false, checkpoint, mcpTools, token).ConfigureAwait(false);
                            return new CodingBatchReadResult(one.Description, one.Observation);
                        }, ct).ConfigureAwait(false);
                    return Outcome("batch_read (" + batch.Count + " reads)", CodingAgentReadBatch.Render(batch));
                }
                case "list_artifacts":
                {
                    IReadOnlyList<CodingAgentArtifactStore.ArtifactMetadata> artifacts = CodingAgentArtifactStore.List(sessionId);
                    string rendered = artifacts.Count == 0
                        ? "(no persisted artifacts for this coding session)"
                        : string.Join("\n", artifacts.Take(30).Select(a =>
                            a.Id + " | " + a.Kind + " | " + a.Characters + " chars / " + a.Lines + " lines | " + a.Source
                            + (a.WasTruncatedAtStorage ? " | STORAGE-CAPPED" : string.Empty)));
                    return Outcome("list_artifacts (" + artifacts.Count + ")", rendered);
                }
                case "read_artifact":
                {
                    int offset = Math.Max(0, action.Offset ?? 0);
                    int length = action.Length ?? CodingAgentArtifactStore.DefaultReadCharacters;
                    CodingAgentArtifactStore.ReadResult? page = CodingAgentArtifactStore.Read(sessionId, action.ArtifactId, offset, length);
                    return page == null
                        ? Outcome("read_artifact \"" + action.ArtifactId + "\"", "(artifact not found or corrupt)")
                        : Outcome("read_artifact \"" + action.ArtifactId + "\" @" + page.Offset + " +" + page.ReturnedCharacters,
                            RenderPage(page));
                }
                case "search_artifact":
                {
                    string? targetArtifactId = action.ArtifactId;
                    if (string.IsNullOrWhiteSpace(targetArtifactId))
                    {
                        IReadOnlyList<CodingAgentArtifactStore.ArtifactMetadata> list = CodingAgentArtifactStore.List(sessionId);
                        targetArtifactId = list.FirstOrDefault()?.Id;
                    }
                    if (string.IsNullOrWhiteSpace(targetArtifactId))
                        return Outcome("search_artifact", "(no artifacts available in session)");

                    CodingAgentArtifactStore.SearchResult? search = CodingAgentArtifactStore.Search(
                        sessionId, targetArtifactId, action.Pattern, action.Regex, action.Count ?? 40);
                    if (search == null)
                        return Outcome("search_artifact \"" + targetArtifactId + "\"", "(artifact/query invalid, artifact not found or corrupt)");
                    string matches = search.Matches.Count == 0
                        ? "(no matches)"
                        : string.Join("\n", search.Matches.Select(m => m.LineNumber + ": " + m.Line));
                    return Outcome("search_artifact \"" + targetArtifactId + "\" for \"" + Truncate(action.Pattern, 120) + "\" (" + search.Matches.Count + ")",
                        matches);
                }
                case "write_file":
                {
                    string full = ResolveSafePath(root, action.Path);
                    // Live suite: the agent rewrote the same result.json with identical content 14 times
                    // until the task timed out. An identical write changes nothing: say so and count it
                    // as no progress, so the non-progress bound stops the loop.
                    if (File.Exists(full))
                    {
                        string existing;
                        try { existing = await File.ReadAllTextAsync(full, ct).ConfigureAwait(false); }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { existing = "\u0000"; }
                        if (string.Equals(existing, action.Content ?? string.Empty, StringComparison.Ordinal))
                            return Outcome("write_file \"" + action.Path + "\" (unchanged)",
                                "(no change: the file already contains exactly this content. Do not write it again; check that "
                                + "the path is the one the task requires, run the verification, or report done.)");
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(full) ?? root);
                    TrackOwnWrite(orchestratorTaskId, root, action.Path, before: true);
                    await File.WriteAllTextAsync(full, action.Content ?? string.Empty, new UTF8Encoding(false), ct).ConfigureAwait(false);
                    TrackOwnWrite(orchestratorTaskId, root, action.Path, before: false);
                    return Outcome("write_file \"" + action.Path + "\" (" + (action.Content?.Length ?? 0) + " chars)",
                        "(written)", IsTestableCodePath(action.Path), progress: true);
                }
                case "list_directory":
                {
                    string full = ResolveSafePath(root, action.Path ?? string.Empty);
                    if (!Directory.Exists(full)) return Outcome("list_directory \"" + action.Path + "\"", "(directory does not exist)");
                    List<string> entries = Directory.EnumerateFileSystemEntries(full)
                        .Take(MaxDirectoryEntries + 1)
                        .Select(p => Path.GetFileName(p) + (Directory.Exists(p) ? "/" : string.Empty))
                        .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    bool truncated = entries.Count > MaxDirectoryEntries;
                    if (truncated) entries.RemoveAt(entries.Count - 1);
                    string rendered = string.Join("\n", entries)
                        + (truncated ? "\n… (directory listing capped at " + MaxDirectoryEntries + " entries)" : string.Empty);
                    return Outcome("list_directory \"" + action.Path + "\"", PersistEvidence(sessionId, "directory-list", action.Path ?? string.Empty, rendered));
                }
                case "run_command":
                {
                    if (string.IsNullOrWhiteSpace(action.Command)) return Outcome("run_command (empty)", "(no command given)");
                    string? denyReason = CodingAgentTools.DenyReasonFor(action.Command);
                    if (denyReason != null)
                        return Outcome("run_command \"" + Truncate(action.Command, 200) + "\" (denied)", denyReason);

                    if (permissionMode == CodingAgentPermissionMode.Ask && !hasExactCommandApproval)
                    {
                        if (string.IsNullOrWhiteSpace(orchestratorTaskId))
                            return Outcome("run_command (approval unavailable)",
                                "Ask mode requires a durable orchestrator task so command approval can pause and resume safely. No command was run.");

                        const string approve = CodingAgentPermissionPolicy.ApproveExactCommandOnce;
                        const string decline = CodingAgentPermissionPolicy.DeclineCommand;
                        AgentUserQuestionDto question = await AgentOrchestratorClient.Current.AskUserQuestionAsync(
                            orchestratorTaskId,
                            new AskAgentUserQuestionRequest
                            {
                                Question = "Approve running this exact shell command once?\n\n" + action.Command,
                                Options = new List<string> { approve, decline },
                                AllowOther = false
                            },
                            ct).ConfigureAwait(false);
                        CodingAgentCommandApproval.Stage(checkpoint, question.Id, action.Command);
                        return new CodingExecutionOutcome(
                            "run_command paused for exact-command approval -> " + question.Id,
                            "The command is pending explicit approval. It has not run. Approval is valid only for this exact command text and one execution.",
                            MadeMaterialProgress: true,
                            UserQuestionId: question.Id);
                    }

                    string? changesBefore = await CodingAgentTools.TestableChangeFingerprintAsync(root, IsTestableCodePath, ct).ConfigureAwait(false);
                    // Repeat epochs follow any visible Git-worktree file change, not only source-code edits.
                    // An unavailable fingerprint advances the epoch conservatively so stale reads are not trusted.
                    string? workspaceBefore = await CodingAgentTools.TestableChangeFingerprintAsync(
                        root, static _ => true, ct).ConfigureAwait(false);
                    // A command newly invented in a modified manifest after
                    // dispatch cannot authorize its own fake green check.
                    bool discoveredVerifierBeforeRun = IsVerificationCommand(root, action.Command);
                    string? verifierManifestBeforeRun = discoveredVerifierBeforeRun
                        ? await CodingVerificationManifestPolicy.CaptureAsync(root, ct).ConfigureAwait(false)
                        : null;
                    IReadOnlyList<string> programCandidatesBefore = CodingVerificationDiscovery.WorkingDirectoryProgramCandidates(root, action.Command);
                    // The scripts an npm run executes are fixed when it starts; a script could restore
                    // package.json on its way out, so the pre-run bodies are captured here (review finding).
                    string? npmScript = CodingNpmScriptRunner.ScriptName(action.Command);
                    string? npmLifecycleBefore = npmScript == null ? null
                        : CodingNpmScriptRunner.LifecycleFingerprint(ReadPackageJson(root), npmScript);
                    WindowsCodingSandbox.Result sandbox = await WindowsCodingSandbox
                        .RunAsync(root, action.Command, ct, TimeSpan.FromMinutes(10), WindowsCodingSandbox.NetworkMode.None)
                        .ConfigureAwait(false);
                    await CodingAgentTools.RemoveNuGetMigrationMarkersAsync(root, ct).ConfigureAwait(false);
                    string? changesAfter = await CodingAgentTools.TestableChangeFingerprintAsync(root, IsTestableCodePath, ct).ConfigureAwait(false);
                    string? workspaceAfter = await CodingAgentTools.TestableChangeFingerprintAsync(
                        root, static _ => true, ct).ConfigureAwait(false);
                    bool workspaceMutated = workspaceBefore == null || workspaceAfter == null
                        || !string.Equals(workspaceBefore, workspaceAfter, StringComparison.Ordinal);
                    // An unavailable fingerprint cannot prove that the command left testable
                    // source unchanged, so discard old check evidence conservatively.
                    bool commandMutatedCode = changesBefore == null || changesAfter == null
                        || !string.Equals(changesBefore, changesAfter, StringComparison.Ordinal);
                    bool verification = discoveredVerifierBeforeRun
                        && IsVerificationCommand(root, action.Command);
                    if (verification)
                    {
                        string? manifestAfter = await CodingVerificationManifestPolicy
                            .CaptureAsync(root, ct).ConfigureAwait(false);
                        verification = verifierManifestBeforeRun != null
                            && string.Equals(verifierManifestBeforeRun, manifestAfter, StringComparison.Ordinal);
                    }
                    if (sandbox.ExitCode == 0 && (verification || !commandMutatedCode))
                    {
                        (List<string> Changed, HashSet<string> Tracked)? scope =
                            await CodingAgentTools.ChangeScopeAsync(root, ct).ConfigureAwait(false);
                        if (!verification)
                            verification = scope != null && CodingVerificationDiscovery.IsScopedVerificationCommand(
                                root, action.Command, scope.Value.Changed, path => scope.Value.Tracked.Contains(path));
                        // cmd.exe searches the working directory before PATH: a program resolved from
                        // there must be a tracked, unchanged file, otherwise the "verification" may
                        // have run an agent-written stub.
                        if (verification && CodingVerificationDiscovery.UsesUntrustedWorkingDirectoryProgram(
                                root, action.Command,
                                scope?.Changed ?? (IReadOnlyCollection<string>)Array.Empty<string>(),
                                path => scope != null && scope.Value.Tracked.Contains(path), programCandidatesBefore))
                            verification = false;
                        // Review finding: `npm test` runs whatever package.json says, so an agent could
                        // redefine "test" as "exit 0". It counts only when the pre/main/post scripts that
                        // ran (captured before the run) AND the file afterwards match HEAD exactly.
                        if (verification && npmScript != null)
                        {
                            (int headExit, string headPackage) = await CodingAgentTools.RunGitAsync(
                                root, ct, includeStderr: false, "show", "HEAD:package.json").ConfigureAwait(false);
                            string? headLifecycle = headExit == 0 ? CodingNpmScriptRunner.LifecycleFingerprint(headPackage, npmScript) : null;
                            string? after = CodingNpmScriptRunner.LifecycleFingerprint(ReadPackageJson(root), npmScript);
                            if (headLifecycle == null
                                || !string.Equals(headLifecycle, npmLifecycleBefore, StringComparison.Ordinal)
                                || !string.Equals(headLifecycle, after, StringComparison.Ordinal))
                                verification = false;
                        }
                    }
                    if (!verification && sandbox.ExitCode == 0)
                    {
                        (List<string> Changed, HashSet<string> Tracked)? syntaxScope =
                            await CodingAgentTools.ChangeScopeAsync(root, ct).ConfigureAwait(false);
                        if (syntaxScope != null && CodingVerificationDiscovery.IsNodeSyntaxCheck(action.Command, syntaxScope.Value.Changed))
                            verification = true;
                    }
                    // Exit code 0 alone is not verification: a run that executed no tests also exits 0 (CF-06).
                    // Unrecognised output keeps the exit-code rule, and the evidence says that it does.
                    CodingVerificationCommandPolicy.TestRunEvidence? testEvidence = verification
                        ? CodingVerificationCommandPolicy.ParseTestRunSummary(sandbox.Output)
                        : null;
                    bool executedNoTests = testEvidence?.ExecutedNoTests == true;
                    string observation = sandbox.Error == null
                        ? sandbox.Output
                        : sandbox.Error + (string.IsNullOrWhiteSpace(sandbox.Output) ? string.Empty : "\n" + sandbox.Output);
                    if (testEvidence != null) observation += "\n" + testEvidence.Note;
                    return Outcome(
                        "run_command [AppContainer:no-network] \"" + Truncate(action.Command, 200) + "\" (exit " + sandbox.ExitCode + ")"
                            + (sandbox.SetupTimings == null ? string.Empty : " [setup " + sandbox.SetupTimings + "]"),
                        PersistEvidence(sessionId, verification ? "verification-output" : "command-output", action.Command ?? string.Empty, observation),
                        mutated: commandMutatedCode,
                        verificationAttempted: verification,
                        verificationSucceeded: verification && sandbox.ExitCode == 0 && !executedNoTests,
                        progress: sandbox.ExitCode == 0 && !executedNoTests,
                        infrastructureBlocked: WindowsCodingSandbox.IsInfrastructureUnavailable(sandbox),
                        commandExitCode: sandbox.ExitCode,
                        workspaceMutated: workspaceMutated);
                }
                case "restore_packages":
                {
                    if (string.IsNullOrWhiteSpace(orchestratorTaskId))
                        return Outcome("restore_packages (denied)", "Package restore is unavailable outside a durable task with explicit permission grants.");
                    CodingPackageRestoreResult restoredPackages = await CodingPackageRestoreService.RestoreAsync(
                        root, orchestratorTaskId, action.Path, action.Ecosystem, ct).ConfigureAwait(false);
                    string source = "restore " + restoredPackages.Ecosystem + " @ " + restoredPackages.RelativeRoot;
                    string observation = restoredPackages.Success
                        ? restoredPackages.Output
                        : (restoredPackages.Error ?? "Package restore failed")
                            + (string.IsNullOrWhiteSpace(restoredPackages.Output) ? string.Empty : "\n" + restoredPackages.Output);
                    return Outcome(
                        "restore_packages " + restoredPackages.Ecosystem + " @ " + restoredPackages.RelativeRoot
                            + " [AppContainer:InternetClient] (exit " + restoredPackages.ExitCode + ")",
                        PersistEvidence(sessionId, "package-restore-output", source, observation),
                        progress: restoredPackages.Success);
                }
                case "replace_range":
                {
                    string full = ResolveSafePath(root, action.Path);
                    if (!File.Exists(full)) return Outcome("replace_range \"" + action.Path + "\"", "(file does not exist - use create_file for a new file)");
                    TrackOwnWrite(orchestratorTaskId, root, action.Path, before: true);
                    (bool success, bool changed, string? error, string? note) =
                        await CodingAgentTools.TryApplyReplaceRangeFileAsync(full, action.OldText, action.NewText, ct)
                            .ConfigureAwait(false);
                    TrackOwnWrite(orchestratorTaskId, root, action.Path, before: false);
                    if (!success) return Outcome("replace_range \"" + action.Path + "\"", "(failed: " + error + ")");
                    if (!changed)
                        return Outcome("replace_range \"" + action.Path + "\" (unchanged)",
                            "(no change: " + (note ?? "replacement produces no content change")
                                + "; do not repeat this replacement. Run the required checks before reporting completion.)");
                    // A tolerated near-miss is reported, never silently absorbed: the agent - and
                    // the human reading the log - must see that the text did not match exactly.
                    return Outcome("replace_range \"" + action.Path + "\"",
                        note == null ? "(applied)" : "(applied - " + note + ")",
                        IsTestableCodePath(action.Path), progress: true);
                }
                case "create_file":
                {
                    string full = ResolveSafePath(root, action.Path);
                    if (File.Exists(full)) return Outcome("create_file \"" + action.Path + "\"", "(already exists - use write_file or replace_range to modify it)");
                    Directory.CreateDirectory(Path.GetDirectoryName(full) ?? root);
                    TrackOwnWrite(orchestratorTaskId, root, action.Path, before: true);
                    await File.WriteAllTextAsync(full, action.Content ?? string.Empty, new UTF8Encoding(false), ct).ConfigureAwait(false);
                    TrackOwnWrite(orchestratorTaskId, root, action.Path, before: false);
                    return Outcome("create_file \"" + action.Path + "\"", "(created)", IsTestableCodePath(action.Path), progress: true);
                }
                case "delete_file":
                {
                    string full = ResolveSafePath(root, action.Path);
                    if (!File.Exists(full)) return Outcome("delete_file \"" + action.Path + "\"", "(file does not exist)");
                    bool testable = IsTestableCodePath(action.Path);
                    TrackOwnWrite(orchestratorTaskId, root, action.Path, before: true);
                    File.Delete(full);
                    TrackOwnWrite(orchestratorTaskId, root, action.Path, before: false);
                    return Outcome("delete_file \"" + action.Path + "\"", "(deleted)", testable, progress: true);
                }
                case "rename_file":
                {
                    string fromFull = ResolveSafePath(root, action.Path);
                    string toFull = ResolveSafePath(root, action.NewPath);
                    if (!File.Exists(fromFull)) return Outcome("rename_file \"" + action.Path + "\" -> \"" + action.NewPath + "\"", "(source file does not exist)");
                    if (File.Exists(toFull)) return Outcome("rename_file \"" + action.Path + "\" -> \"" + action.NewPath + "\"", "(a file already exists at the destination)");
                    Directory.CreateDirectory(Path.GetDirectoryName(toFull) ?? root);
                    TrackOwnWrite(orchestratorTaskId, root, action.Path, before: true);
                    TrackOwnWrite(orchestratorTaskId, root, action.NewPath, before: true);
                    File.Move(fromFull, toFull);
                    TrackOwnWrite(orchestratorTaskId, root, action.Path, before: false);
                    TrackOwnWrite(orchestratorTaskId, root, action.NewPath, before: false);
                    return Outcome("rename_file \"" + action.Path + "\" -> \"" + action.NewPath + "\"",
                        "(renamed)", IsTestableCodePath(action.Path) || IsTestableCodePath(action.NewPath), progress: true);
                }
                case "grep_search":
                {
                    if (string.IsNullOrWhiteSpace(action.Pattern)) return Outcome("grep_search (empty pattern)", "(no pattern given)");
                    List<CodingAgentTools.GrepMatch> matches = CodingAgentTools.Grep(root, action.Pattern, action.Regex, ct);
                    if (matches.Count == 0 && !action.Regex && (action.Pattern.Contains('|') || action.Pattern.Contains(".*") || action.Pattern.Contains(@"\b")))
                    {
                        var regexMatches = CodingAgentTools.Grep(root, action.Pattern, true, ct);
                        if (regexMatches.Count > 0)
                        {
                            matches = regexMatches;
                            action.Regex = true;
                        }
                    }
                    string rendered = matches.Count == 0
                        ? "(no matches)" + (!action.Regex && action.Pattern.Contains('|')
                            ? " Pattern was treated literally (regex=false); use regex=true for | alternation." : string.Empty)
                        : string.Join("\n", matches.Select(m => m.RelativePath + ":" + m.LineNumber + ": " + m.Line));
                    return Outcome("grep_search \"" + Truncate(action.Pattern, 120) + "\" (" + matches.Count + " match(es))",
                        PersistEvidence(sessionId, "grep-results", action.Pattern, rendered));
                }
                case "glob_search":
                {
                    if (string.IsNullOrWhiteSpace(action.Pattern)) return Outcome("glob_search (empty pattern)", "(no pattern given)");
                    List<string> files = CodingAgentTools.Glob(root, action.Pattern, ct);
                    string rendered = files.Count == 0 ? "(no matches)" : string.Join("\n", files);
                    return Outcome("glob_search \"" + action.Pattern + "\" (" + files.Count + " file(s))",
                        PersistEvidence(sessionId, "glob-results", action.Pattern, rendered));
                }
                case "git_status":
                {
                    (int exitCode, string output) = await CodingAgentTools.RunGitAsync(root, ct, "status", "--porcelain=v1", "--branch").ConfigureAwait(false);
                    return Outcome("git_status (exit " + exitCode + ")", PersistEvidence(sessionId, "git-status", "git status", output));
                }
                case "git_diff":
                {
                    (int exitCode, string output) = string.IsNullOrWhiteSpace(action.Path)
                        ? await CodingAgentTools.RunGitDiffAsync(root, ct).ConfigureAwait(false)
                        : await CodingAgentTools.RunGitDiffAsync(root, ct, "--", action.Path).ConfigureAwait(false);
                    bool hasChanges = exitCode == 0 && !string.IsNullOrWhiteSpace(output);
                    string evidence = hasChanges ? output : "(no changes)";
                    return Outcome("git_diff" + (string.IsNullOrWhiteSpace(action.Path) ? string.Empty : " \"" + action.Path + "\"") + " (exit " + exitCode + ")",
                        PersistEvidence(sessionId, "git-diff", string.IsNullOrWhiteSpace(action.Path) ? "working-tree" : action.Path, evidence), progress: hasChanges);
                }
                case "git_log":
                {
                    int count = action.Count is > 0 and <= 50 ? action.Count.Value : 10;
                    (int exitCode, string output) = await CodingAgentTools.RunGitAsync(root, ct, "log", "-n", count.ToString(), "--oneline").ConfigureAwait(false);
                    return Outcome("git_log (exit " + exitCode + ")", PersistEvidence(sessionId, "git-log", "git log -n " + count, output));
                }
                case "web_search":
                {
                    CodingWebSearchClient.Outcome found = await CodingWebSearchClient
                        .SearchAsync(action.Search, ct).ConfigureAwait(false);
                    return Outcome("web_search (" + (found.Success ? "ok" : "unavailable") + ")",
                        PersistEvidence(sessionId, "web-search", action.Search, found.Evidence));
                }
                case "browser_navigate":
                    return BrowserOutcome(sessionId, "browser_navigate", await BrowserDevToolsService.Current.NavigateAsync(action.Url, ct).ConfigureAwait(false), progressOnSuccess: true);
                case "browser_dom":
                    return BrowserOutcome(sessionId, "browser_dom", await BrowserDevToolsService.Current.GetDomAsync(ct).ConfigureAwait(false), pageDerived: true);
                case "browser_console":
                    return BrowserOutcome(sessionId, "browser_console", await BrowserDevToolsService.Current.GetConsoleAsync(ct).ConfigureAwait(false), pageDerived: true);
                case "browser_network":
                    return BrowserOutcome(sessionId, "browser_network", await BrowserDevToolsService.Current.GetNetworkAsync(ct).ConfigureAwait(false), pageDerived: true);
                case "browser_eval":
                    return BrowserOutcome(sessionId, "browser_eval", await BrowserDevToolsService.Current.EvaluateAsync(action.Expression, ct).ConfigureAwait(false), progressOnSuccess: true, pageDerived: true);
                case "browser_click":
                {
                    string script = BrowserDomActionScriptBuilder.BuildClick(action.Selector);
                    BrowserDevToolsService.BrowserResult result = await BrowserDevToolsService.Current.EvaluateUiActionAsync(script, ct).ConfigureAwait(false);
                    return BrowserOutcome(sessionId, "browser_click \"" + Truncate(action.Selector, 160) + "\"", result, progressOnSuccess: true);
                }
                case "browser_type":
                {
                    string script = BrowserDomActionScriptBuilder.BuildType(action.Selector, action.Text);
                    BrowserDevToolsService.BrowserResult result = await BrowserDevToolsService.Current.EvaluateUiActionAsync(script, ct).ConfigureAwait(false);
                    return BrowserOutcome(sessionId, "browser_type \"" + Truncate(action.Selector, 160) + "\"", result, progressOnSuccess: true);
                }
                case "browser_assert":
                    return BrowserOutcome(sessionId, "browser_assert [" + action.Target + "]",
                        await BrowserDevToolsService.Current.AssertDomAsync(
                            action.Selector, action.Target, action.Text, ct).ConfigureAwait(false),
                        progressOnSuccess: true);
                case "browser_screenshot":
                    return BrowserOutcome(sessionId, "browser_screenshot", await BrowserDevToolsService.Current.InspectScreenshotAsync(action.Goal, ct).ConfigureAwait(false), pageDerived: true);
                case "browser_reset":
                    await BrowserDevToolsService.Current.ResetAsync(ct).ConfigureAwait(false);
                    return Outcome("browser_reset", "Dedicated PaRuX browser session reset.", progress: true);
                case "ask_user":
                {
                    if (string.IsNullOrWhiteSpace(orchestratorTaskId))
                        return Outcome("ask_user (denied)", "Durable user questions are available only inside an orchestrator coding task.");
                    AgentUserQuestionDto question = await AgentOrchestratorClient.Current.AskUserQuestionAsync(
                        orchestratorTaskId,
                        new AskAgentUserQuestionRequest
                        {
                            Question = action.Question,
                            Options = action.Options?.ToList() ?? new List<string>(),
                            AllowOther = action.AllowOther
                        },
                        ct).ConfigureAwait(false);
                    return new CodingExecutionOutcome(
                        "ask_user -> " + question.Id,
                        "Paused for user input: " + question.Question,
                        MadeMaterialProgress: true,
                        UserQuestionId: question.Id);
                }
                case "delegate_task":
                {
                    if (string.IsNullOrWhiteSpace(orchestratorTaskId))
                        return Outcome("delegate_task (denied)", "Self-delegation is available only inside a durable orchestrator coding task.");
                    if (action.ContinueParent)
                    {
                        string? refusal = ContinuationRefusal(
                            new[] { (action.Role, (IReadOnlyList<string>?)action.Capabilities) });
                        if (refusal != null) return Outcome("delegate_task (continueParent rejected)", refusal);
                    }
                    CodingDelegationResult delegated = await CodingAgentDelegationService.DelegateAsync(
                        orchestratorTaskId,
                        action.Goal,
                        action.Role,
                        action.Capabilities,
                        action.ContextRefs,
                        action.MaxRepairAttempts,
                        ct,
                        continueParent: action.ContinueParent).ConfigureAwait(false);
                    if (delegated.Success && delegated.ParentContinued)
                        return new CodingExecutionOutcome(
                            "delegate_task -> " + delegated.ChildTaskId + " (parent continues)",
                            delegated.Message + " Keep working; use collect_children to read its result. done is held until it finishes.",
                            MadeMaterialProgress: true,
                            ContinuedChildTaskIds: delegated.ChildTaskIds);
                    return delegated.Success
                        ? new CodingExecutionOutcome(
                            "delegate_task -> " + delegated.ChildTaskId,
                            delegated.Message,
                            MadeMaterialProgress: true,
                            DelegatedChildTaskId: delegated.ChildTaskId,
                            DelegatedChildTaskIds: delegated.ChildTaskIds)
                        : Outcome("delegate_task (rejected)", delegated.Message);
                }
                case "delegate_tasks":
                {
                    if (string.IsNullOrWhiteSpace(orchestratorTaskId))
                        return Outcome("delegate_tasks (denied)", "Batch self-delegation is available only inside a durable orchestrator coding task.");
                    List<CodingDelegationSpec> specs = (action.Children ?? new List<CodingDelegationActionItem>())
                        .Select(child => new CodingDelegationSpec(
                            child.Goal,
                            child.Role,
                            child.Capabilities,
                            child.ContextRefs,
                            child.MaxRepairAttempts))
                        .ToList();
                    if (action.ContinueParent)
                    {
                        string? refusal = ContinuationRefusal(specs
                            .Select(spec => (spec.Role, (IReadOnlyList<string>?)spec.Capabilities)).ToList());
                        if (refusal != null) return Outcome("delegate_tasks (continueParent rejected)", refusal);
                    }
                    CodingDelegationResult delegated = await CodingAgentDelegationService
                        .DelegateManyAsync(orchestratorTaskId, specs, ct, continueParent: action.ContinueParent).ConfigureAwait(false);
                    if (delegated.Success && delegated.ParentContinued)
                        return new CodingExecutionOutcome(
                            "delegate_tasks -> " + string.Join(", ", delegated.ChildTaskIds ?? Array.Empty<string>()) + " (parent continues)",
                            delegated.Message + " Keep working; use collect_children to read their results. done is held until they finish.",
                            MadeMaterialProgress: true,
                            ContinuedChildTaskIds: delegated.ChildTaskIds);
                    return delegated.Success
                        ? new CodingExecutionOutcome(
                            "delegate_tasks -> " + string.Join(", ", delegated.ChildTaskIds ?? Array.Empty<string>()),
                            delegated.Message,
                            MadeMaterialProgress: true,
                            DelegatedChildTaskId: delegated.ChildTaskId,
                            DelegatedChildTaskIds: delegated.ChildTaskIds)
                        : Outcome("delegate_tasks (rejected)", delegated.Message);
                }
                default:
                    return Outcome("unrecognized action \"" + action.Action + "\" (ignored)", string.Empty);
            }
        }

        private static readonly System.Text.RegularExpressions.Regex ExitCodeInDescription =
            new(@"\(exit (-?\d+)\)", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        /// <summary>
        /// Records the real exit code of an accepted verification command. A later run of the same
        /// command replaces the earlier one (fix, then rerun), so only the latest state per command
        /// counts; a different command that failed still counts as a failure.
        /// </summary>
        internal static void RecordExecutedCheck(CodingAgentCheckpoint checkpoint, string command, int exitCode)
        {
            if (string.IsNullOrWhiteSpace(command)) return;
            string normalized = command.Trim();
            checkpoint.ExecutedChecks.RemoveAll(check => string.Equals(check.Command, normalized, StringComparison.OrdinalIgnoreCase));
            checkpoint.ExecutedChecks.Add(new CodingExecutedCheckRecord { Command = normalized, ExitCode = exitCode, Scope = "." });
            if (checkpoint.ExecutedChecks.Count > CodingAgentCheckpointStore.MaximumExecutedChecks)
                checkpoint.ExecutedChecks.RemoveRange(0, checkpoint.ExecutedChecks.Count - CodingAgentCheckpointStore.MaximumExecutedChecks);
        }

        internal static void RecordExecutedCheck(CodingAgentCheckpoint checkpoint, string command, string description)
        {
            System.Text.RegularExpressions.Match match = ExitCodeInDescription.Match(description ?? string.Empty);
            if (!match.Success || !int.TryParse(match.Groups[1].Value, out int exitCode)) return;
            RecordExecutedCheck(checkpoint, command, exitCode);
        }

        private static async Task<bool> HasChangedScriptAsync(string root, CancellationToken ct)
        {
            (List<string> Changed, HashSet<string> Tracked)? scope = await CodingAgentTools.ChangeScopeAsync(root, ct).ConfigureAwait(false);
            return scope != null && scope.Value.Changed.Any(path => CodingVerificationDiscovery.IsScriptSyntaxCheckable(path));
        }

        /// <summary>Null when a continuing delegation is allowed; otherwise the model-facing reason it is not.</summary>
        private static string? ContinuationRefusal(IReadOnlyList<(string? Role, IReadOnlyList<string>? Capabilities)> children)
        {
            if (!CodingAgentFeatureFlags.ParentContinuationEnabled)
                return "Parent continuation is disabled for this session; delegate without continueParent (the parent pauses).";
            return CodingAgentContinuation.TryValidateRequest(children, out string error)
                ? null
                : "continueParent refused: " + error + ".";
        }

        private static async Task<IReadOnlyDictionary<string, AgentTaskDto?>> FetchChildStatesAsync(
            IReadOnlyList<string> childIds, CancellationToken ct)
        {
            var states = new Dictionary<string, AgentTaskDto?>(StringComparer.Ordinal);
            foreach (string id in childIds)
            {
                ct.ThrowIfCancellationRequested();
                try { states[id] = await AgentOrchestratorClient.Current.GetAsync(id, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) when (ex is InvalidDataException or LocalAiException or IOException or UnauthorizedAccessException or System.Net.Http.HttpRequestException)
                {
                    // A missing record is treated as live by the continuation policy.
                    states[id] = null;
                }
            }
            return states;
        }

        private static async Task CancelLiveContinuedChildrenAsync(IReadOnlyList<string> childIds, Action<string>? onStep)
        {
            if (childIds.Count == 0) return;
            using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            int cancelled = 0;
            foreach (string id in childIds)
            {
                try
                {
                    AgentTaskDto child = await AgentOrchestratorClient.Current.GetAsync(id, bound.Token).ConfigureAwait(false);
                    if (AgentTaskStatus.Terminal.Contains(child.Status)) continue;
                    await AgentOrchestratorClient.Current.CancelAsync(id, bound.Token).ConfigureAwait(false);
                    cancelled++;
                }
                catch (Exception) { /* best effort: Core still owns the child and the user can stop it there */ }
            }
            if (cancelled > 0)
                try { onStep?.Invoke("STOP also cancelled " + cancelled + " running read-only child task(s)."); } catch (Exception) { }
        }

        private static string? ReadOnlyActionKey(CodingAgentAction action)
        {
            string kind = action.Action.ToLowerInvariant();
            // Use only fields consumed by that action. Local models may populate unrelated optional
            // schema fields differently on every turn, even when the executed read is identical.
            return kind switch
            {
                "git_status" or "list_artifacts" => kind,
                "git_diff" or "list_directory" or "read_file" => kind + "|" + action.Path,
                "git_log" => kind + "|" + action.Count,
                "read_file_range" => string.Join("|", kind, action.Path, action.Offset, action.Length),
                "read_file_lines" => string.Join("|", kind, action.Path, action.Line, action.Count),
                "grep_search" => string.Join("|", kind, action.Pattern, action.Regex),
                "glob_search" => kind + "|" + action.Pattern,
                "web_search" => kind + "|" + action.Search,
                "read_artifact" => string.Join("|", kind, action.ArtifactId, action.Offset, action.Length),
                "search_artifact" => string.Join("|", kind, action.ArtifactId, action.Pattern),
                "batch_read" => action.Requests == null ? null
                    : kind + "|" + string.Join(";", action.Requests.Select(CodingAgentReadBatch.Key)),
                _ => null
            };
        }

        private static CodingExecutionOutcome BrowserOutcome(
            string sessionId,
            string description,
            BrowserDevToolsService.BrowserResult result,
            bool progressOnSuccess = false,
            bool pageDerived = false)
        {
            string observation = result.Success
                ? result.Observation
                : (result.Error ?? "Browser action failed") + (string.IsNullOrWhiteSpace(result.Observation) ? string.Empty : "\n" + result.Observation);
            // DOM, console, network, script and screenshot output is authored by the web page.
            if (pageDerived) observation = BrowserEvidenceLabelPolicy.LabelPageDerived(observation);
            return new CodingExecutionOutcome(
                description + (result.Success ? " (ok)" : " (failed)"),
                PersistEvidence(sessionId, "browser-evidence", description, observation),
                MadeMaterialProgress: progressOnSuccess && result.Success,
                BrowserActionSucceeded: result.Success);
        }

        private static string PersistEvidence(string sessionId, string kind, string source, string? evidence)
        {
            string text = evidence ?? string.Empty;
            if (text.Length <= MaxObservationChars) return text;

            CodingAgentArtifactStore.ArtifactMetadata artifact = CodingAgentArtifactStore.SaveText(sessionId, kind, source, text);
            return Truncate(text, MaxObservationChars)
                + "\n[FULL EVIDENCE artifactId=" + artifact.Id
                + "; kind=" + artifact.Kind
                + "; chars=" + artifact.Characters
                + "; lines=" + artifact.Lines
                + "; sha256=" + artifact.Sha256
                + (artifact.WasTruncatedAtStorage ? "; storageCapped=true" : string.Empty)
                + ". Use read_artifact/search_artifact; do not rerun the tool merely to recover truncated output.]";
        }

        private static string RenderPage(CodingAgentArtifactStore.ReadResult page) =>
            "source=" + page.Metadata.Source
            + "; totalChars=" + page.Metadata.Characters
            + "; totalLines=" + page.Metadata.Lines
            + "; sha256=" + page.Metadata.Sha256
            + "; offset=" + page.Offset
            + "; returned=" + page.ReturnedCharacters
            + "; hasMore=" + page.HasMore
            + "\n" + page.Text;

        private static CodingExecutionOutcome Outcome(
            string description,
            string observation,
            bool mutated = false,
            bool verificationAttempted = false,
            bool verificationSucceeded = false,
            bool progress = false,
            bool infrastructureBlocked = false,
            int? commandExitCode = null,
            bool workspaceMutated = false) =>
            new(description, observation, mutated, verificationAttempted, verificationSucceeded, progress,
                infrastructureBlocked, CommandExitCode: commandExitCode,
                WorkspaceMutated: workspaceMutated);

        private static string? ReadPackageJson(string root)
        {
            try { return File.ReadAllText(Path.Combine(root, "package.json")); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
        }

        private static bool IsVerificationCommand(string root, string command)
        {
            if (string.IsNullOrWhiteSpace(command)) return false;
            foreach (CodingVerificationDiscovery.VerificationCommand candidate in CodingVerificationDiscovery.Discover(root))
            {
                if (CodingVerificationCommandPolicy.IsExactMatch(candidate.Command, command))
                    return true;
            }
            return false;
        }

        // One shared predicate decides which edits require deterministic verification (CF-01); see CodingVerificationDiscovery.
        private static bool IsTestableCodePath(string path) =>
            CodingVerificationDiscovery.IsBuildOrTestableCodePath(path);

        private static string ResolveSafePath(string root, string relativePath)
        {
            string combined = Path.GetFullPath(Path.Combine(root, relativePath ?? string.Empty));
            string rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
            StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!combined.StartsWith(rootWithSeparator, comparison)
                && !string.Equals(combined, root, comparison))
                throw new InvalidOperationException("That path would escape the workspace folder.");

            string relative = Path.GetRelativePath(root, combined);
            string current = root;
            foreach (string segment in relative.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (segment == ".") continue;
                current = Path.Combine(current, segment);
                if (!File.Exists(current) && !Directory.Exists(current)) continue;
                try
                {
                    if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                        throw new InvalidOperationException("Workspace file actions may not traverse a symlink or junction.");
                }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
            }
            return combined;
        }

        private static string Truncate(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return s ?? string.Empty;
            return s.Length <= max ? s : s.Substring(0, max) + "\n… (truncated)";
        }

        internal static CodingAgentAction? TryParseAction(string text) => CodingAgentActionJson.TryParse(text);

        internal static CodingAgentAction? TryParseAction(string text, out string parseError) =>
            CodingAgentActionJson.TryParse(text, out parseError);

        private static bool TooLong(string? value, int max) => value != null && value.Length > max;

        internal static bool TryValidateAction(
            [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] CodingAgentAction? action, out string error)
        {
            error = string.Empty;
            if (action == null || string.IsNullOrWhiteSpace(action.Action))
            {
                error = "missing action";
                return false;
            }
            string kind = action.Action.Trim().ToLowerInvariant();
            if (TooLong(action.NewPath, 4096)) { error = "newPath exceeds 4096 characters"; return false; }
            if (TooLong(action.Content, 1_000_000)) { error = "content exceeds 1000000 characters"; return false; }
            if (TooLong(action.OldText, 250_000)) { error = "oldText exceeds 250000 characters"; return false; }
            if (TooLong(action.NewText, 1_000_000)) { error = "newText exceeds 1000000 characters"; return false; }
            if (TooLong(action.Command, 8192)) { error = "command exceeds 8192 characters"; return false; }
            if (TooLong(action.Pattern, 4096)) { error = "pattern exceeds 4096 characters"; return false; }
            if (TooLong(action.ArtifactId, 128)) { error = "artifactId exceeds 128 characters"; return false; }
            if (TooLong(action.McpServerId, 64)) { error = "mcpServerId exceeds 64 characters"; return false; }
            if (TooLong(action.McpToolName, 128)) { error = "mcpToolName exceeds 128 characters"; return false; }
            if (action.McpArguments is JsonElement mcpArguments
                && mcpArguments.GetRawText().Length > CodingAgentMcpToolPolicy.MaximumArgumentCharacters)
            { error = "mcpArguments exceeds 16384 characters"; return false; }
            if (TooLong(action.Summary, 8000)) { error = "summary exceeds 8000 characters"; return false; }
            if (TooLong(action.Url, 4096)) { error = "url exceeds 4096 characters"; return false; }
            if (TooLong(action.Expression, 8000)) { error = "expression exceeds 8000 characters"; return false; }
            if (TooLong(action.Selector, 4096)) { error = "selector exceeds 4096 characters"; return false; }
            if (TooLong(action.Text, 20000)) { error = "text exceeds 20000 characters"; return false; }
            if (TooLong(action.Goal, 4000)) { error = "goal exceeds 4000 characters"; return false; }
            if (TooLong(action.Question, 2000)) { error = "question exceeds 2000 characters"; return false; }
            if (action.Count is < 1 or > 100) { error = "count must be between 1 and 100"; return false; }
            if (action.Offset is < 0 or > 16_777_216) { error = "offset is outside the schema range"; return false; }
            if (action.Length is < 1 or > 64_000) { error = "length must be between 1 and 64000"; return false; }
            if ((kind is "delegate_task" or "delegate_tasks") && action.MaxRepairAttempts is < 0 or > 3) { error = "maxRepairAttempts must be between 0 and 3"; return false; }
            if ((kind is "delegate_task" or "delegate_tasks") && action.Capabilities is { Count: > 32 }) { error = "capabilities exceeds 32 items"; return false; }
            if ((kind is "delegate_task" or "delegate_tasks") && action.Capabilities?.Any(value => value != null && value.Length > 80) == true)
            { error = "a capability exceeds 80 characters"; return false; }
            if ((kind is "delegate_task" or "delegate_tasks") && action.ContextRefs is { Count: > 3 }) { error = "contextRefs exceeds 3 items"; return false; }
            if ((kind is "delegate_task" or "delegate_tasks") && action.ContextRefs != null)
            {
                var seenRefs = new HashSet<string>(StringComparer.Ordinal);
                foreach (string value in action.ContextRefs)
                {
                    if (value is not ("parent-result" or "parent-context" or "parent-repair"))
                    { error = "contextRefs contains an unsupported value"; return false; }
                    if (!seenRefs.Add(value)) { error = "contextRefs contains duplicate values"; return false; }
                }
            }
            if (kind == "ask_user" && action.Options is { Count: > 6 }) { error = "options exceeds 6 items"; return false; }
            if (kind == "ask_user" && action.Options?.Any(value => string.IsNullOrWhiteSpace(value) || value.Length > 240) == true)
            { error = "options contains an empty or overlong value"; return false; }
            if (kind == "ask_user" && action.Options != null
                && action.Options.Count != action.Options.Distinct(StringComparer.OrdinalIgnoreCase).Count())
            { error = "options contains duplicate values"; return false; }

            bool Has(string? value) => !string.IsNullOrWhiteSpace(value);
            bool ValidRole(string? role) => !Has(role) || role is "planner" or "research" or "review" or "documentation" or "testing" or "security" or "coding" or "binary-analysis" or "ui-verification" or "computer-use";
            if ((kind is "delegate_task" or "delegate_tasks") && !ValidRole(action.Role))
            { error = "role is outside the schema enum"; return false; }
            if (kind == "restore_packages" && Has(action.Ecosystem) && action.Ecosystem is not ("dotnet" or "npm" or "pnpm" or "yarn" or "cargo" or "go" or "uv" or "maven" or "gradle" or "flutter" or "dart"))
            { error = "ecosystem is outside the schema enum"; return false; }

            if (kind == "delegate_tasks" && action.Children != null)
            {
                if (action.Children.Count is < 2 or > 6)
                { error = "children must contain between 2 and 6 items"; return false; }
                var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (CodingDelegationActionItem child in action.Children)
                {
                    if (child == null || string.IsNullOrWhiteSpace(child.Goal) || child.Goal.Length > 4000)
                    { error = "children contains an empty or overlong goal"; return false; }
                    if (!ValidRole(child.Role))
                    { error = "children contains a role outside the schema enum"; return false; }
                    if (child.MaxRepairAttempts is < 0 or > 3)
                    { error = "a child maxRepairAttempts must be between 0 and 3"; return false; }
                    if (child.Capabilities is { Count: > 32 }
                        || child.Capabilities?.Any(value => value != null && value.Length > 80) == true)
                    { error = "a child capabilities list is outside schema bounds"; return false; }
                    if (child.ContextRefs is { Count: > 3 })
                    { error = "a child contextRefs exceeds 3 items"; return false; }
                    if (child.ContextRefs != null)
                    {
                        var childRefs = new HashSet<string>(StringComparer.Ordinal);
                        foreach (string value in child.ContextRefs)
                        {
                            if (value is not ("parent-result" or "parent-context" or "parent-repair"))
                            { error = "a child contextRefs contains an unsupported value"; return false; }
                            if (!childRefs.Add(value))
                            { error = "a child contextRefs contains duplicate values"; return false; }
                        }
                    }
                    string identity = child.Goal.Trim() + "\u001f" + (child.Role ?? string.Empty).Trim();
                    if (!identities.Add(identity))
                    { error = "children contains a duplicate goal/role task"; return false; }
                }
            }

            switch (kind)
            {
                case "list_directory":
                case "list_artifacts":
                case "git_status":
                case "git_diff":
                case "git_log":
                case "update_plan":
                case "browser_dom":
                case "browser_console":
                case "browser_network":
                case "browser_reset":
                case "browser_screenshot":
                case "done":
                    if (kind == "update_plan" && !Has(action.Content) && !Has(action.Summary)) { error = "update_plan requires content or summary"; return false; }
                    return true;
                case "read_file":
                case "read_file_range":
                case "read_file_lines":
                case "write_file":
                case "create_file":
                case "delete_file":
                case "replace_range":
                    if (!Has(action.Path)) { error = kind + " requires path"; return false; }
                    if (kind == "read_file_lines" && action.Line is null or < 1) { error = "read_file_lines requires line >= 1"; return false; }
                    if (kind == "replace_range" && action.OldText == null) { error = "replace_range requires oldText"; return false; }
                    return true;
                case "read_artifact":
                    if (!Has(action.ArtifactId)) { error = "read_artifact requires artifactId"; return false; }
                    return true;
                case "search_artifact":
                    if (!Has(action.Pattern)) { error = "search_artifact requires pattern"; return false; }
                    return true;
                case "rename_file":
                    if (!Has(action.Path) || !Has(action.NewPath)) { error = "rename_file requires path and newPath"; return false; }
                    return true;
                case "grep_search":
                case "glob_search":
                    if (!Has(action.Pattern)) { error = kind + " requires pattern"; return false; }
                    return true;
                case "run_command":
                    if (!Has(action.Command)) { error = "run_command requires command"; return false; }
                    return true;
                case "restore_packages":
                    if (!Has(action.Ecosystem)) { error = "restore_packages requires ecosystem"; return false; }
                    return true;
                case "web_search":
                    if (!CodingWebSearchClient.IsValidQuery(action.Search))
                    { error = "web_search requires a 1-512 character query without control characters"; return false; }
                    return true;
                case "mcp_tool_call":
                    if (!Has(action.McpServerId)) { error = "mcp_tool_call requires mcpServerId"; return false; }
                    if (!Has(action.McpToolName)) { error = "mcp_tool_call requires mcpToolName"; return false; }
                    if (action.McpArguments is not JsonElement arguments || arguments.ValueKind != JsonValueKind.Object)
                    { error = "mcp_tool_call requires mcpArguments as a JSON object"; return false; }
                    return true;
                case "browser_navigate":
                    if (!Has(action.Url)) { error = "browser_navigate requires url"; return false; }
                    return true;
                case "browser_eval":
                    if (!Has(action.Expression)) { error = "browser_eval requires expression"; return false; }
                    return true;
                case "browser_click":
                    if (!Has(action.Selector)) { error = "browser_click requires selector"; return false; }
                    return true;
                case "browser_type":
                    if (!Has(action.Selector)) { error = "browser_type requires selector"; return false; }
                    return true;
                case "browser_assert":
                    if (!BrowserDomAssertionPolicy.IsValid(action.Selector, action.Target, action.Text))
                    {
                        error = "browser_assert requires selector and target=exists|absent|visible|hidden|text_equals|value_equals; text must be <=4096 characters for comparisons";
                        return false;
                    }
                    return true;
                case "ask_user":
                    if (!Has(action.Question)) { error = "ask_user requires question"; return false; }
                    if (!action.AllowOther && (action.Options == null || action.Options.Count == 0))
                    { error = "ask_user with allowOther=false requires at least one option"; return false; }
                    return true;
                case "delegate_task":
                    if (!Has(action.Goal)) { error = "delegate_task requires goal"; return false; }
                    return true;
                case "batch_read":
                    if (!CodingAgentFeatureFlags.BatchReadEnabled)
                    { error = "batch_read is disabled for this session"; return false; }
                    return CodingAgentReadBatch.TryValidate(action.Requests, out error);
                case "collect_children":
                    return true;
                case "delegate_tasks":
                    if (action.Children is not { Count: >= 2 and <= 6 })
                    { error = "delegate_tasks requires 2-6 children"; return false; }
                    return true;
                default:
                    error = "unsupported action '" + action.Action + "'";
                    return false;
            }
        }

        private static string BuildSystemPrompt(
            string root,
            bool canDelegate,
            bool canSafeRestore,
            bool canScriptedRestore,
            bool allowBatchRead = true,
            bool allowParentContinuation = true,
            int residentContextTokens = 0,
            IReadOnlyList<CodingAgentMcpToolDefinition>? mcpTools = null)
        {
            string prompt = CodingAgentPromptBuilder.BuildSystemPrompt(root, canDelegate, canSafeRestore,
                canScriptedRestore, allowBatchRead, allowParentContinuation, residentContextTokens);
            return prompt + CodingAgentMcpToolPolicy.BuildPrompt(mcpTools);
        }
    }
}
