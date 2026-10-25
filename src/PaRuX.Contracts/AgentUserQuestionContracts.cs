namespace PaRuX.Contracts;

/// <summary>
/// One durable user question raised by a long-running agent. The task remains paused until this
/// exact question id is answered; options are display/data only and never executable authority.
/// </summary>
public sealed class AgentUserQuestionDto
{
    public string Id { get; set; } = string.Empty;
    public string Question { get; set; } = string.Empty;
    public List<string> Options { get; set; } = new();
    public bool AllowOther { get; set; } = true;
    public DateTime AskedUtc { get; set; }
}

public sealed class AgentUserQuestionExchangeDto
{
    public string QuestionId { get; set; } = string.Empty;
    public string Question { get; set; } = string.Empty;
    public List<string> Options { get; set; } = new();
    public bool AllowOther { get; set; }
    public string Answer { get; set; } = string.Empty;
    public DateTime AskedUtc { get; set; }
    public DateTime AnsweredUtc { get; set; }
}

/// <summary>Safe cross-surface projection: pending prompt plus bounded answered history.</summary>
public sealed class AgentUserQuestionStateDto
{
    public AgentUserQuestionDto? PendingQuestion { get; set; }
    public List<AgentUserQuestionExchangeDto> RecentExchanges { get; set; } = new();
}

public sealed class AskAgentUserQuestionRequest
{
    public string Question { get; set; } = string.Empty;
    public List<string> Options { get; set; } = new();
    public bool AllowOther { get; set; } = true;
}

public sealed class AnswerAgentUserQuestionRequest
{
    public string QuestionId { get; set; } = string.Empty;
    public string Answer { get; set; } = string.Empty;
}
