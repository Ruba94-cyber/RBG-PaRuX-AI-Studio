namespace PaRuX.Contracts;

/// <summary>
/// Desktop-only repository authority exposed only by loopback-owner routes. The generic task DTO
/// deliberately never contains these paths, so paired/mobile clients cannot discover workstation
/// filesystem layout. "primary" is the backward-compatible AgentWorkspaceAssignmentStore binding;
/// additional aliases extend one durable task to a bounded multi-repository workspace.
/// </summary>
public sealed class AgentRepositoryAuthorityDto
{
    public string Alias { get; set; } = string.Empty;
    public string WorkspaceRoot { get; set; } = string.Empty;
    public bool IsPrimary { get; set; }
    public DateTime AssignedUtc { get; set; }
}

public sealed class AgentRepositorySetDto
{
    public string TaskId { get; set; } = string.Empty;
    public List<AgentRepositoryAuthorityDto> Repositories { get; set; } = new();
}

public sealed class AddAgentRepositoryAuthorityRequest
{
    public string Alias { get; set; } = string.Empty;
    public string WorkspaceRoot { get; set; } = string.Empty;
}

/// <summary>
/// Replaces the complete pre-execution repository authority set for one desktop task in one
/// scheduler-safe operation. The primary root remains the backwards-compatible workspace binding;
/// additional aliases are independently bounded authorities. This request is desktop/loopback-only.
/// </summary>
public sealed class ReplaceAgentRepositorySetRequest
{
    public string PrimaryWorkspaceRoot { get; set; } = string.Empty;
    public List<AddAgentRepositoryAuthorityRequest> AdditionalRepositories { get; set; } = new();
}


/// <summary>
/// Shared desktop/server authority identity contract. Aliases are reused
/// as Windows directory names, Git branch components and cmd.exe pushd
/// arguments; fail before persisting an unsafe repository assignment.
/// </summary>
public static class AgentRepositoryAliasValidation
{
    public static bool IsValid(string? alias)
    {
        if (string.IsNullOrEmpty(alias) || alias.Length > 32
            || !IsAsciiLetter(alias[0])
            || alias.EndsWith(".", StringComparison.Ordinal))
            return false;
        for (int i = 1; i < alias.Length; i++)
        {
            char c = alias[i];
            if (!IsAsciiLetter(c) && !(c >= '0' && c <= '9')
                && c is not '.' and not '_' and not '-')
                return false;
        }

        return !IsWindowsReservedDeviceName(alias);
    }

    public static bool IsWindowsReservedDeviceName(string? name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        string basename = name.Split('.')[0].ToUpperInvariant();
        if (basename is "CON" or "PRN" or "AUX" or "NUL" or "CLOCK$")
            return true;
        return basename.Length == 4
            && (basename.StartsWith("COM", StringComparison.Ordinal)
                || basename.StartsWith("LPT", StringComparison.Ordinal))
            && basename[3] is >= '1' and <= '9';
    }

    private static bool IsAsciiLetter(char c) =>
        c is >= 'A' and <= 'Z' or >= 'a' and <= 'z';
}
