namespace PaRuX.Contracts;

// ---- Skills: declarative, inspectable capabilities. Server-owned registry,
// ---- shared by Windows and Android so both see the same skill set and routing decisions. ----

public sealed class SkillManifest
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Icon { get; set; } = string.Empty;
    public string Category { get; set; } = "general";
    public string Version { get; set; } = "1.0.0";
    public string Author { get; set; } = "PaRuX";
    /// <summary>"builtin" | "user" | "imported"</summary>
    public string Source { get; set; } = "builtin";
    public string License { get; set; } = string.Empty;
    public string Attribution { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public List<string> Platforms { get; set; } = new() { "Windows", "Android" };
    public string PreferredModelRole { get; set; } = "MAIN_LLM";
    public List<string> AllowedModelRoles { get; set; } = new();
    public List<string> RequiredCapabilities { get; set; } = new();
    public List<string> AllowedTools { get; set; } = new();
    public string InternetPolicy { get; set; } = "off";
    public string MemoryPolicy { get; set; } = "optional";
    public string RagPolicy { get; set; } = "optional";
    public string SlashCommand { get; set; } = string.Empty;
    public List<string> TriggerExamples { get; set; } = new();
    public List<string> Workflow { get; set; } = new();
    public List<string> Dependencies { get; set; } = new();
    public SkillPermissions Permissions { get; set; } = new();
    public int TimeoutSeconds { get; set; } = 120;
    public string RetryPolicy { get; set; } = "none";
    public string FallbackSkillId { get; set; } = string.Empty;
    public string ConfirmationPolicy { get; set; } = "never";
    public List<string> SubSkillIds { get; set; } = new();
    public List<SkillParameterDto> Parameters { get; set; } = new();
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
}

public sealed class SkillParameterDto
{
    public string Name { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string Type { get; set; } = "select";
    public List<string> Options { get; set; } = new();
    public string Default { get; set; } = string.Empty;
}

/// <summary>
/// Declarative permission matrix for Skills/Tools/MCP. Existing manifests deserialize safely:
/// every newly-added permission defaults false, preserving least privilege. These flags are a
/// server-side trust boundary, not merely UI labels.
/// </summary>
public sealed class SkillPermissions
{
    public bool ReadFiles { get; set; }
    public bool WriteFiles { get; set; }
    public bool DeleteFiles { get; set; }
    public bool Shell { get; set; }
    public bool Network { get; set; }
    public bool Browser { get; set; }
    public bool ComputerUse { get; set; }
    public bool ExternalProcesses { get; set; }
}

public sealed class SkillCreateRequest
{
    public SkillManifest Manifest { get; set; } = new();
}

public sealed class SkillEnabledRequest
{
    public bool Enabled { get; set; } = true;
}

public sealed class SkillRouteRequest
{
    public string Message { get; set; } = string.Empty;
    public string? ForcedSkillId { get; set; }
}

public sealed class SkillRouteResult
{
    public SkillManifest? Skill { get; set; }
    public string Mode { get; set; } = "none";
    public string Reason { get; set; } = string.Empty;
}
