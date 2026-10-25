using System.Text.RegularExpressions;
using PaRuX.Contracts;

namespace PaRuX.Server.Orchestration;

/// <summary>
/// vNext task-graph scheduler. It decides which queued tasks may start, respecting DAG readiness
/// and the active PaRuX Execution Profile's cross-surface parallelism budget. Work execution stays
/// in the worker services; scheduling remains deterministic and independently testable.
/// </summary>
public sealed class AgentOrchestrator
{
    private const string DelegationWaitingStagePrefix = "Waiting for delegated child ";

    private readonly TaskStore _tasks;
    private readonly Func<ModelExecutionProfile> _executionProfile;

    public AgentOrchestrator(TaskStore tasks)
        : this(tasks, () => global::PaRuX.Server.ExecutionProfileStore.Current.Get().Effective)
    {
    }

    /// <summary>
    /// Testable profile resolver seam. Production callers use the default constructor above; this
    /// overload exists so scheduler-budget behaviour can be verified without mutating global profile
    /// state shared by other tests or surfaces.
    /// </summary>
    public AgentOrchestrator(TaskStore tasks, Func<ModelExecutionProfile> executionProfile)
    {
        _tasks = tasks ?? throw new ArgumentNullException(nameof(tasks));
        _executionProfile = executionProfile ?? throw new ArgumentNullException(nameof(executionProfile));
    }

    private static readonly (string Role, string[] Keywords)[] RoleKeywords =
    {
        ("binary-analysis", new[]
        {
            "binary analysis", "analisi binaria", "reverse engineering", "reverse-engineer", "reverse engineer",
            "decompile", "decompiler", "decompil", "disassemble", "disassembler", "disassembl",
            "pe file", "pe/", "elf file", "mach-o", "executable binary", "binary artifact",
            ".exe", ".dll", "firmware image", "inspect binary", "analyze binary", "analyse binary"
        }),
        ("security", new[] { "security", "vulnerab", "auth", "exploit", "cve" }),
        ("computer-use", new[]
        {
            "computer use", "control pc", "control the pc", "click ", "double-click", "right-click",
            "open the app", "open app", "launch the app", "launch app", "navigate to", "type into",
            "press the ", "use the gui", "operate the gui", "operate windows"
        }),
        ("ui-verification", new[]
        {
            "verify the ui", "verify ui", "check the screen", "confirm the screen", "confirm the ui",
            "inspect the screen", "inspect screenshot", "screenshot", "visibly shows", "visible on screen"
        }),
        ("testing", new[] { "test", "regression", "qa" }),
        ("review", new[] { "review", "audit", "critique" }),
        ("documentation", new[] { "document", "docs", "readme", "changelog" }),
        ("research", new[] { "research", "investigate", "compare", "benchmark" }),
        ("planner", new[] { "plan", "decompose", "roadmap", "break down" })
    };

    /// <summary>Roles that describe work *about* code rather than changes *to* it. A goal that plainly
    /// asks for an edit outranks an incidental mention of one of their keywords.</summary>
    private static readonly HashSet<string> AdvisoryRoles =
        new(StringComparer.Ordinal) { "testing", "review", "documentation", "research", "planner" };

