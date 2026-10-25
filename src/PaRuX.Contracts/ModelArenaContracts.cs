namespace PaRuX.Contracts;

/// <summary>A built-in Model Arena task. The prompt is fixed text; an automatic check, when present, is a static (non-executing) rule.</summary>
public sealed class ArenaTaskDto
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    /// <summary>"chat" | "reasoning" | "coding" | "custom".</summary>
    public string Category { get; set; } = "chat";
    public string Prompt { get; set; } = string.Empty;
    public bool HasAutomaticCheck { get; set; }
}

public sealed class ArenaStartRequest
{
    /// <summary>2 or 3 installed model file names (never paths).</summary>
    public List<string> ModelFileNames { get; set; } = new();
    public List<string> TaskIds { get; set; } = new();
    /// <summary>Optional user prompt run as one extra "custom" task (no automatic check).</summary>
    public string? CustomPrompt { get; set; }
    public int MaxTokens { get; set; } = 256;
}

public sealed class ArenaCheckResultDto
{
    /// <summary>"none" | "automatic-check".</summary>
    public string Kind { get; set; } = "none";
    public bool? Passed { get; set; }
    public string Detail { get; set; } = string.Empty;
}

public sealed class ArenaModelResultDto
{
    public string ModelFileName { get; set; } = string.Empty;
    /// <summary>"completed" | "failed" | "skipped" | "cancelled" | "timed_out".</summary>
    public string Status { get; set; } = "skipped";
    public string? Error { get; set; }
    public string Output { get; set; } = string.Empty;
    public double? LoadMs { get; set; }
    public double? TtftMs { get; set; }
    public double? DecodeTokensPerSecond { get; set; }
    public int GeneratedTokens { get; set; }
    public double? TotalMs { get; set; }
    public ArenaCheckResultDto Check { get; set; } = new();
}

public sealed class ArenaPromptResultDto
{
    public string TaskId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Prompt { get; set; } = string.Empty;
    public List<ArenaModelResultDto> Results { get; set; } = new();
    /// <summary>Model file name the user judged better for this prompt; null when not judged. Never auto-set.</summary>
    public string? UserPick { get; set; }
    public DateTime? UserPickUtc { get; set; }
}

public sealed class ArenaRunDto
{
    public string Id { get; set; } = string.Empty;
    /// <summary>"running" | "completed" | "failed" | "cancelled".</summary>
    public string Status { get; set; } = "running";
    public string Stage { get; set; } = string.Empty;
    public string? Error { get; set; }
    public DateTime StartedUtc { get; set; }
    public DateTime? CompletedUtc { get; set; }
    public List<string> ModelFileNames { get; set; } = new();
    public List<ArenaPromptResultDto> Prompts { get; set; } = new();
    public string Provenance { get; set; } = "measured locally on this PC";
}

public sealed class ArenaJudgeRequest
{
    public int PromptIndex { get; set; }
    /// <summary>One of the run's models, or null to clear the judgement.</summary>
    public string? ModelFileName { get; set; }
}
