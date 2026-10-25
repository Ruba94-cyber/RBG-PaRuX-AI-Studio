using System.Text.Json;
using System.Text.Json.Serialization;
using PaRuX.Contracts;
using PaRuX.Server.ContextGraph;
using PaRuX.Server.Orchestration;

namespace PaRuX.Server.ProjectManager;

/// <summary>
/// Durable Project Manager backed by the existing Context Graph and TaskStore. Planning requests a
/// strict JSON schema when the active local provider supports it, then applies deterministic local
/// validation. Specialist tool authority is persisted explicitly on each created task; workers do
/// not infer/widen capabilities later from a role name. Project views also include delegated task
/// descendants from TaskStore so self-delegated work remains visible in the same durable Project.
/// </summary>
public sealed class ProjectManagerService
{
    internal const int MaxTasks = 8;
    private const string ProjectEntityType = "project";
    private const string ProjectSource = "project-manager";
    private const string TaskEntityType = "task";
    private const string ContainsRelation = "CONTAINS";
    private const string ContextCapability = "context.read";
    private const string WebResearchCapability = "web.research";
    private const string RepositoryReadCapability = "repository.read";
    private const string DelegationCapability = "agent.delegate";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static readonly IReadOnlySet<string> AllowedRoles = new HashSet<string>(StringComparer.Ordinal)
    {
        "planner", "research", "review", "documentation", "testing", "security",
        "coding", "binary-analysis", "computer-use", "ui-verification"
    };

