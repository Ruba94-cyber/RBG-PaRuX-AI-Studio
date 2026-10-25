namespace PaRuX.Contracts;

/// <summary>
/// One server-resolved piece of durable Project context. Identity is an opaque PaRuX id/source,
/// never a Windows filesystem path. Content is already bounded and safe to show/inject.
/// </summary>
public sealed class ProjectKnowledgeItemDto
{
    public string Kind { get; set; } = string.Empty;
    public string Identity { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? Status { get; set; }
    public string Content { get; set; } = string.Empty;
    public DateTime UpdatedUtc { get; set; }
}

/// <summary>
/// Explicit, bounded context pack for one durable Project. This is intentionally different from a
/// global semantic search: callers name the Project id and Core resolves only authoritative work
/// attached to that Project (currently real AgentTasks and ArtifactStore records).
/// </summary>
public sealed class ProjectKnowledgePackDto
{
    public string ProjectId { get; set; } = string.Empty;
    public string Goal { get; set; } = string.Empty;
    public DateTime UpdatedUtc { get; set; }
    public List<ProjectKnowledgeItemDto> Items { get; set; } = new();
    /// <summary>Ready-to-inject bounded text representation of Goal + Items.</summary>
    public string PromptText { get; set; } = string.Empty;
    public int UsedCharacters { get; set; }
    public int MaxCharacters { get; set; }
    public bool Truncated { get; set; }
}
