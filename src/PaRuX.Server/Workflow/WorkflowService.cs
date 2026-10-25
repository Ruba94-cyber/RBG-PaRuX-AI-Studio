using PaRuX.Contracts;
using PaRuX.Server.Orchestration;

namespace PaRuX.Server.Workflow;

/// <summary>
/// Workflow Builder: validates each immutable template version and instantiates it as a real
/// dependency-respecting task graph through TaskStore. A revision points to exactly one previous
/// version; branching from a version that already has a successor is rejected so version numbers
/// remain a simple, explainable linear history.
/// </summary>
public sealed class WorkflowService
{
    public const int MaximumSteps = 64;
    public const int MaximumGoalCharacters = 16_000;
    public const int MaximumInputCharacters = 64_000;

    private readonly WorkflowStore _store;
    private readonly TaskStore _tasks;

    public WorkflowService(WorkflowStore store, TaskStore tasks)
    {
        _store = store;
        _tasks = tasks;
    }

    public WorkflowDto Create(CreateWorkflowRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name)) throw new InvalidDataException("A workflow name is required.");
        if (request.Steps == null || request.Steps.Count == 0) throw new InvalidDataException("A workflow needs at least one step.");
        if (request.Steps.Count > MaximumSteps)
            throw new InvalidDataException($"A workflow may contain at most {MaximumSteps} steps.");

        if (!string.IsNullOrWhiteSpace(request.PreviousVersionId))
        {
            WorkflowDto previous = _store.Get(request.PreviousVersionId)
                ?? throw new InvalidDataException("Previous workflow version not found.");
            if (_store.HasSuccessor(previous.Id))
                throw new InvalidDataException(
                    $"Workflow v{previous.VersionNumber} already has a successor. Create the next revision from the latest version instead of branching history.");
        }

        for (int i = 0; i < request.Steps.Count; i++)
        {
            WorkflowStepTemplate step = request.Steps[i];
            if (string.IsNullOrWhiteSpace(step.Goal))
                throw new InvalidDataException($"Step {i} needs a non-empty goal.");
            if (step.Goal.Length > MaximumGoalCharacters)
                throw new InvalidDataException($"Step {i} goal exceeds the {MaximumGoalCharacters}-character limit.");
            if (!string.IsNullOrWhiteSpace(step.Role)
                && !AgentWorkerService.CanExecute(step.Role)
                && !AgentWorkerService.RequiresDesktopWorker(step.Role))
                throw new InvalidDataException(
                    $"Step {i} uses unknown or non-executable agent role \"{step.Role}\". " +
                    "Known executable roles are planner, research, review, documentation, testing, security, coding, binary-analysis, ui-verification, and computer-use.");

            // A duplicate dependency does not add meaning and would otherwise produce duplicate
            // task ids in the instantiated graph. Normalize once at creation so the persisted
            // template itself is canonical, not only each individual run.
            step.DependsOnStepIndexes = step.DependsOnStepIndexes.Distinct().ToList();
            foreach (int dependsOn in step.DependsOnStepIndexes)
            {
                if (dependsOn < 0 || dependsOn >= request.Steps.Count)
                    throw new InvalidDataException($"Step {i} depends on an out-of-range step index {dependsOn}.");
                if (dependsOn >= i)
                    throw new InvalidDataException(
                        $"Step {i} depends on step {dependsOn}, which is not strictly earlier - " +
                        "a step may only depend on an earlier step, so every workflow is a valid acyclic graph by construction.");
            }
        }

        return _store.Create(request);
    }

    public List<WorkflowDto> List() => _store.List();
    public WorkflowDto? Get(string id) => _store.Get(id);
    public bool Delete(string id) => _store.Delete(id);

    public WorkflowRunResult Run(string workflowId, RunWorkflowRequest request)
    {
        WorkflowDto workflow = _store.Get(workflowId) ?? throw new InvalidDataException("Workflow not found.");
        string input = request.Input ?? string.Empty;
        if (input.Length > MaximumInputCharacters)
            throw new InvalidDataException($"Workflow input exceeds the {MaximumInputCharacters}-character limit.");

        var createdIds = new string[workflow.Steps.Count];
        for (int i = 0; i < workflow.Steps.Count; i++)
        {
            WorkflowStepTemplate step = workflow.Steps[i];
            string goal = input.Length == 0 ? step.Goal : step.Goal.Replace("{{input}}", input, StringComparison.Ordinal);
            if (goal.Length > MaximumGoalCharacters + MaximumInputCharacters)
                throw new InvalidDataException("Expanded workflow step goal is too large to execute safely.");
            List<string>? dependsOn = step.DependsOnStepIndexes.Count > 0
                ? step.DependsOnStepIndexes.Select(index => createdIds[index]).Distinct().ToList()
                : null;

            AgentTaskDto task = _tasks.Create(new CreateAgentTaskRequest
            {
                Goal = goal,
                Role = step.Role,
                DependsOnTaskIds = dependsOn,
            });
            createdIds[i] = task.Id;
        }

        return new WorkflowRunResult { WorkflowId = workflowId, CreatedTaskIds = createdIds.ToList() };
    }
}