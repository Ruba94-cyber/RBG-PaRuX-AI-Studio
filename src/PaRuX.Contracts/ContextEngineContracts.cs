namespace PaRuX.Contracts;

/// <summary>
/// One explainable unit selected by the central Context Engine. Content is always bounded before
/// it leaves the server; Source identifies the real backing record/file/entity/URL.
/// </summary>
public sealed class ContextItemDto
{
    public required string Kind { get; set; }
    public required string Source { get; set; }
    public required string Title { get; set; }
    public required string Content { get; set; }
    public required int UsedCharacters { get; set; }
    public required bool Truncated { get; set; }
}

/// <summary>A single budgeted, deduplicated context pack shared by chat and, through the public
/// build endpoint, other local runtimes. Individual stores remain the source of truth.</summary>
public sealed class ContextPackDto
{
    public required string PromptText { get; set; }
    public required List<ContextItemDto> Items { get; set; }
    public required List<string> SourceLabels { get; set; }
    public required int UsedCharacters { get; set; }
    public required int MaxCharacters { get; set; }
    public required bool Truncated { get; set; }
    public required int DuplicateItemsDropped { get; set; }
}

/// <summary>Authenticated Knowledge/Context search request. Web remains subject to the user's
/// global Internet setting even when requested here. Conversation history is opt-in and scoped
/// to one explicit conversation id; the Context Engine never searches unrelated chats implicitly.</summary>
public sealed class ContextBuildRequest
{
    public const int MaximumQueryCharacters = 4_000;
    public required string Query { get; set; }
    public int MaxCharacters { get; set; } = 12_000;
    public bool IncludeMemory { get; set; } = true;
    public bool IncludeRag { get; set; } = true;
    public bool IncludeGraph { get; set; } = true;
    public bool IncludeArtifacts { get; set; } = true;
    public bool IncludeWeb { get; set; }
    public bool IncludeRepository { get; set; }
    public string? RepositoryPath { get; set; }
    /// <summary>When true, include bounded text extracted from the explicitly selected persisted
    /// conversation. No other conversation is searched or inferred.</summary>
    public bool IncludeConversationHistory { get; set; }
    /// <summary>Required when IncludeConversationHistory is true. This is the id used by
    /// ConversationStore and the /api/v1/conversations endpoints.</summary>
    public string? ConversationId { get; set; }
}
