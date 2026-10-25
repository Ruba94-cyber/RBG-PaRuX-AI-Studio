namespace PaRuX.Contracts;

/// <summary>
/// Desktop-only binding between a durable agent task and the repository root it is allowed to use.
/// This is intentionally separate from AgentTaskDto: paired/mobile clients may inspect/control a
/// task without learning workstation filesystem paths, and cannot widen a task to an arbitrary path.
/// </summary>
public sealed class AgentWorkspaceAssignmentDto
{
    public string TaskId { get; set; } = string.Empty;
    public string WorkspaceRoot { get; set; } = string.Empty;
    public DateTime AssignedUtc { get; set; }
}

public sealed class SetAgentWorkspaceAssignmentRequest
{
    public string WorkspaceRoot { get; set; } = string.Empty;
}
