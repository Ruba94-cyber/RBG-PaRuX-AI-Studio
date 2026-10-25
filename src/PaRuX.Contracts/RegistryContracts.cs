namespace PaRuX.Contracts;

/// <summary>
/// vNext Phase 29 - the unified Skill/Plugin/MCP registry view (directive section 50-54). "The
/// user should not need to care whether a capability internally uses: Skill / MCP / REST / CLI /
/// Python / Native API. The UI should present: Capability / Integration, and Inspector reveals
/// technical implementation." This is a read-model aggregating the two real, separate registries
/// that already exist (SkillRegistryService, McpClientRegistry) into one common shape - it does
/// not change how either underlying system works, and it is not a third persistence layer: there
/// is nothing here to store, only to combine on read.
/// </summary>
public sealed class UnifiedCapabilityDto
{
    public required string Id { get; set; }
    public required string Name { get; set; }
    public required string Description { get; set; }
    /// <summary>"skill" | "mcp" - the underlying implementation kind, shown by the Inspector, not
    /// the primary user-facing label (directive section 54).</summary>
    public required string ProviderType { get; set; }
    public required string Source { get; set; }
    public required bool Enabled { get; set; }
    /// <summary>Only meaningful for provider_type=mcp - whether the last real connection attempt
    /// succeeded. Always true for skills (they have no separate connection concept).</summary>
    public required bool Reachable { get; set; }
    /// <summary>Tool/capability count - a skill's own declared AllowedTools count, or an MCP
    /// server's real discovered tool count.</summary>
    public required int ToolCount { get; set; }
    /// <summary>Raw JSON of the underlying SkillManifest or McpServerStatus, for an Inspector UI
    /// to show "how this actually works" without this DTO needing to anticipate every field
    /// either underlying type might ever have.</summary>
    public required string TechnicalDetailsJson { get; set; }
    /// <summary>Phase 37 (License/Trust Manager) v1: populated for skills from SkillManifest.
    /// License/Author; left null for MCP servers, which have no license/author concept of their
    /// own - they're arbitrary user-configured commands, not published packages.</summary>
    public string? License { get; set; }
    public string? Author { get; set; }
}

public sealed class UnifiedCapabilityListResponse
{
    public required List<UnifiedCapabilityDto> Capabilities { get; set; }
}
