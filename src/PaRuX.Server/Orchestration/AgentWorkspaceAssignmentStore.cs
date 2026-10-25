using System.Text.Json;
using PaRuX.Contracts;

namespace PaRuX.Server.Orchestration;

/// <summary>
/// Desktop-only durable binding between an orchestrator task and the local workspace that task is
/// allowed to use. The path is intentionally NOT serialized on AgentTaskDto: paired/mobile clients
/// may inspect and control a task without learning workstation filesystem paths, and the
/// paired-device endpoint policy never exposes the routes backed by this store.
///
/// Coding tasks use the assigned directory as their repository root and materialize an isolated git
/// worktree. Binary-analysis tasks use it as their artifact workspace. Planner/review/documentation/
/// testing/security tasks may receive the same authority only for server-side READ-ONLY repository
/// tools; their worker never writes files or launches commands from this authority.
///
/// Corruption is fail-closed: an unreadable authority file throws instead of masquerading as "no
/// assignment", because the latter could make a migration fallback execute a task in a different
/// workspace. Tests use the injectable state directory; production uses ServerState.StateDirectory.
/// </summary>
public sealed class AgentWorkspaceAssignmentStore
{
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
    private static readonly IReadOnlySet<string> WorkspaceRoles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "coding", "binary-analysis", "planner", "review", "documentation", "testing", "security"
    };

    public static AgentWorkspaceAssignmentStore Current { get; } = new(ServerState.StateDirectory);

    private readonly string _stateDirectory;
    private string FilePath => Path.Combine(_stateDirectory, "agent-workspaces.json");

    public AgentWorkspaceAssignmentStore(string stateDirectory)
    {
        if (string.IsNullOrWhiteSpace(stateDirectory)) throw new ArgumentException("A state directory is required.", nameof(stateDirectory));
        _stateDirectory = Path.GetFullPath(stateDirectory);
    }

    public static bool RoleSupportsWorkspace(string? role) =>
        string.IsNullOrWhiteSpace(role)
        || string.Equals(role, "auto", StringComparison.OrdinalIgnoreCase)
        || WorkspaceRoles.Contains(role);

    public static bool RoleMayMutateWorkspace(string? role) =>
        string.Equals(role, "coding", StringComparison.OrdinalIgnoreCase);

    public AgentWorkspaceAssignmentDto? Get(string taskId)
    {
        if (string.IsNullOrWhiteSpace(taskId)) return null;
        lock (Gate)
        {
            Dictionary<string, AgentWorkspaceAssignmentDto> all = LoadUnsafe();
            return all.TryGetValue(taskId.Trim(), out AgentWorkspaceAssignmentDto? value)
                ? Clone(value)
                : null;
        }
    }

    public AgentWorkspaceAssignmentDto Set(string taskId, string workspaceRoot, AgentTaskDto task)
    {
        if (task == null) throw new InvalidDataException("Task not found.");
        if (!string.Equals(task.Id, taskId, StringComparison.Ordinal))
            throw new InvalidDataException("Workspace assignment task id mismatch.");
        if (!RoleSupportsWorkspace(task.Role))
            throw new InvalidDataException("This task role does not accept desktop repository authority.");
        if (string.IsNullOrWhiteSpace(workspaceRoot))
            throw new InvalidDataException("workspaceRoot is required.");

        string canonical;
        try { canonical = Path.GetFullPath(workspaceRoot.Trim()); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new InvalidDataException("workspaceRoot is not a valid local path.");
        }
        if (!Directory.Exists(canonical))
            throw new InvalidDataException("workspaceRoot must be an existing local directory.");

        lock (Gate)
        {
            Dictionary<string, AgentWorkspaceAssignmentDto> all = LoadUnsafe();
            if (all.TryGetValue(taskId, out AgentWorkspaceAssignmentDto? existing))
            {
                if (PathEquals(existing.WorkspaceRoot, canonical)) return Clone(existing);

                bool executionStarted = !string.IsNullOrWhiteSpace(task.WorktreePath)
                    || task.Status is AgentTaskStatus.Running or AgentTaskStatus.Verifying
                    || task.ProgressPercent > 0;
                if (executionStarted)
                    throw new InvalidDataException(
                        "This agent task has already started with a workspace authority. The assignment cannot be changed; create a new task instead.");
            }

            var assignment = new AgentWorkspaceAssignmentDto
            {
                TaskId = taskId,
                WorkspaceRoot = canonical,
                AssignedUtc = DateTime.UtcNow
            };
            all[taskId] = assignment;
            SaveUnsafe(all);
            return Clone(assignment);
        }
    }

    public bool Delete(string taskId)
    {
        if (string.IsNullOrWhiteSpace(taskId)) return false;
        lock (Gate)
        {
            Dictionary<string, AgentWorkspaceAssignmentDto> all = LoadUnsafe();
            if (!all.Remove(taskId.Trim())) return false;
            SaveUnsafe(all);
            return true;
        }
    }

    private Dictionary<string, AgentWorkspaceAssignmentDto> LoadUnsafe()
    {
        if (!File.Exists(FilePath))
            return new Dictionary<string, AgentWorkspaceAssignmentDto>(StringComparer.Ordinal);
        try
        {
            Dictionary<string, AgentWorkspaceAssignmentDto>? parsed =
                JsonSerializer.Deserialize<Dictionary<string, AgentWorkspaceAssignmentDto>>(
                    File.ReadAllText(FilePath), JsonOptions);
            if (parsed == null)
                throw new InvalidDataException("The desktop agent workspace authority file is empty or invalid.");
            return new Dictionary<string, AgentWorkspaceAssignmentDto>(parsed, StringComparer.Ordinal);
        }
        catch (InvalidDataException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new InvalidDataException(
                "The desktop agent workspace authority file could not be read safely. Filesystem-capable agent tasks are blocked until it is repaired or reset.", ex);
        }
    }

    private void SaveUnsafe(Dictionary<string, AgentWorkspaceAssignmentDto> all)
    {
        Directory.CreateDirectory(_stateDirectory);
        string destination = FilePath;
        string temporary = destination + "." + Environment.ProcessId + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(all, JsonOptions));
        try
        {
            if (File.Exists(destination)) File.Replace(temporary, destination, destinationBackupFileName: null);
            else File.Move(temporary, destination);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
        }
    }

    private static bool PathEquals(string left, string right)
    {
        try
        {
            string a = Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string b = Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.Equals(a, b, OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        catch { return false; }
    }

    private static AgentWorkspaceAssignmentDto Clone(AgentWorkspaceAssignmentDto value) => new()
    {
        TaskId = value.TaskId,
        WorkspaceRoot = value.WorkspaceRoot,
        AssignedUtc = value.AssignedUtc
    };
}
