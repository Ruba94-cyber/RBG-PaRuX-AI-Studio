using System.Text.Json;
using PaRuX.Contracts;

namespace PaRuX.Server.Orchestration;

/// <summary>
/// Durable additional repository authorities for one agent task. The historic single-workspace
/// assignment remains the primary authority for backward compatibility; this store only persists
/// additional aliases and composes them with that primary assignment on reads.
///
/// Repository sets are mutable while a task is queued/waiting or has merely been scheduler-dispatched
/// at 0% with no worktree. AgentCodingWorkerService establishes the practical freeze by moving a
/// claimed coding task to progress=1 before re-reading authorities and creating worktrees. A child
/// task that already inherited its parent's primary repository may deterministically inherit the
/// parent's additional aliases during that same pre-freeze window; no new path is invented or widened.
/// </summary>
public sealed class AgentRepositorySetStore
{
    public const int MaximumRepositoriesPerTask = 8;
    public const string PrimaryAlias = "primary";
    
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static AgentRepositorySetStore Current { get; } = new(
        ServerState.StateDirectory, AgentWorkspaceAssignmentStore.Current);

    private readonly string _stateDirectory;
    private readonly AgentWorkspaceAssignmentStore _primary;
    private string FilePath => Path.Combine(_stateDirectory, "agent-repository-sets.json");

    private sealed class StoredAuthority
    {
        public string Alias { get; set; } = string.Empty;
        public string WorkspaceRoot { get; set; } = string.Empty;
        public DateTime AssignedUtc { get; set; }
    }

    public AgentRepositorySetStore(string stateDirectory, AgentWorkspaceAssignmentStore primary)
    {
        if (string.IsNullOrWhiteSpace(stateDirectory)) throw new ArgumentException("A state directory is required.", nameof(stateDirectory));
        _stateDirectory = Path.GetFullPath(stateDirectory);
        _primary = primary ?? throw new ArgumentNullException(nameof(primary));
    }

    public AgentRepositorySetDto Get(string taskId, AgentTaskDto task)
    {
        ValidateTask(taskId, task);
        AgentWorkspaceAssignmentDto? primary = _primary.Get(taskId);
        List<StoredAuthority> additional;

        lock (Gate)
        {
            Dictionary<string, List<StoredAuthority>> all = LoadUnsafe();
            if (!all.TryGetValue(taskId, out additional!))
            {
                additional = new List<StoredAuthority>();
                if (CanInheritAtRead(task, primary)
                    && !string.IsNullOrWhiteSpace(task.ParentId)
                    && all.TryGetValue(task.ParentId, out List<StoredAuthority>? parentAdditional)
                    && parentAdditional.Count > 0)
                {
                    int remaining = MaximumRepositoriesPerTask - 1; // primary already occupies one slot.
                    additional = parentAdditional
                        .Take(remaining)
                        .Select(item => new StoredAuthority
                        {
                            Alias = item.Alias,
                            WorkspaceRoot = item.WorkspaceRoot,
                            AssignedUtc = DateTime.UtcNow
                        })
                        .ToList();
                    if (additional.Count > 0)
                    {
                        all[taskId] = additional;
                        SaveUnsafe(all);
                    }
                }
            }
            else
            {
                additional = additional.ToList();
            }
        }

        return GetComposedUnsafe(taskId, additional, primary);
    }

