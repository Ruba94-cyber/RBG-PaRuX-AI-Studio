using System.Text.Json;
using PaRuX.Contracts;

namespace PaRuX.Server.Orchestration;

/// <summary>
/// Authoritative v2 repository set for tasks that use more than the historical single workspace.
/// The COMPLETE set (primary + aliases) is persisted as one atomically-replaced JSON document, so a
/// coding worker can observe either the old/no set or the complete new set, never a half-configured
/// sequence of repository additions. The legacy primary/additional stores remain read compatibility
/// for existing tasks only; once a v2 set exists this store is authoritative for execution.
/// </summary>
public sealed class AgentRepositoryAuthoritySetStore
{
    private static readonly object Gate = new();
    
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static AgentRepositoryAuthoritySetStore Current { get; } = new(ServerState.StateDirectory);

    private readonly string _stateDirectory;
    private string FilePath => Path.Combine(_stateDirectory, "agent-repository-authority-sets-v2.json");

    public AgentRepositoryAuthoritySetStore(string stateDirectory)
    {
        if (string.IsNullOrWhiteSpace(stateDirectory))
            throw new ArgumentException("A state directory is required.", nameof(stateDirectory));
        _stateDirectory = Path.GetFullPath(stateDirectory);
    }

    public AgentRepositorySetDto? Get(string taskId)
    {
        if (string.IsNullOrWhiteSpace(taskId)) return null;
        lock (Gate)
        {
            Dictionary<string, AgentRepositorySetDto> all = LoadUnsafe();
            return all.TryGetValue(taskId.Trim(), out AgentRepositorySetDto? value) ? Clone(value) : null;
        }
    }

    public AgentRepositorySetDto Replace(string taskId, ReplaceAgentRepositorySetRequest request, AgentTaskDto task)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateTask(taskId, task);
        if (!AgentWorkspaceAssignmentStore.RoleSupportsWorkspace(task.Role))
            throw new InvalidDataException("This task role does not accept desktop repository authority.");
        EnsureNotExecutionStarted(task);

        string primary = CanonicalExistingDirectory(request.PrimaryWorkspaceRoot);
        List<AddAgentRepositoryAuthorityRequest> additional = request.AdditionalRepositories ?? new();
        if (additional.Count + 1 > AgentRepositorySetStore.MaximumRepositoriesPerTask)
            throw new InvalidDataException("A task may have at most " + AgentRepositorySetStore.MaximumRepositoriesPerTask + " repository authorities.");

        DateTime now = DateTime.UtcNow;
        var repositories = new List<AgentRepositoryAuthorityDto>
        {
            new()
            {
                Alias = AgentRepositorySetStore.PrimaryAlias,
                WorkspaceRoot = primary,
                IsPrimary = true,
                AssignedUtc = now
            }
        };
        var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { AgentRepositorySetStore.PrimaryAlias };
        var paths = new List<string> { primary };