    private static readonly LocalStructuredOutputRequest PlanningSchema = new(
        "parux_project_plan",
        "{\"type\":\"array\",\"minItems\":1,\"maxItems\":8,\"items\":{\"type\":\"object\",\"properties\":{"
        + "\"description\":{\"type\":\"string\",\"minLength\":1,\"maxLength\":2000},"
        + "\"role\":{\"type\":\"string\",\"enum\":[\"planner\",\"research\",\"review\",\"documentation\",\"testing\",\"security\",\"coding\",\"binary-analysis\",\"computer-use\",\"ui-verification\"]},"
        + "\"dependsOn\":{\"type\":\"array\",\"maxItems\":8,\"items\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":7},\"uniqueItems\":true}"
        + "},\"required\":[\"description\",\"role\",\"dependsOn\"],\"additionalProperties\":false}}",
        Strict: true);

    private readonly ILocalCompletionClient _client;
    private readonly TaskStore _tasks;
    private readonly ContextGraphStore _graph;
    private readonly AgentRepositoryAuthoritySetStore _repositoryAuthorities;
    private readonly AgentWorkspaceAssignmentStore _workspaceAssignments;
    private readonly object _attachGate = new();

    public ProjectManagerService(
        ILocalCompletionClient client,
        TaskStore tasks,
        ContextGraphStore graph,
        AgentRepositoryAuthoritySetStore? repositoryAuthorities = null,
        AgentWorkspaceAssignmentStore? workspaceAssignments = null)
    {
        _client = client;
        _tasks = tasks;
        _graph = graph;
        _repositoryAuthorities = repositoryAuthorities ?? AgentRepositoryAuthoritySetStore.Current;
        _workspaceAssignments = workspaceAssignments ?? AgentWorkspaceAssignmentStore.Current;
    }

    /// <summary>
    /// A server restart can interrupt repository authority provisioning after task creation.
    /// Those tasks were deliberately withheld from the scheduler, so fail them and remove any
    /// partial authority before the app starts accepting new work.
    /// </summary>
    internal void RecoverInterruptedRepositoryProvisioning()
    {
        List<string> abandonedTaskIds = _tasks.ListByStatus(AgentTaskStatus.Waiting)
            .Where(task => string.Equals(task.Stage, TaskStore.ProjectRepositoryProvisioningStage, StringComparison.Ordinal))
            .Select(task => task.Id)
            .ToList();
        if (abandonedTaskIds.Count == 0) return;

        FailRepositoryProvisioningTasks(abandonedTaskIds,
            new InvalidOperationException("The server restarted before repository authority provisioning finished."));
    }

    internal sealed record PlannedTask(
        [property: JsonPropertyName("description")] string? Description,
        [property: JsonPropertyName("role")] string? Role,
        [property: JsonPropertyName("dependsOn")] List<int>? DependsOn);

    private static readonly System.Text.RegularExpressions.Regex RepositoryFilePattern = new(
        @"[\w\-./\\]+\.(cs|csproj|xaml|ts|tsx|js|jsx|mjs|py|rs|go|java|kt|swift|c|h|cpp|hpp|sql|ps1|sh|md|markdown|json|yml|yaml|xml|wxs|razor)\b",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    public List<ProjectDto> ListProjects() => _graph.ListEntities(ProjectEntityType)
        .Where(IsManagedProject)
        .Select(ToProjectDto)
        .OrderByDescending(project => project.UpdatedUtc)
        .ToList();

    public ProjectDto? GetProject(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        ContextEntityDto? entity = _graph.GetEntity(id);
        return entity != null && IsManagedProject(entity) ? ToProjectDto(entity) : null;
    }

    public bool DeleteProject(string id)
    {
        ContextEntityDto? entity = _graph.GetEntity(id);
        if (entity == null || !IsManagedProject(entity)) return false;
        return _graph.DeleteEntity(id);
    }

    /// <summary>
    /// Creates the complete durable task graph. Desktop callers may provide a repository root;
    /// those tasks stay out of the scheduler until all graph edges and repository authority are
    /// persisted. Existing/mobile callers omit it and retain the offline restore default.
    /// </summary>
    public async Task<ProjectDto> CreateProjectAsync(
        string goal,
        CancellationToken cancellationToken,
        string? codingPermissionProfile = null,
        string? workspaceRoot = null)
    {
        if (string.IsNullOrWhiteSpace(goal)) throw new InvalidDataException("A project goal is required.");
        goal = goal.Trim();
        string normalizedCodingProfile = CodingTaskPermissionProfile.Normalize(codingPermissionProfile);
        string? normalizedWorkspaceRoot = CanonicalOptionalDirectory(workspaceRoot);

        List<PlannedTask> plan = await PlanAsync(goal, cancellationToken).ConfigureAwait(false);
        if (normalizedWorkspaceRoot != null
            && plan.Any(task => string.Equals(task.Role, "coding", StringComparison.Ordinal))
            && !Directory.Exists(Path.Combine(normalizedWorkspaceRoot, ".git"))
            && !File.Exists(Path.Combine(normalizedWorkspaceRoot, ".git")))
            throw new InvalidDataException("This Project contains coding tasks, so its repository must be a Git working tree.");

        ContextEntityDto projectEntity = _graph.CreateEntity(new CreateContextEntityRequest
        {
            EntityType = ProjectEntityType,
            Name = goal,
            Source = ProjectSource,
            MetadataJson = JsonSerializer.Serialize(new
            {
                schemaVersion = 4,
                goal,
                codingPermissionProfile = normalizedCodingProfile
            }, JsonOptions)
        });

        var taskIdByIndex = new Dictionary<int, string>();
        var deferredTaskIds = new List<string>();

        try
        {
            for (int i = 0; i < plan.Count; i++)
            {
                PlannedTask planned = plan[i];
                List<string> dependsOnIds = (planned.DependsOn ?? new List<int>())
                    .Where(index => index >= 0 && index < i && taskIdByIndex.ContainsKey(index))
                    .Select(index => taskIdByIndex[index])
                    .Distinct(StringComparer.Ordinal)
                    .ToList();

                List<string> capabilities = DefaultCapabilitiesForRole(planned.Role);
                if (string.Equals(planned.Role, "coding", StringComparison.Ordinal))
                {
                    capabilities.AddRange(CodingTaskPermissionProfile.RootCapabilitiesFor(normalizedCodingProfile));
                    capabilities = capabilities.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                }

                var request = new CreateAgentTaskRequest
                {
                    Goal = planned.Description!,
                    Role = planned.Role,
                    DependsOnTaskIds = dependsOnIds,
                    GrantedCapabilities = capabilities,
                    MaxRepairAttempts = 2
                };
                AgentTaskDto task = normalizedWorkspaceRoot == null
                    ? _tasks.Create(request)
                    : _tasks.CreateDeferred(request, TaskStore.ProjectRepositoryProvisioningStage);
                if (normalizedWorkspaceRoot != null)
                    deferredTaskIds.Add(task.Id);
                _tasks.AppendLog(task.Id, "Created by Project Manager for project \"" + goal + "\""
                    + (string.Equals(planned.Role, "coding", StringComparison.Ordinal)
                        ? "; coding permission profile: " + normalizedCodingProfile
                        : string.Empty)
                    + (capabilities.Count == 0 ? string.Empty : "; granted capabilities: " + string.Join(", ", capabilities)));
                if (normalizedWorkspaceRoot != null)
                {
                    if (AgentWorkspaceAssignmentStore.RoleSupportsWorkspace(task.Role))
                    {
                        AgentRepositorySetDto set = _repositoryAuthorities.Replace(task.Id, new ReplaceAgentRepositorySetRequest
                        {
                            PrimaryWorkspaceRoot = normalizedWorkspaceRoot
                        }, task);
                        string primaryRoot = set.Repositories.Single(repository => repository.IsPrimary).WorkspaceRoot;
                        _workspaceAssignments.Set(task.Id, primaryRoot, task);
                        _tasks.AppendLog(task.Id, "Desktop repository authority assigned before scheduling.");
                    }
                }
                taskIdByIndex[i] = task.Id;
                AttachTask(projectEntity.Id, task.Id);
            }

            // Keep tasks out of the scheduler until every Project edge and selected repository grant
            // is persisted, then expose the entire batch in one TaskStore transaction.
            ProjectDto result = ToProjectDto(projectEntity);
            if (normalizedWorkspaceRoot != null)
                _tasks.QueueDeferred(deferredTaskIds, TaskStore.ProjectRepositoryProvisioningStage);
            return result;
        }
        catch (Exception exception) when (normalizedWorkspaceRoot != null)
        {
            FailRepositoryProvisioningTasks(deferredTaskIds, exception);
            throw;
        }
    }

    private void FailRepositoryProvisioningTasks(IEnumerable<string> taskIds, Exception exception)
    {
        string status = exception is OperationCanceledException ? AgentTaskStatus.Cancelled : AgentTaskStatus.Failed;
        string message = exception.Message.Length <= 4_000 ? exception.Message : exception.Message[..4_000];
        foreach (string id in taskIds)
        {
            try
            {
                AgentTaskDto? task = _tasks.Get(id);
                if (task == null || AgentTaskStatus.Terminal.Contains(task.Status)) continue;
                try { _repositoryAuthorities.Delete(id, task); } catch { }
                try { _workspaceAssignments.Delete(id); } catch { }
                _tasks.UpdateStatus(id, new UpdateAgentTaskStatusRequest
                {
                    Status = status,
                    Stage = "Project repository provisioning failed",
                    Error = message
                });
                try { _tasks.AppendLog(id, "Project repository provisioning failed; task was stopped before scheduler execution: " + message); }
                catch { }
            }
            catch { /* Keep cleanup best-effort so the original provisioning failure is preserved. */ }
        }
    }

    /// <summary>
    /// Authority is assigned when the durable task is created, not inferred by the worker later.
    /// Research gets web access because that is its purpose. Repository-aware specialist roles get
    /// read-only repository authority, never repository mutation. Delegation is bounded by the
    /// worker/TaskStore and cannot widen parent capabilities. Coding package-restore authority is
    /// supplied separately by the explicit creation profile; root coding tasks always receive
    /// bounded agent.delegate so the native subagent path is reachable.
    /// </summary>
    internal static List<string> DefaultCapabilitiesForRole(string? role) => role switch
    {
        "research" => new List<string> { ContextCapability, WebResearchCapability, DelegationCapability },
        "planner" or "review" or "testing" or "security"
            => new List<string> { ContextCapability, RepositoryReadCapability, DelegationCapability },
        "documentation" => new List<string> { ContextCapability, RepositoryReadCapability },
        _ => new List<string>()
    };

    public ProjectDto AttachTask(string projectId, string taskId)
    {
        if (string.IsNullOrWhiteSpace(projectId)) throw new InvalidDataException("A project id is required.");
        if (string.IsNullOrWhiteSpace(taskId)) throw new InvalidDataException("An agent task id is required.");

        lock (_attachGate)
        {
            ContextEntityDto project = _graph.GetEntity(projectId)
                ?? throw new InvalidDataException("Project not found.");
            if (!IsManagedProject(project))
                throw new InvalidDataException("The target entity is not a PaRuX-managed Project.");

            AgentTaskDto task = _tasks.Get(taskId)
                ?? throw new InvalidDataException("Agent task not found.");

            ContextNeighborhoodResponse neighborhood = _graph.GetNeighborhood(project.Id)
                ?? throw new InvalidDataException("Project context could not be loaded.");

            foreach (ContextRelationDto relation in neighborhood.OutgoingRelations.Where(IsContainsRelation))
            {
                ContextEntityDto? target = neighborhood.NeighborEntities
                    .FirstOrDefault(entity => entity.Id == relation.ToEntityId);
                if (target == null || !string.Equals(target.EntityType, TaskEntityType, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (string.Equals(TryReadTaskId(target.MetadataJson), task.Id, StringComparison.Ordinal))
                    return ToProjectDto(project);
            }

            ContextEntityDto? taskEntity = _graph.ListEntities(TaskEntityType)
                .FirstOrDefault(entity => string.Equals(TryReadTaskId(entity.MetadataJson), task.Id, StringComparison.Ordinal));

            if (taskEntity == null)
            {
                taskEntity = _graph.CreateEntity(new CreateContextEntityRequest
                {
                    EntityType = TaskEntityType,
                    Name = task.Goal,
                    Source = ProjectSource,
                    MetadataJson = JsonSerializer.Serialize(new { orchestratorTaskId = task.Id }, JsonOptions)
                });
            }

            _graph.CreateRelation(new CreateContextRelationRequest
            {
                FromEntityId = project.Id,
                RelationType = ContainsRelation,
                ToEntityId = taskEntity.Id,
                Source = ProjectSource,
                MetadataJson = JsonSerializer.Serialize(new { orchestratorTaskId = task.Id }, JsonOptions)
            });

            _tasks.AppendLog(task.Id, "Attached to Project \"" + (ReadGoal(project) ?? project.Name) + "\" (" + project.Id + ")");
            return ToProjectDto(project);
        }
    }

    private ProjectDto ToProjectDto(ContextEntityDto entity)
    {
        ContextNeighborhoodResponse? neighborhood = _graph.GetNeighborhood(entity.Id);
        var taskIds = new HashSet<string>(StringComparer.Ordinal);
        DateTime updatedUtc = entity.UpdatedUtc;

        if (neighborhood != null)
        {
            foreach (ContextRelationDto relation in neighborhood.OutgoingRelations.Where(IsContainsRelation))
            {
                if (relation.CreatedUtc > updatedUtc) updatedUtc = relation.CreatedUtc;
                ContextEntityDto? target = neighborhood.NeighborEntities.FirstOrDefault(e => e.Id == relation.ToEntityId);
                if (target == null || !string.Equals(target.EntityType, TaskEntityType, StringComparison.OrdinalIgnoreCase)) continue;
                string? taskId = TryReadTaskId(target.MetadataJson);
                if (!string.IsNullOrWhiteSpace(taskId)) taskIds.Add(taskId);
            }
        }

        if (taskIds.Count > 0)
        {
            List<AgentTaskDto> allTasks = _tasks.List();
            bool changed;
            do
            {
                changed = false;
                foreach (AgentTaskDto candidate in allTasks)
                {
                    if (string.IsNullOrWhiteSpace(candidate.ParentId) || taskIds.Contains(candidate.Id)) continue;
                    if (!taskIds.Contains(candidate.ParentId)) continue;
                    taskIds.Add(candidate.Id);
                    if (candidate.UpdatedUtc > updatedUtc) updatedUtc = candidate.UpdatedUtc;
                    changed = true;
                }
            }
            while (changed);
        }

        return new ProjectDto
        {
            Id = entity.Id,
            Goal = ReadGoal(entity) ?? entity.Name,
            TaskIds = taskIds.ToList(),
            CreatedUtc = entity.CreatedUtc,
            UpdatedUtc = updatedUtc
        };
    }

    private static bool IsContainsRelation(ContextRelationDto relation) =>
        string.Equals(relation.RelationType, ContainsRelation, StringComparison.OrdinalIgnoreCase);

    private static bool IsManagedProject(ContextEntityDto entity) =>
        string.Equals(entity.EntityType, ProjectEntityType, StringComparison.OrdinalIgnoreCase)
        && string.Equals(entity.Source, ProjectSource, StringComparison.OrdinalIgnoreCase);

    private static string? ReadGoal(ContextEntityDto entity)
    {
        if (string.IsNullOrWhiteSpace(entity.MetadataJson)) return null;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(entity.MetadataJson);
            return doc.RootElement.TryGetProperty("goal", out JsonElement value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() : null;
        }
        catch (JsonException) { return null; }
    }

    private static string? TryReadTaskId(string? metadataJson)
    {
        if (string.IsNullOrWhiteSpace(metadataJson)) return null;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(metadataJson);
            return doc.RootElement.TryGetProperty("orchestratorTaskId", out JsonElement value)
                && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        }
        catch (JsonException) { return null; }
    }

    private async Task<List<PlannedTask>> PlanAsync(string goal, CancellationToken cancellationToken)
    {
        CompletionResult result;
        try
        {
            result = await _client.CompleteStructuredAsync(
                SystemPrompt, goal, PlanningSchema, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch { return SingleFallbackTask(goal); }

        if (!result.Success || string.IsNullOrWhiteSpace(result.Content)) return SingleFallbackTask(goal);
        List<PlannedTask>? parsed = TryParsePlan(result.Content);
        List<PlannedTask> validated = ValidatePlan(parsed);
        return validated.Count == 0 ? SingleFallbackTask(goal) : validated;
    }

    private static List<PlannedTask> SingleFallbackTask(string goal) =>
        new() { new PlannedTask(goal.Trim(),
            AgentOrchestrator.InferRole(goal) == "coding" && IsExplicitRepositoryEdit(goal) ? "coding" : "planner",
            new List<int>()) };

    private static bool IsExplicitRepositoryEdit(string value) =>
        RepositoryFilePattern.IsMatch(value) && AgentOrchestrator.IsExplicitRepositoryEdit(value);

    private static string? CanonicalOptionalDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        string canonical;
        try { canonical = Path.GetFullPath(path.Trim()); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new InvalidDataException("workspaceRoot is not a valid local path.");
        }
        if (!Directory.Exists(canonical)) throw new InvalidDataException("workspaceRoot must be an existing local directory.");
        return canonical;
    }

    internal static List<PlannedTask> ValidatePlan(List<PlannedTask>? input)
    {
        if (input == null || input.Count == 0) return new List<PlannedTask>();
        var output = new List<PlannedTask>();

        foreach (PlannedTask raw in input.Take(MaxTasks))
        {
            if (string.IsNullOrWhiteSpace(raw.Description)) continue;
            string description = raw.Description.Trim();
            if (description.Length > 2_000) description = description[..2_000];

            string role = (raw.Role ?? string.Empty).Trim().ToLowerInvariant();
            if (!AllowedRoles.Contains(role))
                role = AgentOrchestrator.InferRole(description);
            else if (role is "planner" or "review" or "documentation" or "testing"
                && AgentOrchestrator.InferRole(description) == "coding")
                role = "coding";

            int currentIndex = output.Count;
            List<int> dependencies = (raw.DependsOn ?? new List<int>())
                .Where(index => index >= 0 && index < currentIndex)
                .Distinct()
                .OrderBy(index => index)
                .ToList();

            output.Add(new PlannedTask(description, role, dependencies));
        }
        return output;
    }

    private static List<PlannedTask>? TryParsePlan(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        int start = text.IndexOf('[');
        int end = text.LastIndexOf(']');
        if (start < 0 || end <= start) return null;
        try
        {
            return JsonSerializer.Deserialize<List<PlannedTask>>(text[start..(end + 1)], JsonOptions);
        }
        catch (JsonException) { return null; }
    }

    internal const string SystemPrompt =
        "You are PaRuX's Autonomous Project Manager. Given a goal, create the smallest complete dependency graph, " +
        "between 1 and 8 concrete tasks. Return only data matching the supplied JSON schema. Every dependsOn index " +
        "must refer to an EARLIER task. Avoid duplicate or ceremonial tasks. Each task must have a machine-actionable " +
        "description and the narrowest real role capable of completing it.\n\n" +
        "ROLE CAPABILITIES ARE NOT EQUAL. The \"coding\" role owns repository/file edits and sandboxed shell/build/test " +
        "execution in an isolated git worktree. The \"binary-analysis\" role inspects/decompiles/disassembles PE/ELF/" +
        "Mach-O/firmware evidence. The \"computer-use\" role operates the real Windows GUI under user approval. The " +
        "\"ui-verification\" role observes the real screen without clicking/typing. Server-side planner/review/testing/security " +
        "receive bounded local context plus READ-ONLY repository tools and may create bounded isolated child tasks; documentation " +
        "gets bounded context + read-only repository tools; research gets bounded local context + web research + bounded delegation. " +
        "Root coding tasks can also create bounded delegated children; all child capabilities remain subsets of the parent. " +
        "The global Internet switch remains authoritative and repository mutation stays coding-only. " +
        "Put independent work in parallel where useful, verification after implementation, and make dependencies encode the actual order.";
}
