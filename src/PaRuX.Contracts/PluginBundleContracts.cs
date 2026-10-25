namespace PaRuX.Contracts;

public sealed class PaRuXPluginManifest
{
    public string Id { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Version { get; set; } = "1.0.0";
    public string Publisher { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string License { get; set; } = string.Empty;
    public List<string> SkillFiles { get; set; } = new();
    public List<string> AutomationFiles { get; set; } = new();
    public List<string> McpFiles { get; set; } = new();
    public PluginPermissionDeclaration Permissions { get; set; } = new();
}

public sealed class PluginPermissionDeclaration
{
    public bool ReadFiles { get; set; }
    public bool WriteFiles { get; set; }
    public bool DeleteFiles { get; set; }
    public bool Shell { get; set; }
    public bool Network { get; set; }
    public bool Browser { get; set; }
    public bool ComputerUse { get; set; }
    public bool ExternalProcesses { get; set; }
    public bool Automations { get; set; }
    public bool RemoteMcp { get; set; }
}

public sealed class PluginBundleInspection
{
    public bool Valid { get; set; }
    public string? Error { get; set; }
    public string BundleSha256 { get; set; } = string.Empty;
    public PaRuXPluginManifest Manifest { get; set; } = new();
    public string PublisherKeyFingerprint { get; set; } = string.Empty;
    public bool SignaturePresent { get; set; }
    public bool SignatureValid { get; set; }
    public bool PublisherTrusted { get; set; }
    public PluginPermissionDeclaration EffectivePermissions { get; set; } = new();
    public List<string> PermissionDiff { get; set; } = new();
    public int SkillCount { get; set; }
    public int AutomationCount { get; set; }
    public int McpCount { get; set; }
    public List<string> Warnings { get; set; } = new();
}

public sealed class PluginInstallRequest
{
    public string BundlePath { get; set; } = string.Empty;
    public bool AllowUnsigned { get; set; }
    public bool TrustPublisher { get; set; }
}

public sealed class PluginInstallResult
{
    public string PluginId { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public bool Installed { get; set; }
    public List<string> SkillIds { get; set; } = new();
    public List<string> AutomationIds { get; set; } = new();
    public int StagedMcpDefinitions { get; set; }
    public string InstallDirectory { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
}

public sealed class PluginPublisherTrustDto
{
    public string Fingerprint { get; set; } = string.Empty;
    public string Publisher { get; set; } = string.Empty;
    public DateTime TrustedUtc { get; set; }
}