    private static readonly Regex ExplicitRepositoryEditCommand = new(
        @"^\s*(?:(?:in|within|nel|nella)\s+(?:(?:the|this|a|an|il|la|l')\s+)?(?:(?:isolated)\s+)?(?:repository(?: assigned to this project)?|repo|project|codebase)[,\s:]+)?(?:please\s+|per favore\s+)?(?:fix|repair|implement|add|remove|delete|refactor|rename|rewrite|correct|resolve|update|migrate|port|introduce|extract|inline|edit|modify|change|write|create|correggi|implementa|aggiungi|rimuovi|rinomina|sistema|risolvi|aggiorna|modifica|scrivi|crea)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ExplicitAdvisoryDeliverableCommand = new(
        @"^\s*(?:(?:in|within|nel|nella)\s+(?:(?:the|this|a|an|il|la|l')\s+)?(?:(?:isolated)\s+)?(?:repository(?: assigned to this project)?|repo|project|codebase)[,\s:]+)?(?:please\s+|per favore\s+)?(?:write|create|draft|prepare)\s+(?:(?:a|an|the)\s+)?(?<deliverable>review|plan|proposal|summary|analysis|report|assessment|audit|critique|recommendation)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex SourceFileReference = new(
        @"[\w\-./\\]+\.(cs|csproj|xaml|ts|tsx|js|jsx|mjs|py|rs|go|java|kt|swift|c|h|cpp|hpp|sql|ps1|sh|md|markdown|json|yml|yaml|xml|wxs|razor)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    internal static bool IsExplicitRepositoryEdit(string goal) =>
        SourceFileReference.IsMatch(goal ?? string.Empty) && ExplicitRepositoryEditCommand.IsMatch(goal ?? string.Empty);

    public static string InferRole(string goal)
    {
        string lowered = (goal ?? string.Empty).ToLowerInvariant();
        Match advisoryDeliverable = ExplicitAdvisoryDeliverableCommand.Match(lowered);
        bool advisoryNamesAFile = advisoryDeliverable.Success && IsNamedAdvisoryFile(advisoryDeliverable, lowered);
        if (advisoryDeliverable.Success && !advisoryNamesAFile)
        {
            string deliverable = advisoryDeliverable.Groups["deliverable"].Value;
            if (deliverable == "plan") return "planner";
            if (deliverable is "review" or "audit" or "critique") return "review";
        }

        string? matched = null;
        foreach ((string role, string[] keywords) in RoleKeywords)
        {
            if (!keywords.Any(keyword => MentionsKeyword(lowered, keyword))) continue;
            matched = role;
            break;
        }

        if (matched == null) return "coding";

        // "The existing tests must still pass" is a constraint on a code change, not a request for a
        // testing task - but plain substring matching read the word "test" inside it and routed the
        // goal to a role with no repository authority, where it could only ever fail. An explicit
        // edit verb aimed at a named source file settles what the goal is actually asking for.
        if (AdvisoryRoles.Contains(matched)
            && (!advisoryDeliverable.Success || advisoryNamesAFile)
            && IsExplicitRepositoryEdit(lowered))
            return "coding";

        return matched;
    }

    private static bool IsNamedAdvisoryFile(Match advisoryDeliverable, string goal)
    {
        int extensionStart = advisoryDeliverable.Groups["deliverable"].Index
            + advisoryDeliverable.Groups["deliverable"].Length;
        if (extensionStart >= goal.Length || goal[extensionStart] != '.') return false;

        int extensionEnd = extensionStart + 1;
        while (extensionEnd < goal.Length && char.IsLetterOrDigit(goal[extensionEnd])) extensionEnd++;
        return extensionEnd > extensionStart + 1
            && (extensionEnd == goal.Length || !char.IsLetterOrDigit(goal[extensionEnd]));
    }

    /// <summary>
    /// Matches a keyword at a word boundary, allowing the word to continue ("test" matches "tests"
    /// and "testing"). Raw substring matching found "test" inside "latest", "plan" inside
    /// "explanation" and "auth" inside "author", each of which silently changed which specialist -
    /// and therefore which tool grants - a task was given.
    /// </summary>
    private static bool MentionsKeyword(string loweredGoal, string keyword)
    {
        string trimmed = keyword.Trim();
        if (trimmed.Length == 0) return false;
        int index = 0;
        while ((index = loweredGoal.IndexOf(trimmed, index, StringComparison.Ordinal)) >= 0)
        {
            bool startsWord = index == 0 || !char.IsLetterOrDigit(loweredGoal[index - 1]);
            if (startsWord) return true;
            index++;
        }
        return false;
    }

    /// <summary>
    /// Dispatches only as many ready tasks as the active Execution Profile allows. Running and
    /// verifying tasks consume a slot. Waiting tasks normally consume one too, because they may
    /// resume immediately; the narrow exception is a parent explicitly suspended behind one or more
    /// live delegated children. That parent cannot make progress until its direct children become
    /// terminal, so charging both the suspended parent and its child can starve delegation when the
    /// profile is already at its parallel-task cap.
    /// </summary>
    public List<string> RunSchedulingPass()
    {
        ModelExecutionProfile execution = _executionProfile();
        int maxParallel = Math.Clamp(execution.Budget.MaxParallelTasks, 1, 8);
        List<AgentTaskDto> allTasks = _tasks.List();
        List<AgentTaskDto> activeTasks = _tasks.ListByStatus(
            AgentTaskStatus.Running,
            AgentTaskStatus.Waiting,
            AgentTaskStatus.Verifying);
        int active = activeTasks.Count(task => ConsumesExecutionSlot(task, allTasks));
        int availableSlots = Math.Max(0, maxParallel - active);
        if (availableSlots == 0) return new List<string>();

        var dispatched = new List<string>();
        foreach (AgentTaskDto task in _tasks.ListByStatus(AgentTaskStatus.Queued))
        {
            if (dispatched.Count >= availableSlots) break;
            if (!_tasks.IsReadyToRun(task.Id))
            {
                string? unreachableReason = FindUnreachableDependencyReason(task);
                if (unreachableReason != null)
                {
                    _tasks.UpdateStatus(task.Id, new UpdateAgentTaskStatusRequest
                    {
                        Status = AgentTaskStatus.Blocked,
                        Stage = "Blocked: " + unreachableReason
                    });
                    _tasks.AppendLog(task.Id, "Scheduler cascaded task to blocked: " + unreachableReason);
                }
                continue;
            }

            string? role = task.Role;
            if (string.IsNullOrWhiteSpace(role))
            {
                role = InferRole(task.Goal);
                _tasks.SetRole(task.Id, role);
            }

            _tasks.UpdateStatus(task.Id, new UpdateAgentTaskStatusRequest
            {
                Status = AgentTaskStatus.Running,
                Stage = "Dispatched to " + role + " role"
            });
            _tasks.AppendLog(task.Id,
                $"Scheduler dispatched task to role: {role}; execution profile={execution.QualityProfile}, " +
                $"parallel slot {active + dispatched.Count + 1}/{maxParallel}.");
            dispatched.Add(task.Id);
        }
        return dispatched;
    }

    private static bool ConsumesExecutionSlot(AgentTaskDto task, IReadOnlyList<AgentTaskDto> allTasks)
    {
        if (!string.Equals(task.Status, AgentTaskStatus.Waiting, StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(task.Stage, TaskStore.ProjectRepositoryProvisioningStage, StringComparison.Ordinal)) return false;
        if (task.Stage?.StartsWith(DelegationWaitingStagePrefix, StringComparison.Ordinal) != true) return true;

        bool hasLiveDelegatedChild = allTasks.Any(child =>
            string.Equals(child.ParentId, task.Id, StringComparison.Ordinal) &&
            !AgentTaskStatus.Terminal.Contains(child.Status));
        return !hasLiveDelegatedChild;
    }

    private string? FindUnreachableDependencyReason(AgentTaskDto task)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>(task.DependsOnTaskIds);

        while (queue.Count > 0)
        {
            string depId = queue.Dequeue();
            if (!visited.Add(depId)) continue;

            AgentTaskDto? dep = _tasks.Get(depId);
            if (dep == null) continue;
            if (dep.Status == AgentTaskStatus.Failed || dep.Status == AgentTaskStatus.Cancelled)
            {
                return "depends on task " + dep.Id + " (" + (dep.Role ?? "unassigned") + ") which is " +
                       dep.Status + " and can never complete";
            }
            foreach (string grandDepId in dep.DependsOnTaskIds) queue.Enqueue(grandDepId);
        }
        return null;
    }
}