    public AgentRepositorySetDto Add(
        string taskId, AddAgentRepositoryAuthorityRequest request, AgentTaskDto task)
    {
        ValidateTask(taskId, task);
        if (!AgentWorkspaceAssignmentStore.RoleSupportsWorkspace(task.Role))
            throw new InvalidDataException("This task role does not accept desktop repository authority.");
        EnsureNotExecutionStarted(task);

        string alias = NormalizeAlias(request.Alias);
        if (string.Equals(alias, PrimaryAlias, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Alias 'primary' is reserved for the task's backward-compatible primary repository authority.");
        string canonical = CanonicalExistingDirectory(request.WorkspaceRoot);

        AgentWorkspaceAssignmentDto? primary = _primary.Get(taskId);
        if (primary != null && PathEquals(primary.WorkspaceRoot, canonical))
            throw new InvalidDataException("That repository is already assigned as the task's primary authority.");

        lock (Gate)
        {
            Dictionary<string, List<StoredAuthority>> all = LoadUnsafe();
            if (!all.TryGetValue(taskId, out List<StoredAuthority>? list))
            {
                list = new List<StoredAuthority>();
                all[taskId] = list;
            }

            StoredAuthority? sameAlias = list.FirstOrDefault(item =>
                string.Equals(item.Alias, alias, StringComparison.OrdinalIgnoreCase));
            if (sameAlias != null)
            {
                if (PathEquals(sameAlias.WorkspaceRoot, canonical)) return GetComposedUnsafe(taskId, list, primary);
                throw new InvalidDataException("Repository alias '" + alias + "' is already bound to another path for this task.");
            }
            if (list.Any(item => PathEquals(item.WorkspaceRoot, canonical)))
                throw new InvalidDataException("That repository path is already assigned to this task under another alias.");

            int currentCount = list.Count + (primary == null ? 0 : 1);
            if (currentCount >= MaximumRepositoriesPerTask)
                throw new InvalidDataException("A task may have at most " + MaximumRepositoriesPerTask + " repository authorities.");

            list.Add(new StoredAuthority
            {
                Alias = alias,
                WorkspaceRoot = canonical,
                AssignedUtc = DateTime.UtcNow
            });
            SaveUnsafe(all);
            return GetComposedUnsafe(taskId, list, primary);
        }
    }

    public bool Remove(string taskId, string alias, AgentTaskDto task)
    {
        ValidateTask(taskId, task);
        EnsureNotExecutionStarted(task);
        string normalized = NormalizeAlias(alias);
        if (string.Equals(normalized, PrimaryAlias, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The primary repository is managed by the primary workspace endpoint and cannot be removed through the additional-repository API.");

        lock (Gate)
        {
            Dictionary<string, List<StoredAuthority>> all = LoadUnsafe();
            if (!all.TryGetValue(taskId, out List<StoredAuthority>? list)) return false;
            int removed = list.RemoveAll(item => string.Equals(item.Alias, normalized, StringComparison.OrdinalIgnoreCase));
            if (removed == 0) return false;
            if (list.Count == 0) all.Remove(taskId);
            SaveUnsafe(all);
            return true;
        }
    }

    public void Inherit(string parentTaskId, AgentTaskDto parent, string childTaskId, AgentTaskDto child)
    {
        if (!AgentWorkspaceAssignmentStore.RoleSupportsWorkspace(child.Role)) return;
        AgentRepositorySetDto parentSet = Get(parentTaskId, parent);
        if (parentSet.Repositories.Count <= 1) return;

        foreach (AgentRepositoryAuthorityDto authority in parentSet.Repositories.Where(item => !item.IsPrimary))
        {
            Add(childTaskId, new AddAgentRepositoryAuthorityRequest
            {
                Alias = authority.Alias,
                WorkspaceRoot = authority.WorkspaceRoot
            }, child);
        }
    }

    private static bool CanInheritAtRead(AgentTaskDto task, AgentWorkspaceAssignmentDto? primary) =>
        primary != null
        && AgentWorkspaceAssignmentStore.RoleSupportsWorkspace(task.Role)
        && string.IsNullOrWhiteSpace(task.WorktreePath)
        && (task.Status is AgentTaskStatus.Queued or AgentTaskStatus.Waiting
            || (task.Status == AgentTaskStatus.Running && task.ProgressPercent == 0));

    private static AgentRepositorySetDto GetComposedUnsafe(
        string taskId, List<StoredAuthority> additional, AgentWorkspaceAssignmentDto? primary)
    {
        var repositories = new List<AgentRepositoryAuthorityDto>();
        if (primary != null)
        {
            repositories.Add(new AgentRepositoryAuthorityDto
            {
                Alias = PrimaryAlias,
                WorkspaceRoot = primary.WorkspaceRoot,
                IsPrimary = true,
                AssignedUtc = primary.AssignedUtc
            });
        }
        repositories.AddRange(additional.OrderBy(item => item.Alias, StringComparer.OrdinalIgnoreCase).Select(item =>
            new AgentRepositoryAuthorityDto
            {
                Alias = item.Alias,
                WorkspaceRoot = item.WorkspaceRoot,
                IsPrimary = false,
                AssignedUtc = item.AssignedUtc
            }));
        return new AgentRepositorySetDto { TaskId = taskId, Repositories = repositories };
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
        string normalized = (alias ?? string.Empty).Trim();
        if (!AgentRepositoryAliasValidation.IsValid(normalized))
            throw new InvalidDataException("Repository alias must start with a letter and contain only letters, numbers, '.', '_' or '-' (maximum 32 characters).");
        return normalized;
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

    private Dictionary<string, List<StoredAuthority>> LoadUnsafe()
    {
        if (!File.Exists(FilePath)) return new Dictionary<string, List<StoredAuthority>>(StringComparer.Ordinal);
        try
        {
            Dictionary<string, List<StoredAuthority>>? parsed = JsonSerializer.Deserialize<Dictionary<string, List<StoredAuthority>>>(
                File.ReadAllText(FilePath), JsonOptions);
            if (parsed == null) throw new InvalidDataException("The additional repository authority file is empty or invalid.");
            return new Dictionary<string, List<StoredAuthority>>(parsed, StringComparer.Ordinal);
        }
        catch (InvalidDataException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new InvalidDataException("The additional repository authority file could not be read safely. Multi-repository agent execution is blocked until it is repaired or reset.", ex);
        }
    }

    private void SaveUnsafe(Dictionary<string, List<StoredAuthority>> all)
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
