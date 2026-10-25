using System.Text.Json;
using PaRuX.Contracts;

namespace PaRuX.Server.Registry;

/// <summary>
/// vNext Phase 29 - combines SkillRegistryService and McpClientRegistry into one read-model
/// (directive section 50-54: a unified Capability/Integration view, technical details available
/// for an Inspector but not the primary label). Deliberately not a new store or a new
/// permission/security boundary - both underlying registries keep their own real enable/disable,
/// install, and connection logic exactly as they already work; this only presents them together.
/// </summary>
public sealed class UnifiedRegistryService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly SkillRegistryService _skills;
    private readonly McpClientRegistry _mcp;

    public UnifiedRegistryService(SkillRegistryService skills, McpClientRegistry mcp)
    {
        _skills = skills;
        _mcp = mcp;
    }

    public async Task<List<UnifiedCapabilityDto>> ListAsync(CancellationToken cancellationToken)
    {
        var capabilities = new List<UnifiedCapabilityDto>();

        foreach (SkillManifest skill in _skills.List())
        {
            capabilities.Add(new UnifiedCapabilityDto
            {
                Id = "skill:" + skill.Id,
                Name = skill.DisplayName,
                Description = skill.Description,
                ProviderType = "skill",
                Source = skill.Source,
                Enabled = skill.Enabled,
                Reachable = true,
                ToolCount = skill.AllowedTools.Count,
                TechnicalDetailsJson = JsonSerializer.Serialize(skill, JsonOptions),
                License = string.IsNullOrWhiteSpace(skill.License) ? null : skill.License,
                Author = skill.Author,
            });
        }

        List<McpServerStatus> mcpStatuses = await _mcp.GetStatusesAsync(cancellationToken).ConfigureAwait(false);
        foreach (McpServerStatus server in mcpStatuses)
        {
            capabilities.Add(new UnifiedCapabilityDto
            {
                Id = "mcp:" + server.Id,
                Name = server.Name,
                Description = "MCP server (" + server.Command + ")",
                ProviderType = "mcp",
                Source = "mcp",
                Enabled = server.Enabled,
                Reachable = server.Connected,
                ToolCount = server.ToolCount,
                TechnicalDetailsJson = JsonSerializer.Serialize(server, JsonOptions)
            });
        }

        return capabilities;
    }
}
