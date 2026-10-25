namespace PaRuX.Contracts;

// ---- Interactive Response Protocol: structured assistant messages, per spec
// ---- section 42. Chat replies stop being markdown-only - a reply can carry
// ---- real, typed blocks (citations, progress, cards) alongside its text.
// ---- Streamed as an additive SSE marker frame (parux_event: "interactive"),
// ---- the same pattern already used for skill_selected/rag_sources - a strict
// ---- OpenAI-compatible client ignores it; a PaRuX client renders it. ----

public sealed class InteractiveMessage
{
    /// <summary>Plain text/markdown, always present - every client can render this alone and be correct.</summary>
    public string Markdown { get; set; } = string.Empty;
    public List<InteractiveBlock> Blocks { get; set; } = new();
    public List<InteractiveAction> Actions { get; set; } = new();
    public List<InteractiveCitation> Citations { get; set; } = new();
    public Dictionary<string, string> Metadata { get; set; } = new();
}

public sealed class InteractiveBlock
{
    /// <summary>"source_card" | "file_card" | "image_card" | "presentation_card" | "skill_card" | "progress" | "table"</summary>
    public string Type { get; set; } = string.Empty;
    /// <summary>Block-specific payload as JSON (kept as a string so new block types never require a contract change).</summary>
    public string DataJson { get; set; } = string.Empty;
}

public sealed class InteractiveAction
{
    /// <summary>"button" | "toggle" | "dropdown" | "confirmation"</summary>
    public string Type { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    /// <summary>Opaque id the client sends back verbatim if the user activates this action.</summary>
    public string ActionId { get; set; } = string.Empty;
    public List<string> Options { get; set; } = new();
}

public sealed class InteractiveCitation
{
    public int Index { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
}