        foreach (AddAgentRepositoryAuthorityRequest item in additional)
        {
            string alias = NormalizeAlias(item.Alias);
            if (!aliases.Add(alias))
                throw new InvalidDataException("Repository alias '" + alias + "' is duplicated.");
            if (string.Equals(alias, AgentRepositorySetStore.PrimaryAlias, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Alias 'primary' is reserved for the primary repository authority.");

            string path = CanonicalExistingDirectory(item.WorkspaceRoot);
            if (paths.Any(existing => PathEquals(existing, path)))
                throw new InvalidDataException("Repository path is duplicated in the authority set: " + path);
            paths.Add(path);
            repositories.Add(new AgentRepositoryAuthorityDto
            {
                Alias = alias,
                WorkspaceRoot = path,
                IsPrimary = false,
                AssignedUtc = now
            });
        }

        var dto = new AgentRepositorySetDto { TaskId = taskId, Repositories = repositories };
        lock (Gate)
        {
            Dictionary<string, AgentRepositorySetDto> all = LoadUnsafe();
            all[taskId] = Clone(dto);
            SaveUnsafe(all);
        }
        return dto;
    }

    public bool Delete(string taskId, AgentTaskDto task)
    {
        ValidateTask(taskId, task);
        EnsureNotExecutionStarted(task);
        lock (Gate)
        {
            Dictionary<string, AgentRepositorySetDto> all = LoadUnsafe();
            if (!all.Remove(taskId)) return false;
            SaveUnsafe(all);
            return true;
        }
    }

    private static void ValidateTask(string taskId, AgentTaskDto task)
    {
        if (task == null) throw new InvalidDataException("Task not found.");
        if (string.IsNullOrWhiteSpace(taskId) || !string.Equals(task.Id, taskId, StringComparison.Ordinal))
            throw new InvalidDataException("Repository authority task id mismatch.");
    }

    private static void EnsureNotExecutionStarted(AgentTaskDto task)
    {
        bool zeroProgressUnmaterialized = task.Status == AgentTaskStatus.Running
            && task.ProgressPercent == 0
            && string.IsNullOrWhiteSpace(task.WorktreePath);
        bool executionStarted = !string.IsNullOrWhiteSpace(task.WorktreePath)
            || task.ProgressPercent > 0
            || task.Status is AgentTaskStatus.Verifying or AgentTaskStatus.Completed or AgentTaskStatus.Failed
                or AgentTaskStatus.Cancelled or AgentTaskStatus.Blocked
            || (task.Status == AgentTaskStatus.Running && !zeroProgressUnmaterialized);
        if (executionStarted)
            throw new InvalidDataException("This agent task has already started. Its repository authority set is immutable; create a new task to change repositories.");
    }

    private static string NormalizeAlias(string? alias)
    {
        string value = (alias ?? string.Empty).Trim();
        if (!AgentRepositoryAliasValidation.IsValid(value))
            throw new InvalidDataException("Repository alias must start with a letter and contain only letters, numbers, '.', '_' or '-' (maximum 32 characters).");
        return value;
    }

    private static string CanonicalExistingDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new InvalidDataException("workspaceRoot is required.");
        string canonical;
        try { canonical = Path.GetFullPath(path.Trim()); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new InvalidDataException("workspaceRoot is not a valid local path.");
        }
        if (!Directory.Exists(canonical)) throw new InvalidDataException("workspaceRoot must be an existing local directory.");
        return canonical;
    }

    private Dictionary<string, AgentRepositorySetDto> LoadUnsafe()
    {
        if (!File.Exists(FilePath))
            return new Dictionary<string, AgentRepositorySetDto>(StringComparer.Ordinal);
        try
        {
            Dictionary<string, AgentRepositorySetDto>? parsed = JsonSerializer.Deserialize<Dictionary<string, AgentRepositorySetDto>>(
                File.ReadAllText(FilePath), JsonOptions);
            if (parsed == null)
                throw new InvalidDataException("The complete repository authority file is empty or invalid.");
            return new Dictionary<string, AgentRepositorySetDto>(parsed, StringComparer.Ordinal);
        }
        catch (InvalidDataException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new InvalidDataException("The complete repository authority file could not be read safely. Multi-repository agent execution is blocked until it is repaired or reset.", ex);
        }
    }

    private void SaveUnsafe(Dictionary<string, AgentRepositorySetDto> all)
    {
        Directory.CreateDirectory(_stateDirectory);
        string temp = FilePath + "." + Environment.ProcessId + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(all, JsonOptions));
        try
        {
            if (File.Exists(FilePath)) File.Replace(temp, FilePath, destinationBackupFileName: null);
            else File.Move(temp, FilePath);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
        }
    }

    private static AgentRepositorySetDto Clone(AgentRepositorySetDto value) => new()
    {
        TaskId = value.TaskId,
        Repositories = value.Repositories.Select(item => new AgentRepositoryAuthorityDto
        {
            Alias = item.Alias,
            WorkspaceRoot = item.WorkspaceRoot,
            IsPrimary = item.IsPrimary,
            AssignedUtc = item.AssignedUtc
        }).ToList()
    };

    private static bool PathEquals(string left, string right)
    {
        try
        {
            string a = Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string b = Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.Equals(a, b, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        catch { return false; }
    }
}
