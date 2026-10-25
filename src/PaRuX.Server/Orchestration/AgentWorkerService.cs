using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PaRuX.Contracts;
using PaRuX.Intelligence;

namespace PaRuX.Server.Orchestration;

/// <summary>
/// Server-side worker for specialist roles. Capabilities are explicit, persisted and fail-closed:
/// local context, web research, read-only repository inspection and bounded self-delegation are
/// available only when granted to the task. Repository authority is task-scoped and desktop-only;
/// specialist repository tools are read-only and never launch a shell or mutate the workspace.
/// Execution time/tool/evidence/delegation limits come from the Core-owned Execution Profile rather
/// than a second set of worker constants, so Chat/Agent/Training/Media share one resource policy.
/// Single-child and parallel specialist delegation both use the same Core-owned prepare ->
/// authority -> atomic commit protocol as the desktop Coding Agent.
/// </summary>
public sealed class AgentWorkerService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static readonly HashSet<string> ServerExecutableRoles = new(StringComparer.Ordinal)
    { "planner", "research", "review", "documentation", "testing", "security" };

    private static readonly HashSet<string> DesktopExecutableRoles = new(StringComparer.Ordinal)
    { "coding", "binary-analysis", "ui-verification", "computer-use" };

    private const string ContextCapability = "context.read";
    private const string WebResearchCapability = "web.research";
    private const string RepositoryReadCapability = "repository.read";
    private const string DelegationCapability = "agent.delegate";
    private const string DelegationWaitingStagePrefix = "Waiting for delegated child ";

    private sealed class ToolDelegationChild
    {
        [JsonPropertyName("childGoal")] public string ChildGoal { get; set; } = string.Empty;
        [JsonPropertyName("role")] public string Role { get; set; } = string.Empty;
        [JsonPropertyName("capabilities")] public List<string>? Capabilities { get; set; }
        [JsonPropertyName("contextRefs")] public List<string>? ContextRefs { get; set; }
        [JsonPropertyName("maxRepairAttempts")] public int? MaxRepairAttempts { get; set; }
        [JsonPropertyName("declaredFiles")] public List<string>? DeclaredFiles { get; set; }
    }

    private sealed class ToolAction
    {
        [JsonPropertyName("action")] public string Action { get; set; } = string.Empty;
        [JsonPropertyName("readsChildOutput")] public bool? ReadsChildOutput { get; set; }
        [JsonPropertyName("query")] public string Query { get; set; } = string.Empty;
        [JsonPropertyName("mode")] public string Mode { get; set; } = "balanced";
        [JsonPropertyName("answer")] public string Answer { get; set; } = string.Empty;
        [JsonPropertyName("path")] public string Path { get; set; } = string.Empty;
        [JsonPropertyName("offset")] public int? Offset { get; set; }
        [JsonPropertyName("length")] public int? Length { get; set; }
        [JsonPropertyName("regex")] public bool Regex { get; set; }
        [JsonPropertyName("count")] public int? Count { get; set; }
        [JsonPropertyName("role")] public string Role { get; set; } = string.Empty;
        [JsonPropertyName("childGoal")] public string ChildGoal { get; set; } = string.Empty;
        [JsonPropertyName("capabilities")] public List<string>? Capabilities { get; set; }
        [JsonPropertyName("contextRefs")] public List<string>? ContextRefs { get; set; }
        [JsonPropertyName("maxRepairAttempts")] public int? MaxRepairAttempts { get; set; }
        [JsonPropertyName("children")] public List<ToolDelegationChild>? Children { get; set; }
        [JsonPropertyName("declaredFiles")] public List<string>? DeclaredFiles { get; set; }
    }

    private sealed record BootstrapEvidence(string Source, string Text, bool RepositoryEvidence);

    private const string DelegationChildSchema =
        "{\"type\":\"object\",\"properties\":{" +
        "\"childGoal\":{\"type\":\"string\",\"minLength\":1,\"maxLength\":3000}," +
        "\"role\":{\"type\":\"string\",\"enum\":[\"planner\",\"research\",\"review\",\"documentation\",\"testing\",\"security\",\"coding\",\"binary-analysis\",\"ui-verification\",\"computer-use\"]}," +
        "\"capabilities\":{\"type\":\"array\",\"maxItems\":16,\"items\":{\"type\":\"string\",\"maxLength\":80}}," +
        "\"contextRefs\":{\"type\":\"array\",\"maxItems\":3,\"items\":{\"type\":\"string\",\"enum\":[\"parent-result\",\"parent-context\",\"parent-repair\"]},\"uniqueItems\":true}," +
        "\"maxRepairAttempts\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":3}," +
        "\"declaredFiles\":{\"type\":\"array\",\"maxItems\":64,\"items\":{\"type\":\"string\",\"maxLength\":260}}" +
        "},\"required\":[\"childGoal\"],\"additionalProperties\":false}";

    private static readonly LocalStructuredOutputRequest ToolActionSchema = new(
        "parux_specialist_action",
        "{\"type\":\"object\",\"properties\":{" +
        "\"action\":{\"type\":\"string\",\"enum\":[\"context_search\",\"web_research\"," +
        "\"repo_list\",\"repo_read\",\"repo_grep\",\"repo_git_status\",\"repo_git_diff\",\"repo_git_log\"," +
        "\"delegate_task\",\"delegate_tasks\",\"answer\"]}," +
        "\"readsChildOutput\":{\"type\":\"boolean\"}," +
        "\"query\":{\"type\":\"string\",\"maxLength\":4000}," +
        "\"mode\":{\"type\":\"string\",\"enum\":[\"fast\",\"balanced\",\"deep\"]}," +
        "\"answer\":{\"type\":\"string\",\"maxLength\":30000}," +
        "\"path\":{\"type\":\"string\",\"maxLength\":4096}," +
        "\"offset\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":16777216}," +
        "\"length\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":64000}," +
        "\"regex\":{\"type\":\"boolean\"}," +
        "\"count\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":120}," +
        "\"role\":{\"type\":\"string\",\"enum\":[\"planner\",\"research\",\"review\",\"documentation\",\"testing\",\"security\",\"coding\",\"binary-analysis\",\"ui-verification\",\"computer-use\"]}," +
        "\"childGoal\":{\"type\":\"string\",\"maxLength\":3000}," +
        "\"capabilities\":{\"type\":\"array\",\"maxItems\":16,\"items\":{\"type\":\"string\",\"maxLength\":80}}," +
        "\"contextRefs\":{\"type\":\"array\",\"maxItems\":3,\"items\":{\"type\":\"string\",\"enum\":[\"parent-result\",\"parent-context\",\"parent-repair\"]},\"uniqueItems\":true}," +
        "\"maxRepairAttempts\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":3}," +
        "\"declaredFiles\":{\"type\":\"array\",\"maxItems\":64,\"items\":{\"type\":\"string\",\"maxLength\":260}}," +
        "\"children\":{\"type\":\"array\",\"minItems\":2,\"maxItems\":6,\"items\":" + DelegationChildSchema + "}" +
        "},\"required\":[\"action\",\"readsChildOutput\"],\"additionalProperties\":false}",
        Strict: true);

    public static readonly TimeSpan DesktopClaimTimeout = TimeSpan.FromSeconds(30);

    private readonly TaskStore _tasks;
    private readonly ILocalCompletionClient _client;
    private readonly IRoleRouter _router;
    private readonly PlannerMemoryContextProvider? _plannerMemory;
    private readonly ContextEngineService? _context;
    private readonly ResearchEngineService? _research;
    private readonly InternetSettingsService? _internet;
    private readonly AgentWorkspaceAssignmentStore? _workspaces;
    private readonly AgentDelegationCommitCoordinator? _delegationCoordinator;
    private readonly ConcurrentDictionary<string, byte> _inFlight = new();
    internal Action? BeforeWaitingDelegatorResumeCommitForTests { get; set; }

    public AgentWorkerService(TaskStore tasks, ILocalCompletionClient client, IRoleRouter router)
        : this(tasks, client, router, plannerMemory: null, context: null, research: null, internet: null,
            workspaceAssignments: null, delegationCoordinator: null)
    {
    }

    public AgentWorkerService(TaskStore tasks, ILocalCompletionClient client, IRoleRouter router, MemoryStore memory)
        : this(tasks, client, router, new PlannerMemoryContextProvider(memory), null, null, null,
            workspaceAssignments: null, delegationCoordinator: null)
    {
    }

    public AgentWorkerService(
        TaskStore tasks,
        ILocalCompletionClient client,
        IRoleRouter router,
        MemoryStore memory,
        ContextEngineService context,
        ResearchEngineService research,
        InternetSettingsService internet)
        : this(tasks, client, router, new PlannerMemoryContextProvider(memory), context, research, internet,
            AgentWorkspaceAssignmentStore.Current, new AgentDelegationCommitCoordinator(tasks))
    {
    }

    internal AgentWorkerService(
        TaskStore tasks,
        ILocalCompletionClient client,
        IRoleRouter router,
        PlannerMemoryContextProvider? plannerMemory,
        ContextEngineService? context,
        ResearchEngineService? research,
        InternetSettingsService? internet)
        : this(tasks, client, router, plannerMemory, context, research, internet,
            workspaceAssignments: null, delegationCoordinator: null)
    {
    }

    internal AgentWorkerService(
        TaskStore tasks,
        ILocalCompletionClient client,
        IRoleRouter router,
        PlannerMemoryContextProvider? plannerMemory,
        ContextEngineService? context,
        ResearchEngineService? research,
        InternetSettingsService? internet,
        AgentWorkspaceAssignmentStore? workspaceAssignments)
        : this(tasks, client, router, plannerMemory, context, research, internet,
            workspaceAssignments, delegationCoordinator: null)
    {
    }

    internal AgentWorkerService(
        TaskStore tasks,
        ILocalCompletionClient client,
        IRoleRouter router,
        PlannerMemoryContextProvider? plannerMemory,
        ContextEngineService? context,
        ResearchEngineService? research,
        InternetSettingsService? internet,
        AgentWorkspaceAssignmentStore? workspaceAssignments,
        AgentDelegationCommitCoordinator? delegationCoordinator)
    {
        _tasks = tasks;
        _client = client;
        _router = router;
        _plannerMemory = plannerMemory;
        _context = context;
        _research = research;
        _internet = internet;
        _workspaces = workspaceAssignments;
        _delegationCoordinator = delegationCoordinator;
    }

    public static bool CanExecute(string? role) => role != null && ServerExecutableRoles.Contains(role);
    public static bool RequiresDesktopWorker(string? role) => role != null && DesktopExecutableRoles.Contains(role);

    private static string SystemPromptFor(string role) => role switch
    {
        "planner" => "You are PaRuX's Planner agent. Produce concrete executable steps grounded in available evidence. Use repository evidence when granted and delegate narrow independent questions when that materially reduces uncertainty. Avoid padding and do not invent repository state.",
        "research" => "You are PaRuX's Research agent. Answer factually from gathered evidence. Distinguish sourced findings from uncertainty and never fabricate a source. Delegate only genuinely separable research when granted.",
        "review" => "You are PaRuX's Review agent. Critically evaluate the goal and real repository/result evidence, identifying concrete regressions, correctness issues and missing verification. Do not accept a producer summary as proof.",
        "documentation" => "You are PaRuX's Documentation agent. Produce clear, concise documentation grounded in supplied and repository evidence; never invent an API or behavior you did not observe.",
        "testing" => "You are PaRuX's Testing agent. Inspect real repository evidence, design concrete tests with expected outcomes and edge cases, and delegate a bounded coding child when actual test execution or mutation is needed and granted.",
        "security" => "You are PaRuX's Security agent. Inspect concrete repository/trust-boundary evidence and identify demonstrable attack surfaces and mitigations; do not invent vulnerabilities. Delegate a narrow reproducer/review when useful and granted.",
        _ => "You are a PaRuX agent. Address the following goal directly and concisely."
    };

    public List<string> RunWorkerPass(CancellationToken cancellationToken)
    {
        ResumeWaitingDelegators();

        var started = new List<string>();
        foreach (AgentTaskDto task in _tasks.ListByStatus(AgentTaskStatus.Running))
        {
            if (!CanExecute(task.Role)) continue;
            if (!_inFlight.TryAdd(task.Id, 0)) continue;
            started.Add(task.Id);
            _ = ExecuteAsync(task, cancellationToken)
                .ContinueWith(completedTask => _inFlight.TryRemove(task.Id, out byte _), TaskScheduler.Default);
        }

        FailUnclaimedDesktopTasks(DateTime.UtcNow, DesktopClaimTimeout);
        return started;
    }

    internal List<string> ResumeWaitingDelegators()
    {
        var resumed = new List<string>();
        List<AgentTaskDto> all = _tasks.List();
        foreach (AgentTaskDto parent in all.Where(task =>
                     task.Status == AgentTaskStatus.Waiting
                     && task.Stage?.StartsWith(DelegationWaitingStagePrefix, StringComparison.Ordinal) == true))
        {
            List<AgentTaskDto> children = all.Where(task => string.Equals(task.ParentId, parent.Id, StringComparison.Ordinal)).ToList();
            if (children.Count == 0 || children.Any(child => !AgentTaskStatus.Terminal.Contains(child.Status))) continue;

            BeforeWaitingDelegatorResumeCommitForTests?.Invoke();
            if (_tasks.TryResumeWaitingDelegator(parent.Id, parent.Stage!, parent.UpdatedUtc,
                    "Resuming after delegated child evidence", Math.Max(parent.ProgressPercent, 50),
                    "All delegated child tasks reached a terminal state; specialist parent resumed with bounded child evidence."))
                resumed.Add(parent.Id);
        }
        return resumed;
    }

    public List<string> FailUnclaimedDesktopTasks(DateTime utcNow, TimeSpan? claimTimeout = null)
    {
        TimeSpan timeout = claimTimeout ?? DesktopClaimTimeout;
        if (timeout < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(claimTimeout));

        var failed = new List<string>();
        foreach (AgentTaskDto task in _tasks.ListByStatus(AgentTaskStatus.Running))
        {
            if (!RequiresDesktopWorker(task.Role)) continue;
            if (!string.IsNullOrWhiteSpace(task.WorktreePath)) continue;
            if (task.ProgressPercent > 0) continue;

            string expectedStage = "Dispatched to " + task.Role + " role";
            if (!string.Equals(task.Stage, expectedStage, StringComparison.Ordinal)) continue;
            if (utcNow - task.UpdatedUtc < timeout) continue;

            string error = $"No desktop worker claimed the '{task.Role}' task within {timeout.TotalSeconds:0} seconds. " +
                "Start the PaRuX Windows desktop worker or run a server-executable role; the task was failed instead of being left running indefinitely.";
            if (_tasks.TryFailUnclaimedDesktopTask(task.Id, expectedStage, error))
                failed.Add(task.Id);
        }
        return failed;
    }

    public async Task ExecuteAsync(AgentTaskDto task, CancellationToken cancellationToken)
    {
        AgentTaskDto? currentTask = _tasks.Get(task.Id);
        if (currentTask == null) return;
        task = currentTask;
        string role = task.Role ?? throw new InvalidOperationException("Task has no role assigned.");
        if (!CanExecute(role))
            throw new InvalidOperationException($"AgentWorkerService cannot execute role '{role}'.");
        if (task.Status != AgentTaskStatus.Running) return;

        using TaskExecutionLease? lease = _tasks.TryClaimExecution(task.Id, task.UpdatedUtc);
        if (lease == null) return;

        DateTime now = DateTime.UtcNow;
        DateTime executionStartedUtc = task.UpdatedUtc.Kind == DateTimeKind.Utc && task.UpdatedUtc <= now
            ? task.UpdatedUtc
            : now;
        AgentExecutionBudget executionBudget;
        try
        {
            executionBudget = AgentExecutionBudget.From(ExecutionProfileStore.Current.Get(null));
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            Fail(lease, "Execution Profile could not be validated before agent execution: " + ex.Message);
            return;
        }

        // Task STOP has its own attempt-scoped token. The execution CTS also carries host shutdown
        // and deadline cancellation, so it cannot identify why an old attempt was cancelled by
        // consulting mutable task status after a manual resume.
        using var executionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lease.StopToken);
        if (executionCts.IsCancellationRequested) return;
        if (!ApplyExecutionDeadline(executionCts, executionBudget, executionStartedUtc))
        {
            FailExecutionBudget(lease, executionBudget);
            return;
        }
        CancellationToken executionToken = executionCts.Token;

        string userPrompt = BuildIsolatedUserPrompt(task);
        AgentDependencyEvidenceCompiler.Compiled dependencyEvidence = CompileDependencyEvidence(task);
        if (!string.IsNullOrWhiteSpace(dependencyEvidence.PromptText))
        {
            userPrompt += "\n\nDIRECT PREDECESSOR EVIDENCE (durable result handoff; no predecessor transcript):\n"
                + Bound(dependencyEvidence.PromptText, executionBudget.ToolObservationCharacters);
            _tasks.TryAppendExecutionLog(lease,
                $"Injected bounded evidence from {dependencyEvidence.DependencyCount} completed predecessor task(s) ({dependencyEvidence.UsedCharacters} chars before Execution Profile bounding)." );
        }

        string childEvidence = CompileChildEvidence(task, executionBudget);
        bool hasUsableToolGrant = HasUsableToolGrant(task);
        if (!hasUsableToolGrant && !string.IsNullOrWhiteSpace(childEvidence))
        {
            userPrompt += "\n\nDELEGATED CHILD EVIDENCE (bounded; child transcripts are not loaded):\n" + childEvidence;
            _tasks.TryAppendExecutionLog(lease, "Injected bounded terminal child evidence for resumed specialist work.");
        }

        if (string.Equals(role, "planner", StringComparison.Ordinal) && _plannerMemory != null)
        {
            string memoryContext = _plannerMemory.Build(task);
            if (!string.IsNullOrWhiteSpace(memoryContext))
            {
                userPrompt += "\n\n" + Bound(memoryContext, executionBudget.ToolObservationCharacters);
                _tasks.TryAppendExecutionLog(lease, $"Planner injected bounded local Memory context ({memoryContext.Length} chars before Execution Profile bounding)." );
            }
        }

        RouteDecisionDto routing;
        try
        {
            routing = await _router.ResolveAsync(new RouteRequest { TaskType = role }, executionToken);
        }
        catch (OperationCanceledException) when (lease.StopToken.IsCancellationRequested)
        {
            return;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && executionCts.IsCancellationRequested)
        {
            FailExecutionBudget(lease, executionBudget);
            return;
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            Fail(lease, "Routing crashed before execution: " + ex.Message);
            return;
        }
        if (!routing.Success)
        {
            Fail(lease, "Could not ensure a model was loaded for this task: " + routing.Message);
            return;
        }

        try
        {
            executionBudget = AgentExecutionBudget.From(ExecutionProfileStore.Current.Get(routing.ModelPath));
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            Fail(lease, "Execution Profile for the routed model could not be validated: " + ex.Message);
            return;
        }
        if (!ApplyExecutionDeadline(executionCts, executionBudget, executionStartedUtc))
        {
            FailExecutionBudget(lease, executionBudget);
            return;
        }

        _tasks.TryAppendExecutionLog(lease, $"Router resolved \"{role}\" -> {routing.ResolvedRole}"
            + (routing.Swapped ? $" (swapped, {routing.DurationMs}ms)" : " (reused, no swap)"));
        _tasks.TryAppendExecutionLog(lease, "Execution Profile enforced for specialist pass: " + executionBudget.Describe() + ".");

        bool isRepairAttempt = !string.IsNullOrWhiteSpace(task.RepairContext);
        _tasks.TryAppendExecutionLog(lease, isRepairAttempt
            ? $"Worker retrying ({role} role, repair attempt {task.RepairAttemptsUsed}/{task.MaxRepairAttempts}) via local model"
            : $"Worker starting ({role} role) via local model");
        if (!string.IsNullOrWhiteSpace(task.DelegatedContext))
            _tasks.TryAppendExecutionLog(lease, $"Using isolated delegated context pack ({task.DelegatedContext.Length} chars, {task.DelegatedContextRefs.Count} ref(s)); no parent transcript was loaded.");

        CompletionResult result;
        try
        {
            result = hasUsableToolGrant
                ? await RunCapabilityToolLoopAsync(
                    task, role, userPrompt, executionBudget, lease, executionToken, childEvidence).ConfigureAwait(false)
                : await _client.CompleteAsync(SystemPromptFor(role), userPrompt, executionToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (lease.StopToken.IsCancellationRequested)
        {
            return;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && executionCts.IsCancellationRequested)
        {
            FailExecutionBudget(lease, executionBudget);
            return;
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            Fail(lease, "Worker crashed: " + ex.Message);
            return;
        }

        AgentTaskDto? latest = _tasks.Get(task.Id);
        if (latest?.Status == AgentTaskStatus.Waiting
            && latest.Stage?.StartsWith(DelegationWaitingStagePrefix, StringComparison.Ordinal) == true)
        {
            _tasks.TryAppendExecutionLog(lease, "Specialist execution paused while delegated child work runs; no verifier handoff occurs yet.");
            return;
        }

        if (!result.Success)
        {
            Fail(lease, result.Error ?? "Unknown worker error");
            return;
        }

        string content = result.Content ?? string.Empty;
        _tasks.TryCommitExecutionUpdate(lease, new UpdateAgentTaskStatusRequest
        {
            Status = AgentTaskStatus.Verifying,
            Stage = "Awaiting verification",
            ProgressPercent = 90,
            ResultJson = JsonSerializer.Serialize(new
            {
                response = content,
                capabilitiesUsed = task.GrantedCapabilities,
                executionProfile = executionBudget.QualityProfile,
                executionModelKey = executionBudget.ModelKey
            }, JsonOptions)
        }, "Worker received a response (" + content.Length + " chars)");
    }

    private static bool ApplyExecutionDeadline(
        CancellationTokenSource executionCts,
        AgentExecutionBudget budget,
        DateTime executionStartedUtc)
    {
        TimeSpan remaining = budget.Remaining(executionStartedUtc, DateTime.UtcNow);
        if (remaining <= TimeSpan.Zero) return false;
        executionCts.CancelAfter(remaining);
        return true;
    }

    private void FailExecutionBudget(TaskExecutionLease lease, AgentExecutionBudget budget) =>
        Fail(lease, $"Execution Profile '{budget.QualityProfile}' wall-clock budget of {budget.MaxWallClockMinutes} minute(s) was exhausted for this specialist execution pass.");

    private AgentDependencyEvidenceCompiler.Compiled CompileDependencyEvidence(AgentTaskDto task)
    {
        var inputs = new List<AgentDependencyEvidenceCompiler.Input>();
        foreach (string dependencyId in task.DependsOnTaskIds)
        {
            AgentTaskDto? dependency = _tasks.Get(dependencyId);
            if (dependency == null || dependency.Status != AgentTaskStatus.Completed) continue;
            inputs.Add(new AgentDependencyEvidenceCompiler.Input(
                dependency.Id, dependency.Role, dependency.VerificationStatus, dependency.ResultJson));
        }
        return AgentDependencyEvidenceCompiler.Compile(inputs);
    }

    private string CompileChildEvidence(AgentTaskDto task, AgentExecutionBudget budget)
    {
        List<AgentTaskDto> children = _tasks.List()
            .Where(child => string.Equals(child.ParentId, task.Id, StringComparison.Ordinal)
                && AgentTaskStatus.Terminal.Contains(child.Status))
            // Put the most recently finished child first so the bounded prefix preserves new
            // evidence when an older result already fills the available prompt budget.
            .OrderByDescending(child => child.UpdatedUtc)
            .ThenByDescending(child => child.CreatedUtc)
            .Take(budget.MaxDirectSubagents)
            .ToList();
        if (children.Count == 0) return string.Empty;

        var sb = new StringBuilder();
        foreach (AgentTaskDto child in children)
        {
            string evidence;
            if (child.Status == AgentTaskStatus.Completed)
            {
                AgentDependencyEvidenceCompiler.Compiled compiled = AgentDependencyEvidenceCompiler.Compile(
                    new[]
                    {
                        new AgentDependencyEvidenceCompiler.Input(
                            child.Id, child.Role, child.VerificationStatus, child.ResultJson)
                    });
                evidence = compiled.PromptText;
            }
            else
            {
                var statusEvidence = new StringBuilder()
                    .Append("- child ").Append(child.Id).Append(" [").Append(child.Role ?? "auto").Append("] ")
                    .Append(child.Status);
                if (!string.IsNullOrWhiteSpace(child.Error))
                    statusEvidence.Append(": ").Append(Bound(child.Error, Math.Min(2_000, budget.ToolObservationCharacters)));
                evidence = statusEvidence.ToString();
            }

            if (string.IsNullOrWhiteSpace(evidence)) continue;
            string separator = sb.Length > 0 ? Environment.NewLine + "---" + Environment.NewLine : string.Empty;
            int remaining = AgentDependencyEvidenceCompiler.DefaultMaximumCharacters - sb.Length - separator.Length;
            if (remaining <= 0) break;
            sb.Append(separator);
            string boundedEvidence = BoundWithinBudget(evidence, remaining);
            sb.Append(boundedEvidence);
            if (boundedEvidence.Length < evidence.Length) break;
        }
        return BoundWithinBudget(sb.ToString(), budget.ToolObservationCharacters);
    }

    private bool HasUsableToolGrant(AgentTaskDto task) =>
        (_context != null && task.GrantedCapabilities.Contains(ContextCapability, StringComparer.Ordinal))
        || (_research != null && _internet != null && task.GrantedCapabilities.Contains(WebResearchCapability, StringComparer.Ordinal))
        || (_workspaces != null && task.GrantedCapabilities.Contains(RepositoryReadCapability, StringComparer.Ordinal))
        || task.GrantedCapabilities.Contains(DelegationCapability, StringComparer.Ordinal);

    internal async Task<CompletionResult> RunCapabilityToolLoopAsync(
        AgentTaskDto task,
        string role,
        string basePrompt,
        AgentExecutionBudget budget,
        TaskExecutionLease lease,
        CancellationToken ct,
        string? initialChildEvidence = null)
    {
        var observations = new List<string>();
        var repositoryObservations = new List<string>();
        string childEvidenceIncluded = initialChildEvidence ?? string.Empty;
        int prefetchedSteps = await PrefetchIndependentEvidenceAsync(
            task, role, budget, observations, repositoryObservations, lease, ct).ConfigureAwait(false);

        string toolPrompt = SystemPromptFor(role) +
            "\n\nYou may request only capability-scoped tools advertised in the user prompt. Return one structured action at a time. " +
            "Use context_search for PaRuX local Memory/RAG/Context Graph/Artifacts; web_research for current public evidence; " +
            "repo_* only for READ-ONLY inspection of the explicitly authorized repository; delegate_task for one narrow independent child, " +
            "or delegate_tasks with 2-6 genuinely independent children when parallel fan-out materially reduces wall time or separates review/research/testing. " +
            "Every action must include readsChildOutput as true or false; set true if it uses any delegated child's result or evidence. " +
            "Do not batch dependent work. Core may keep this parent running beside read-only advisory children. While any child is live, choose only read-only actions " +
            "that do not read child output and do not need the same workspace as a live child; set readsChildOutput=true whenever child results are required. " +
            "Writes, child-output-dependent actions, final answers, and shared-workspace reads pause the parent until all direct children are terminal. " +
            "The parent then resumes with bounded child-result evidence. " +
            "When evidence is sufficient, return action=answer. Never claim a tool ran unless its observation appears in the prompt.";

        for (int step = prefetchedSteps + 1; step <= budget.MaxToolSteps; step++)
        {
            ct.ThrowIfCancellationRequested();
            string latestChildEvidence = CompileChildEvidence(task, budget);
            if (!string.Equals(latestChildEvidence, childEvidenceIncluded, StringComparison.Ordinal))
                childEvidenceIncluded = latestChildEvidence;

            var prompt = new StringBuilder(basePrompt);
            prompt.AppendLine().AppendLine();
            prompt.AppendLine("Tool availability for this task:");
            prompt.AppendLine(task.GrantedCapabilities.Contains(ContextCapability, StringComparer.Ordinal) && _context != null
                ? "- context_search: AVAILABLE (context.read)"
                : "- context_search: NOT GRANTED");
            prompt.AppendLine(task.GrantedCapabilities.Contains(WebResearchCapability, StringComparer.Ordinal) && _research != null && _internet != null
                ? "- web_research: AVAILABLE (web.research; global Internet switch still applies)"
                : "- web_research: NOT GRANTED");

            bool repoGranted = task.GrantedCapabilities.Contains(RepositoryReadCapability, StringComparer.Ordinal) && _workspaces != null;
            AgentWorkspaceAssignmentDto? assignment = null;
            if (repoGranted)
            {
                try { assignment = _workspaces!.Get(task.Id); }
                catch (InvalidDataException ex) { repositoryObservations.Add(Bound("repository authority unavailable: " + ex.Message, budget.RepositoryEvidenceCharacters)); }
            }
            prompt.AppendLine(repoGranted
                ? assignment != null
                    ? "- repo_list/repo_read/repo_grep/repo_git_*: AVAILABLE (repository.read; read-only task authority present)"
                    : "- repo_*: GRANTED but no desktop repository authority is assigned to this task"
                : "- repo_*: NOT GRANTED");
            prompt.AppendLine(task.GrantedCapabilities.Contains(DelegationCapability, StringComparer.Ordinal)
                ? $"- delegate_task / delegate_tasks: AVAILABLE (agent.delegate; max {budget.MaxDirectSubagents} direct children, depth {budget.MaxDelegationDepth}, no capability widening; delegate_tasks accepts 2-6 independent children)"
                : "- delegate_task / delegate_tasks: NOT GRANTED");

            if (repositoryObservations.Count > 0)
            {
                prompt.AppendLine().AppendLine("Observed repository evidence (separate Execution Profile budget):");
                string combinedRepository = string.Join("\n---\n", repositoryObservations.TakeLast(12));
                prompt.AppendLine(Bound(combinedRepository, budget.RepositoryEvidenceCharacters));
            }
            var toolEvidence = BuildToolEvidencePrompt(
                observations, childEvidenceIncluded, budget.ToolObservationCharacters);
            if (!string.IsNullOrWhiteSpace(toolEvidence.PromptText))
                prompt.AppendLine().AppendLine(toolEvidence.PromptText);
            prompt.AppendLine().Append("Choose the next single structured action. This is tool step ")
                .Append(step).Append('/').Append(budget.MaxToolSteps).Append('.');

            AgentStrategyRuntime.StructuredAttempt<ToolAction> structured =
                await AgentStrategyRuntime.CompleteStructuredAsync(
                    _client,
                    toolPrompt,
                    prompt.ToString(),
                    ToolActionSchema,
                    ParseToolAction,
                    budget,
                    ct).ConfigureAwait(false);
            CompletionResult actionResult = structured.Result;
            if (structured.RepairAttempted)
                _tasks.TryAppendExecutionLog(lease, $"Specialist tool step {step}: one bounded structured-output protocol repair was attempted ({budget.StructuredOutputStrategy}).");
            if (!actionResult.Success || string.IsNullOrWhiteSpace(actionResult.Content))
                return actionResult;

            ToolAction? action = structured.Value;
            if (action == null)
            {
                observations.Add("Tool protocol error: response was not a valid action object. Answer directly or return a valid granted tool action.");
                continue;
            }
            if (action.ReadsChildOutput is not bool readsChildOutput)
            {
                observations.Add("Tool protocol error: readsChildOutput must be explicitly true or false.");
                _tasks.TryAppendExecutionLog(lease,
                    $"Specialist tool step {step}: protocol error: readsChildOutput must be explicitly true or false.");
                continue;
            }

            string actionName = (action.Action ?? string.Empty).Trim().ToLowerInvariant();
            string? actionWorkspace = null;
            if (actionName.StartsWith("repo_", StringComparison.Ordinal) && _workspaces != null)
            {
                try { actionWorkspace = _workspaces.Get(task.Id)?.WorkspaceRoot; }
                catch (InvalidDataException) { }
            }

            AgentParentActionIntent intent = AgentParentContinuationPolicy.ForToolAction(
                actionName, readsChildOutput, actionWorkspace,
                AgentParentContinuationPolicy.FilesForToolAction(actionName, action.Path, action.DeclaredFiles));
            IReadOnlyList<AgentParentChildSnapshot> childSnapshots = GetParentContinuationChildren(task.Id);
            AgentParentContinuationDecision continuation = AgentParentContinuationPolicy.Decide(intent, childSnapshots);
            RecordChildTimeline(task.Id, step, actionName, continuation, childSnapshots);
            if (continuation == AgentParentContinuationDecision.WaitForChildren)
            {
                ct.ThrowIfCancellationRequested();
                IReadOnlyList<string> liveChildren;
                try { liveChildren = _tasks.PauseParentForLiveDelegatedChildren(task.Id, lease); }
                catch (InvalidDataException) when (ct.IsCancellationRequested)
                {
                    ct.ThrowIfCancellationRequested();
                    throw;
                }

                if (liveChildren.Count > 0)
                {
                    _tasks.AppendLog(task.Id,
                        $"Specialist tool step {step}: deferred action '{actionName}' until delegated children are terminal; " +
                        $"waiting for {liveChildren.Count} child task(s).");
                    return CompletionResult.Ok(
                        "Parent paused until delegated children are terminal; the pending action will be requested again with bounded child evidence.",
                        actionResult.StructuredOutputApplied);
                }
            }

            // The child may have completed after the loop-top evidence snapshot but before the
            // model returned this dependent action. Recheck even when the dependency policy sees
            // no live children, then ask again with the newly available result before acting.
            if (readsChildOutput || intent.EndsParentTurn)
            {
                string refreshedChildEvidence = CompileChildEvidence(task, budget);
                var refreshedToolEvidence = BuildToolEvidencePrompt(
                    observations, refreshedChildEvidence, budget.ToolObservationCharacters);
                if (!string.Equals(refreshedToolEvidence.ChildEvidenceText,
                    toolEvidence.ChildEvidenceText,
                    StringComparison.Ordinal))
                {
                    childEvidenceIncluded = refreshedChildEvidence;
                    _tasks.TryAppendExecutionLog(lease,
                        $"Specialist tool step {step}: refreshed child evidence; requesting a new action before executing '{actionName}'.");
                    continue;
                }
            }

            _tasks.TryAppendExecutionLog(lease, $"Specialist tool step {step}: executing {actionName}.");

            switch (actionName)
            {
                case "answer":
                    if (!string.IsNullOrWhiteSpace(action.Answer))
                        return CompletionResult.Ok(action.Answer.Trim(), actionResult.StructuredOutputApplied);
                    observations.Add("Tool protocol error: answer action contained an empty answer.");
                    break;

                case "context_search":
                    if (_context == null || !task.GrantedCapabilities.Contains(ContextCapability, StringComparer.Ordinal))
                    {
                        observations.Add("context_search denied: context.read was not granted to this task.");
                        break;
                    }
                    if (string.IsNullOrWhiteSpace(action.Query))
                    {
                        observations.Add("context_search rejected: query is empty.");
                        break;
                    }
                    try
                    {
                        ContextPackDto pack = await _context.BuildAsync(new ContextBuildRequest
                        {
                            Query = action.Query.Trim(),
                            MaxCharacters = budget.ToolObservationCharacters,
                            IncludeMemory = true,
                            IncludeRag = true,
                            IncludeGraph = true,
                            IncludeArtifacts = true,
                            IncludeWeb = false,
                            IncludeRepository = false,
                            IncludeConversationHistory = false
                        }, ct).ConfigureAwait(false);
                        string rendered = Bound(pack.PromptText, budget.ToolObservationCharacters);
                        observations.Add(Bound("context_search query: " + action.Query.Trim() + "\n" +
                            (string.IsNullOrWhiteSpace(rendered) ? "(no matching local context)" : rendered), budget.ToolObservationCharacters));
                        _tasks.TryAppendExecutionLog(lease, $"Specialist tool step {step}: context_search returned {pack.Items.Count} item(s), {pack.UsedCharacters} chars.");
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        observations.Add(Bound("context_search failed: " + ex.Message, budget.ToolObservationCharacters));
                        _tasks.TryAppendExecutionLog(lease, $"Specialist tool step {step}: context_search failed: {ex.Message}");
                    }
                    break;

                case "web_research":
                    if (_research == null || _internet == null || !task.GrantedCapabilities.Contains(WebResearchCapability, StringComparer.Ordinal))
                    {
                        observations.Add("web_research denied: web.research was not granted to this task.");
                        break;
                    }
                    if (!_internet.GetEnabled())
                    {
                        observations.Add("web_research denied: PaRuX global Internet switch is OFF.");
                        break;
                    }
                    if (string.IsNullOrWhiteSpace(action.Query))
                    {
                        observations.Add("web_research rejected: query is empty.");
                        break;
                    }
                    try
                    {
                        string mode = action.Mode is "fast" or "deep" ? action.Mode : "balanced";
                        ResearchResult research = await _research.RunAsync(action.Query.Trim(), mode, ct).ConfigureAwait(false);
                        observations.Add(WebResearchEvidence(
                            "web_research query: " + action.Query.Trim(), research.Answer, research.Sources,
                            budget.ToolObservationCharacters));
                        _tasks.TryAppendExecutionLog(lease, $"Specialist tool step {step}: web_research ({mode}) returned {research.Sources.Count} source(s).");
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        observations.Add(Bound("web_research failed: " + ex.Message, budget.ToolObservationCharacters));
                        _tasks.TryAppendExecutionLog(lease, $"Specialist tool step {step}: web_research failed: {ex.Message}");
                    }
                    break;

                case "repo_list":
                    if (!TryGetRepository(task, out string repoListRoot, out string repoListError))
                    {
                        repositoryObservations.Add(Bound("repo_list denied: " + repoListError, budget.RepositoryEvidenceCharacters));
                        break;
                    }
                    try
                    {
                        IReadOnlyList<string> entries = AgentRepositoryReadTools.List(repoListRoot, action.Path ?? string.Empty);
                        repositoryObservations.Add(Bound("repo_list " + (string.IsNullOrWhiteSpace(action.Path) ? "." : action.Path) + "\n"
                            + (entries.Count == 0 ? "(empty/no entries)" : string.Join("\n", entries)), budget.RepositoryEvidenceCharacters));
                        _tasks.TryAppendExecutionLog(lease, $"Specialist tool step {step}: repo_list returned {entries.Count} entry/entries.");
                    }
                    catch (Exception ex) { repositoryObservations.Add(Bound("repo_list failed: " + ex.Message, budget.RepositoryEvidenceCharacters)); }
                    break;

                case "repo_read":
                    if (!TryGetRepository(task, out string repoReadRoot, out string repoReadError))
                    {
                        repositoryObservations.Add(Bound("repo_read denied: " + repoReadError, budget.RepositoryEvidenceCharacters));
                        break;
                    }
                    if (string.IsNullOrWhiteSpace(action.Path))
                    {
                        repositoryObservations.Add("repo_read rejected: path is empty.");
                        break;
                    }
                    try
                    {
                        int maximumRead = Math.Min(64_000, budget.RepositoryEvidenceCharacters);
                        int requestedRead = Math.Clamp(
                            action.Length ?? AgentRepositoryReadTools.DefaultReadCharacters,
                            1,
                            maximumRead);
                        AgentRepositoryReadTools.ReadResult? read = AgentRepositoryReadTools.Read(
                            repoReadRoot, action.Path.Trim(), Math.Max(0, action.Offset ?? 0), requestedRead);
                        string observation = read == null
                            ? "repo_read: file not found"
                            : "repo_read " + read.RelativePath + " offset=" + read.Offset + " returned=" + read.ReturnedCharacters
                                + " total=" + read.TotalCharacters + " hasMore=" + read.HasMore + "\n" + read.Text;
                        repositoryObservations.Add(Bound(observation, budget.RepositoryEvidenceCharacters));
                        _tasks.TryAppendExecutionLog(lease, $"Specialist tool step {step}: repo_read {action.Path}.");
                    }
                    catch (Exception ex) { repositoryObservations.Add(Bound("repo_read failed: " + ex.Message, budget.RepositoryEvidenceCharacters)); }
                    break;

                case "repo_grep":
                    if (!TryGetRepository(task, out string repoGrepRoot, out string repoGrepError))
                    {
                        repositoryObservations.Add(Bound("repo_grep denied: " + repoGrepError, budget.RepositoryEvidenceCharacters));
                        break;
                    }
                    if (string.IsNullOrWhiteSpace(action.Query))
                    {
                        repositoryObservations.Add("repo_grep rejected: query is empty.");
                        break;
                    }
                    try
                    {
                        IReadOnlyList<AgentRepositoryReadTools.GrepMatch> matches = AgentRepositoryReadTools.Grep(
                            repoGrepRoot, action.Query.Trim(), action.Regex, ct);
                        repositoryObservations.Add(Bound("repo_grep \"" + action.Query.Trim() + "\" (" + matches.Count + ")\n"
                            + (matches.Count == 0 ? "(no matches)" : string.Join("\n", matches.Select(match =>
                                match.RelativePath + ":" + match.LineNumber + ": " + match.Line))), budget.RepositoryEvidenceCharacters));
                        _tasks.TryAppendExecutionLog(lease, $"Specialist tool step {step}: repo_grep returned {matches.Count} match(es).");
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    { repositoryObservations.Add(Bound("repo_grep failed: " + ex.Message, budget.RepositoryEvidenceCharacters)); }
                    break;

                case "repo_git_status":
                case "repo_git_diff":
                case "repo_git_log":
                    if (!TryGetRepository(task, out string repoGitRoot, out string repoGitError))
                    {
                        repositoryObservations.Add(Bound(action.Action + " denied: " + repoGitError, budget.RepositoryEvidenceCharacters));
                        break;
                    }
                    try
                    {
                        string git = action.Action switch
                        {
                            "repo_git_status" => await AgentRepositoryReadTools.GitStatusAsync(repoGitRoot, ct).ConfigureAwait(false),
                            "repo_git_diff" => await AgentRepositoryReadTools.GitDiffAsync(repoGitRoot, action.Path, ct).ConfigureAwait(false),
                            _ => await AgentRepositoryReadTools.GitLogAsync(repoGitRoot, action.Count ?? 10, ct).ConfigureAwait(false)
                        };
                        repositoryObservations.Add(action.Action + "\n" + Bound(git, budget.RepositoryEvidenceCharacters));
                        _tasks.TryAppendExecutionLog(lease, $"Specialist tool step {step}: {action.Action} completed read-only.");
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    { repositoryObservations.Add(Bound(action.Action + " failed: " + ex.Message, budget.RepositoryEvidenceCharacters)); }
                    break;

                case "delegate_task":
                {
                    CompletionResult? delegation = AgentSpecialistDelegationService.TryDelegate(
                        _tasks,
                        _workspaces,
                        _delegationCoordinator,
                        task,
                        new[]
                        {
                            new SpecialistDelegationActionItem(
                                action.ChildGoal,
                                action.Role,
                                action.Capabilities,
                                action.ContextRefs,
                                action.MaxRepairAttempts,
                                action.DeclaredFiles)
                        },
                        observations,
                        actionResult.StructuredOutputApplied,
                        budget,
                        lease,
                        requireMultiple: false,
                        allowParentContinuation: true);
                    if (delegation != null)
                    {
                        if (_tasks.Get(task.Id)?.Status == AgentTaskStatus.Running)
                        {
                            observations.Add(Bound(delegation.Content ?? "Delegated child work is running.", budget.ToolObservationCharacters));
                            _tasks.TryAppendExecutionLog(lease,
                                "Delegated child work committed; parent continues under the dependency policy.");
                            break;
                        }
                        return delegation;
                    }
                    break;
                }

                case "delegate_tasks":
                {
                    IReadOnlyList<SpecialistDelegationActionItem>? children = action.Children?.Select(child =>
                        new SpecialistDelegationActionItem(
                            child.ChildGoal,
                            child.Role,
                            child.Capabilities,
                            child.ContextRefs,
                            child.MaxRepairAttempts,
                            child.DeclaredFiles)).ToList();
                    CompletionResult? delegation = AgentSpecialistDelegationService.TryDelegate(
                        _tasks,
                        _workspaces,
                        _delegationCoordinator,
                        task,
                        children,
                        observations,
                        actionResult.StructuredOutputApplied,
                        budget,
                        lease,
                        requireMultiple: true,
                        allowParentContinuation: true);
                    if (delegation != null)
                    {
                        if (_tasks.Get(task.Id)?.Status == AgentTaskStatus.Running)
                        {
                            observations.Add(Bound(delegation.Content ?? "Delegated child work is running.", budget.ToolObservationCharacters));
                            _tasks.TryAppendExecutionLog(lease,
                                "Delegated child work committed; parent continues under the dependency policy.");
                            break;
                        }
                        return delegation;
                    }
                    break;
                }

                default:
                    observations.Add("Tool protocol error: unsupported action '" + action.Action + "'.");
                    break;
            }
        }

        string repositoryEvidence = Bound(string.Join("\n---\n", repositoryObservations.TakeLast(12)), budget.RepositoryEvidenceCharacters);
        IReadOnlyList<AgentParentChildSnapshot> remainingChildren = GetParentContinuationChildren(task.Id);
        AgentParentActionIntent finalAnswer = AgentParentContinuationPolicy.ForToolAction(
            "answer", readsChildOutput: true, workspaceRoot: null);
        if (AgentParentContinuationPolicy.Decide(finalAnswer, remainingChildren)
            == AgentParentContinuationDecision.WaitForChildren)
        {
            ct.ThrowIfCancellationRequested();
            IReadOnlyList<string> liveChildren;
            try { liveChildren = _tasks.PauseParentForLiveDelegatedChildren(task.Id, lease); }
            catch (InvalidDataException) when (ct.IsCancellationRequested)
            {
                ct.ThrowIfCancellationRequested();
                throw;
            }
            if (liveChildren.Count > 0)
            {
                _tasks.AppendLog(task.Id,
                    "Specialist tool-step budget ended while delegated children were live; parent paused for their terminal evidence.");
                return CompletionResult.Ok(
                    "Parent paused until delegated children are terminal; final answer will be requested after bounded child evidence is available.");
            }
        }

        string finalChildEvidence = CompileChildEvidence(task, budget);
        childEvidenceIncluded = finalChildEvidence;
        var finalEvidence = new StringBuilder();
        var finalToolEvidence = BuildToolEvidencePrompt(
            observations, childEvidenceIncluded, budget.ToolObservationCharacters);
        if (!string.IsNullOrWhiteSpace(finalToolEvidence.PromptText))
            finalEvidence.AppendLine(finalToolEvidence.PromptText);
        if (!string.IsNullOrWhiteSpace(repositoryEvidence))
            finalEvidence.AppendLine("REPOSITORY EVIDENCE:").AppendLine(repositoryEvidence);
        string finalPrompt = basePrompt + "\n\nTool-step budget is exhausted. Produce the best final answer now from the evidence below; do not request another tool.\n\n"
            + finalEvidence;
        return await _client.CompleteAsync(SystemPromptFor(role), finalPrompt, ct).ConfigureAwait(false);
    }

    /// <summary>Surfaces the parent/child scheduling decision in the task status timeline (only while children are live).</summary>
    private void RecordChildTimeline(string parentId, int step, string actionName,
        AgentParentContinuationDecision decision, IReadOnlyList<AgentParentChildSnapshot> children)
    {
        string[] live = children.Where(child => !child.IsTerminal).Select(child => child.Id).ToArray();
        if (live.Length == 0) return;
        try
        {
            _tasks.AppendChildTimeline(parentId,
                decision == AgentParentContinuationDecision.Run ? "step-run" : "step-wait",
                $"step {step}: {actionName}",
                (decision == AgentParentContinuationDecision.Run ? "independent of live children " : "waiting for live children ")
                + string.Join(", ", live));
        }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or InvalidOperationException) { }
    }

    private IReadOnlyList<AgentParentChildSnapshot> GetParentContinuationChildren(string parentId)
    {
        return _tasks.List()
            .Where(child => string.Equals(child.ParentId, parentId, StringComparison.Ordinal))
            .Select(child =>
            {
                bool workspaceKnown = true;
                string? workspaceRoot = null;
                if (_workspaces != null)
                {
                    try { workspaceRoot = _workspaces.Get(child.Id)?.WorkspaceRoot; }
                    catch (InvalidDataException) { workspaceKnown = false; }
                }

                return new AgentParentChildSnapshot(
                    child.Id,
                    AgentTaskStatus.Terminal.Contains(child.Status),
                    workspaceKnown,
                    workspaceRoot,
                    child.DeclaredFiles.Count == 0 ? null : child.DeclaredFiles);
            })
            .ToList();
    }

    private async Task<int> PrefetchIndependentEvidenceAsync(
        AgentTaskDto task,
        string role,
        AgentExecutionBudget budget,
        List<string> observations,
        List<string> repositoryObservations,
        TaskExecutionLease lease,
        CancellationToken ct)
    {
        var factories = new List<Func<Task<BootstrapEvidence>>>();

        if (_context != null && task.GrantedCapabilities.Contains(ContextCapability, StringComparer.Ordinal))
        {
            factories.Add(async () =>
            {
                try
                {
                    ContextPackDto pack = await _context.BuildAsync(new ContextBuildRequest
                    {
                        Query = task.Goal,
                        MaxCharacters = budget.ToolObservationCharacters,
                        IncludeMemory = true,
                        IncludeRag = true,
                        IncludeGraph = true,
                        IncludeArtifacts = true,
                        IncludeWeb = false,
                        IncludeRepository = false,
                        IncludeConversationHistory = false
                    }, ct).ConfigureAwait(false);
                    string rendered = string.IsNullOrWhiteSpace(pack.PromptText) ? "(no matching local context)" : pack.PromptText;
                    return new BootstrapEvidence(
                        "context_search",
                        "parallel context_search for task goal\n" + Bound(rendered, budget.ToolObservationCharacters),
                        false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    return new BootstrapEvidence("context_search", "parallel context_search failed: " + ex.Message, false);
                }
            });
        }

        if (_research != null && _internet != null
            && task.GrantedCapabilities.Contains(WebResearchCapability, StringComparer.Ordinal)
            && _internet.GetEnabled())
        {
            factories.Add(async () =>
            {
                try
                {
                    ResearchResult research = await _research.RunAsync(task.Goal, "fast", ct).ConfigureAwait(false);
                    return new BootstrapEvidence("web_research", WebResearchEvidence(
                        "parallel web_research for task goal", research.Answer, research.Sources,
                        budget.ToolObservationCharacters), false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    return new BootstrapEvidence("web_research", "parallel web_research failed: " + ex.Message, false);
                }
            });
        }

        if (TryGetRepository(task, out string repositoryRoot, out _))
        {
            AgentParentActionIntent repositorySnapshotIntent = AgentParentContinuationPolicy.ForToolAction(
                "repo_list", readsChildOutput: false, workspaceRoot: repositoryRoot);
            if (AgentParentContinuationPolicy.Decide(
                repositorySnapshotIntent, GetParentContinuationChildren(task.Id))
                == AgentParentContinuationDecision.Run)
            {
                string capturedRoot = repositoryRoot;
                factories.Add(async () =>
                {
                    try
                    {
                        Task<string> statusTask = AgentRepositoryReadTools.GitStatusAsync(capturedRoot, ct);
                        IReadOnlyList<string> entries = AgentRepositoryReadTools.List(capturedRoot, string.Empty);
                        string status = await statusTask.ConfigureAwait(false);
                        var evidence = new StringBuilder();
                        evidence.AppendLine("parallel repository snapshot");
                        evidence.AppendLine("git status:").AppendLine(status);
                        evidence.AppendLine("top-level entries:");
                        foreach (string entry in entries.Take(120)) evidence.AppendLine(entry);
                        return new BootstrapEvidence("repository", Bound(evidence.ToString(), budget.RepositoryEvidenceCharacters), true);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        return new BootstrapEvidence("repository", "parallel repository snapshot failed: " + ex.Message, true);
                    }
                });
            }
            else
            {
                _tasks.TryAppendExecutionLog(lease,
                    "Execution Profile skipped repository prefetch because a live delegated child shares or may share the workspace.");
            }
        }

        int allowedFactoryCount = Math.Min(factories.Count, budget.MaxToolSteps);
        if (allowedFactoryCount < 2) return 0;
        IReadOnlyList<Func<Task<BootstrapEvidence>>> selected = factories.Take(allowedFactoryCount).ToArray();
        IReadOnlyList<BootstrapEvidence> prefetched = await AgentStrategyRuntime.RunIndependentEvidenceAsync(
            budget, task.Goal, role, selected).ConfigureAwait(false);
        if (prefetched.Count == 0) return 0;

        foreach (BootstrapEvidence evidence in prefetched)
        {
            if (evidence.RepositoryEvidence)
                repositoryObservations.Add(Bound(evidence.Text, budget.RepositoryEvidenceCharacters));
            else
                observations.Add(Bound(evidence.Text, budget.ToolObservationCharacters));
        }

            _tasks.TryAppendExecutionLog(lease,
            $"Execution Profile {budget.ToolStrategy}: prefetched {prefetched.Count} independent read-only evidence source(s) concurrently; each source consumed one tool-step slot.");
        return prefetched.Count;
    }

    private bool TryGetRepository(AgentTaskDto task, out string root, out string error)
    {
        root = string.Empty;
        error = string.Empty;
        if (!task.GrantedCapabilities.Contains(RepositoryReadCapability, StringComparer.Ordinal))
        {
            error = "repository.read was not granted to this task.";
            return false;
        }
        if (_workspaces == null)
        {
            error = "repository authority is unavailable in this worker context.";
            return false;
        }
        try
        {
            AgentWorkspaceAssignmentDto? assignment = _workspaces.Get(task.Id);
            if (assignment == null)
            {
                error = "no desktop repository authority is assigned to this task.";
                return false;
            }
            root = assignment.WorkspaceRoot;
            if (!Directory.Exists(root))
            {
                error = "the assigned repository no longer exists.";
                return false;
            }
            return true;
        }
        catch (InvalidDataException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static ToolAction? ParseToolAction(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        int start = text.IndexOf('{');
        int end = text.LastIndexOf('}');
        if (start < 0 || end <= start) return null;
        try { return JsonSerializer.Deserialize<ToolAction>(text[start..(end + 1)], JsonOptions); }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// The evidence block for one web_research result. Every part of it is derived from web pages, so the
    /// untrusted-content header leads it, ahead of the answer and the source list.
    /// </summary>
    internal static string WebResearchEvidence(
        string label, string? answer, IReadOnlyList<ResearchSource> sources, int budgetCharacters)
    {
        var evidence = new StringBuilder();
        evidence.AppendLine(WebSearchService.UntrustedContentHeader);
        evidence.AppendLine(label);
        evidence.AppendLine(Bound(answer, budgetCharacters));
        if (sources.Count > 0)
        {
            evidence.AppendLine("Sources:");
            foreach (ResearchSource source in sources.Take(8))
                evidence.Append("- ").Append(source.Title).Append(" — ").AppendLine(source.Url);
        }
        return Bound(evidence.ToString(), budgetCharacters);
    }

    private static string Bound(string? value, int maximum)
    {
        string text = value?.Trim() ?? string.Empty;
        return text.Length <= maximum ? text : text[..maximum] + "\n… (tool evidence truncated)";
    }

    internal static (string PromptText, string ChildEvidenceText) BuildToolEvidencePrompt(
        IReadOnlyList<string> observations,
        string? childEvidence,
        int maximumCharacters)
    {
        const string observationHeader = "Observed tool evidence:";
        const string childHeader = "DELEGATED CHILD EVIDENCE (bounded; child transcripts are not loaded):";
        string combinedObservations = string.Join("\n---\n", observations.TakeLast(8));
        string childText = childEvidence?.Trim() ?? string.Empty;
        int remaining = Math.Max(0, maximumCharacters);
        string childSection = string.Empty;
        string renderedChildEvidence = string.Empty;
        string separator = Environment.NewLine + Environment.NewLine;

        if (childText.Length > 0 && remaining > 0)
        {
            int childHeaderLength = childHeader.Length + Environment.NewLine.Length;
            if (remaining > childHeaderLength)
            {
                renderedChildEvidence = BoundWithinBudget(childText, remaining - childHeaderLength);
                childSection = childHeader + Environment.NewLine + renderedChildEvidence;
            }
            else
            {
                renderedChildEvidence = BoundWithinBudget(childText, remaining);
                childSection = renderedChildEvidence;
            }
            remaining = Math.Max(0, remaining - childSection.Length);
        }

        string observationSection = string.Empty;
        if (combinedObservations.Length > 0 && remaining > 0)
        {
            int separatorLength = childSection.Length > 0 ? separator.Length : 0;
            int observationHeaderLength = observationHeader.Length + Environment.NewLine.Length;
            int observationContentBudget = Math.Max(
                0,
                remaining - separatorLength - observationHeaderLength);
            if (observationContentBudget > 0)
            {
                string renderedObservations = BoundWithinBudget(combinedObservations, observationContentBudget);
                observationSection = observationHeader + Environment.NewLine + renderedObservations;
            }
        }

        string promptText = observationSection.Length == 0
            ? childSection
            : childSection.Length == 0
                ? observationSection
                : observationSection + separator + childSection;
        return (promptText, renderedChildEvidence);
    }

    private static string BoundWithinBudget(string? value, int maximum)
    {
        string text = value?.Trim() ?? string.Empty;
        if (maximum <= 0 || text.Length == 0) return string.Empty;
        if (text.Length <= maximum) return text;

        const string suffix = "\n… (tool evidence truncated)";
        if (maximum <= suffix.Length) return text[..maximum];
        return text[..(maximum - suffix.Length)] + suffix;
    }

    internal static string BuildIsolatedUserPrompt(AgentTaskDto task)
    {
        var prompt = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(task.RepairContext))
        {
            prompt.AppendLine("This is a repair attempt after a previous verification/finish-condition failure.");
            prompt.AppendLine("Verifier feedback:");
            prompt.AppendLine(task.RepairContext);
            prompt.AppendLine();
        }

        prompt.AppendLine("Task goal:");
        prompt.AppendLine(task.Goal);

        if (!string.IsNullOrWhiteSpace(task.DelegatedContext))
        {
            prompt.AppendLine();
            prompt.AppendLine("Explicit bounded delegated context (this is the only parent/subagent context supplied):");
            prompt.AppendLine(task.DelegatedContext);
        }

        if (task.GrantedCapabilities.Count > 0)
        {
            prompt.AppendLine();
            prompt.AppendLine("Granted capability labels (do not assume any capability outside this list):");
            prompt.AppendLine(string.Join(", ", task.GrantedCapabilities));
        }

        if (task.FinishCondition != null)
        {
            prompt.AppendLine();
            prompt.Append("Machine-checkable finish condition: ").Append(task.FinishCondition.Type);
            if (!string.IsNullOrWhiteSpace(task.FinishCondition.Value))
                prompt.Append(" = ").Append(task.FinishCondition.Value);
            prompt.AppendLine(". The independent verifier will check this after your response.");
        }
        return prompt.ToString().Replace("\r\n", "\n").Trim();
    }

    private void Fail(TaskExecutionLease lease, string error)
    {
        _tasks.TryCommitExecutionUpdate(lease, new UpdateAgentTaskStatusRequest
        {
            Status = AgentTaskStatus.Failed,
            Stage = "Worker failed",
            Error = error
        }, "Worker failed: " + error);
    }

}
