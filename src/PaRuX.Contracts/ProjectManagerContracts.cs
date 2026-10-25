namespace PaRuX.Contracts;

public sealed class ProjectDto
{
    public string Id { get; set; } = string.Empty;
    public string Goal { get; set; } = string.Empty;
    public List<string> TaskIds { get; set; } = new();
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
}

public sealed class ProjectListResponse
{
    public List<ProjectDto> Projects { get; set; } = new();
}

public sealed class CreateProjectRequest
{
    public string Goal { get; set; } = string.Empty;
}

/// <summary>
/// Attaches an existing durable AgentTask to an existing Project. The Core owns graph entity/
/// relation creation and idempotency so remote clients never need to compose Context Graph writes.
/// </summary>
public sealed class AttachProjectTaskRequest
{
    public string TaskId { get; set; } = string.Empty;
}
