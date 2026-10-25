using System.ComponentModel;
using ModelContextProtocol.Server;
using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>
/// PaRuX's MCP surface, built on the official ModelContextProtocol.AspNetCore
/// SDK rather than a hand-rolled JSON-RPC implementation. Deliberately narrow:
/// only read-only, already-safe operations are exposed (memory/RAG search,
/// skill listing) - shell, file-write, Computer Use, and coding capabilities
/// are never reachable through MCP, so it can never become a way around the
/// permission boundaries those already enforce. The server-side auth token
/// middleware that gates every other /api/v1/* route also gates the MCP
/// endpoint, so MCP access requires the same credential as everything else.
/// </summary>
[McpServerToolType]
public static class McpTools
{
    [McpServerTool, Description("Searches PaRuX's server-owned Memory store (User/Project/Conversation/Agent scopes) by keyword overlap and returns the top matches. Read-only.")]
    public static List<MemoryEntryDto> SearchMemory(MemoryStore memory, [Description("The search text")] string query, [Description("Maximum results to return")] int maxResults = 5) =>
        memory.Retrieve(query, Math.Clamp(maxResults, 1, 20));

    [McpServerTool, Description("Searches PaRuX's server-owned local RAG index (your indexed folders) by keyword overlap and returns the top matching chunks with their source file. Read-only.")]
    public static List<McpRagChunk> SearchDocuments(RagStore rag, [Description("The search text")] string query, [Description("Maximum results to return")] int maxResults = 4) =>
        rag.Retrieve(query, Math.Clamp(maxResults, 1, 20))
            .Select(c => new McpRagChunk { FilePath = c.FilePath, Content = c.Content })
            .ToList();

    [McpServerTool, Description("Lists PaRuX's available Skills - id, display name, description, category, and whether each is currently enabled. Read-only.")]
    public static List<McpSkillSummary> ListSkills(SkillRegistryService skills) =>
        skills.List().Select(s => new McpSkillSummary
        {
            Id = s.Id, DisplayName = s.DisplayName, Description = s.Description,
            Category = s.Category, Enabled = s.Enabled
        }).ToList();
}

public sealed class McpRagChunk
{
    public string FilePath { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
}

public sealed class McpSkillSummary
{
    public string Id { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public bool Enabled { get; set; }
}
