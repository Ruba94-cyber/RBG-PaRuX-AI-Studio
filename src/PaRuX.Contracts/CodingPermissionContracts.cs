namespace PaRuX.Contracts;

/// <summary>Permission preset captured by one desktop coding task, never a global setting.</summary>
public static class CodingRunPermissionModes
{
    public const string Bypass = "bypass";
    public const string Ask = "ask";
    public const string Plan = "plan";

    public static bool IsKnown(string? value) =>
        string.Equals(value, Bypass, StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, Ask, StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, Plan, StringComparison.OrdinalIgnoreCase);

    public static string Normalize(string? value) => IsKnown(value)
        ? value!.Trim().ToLowerInvariant()
        : Bypass;
}

/// <summary>
/// Canonical permission profile copied into a NEW coding task. The profile controls only package
/// restore authority; normal shell execution never gains network access. Root coding tasks also get
/// the bounded agent.delegate capability through <see cref="RootCapabilitiesFor"/> so the native
/// subagent action is reachable without tying delegation semantics to a network profile.
/// </summary>
public static class CodingTaskPermissionProfile
{
    public const string Offline = "offline";
    public const string PackageFetch = "package-fetch";
    public const string TrustedRestore = "trusted-restore";

    public const string SafeRestoreCapability = "coding.package-restore";
    public const string ScriptedRestoreCapability = "coding.package-restore.scripts";
    public const string AgentDelegateCapability = "agent.delegate";

    public static bool IsKnown(string? value)
    {
        string normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
        return normalized is Offline or PackageFetch or TrustedRestore;
    }

    public static string Normalize(string? value) => (value ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        PackageFetch => PackageFetch,
        TrustedRestore => TrustedRestore,
        _ => Offline
    };

    /// <summary>Restore-only capabilities represented by the profile itself.</summary>
    public static IReadOnlyList<string> CapabilitiesFor(string? profile) => Normalize(profile) switch
    {
        PackageFetch => new[] { SafeRestoreCapability },
        TrustedRestore => new[] { SafeRestoreCapability, ScriptedRestoreCapability },
        _ => Array.Empty<string>()
    };

    /// <summary>
    /// Capabilities for a newly-created root coding task. Delegation is baseline coding authority;
    /// child creation still cannot widen it because TaskStore enforces subset grants and depth/fanout.
    /// </summary>
    public static IReadOnlyList<string> RootCapabilitiesFor(string? profile)
    {
        var grants = new List<string> { AgentDelegateCapability };
        grants.AddRange(CapabilitiesFor(profile));
        return grants;
    }

    public static string FromCapabilities(IEnumerable<string>? capabilities)
    {
        var grants = new HashSet<string>(capabilities ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        if (grants.Contains(ScriptedRestoreCapability)) return TrustedRestore;
        if (grants.Contains(SafeRestoreCapability)) return PackageFetch;
        return Offline;
    }
}

/// <summary>
/// Loopback-desktop Project creation request. Mobile/LAN Project creation intentionally keeps the
/// existing request and therefore receives the offline coding profile by default.
/// </summary>
public sealed class CreateDesktopProjectRequest
{
    public string Goal { get; set; } = string.Empty;
    public string? WorkspaceRoot { get; set; }
    public string CodingPermissionProfile { get; set; } = CodingTaskPermissionProfile.Offline;
}

/// <summary>
/// Loopback-desktop request used by Chat, CLI and IDE integrations to create the same native coding
/// task shape: explicit repository authority, bounded self-delegation and an immutable-at-creation
/// package-restore profile. Additional repositories are collected before task creation and persisted
/// as one complete authority set, so the coding worker can never observe a partially-built topology.
/// The generic task DTO still never carries workstation paths.
/// </summary>
public sealed class CreateDesktopCodingTaskRequest
{
    public string Goal { get; set; } = string.Empty;
    public string WorkspaceRoot { get; set; } = string.Empty;
    public List<AddAgentRepositoryAuthorityRequest> AdditionalRepositories { get; set; } = new();
    public string CodingPermissionProfile { get; set; } = CodingTaskPermissionProfile.Offline;
    public string CodingRunPermissionMode { get; set; } = CodingRunPermissionModes.Bypass;
    public int MaxRepairAttempts { get; set; } = 2;
}

/// <summary>
/// Desktop-only manual resume request. Core owns the state-transition rules; Source is audit text,
/// never an authority selector.
/// </summary>
public sealed class ResumeDesktopAgentTaskRequest
{
    public string Source { get; set; } = "desktop";
}
