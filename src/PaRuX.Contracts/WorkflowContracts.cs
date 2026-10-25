namespace PaRuX.Contracts;

// ---- Workflow Builder: reusable, versionable templates that instantiate into a REAL
// ---- dependency-respecting task graph via the existing TaskStore/AgentOrchestrator. A workflow
// ---- version is immutable: editing means creating a new row linked to PreviousVersionId. ----

public sealed class WorkflowStepTemplate
{
    public string Name { get; set; } = string.Empty;
    /// <summary>May contain the literal placeholder "{{input}}", substituted with whatever the
    /// caller passes to RunWorkflowRequest.Input at run time.</summary>
    public string Goal { get; set; } = string.Empty;
    public string? Role { get; set; }
    /// <summary>Indexes into the workflow's own Steps list - each must be strictly less than this
    /// step's own index, enforced at create time.</summary>
    public List<int> DependsOnStepIndexes { get; set; } = new();
}

public sealed class WorkflowDto
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public List<WorkflowStepTemplate> Steps { get; set; } = new();
    /// <summary>1 for an original template; previous.VersionNumber + 1 for an immutable revision.</summary>
    public int VersionNumber { get; set; } = 1;
    /// <summary>Immediate predecessor, null for the first version. Old versions remain runnable.</summary>
    public string? PreviousVersionId { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
}

public sealed class CreateWorkflowRequest
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public List<WorkflowStepTemplate> Steps { get; set; } = new();
    /// <summary>Set when saving an edited copy of an existing workflow. The Core verifies the id
    /// exists and creates a new immutable version rather than overwriting the predecessor.</summary>
    public string? PreviousVersionId { get; set; }
}

public sealed class RunWorkflowRequest
{
    public string? Input { get; set; }
}

public sealed class WorkflowRunResult
{
    public string WorkflowId { get; set; } = string.Empty;
    /// <summary>Real AgentTask ids, in the same order as the workflow's Steps - CreatedTaskIds[i]
    /// is the real task created for Steps[i].</summary>
    public List<string> CreatedTaskIds { get; set; } = new();
}
