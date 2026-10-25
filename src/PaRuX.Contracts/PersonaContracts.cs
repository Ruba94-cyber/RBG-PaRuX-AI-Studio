namespace PaRuX.Contracts;

// ---- Personas: a named system-prompt addition that shapes the assistant's tone/personality,
// ---- layered on top of whatever route/skill-specific system prompt is already in play (see
// ---- ChatProxyService.ApplyPersonaPrompt). Server-owned, same reasoning as Memory/RAG/Skills -
// ---- every paired device gets the same personality, not just the desktop client. ----

public sealed class Persona
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string SystemPrompt { get; set; } = string.Empty;
    /// <summary>Built-ins ship with PaRuX and can't be deleted or edited; user-created personas can be.</summary>
    public bool IsBuiltIn { get; set; }
}

public sealed class PersonaListResponse
{
    public List<Persona> Personas { get; set; } = new();
    public string ActivePersonaId { get; set; } = string.Empty;
}

public sealed class CreatePersonaRequest
{
    public string Name { get; set; } = string.Empty;
    public string SystemPrompt { get; set; } = string.Empty;
}

public sealed class SetActivePersonaRequest
{
    public string PersonaId { get; set; } = string.Empty;
}
