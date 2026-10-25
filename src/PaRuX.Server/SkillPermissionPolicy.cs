using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>
/// Server-side validation for declarative skill tool access. It intentionally classifies only
/// high-confidence privileged tool names/prefixes; generic capability labels such as "git" are
/// left to their owning subsystem. A wildcard is treated as maximum privilege and therefore
/// requires every permission flag.
/// </summary>
public static class SkillPermissionPolicy
{
    public static void Validate(SkillManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        SkillPermissions permissions = manifest.Permissions ?? new SkillPermissions();
        manifest.Permissions = permissions;

        foreach (string raw in manifest.AllowedTools ?? new List<string>())
        {
            string tool = (raw ?? string.Empty).Trim();
            if (tool.Length == 0) continue;
            if (tool == "*")
            {
                if (!AllGranted(permissions))
                    throw new InvalidDataException("A skill may use allowedTools ['*'] only when every privileged permission is explicitly granted.");
                continue;
            }

            string? missing = RequiredPermission(tool, permissions);
            if (missing != null)
                throw new InvalidDataException("Tool '" + tool + "' requires skill permission '" + missing + "'.");
        }

        if (PolicyRequiresInternet(manifest.InternetPolicy) && !permissions.Network)
            throw new InvalidDataException("internetPolicy 'required' requires the Network permission.");
    }

    internal static string? RequiredPermission(string toolName, SkillPermissions p)
    {
        string name = toolName.ToLowerInvariant();
        bool Is(params string[] values) => values.Any(v => name == v || name.StartsWith(v, StringComparison.Ordinal));

        if (Is("delete_file", "remove_file", "delete_path", "remove_path")) return p.DeleteFiles ? null : nameof(SkillPermissions.DeleteFiles);
        if (Is("write_file", "create_file", "rename_file", "move_file", "patch_file", "apply_patch")) return p.WriteFiles ? null : nameof(SkillPermissions.WriteFiles);
        if (Is("read_file", "grep_search", "glob_search", "list_directory", "search_files")) return p.ReadFiles ? null : nameof(SkillPermissions.ReadFiles);
        if (Is("run_command", "shell", "terminal", "exec_command")) return p.Shell ? null : nameof(SkillPermissions.Shell);
        if (Is("computer_use", "desktop_", "mouse_", "keyboard_", "screen_")) return p.ComputerUse ? null : nameof(SkillPermissions.ComputerUse);
        if (Is("browser_")) return p.Browser && p.Network ? null : !p.Browser ? nameof(SkillPermissions.Browser) : nameof(SkillPermissions.Network);
        if (Is("web_", "http_", "network_")) return p.Network ? null : nameof(SkillPermissions.Network);
        if (Is("launch_process", "start_process", "external_process")) return p.ExternalProcesses ? null : nameof(SkillPermissions.ExternalProcesses);
        return null;
    }

    private static bool AllGranted(SkillPermissions p) =>
        p.ReadFiles && p.WriteFiles && p.DeleteFiles && p.Shell && p.Network && p.Browser && p.ComputerUse && p.ExternalProcesses;

    private static bool PolicyRequiresInternet(string? policy) =>
        string.Equals(policy, "required", StringComparison.OrdinalIgnoreCase);
}
