using PaRuX.Contracts;
using PaRuX.Server.ContextGraph;
using PaRuX.Server.Orchestration;
using PaRuX.Server.ProjectManager;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>Project Manager hermetic coverage: schema-constrained planning creates a real task graph
/// and durable Context Graph Project, while deterministic validation still protects providers that
/// cannot enforce the schema.</summary>
public sealed class ProjectManagerServiceTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "parux-pm-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private (ProjectManagerService pm, TaskStore tasks, ContextGraphStore graph, FakeLocalCompletionClient client) Build()
    {
        var client = new FakeLocalCompletionClient();
        var tasks = new TaskStore(_tempDir);
        var graph = new ContextGraphStore(_tempDir);
        return (new ProjectManagerService(client, tasks, graph), tasks, graph, client);
    }

    private static ContextEntityDto CreateManagedProjectEntity(ContextGraphStore graph, string goal = "Manual project") =>
        graph.CreateEntity(new CreateContextEntityRequest
        {
            EntityType = "project",
            Name = goal,
            Source = "project-manager",
            MetadataJson = "{\"schemaVersion\":3,\"goal\":\"" + goal + "\"}"
        });

    [Fact]
    public async Task CreateProjectAsync_rejects_an_empty_goal()
    {
        var (pm, _, _, _) = Build();
        await Assert.ThrowsAsync<InvalidDataException>(() => pm.CreateProjectAsync("  ", CancellationToken.None));
    }

    [Fact]
    public async Task Project_manager_requests_a_strict_structured_plan_when_provider_supports_it()
    {
        var (pm, _, _, client) = Build();
        client.HonorStructuredOutput = true;
        client.EnqueueOk("[{\"description\":\"Implement it\",\"role\":\"coding\",\"dependsOn\":[]}]");

        await pm.CreateProjectAsync("Implement it", CancellationToken.None);

        FakeLocalCompletionClient.StructuredCall call = Assert.Single(client.StructuredCalls);
        Assert.Equal("parux_project_plan", call.Request.Name);
        Assert.True(call.Request.Strict);
        Assert.Contains("additionalProperties\":false", call.Request.JsonSchema, StringComparison.Ordinal);
        Assert.Contains("\"maxItems\":8", call.Request.JsonSchema, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_well_formed_plan_creates_real_tasks_with_correct_dependency_wiring_and_repair_budget()
    {
        var (pm, tasks, graph, client) = Build();
        client.EnqueueOk(@"[
            {""description"":""Design the settings schema"",""role"":""planner"",""dependsOn"":[]},
            {""description"":""Implement the settings screen"",""role"":""coding"",""dependsOn"":[0]},
            {""description"":""Write tests for the settings screen"",""role"":""testing"",""dependsOn"":[1]}
        ]");

        ProjectDto project = await pm.CreateProjectAsync("Add a settings screen", CancellationToken.None);

        Assert.Equal(3, project.TaskIds.Count);
        AgentTaskDto design = tasks.Get(project.TaskIds[0])!;
        AgentTaskDto implement = tasks.Get(project.TaskIds[1])!;
        AgentTaskDto test = tasks.Get(project.TaskIds[2])!;

        Assert.Equal("Design the settings schema", design.Goal);
        Assert.Empty(design.DependsOnTaskIds);
        Assert.Equal("Implement the settings screen", implement.Goal);
        Assert.Equal(new[] { design.Id }, implement.DependsOnTaskIds);
        Assert.Equal("Write tests for the settings screen", test.Goal);
        Assert.Equal(new[] { implement.Id }, test.DependsOnTaskIds);
        Assert.All(new[] { design, implement, test }, task => Assert.Equal(2, task.MaxRepairAttempts));

        ContextNeighborhoodResponse projectNeighborhood = graph.GetNeighborhood(project.Id)!;
        Assert.Equal(3, projectNeighborhood.OutgoingRelations.Count);
        Assert.All(projectNeighborhood.OutgoingRelations, r => Assert.Equal("CONTAINS", r.RelationType));
        Assert.Equal(3, projectNeighborhood.OutgoingRelations.Select(r => r.ToEntityId).Distinct().Count());
    }

    [Fact]
    public async Task Specialist_capabilities_are_explicit_and_repository_gui_roles_are_not_widened()
    {
        var (pm, tasks, _, client) = Build();
        client.EnqueueOk(@"[
            {""description"":""Research current evidence"",""role"":""research"",""dependsOn"":[]},
            {""description"":""Review the evidence"",""role"":""review"",""dependsOn"":[0]},
            {""description"":""Implement the fix"",""role"":""coding"",""dependsOn"":[1]},
            {""description"":""Inspect the real GUI"",""role"":""computer-use"",""dependsOn"":[2]}
        ]");

        ProjectDto project = await pm.CreateProjectAsync("Evidence to implementation", CancellationToken.None);
        AgentTaskDto research = tasks.Get(project.TaskIds[0])!;
        AgentTaskDto review = tasks.Get(project.TaskIds[1])!;
        AgentTaskDto coding = tasks.Get(project.TaskIds[2])!;
        AgentTaskDto gui = tasks.Get(project.TaskIds[3])!;

        Assert.Equal(new[] { "context.read", "web.research", "agent.delegate" }, research.GrantedCapabilities);
        Assert.Equal(new[] { "context.read", "repository.read", "agent.delegate" }, review.GrantedCapabilities);
        Assert.Equal(new[] { CodingTaskPermissionProfile.AgentDelegateCapability }, coding.GrantedCapabilities);
        Assert.Equal(CodingTaskPermissionProfile.Offline,
            CodingTaskPermissionProfile.FromCapabilities(coding.GrantedCapabilities));
        Assert.DoesNotContain(CodingTaskPermissionProfile.SafeRestoreCapability, coding.GrantedCapabilities);
        Assert.DoesNotContain(CodingTaskPermissionProfile.ScriptedRestoreCapability, coding.GrantedCapabilities);
        Assert.Empty(gui.GrantedCapabilities);
    }

    [Theory]
    [InlineData("planner")]
    [InlineData("review")]
    [InlineData("testing")]
    [InlineData("security")]
    public void Repository_specialists_receive_context_read_only_repo_and_bounded_delegation(string role)
    {
        Assert.Equal(new[] { "context.read", "repository.read", "agent.delegate" },
            ProjectManagerService.DefaultCapabilitiesForRole(role));
    }

    [Fact]
    public void Documentation_receives_repo_read_without_child_spawning()
    {
        Assert.Equal(new[] { "context.read", "repository.read" },
            ProjectManagerService.DefaultCapabilitiesForRole("documentation"));
    }

    [Fact]
    public void AttachTask_is_idempotent_and_project_freshness_tracks_the_contains_relation()
    {
        var (pm, tasks, graph, _) = Build();
        ContextEntityDto projectEntity = CreateManagedProjectEntity(graph);
        AgentTaskDto task = tasks.Create(new CreateAgentTaskRequest { Goal = "Continue implementation", Role = "coding" });

        ProjectDto first = pm.AttachTask(projectEntity.Id, task.Id);
        ProjectDto second = pm.AttachTask(projectEntity.Id, task.Id);
        ContextNeighborhoodResponse neighborhood = graph.GetNeighborhood(projectEntity.Id)!;

        Assert.Equal(first.Id, second.Id);
        Assert.Single(first.TaskIds);
        Assert.Equal(task.Id, first.TaskIds[0]);
        Assert.Equal(first.TaskIds, second.TaskIds);
        Assert.Single(neighborhood.OutgoingRelations.Where(r => r.RelationType == "CONTAINS"));
        Assert.Equal(1, graph.ListEntities("task").Count(e => e.MetadataJson?.Contains(task.Id, StringComparison.Ordinal) == true));

        DateTime relationCreated = neighborhood.OutgoingRelations.Single(r => r.RelationType == "CONTAINS").CreatedUtc;
        Assert.Equal(relationCreated, second.UpdatedUtc);
        Assert.True(second.UpdatedUtc >= second.CreatedUtc);
    }

    [Fact]
    public void Project_view_includes_self_delegated_task_descendants_without_fake_duplicate_graph_entities()
    {
        var (pm, tasks, graph, _) = Build();
        ContextEntityDto projectEntity = CreateManagedProjectEntity(graph, "Delegation project");
        AgentTaskDto parent = tasks.Create(new CreateAgentTaskRequest
        {
            Goal = "Review implementation",
            Role = "review",
            GrantedCapabilities = new List<string> { "context.read", "repository.read", "agent.delegate" }
        });
        pm.AttachTask(projectEntity.Id, parent.Id);

        DelegateAgentTaskResponse delegated = tasks.DelegateSubtask(new DelegateAgentTaskRequest
        {
            ParentTaskId = parent.Id,
            Goal = "Inspect the security boundary",
            Role = "security",
            GrantedCapabilities = new List<string> { "context.read", "repository.read" }
        });

        ProjectDto loaded = pm.GetProject(projectEntity.Id)!;

        Assert.Contains(parent.Id, loaded.TaskIds);
        Assert.Contains(delegated.ChildTaskId, loaded.TaskIds);
        Assert.Single(graph.ListEntities("task").Where(e => e.MetadataJson?.Contains(parent.Id, StringComparison.Ordinal) == true));
        Assert.Empty(graph.ListEntities("task").Where(e => e.MetadataJson?.Contains(delegated.ChildTaskId, StringComparison.Ordinal) == true));
    }

    [Fact]
    public void AttachTask_reuses_a_legacy_android_task_entity_instead_of_creating_a_duplicate()
    {
        var (pm, tasks, graph, _) = Build();
        ContextEntityDto projectEntity = CreateManagedProjectEntity(graph, "Cross-device project");
        AgentTaskDto task = tasks.Create(new CreateAgentTaskRequest { Goal = "Work created from phone", Role = "coding" });
        ContextEntityDto legacyTaskEntity = graph.CreateEntity(new CreateContextEntityRequest
        {
            EntityType = "task",
            Name = task.Goal,
            Source = "android-project-bridge",
            MetadataJson = "{\"orchestratorTaskId\":\"" + task.Id + "\"}"
        });

        ProjectDto attached = pm.AttachTask(projectEntity.Id, task.Id);
        ContextNeighborhoodResponse neighborhood = graph.GetNeighborhood(projectEntity.Id)!;

        Assert.Contains(task.Id, attached.TaskIds);
        Assert.Single(graph.ListEntities("task").Where(e => e.MetadataJson?.Contains(task.Id, StringComparison.Ordinal) == true));
        ContextRelationDto relation = Assert.Single(neighborhood.OutgoingRelations.Where(r => r.RelationType == "CONTAINS"));
        Assert.Equal(legacyTaskEntity.Id, relation.ToEntityId);
    }

    [Fact]
    public void AttachTask_rejects_unknown_or_non_project_targets_and_unknown_tasks()
    {
        var (pm, tasks, graph, _) = Build();
        ContextEntityDto project = CreateManagedProjectEntity(graph);
        AgentTaskDto realTask = tasks.Create(new CreateAgentTaskRequest { Goal = "Real task" });
        ContextEntityDto wrongEntity = graph.CreateEntity(new CreateContextEntityRequest
        {
            EntityType = "note",
            Name = "Not a project",
            Source = "project-manager"
        });

        Assert.Throws<InvalidDataException>(() => pm.AttachTask("missing-project", realTask.Id));
        Assert.Throws<InvalidDataException>(() => pm.AttachTask(wrongEntity.Id, realTask.Id));
        Assert.Throws<InvalidDataException>(() => pm.AttachTask(project.Id, "missing-task"));
    }

    [Fact]
    public async Task A_forward_dependency_reference_is_dropped_not_wired_in()
    {
        var (pm, tasks, _, client) = Build();
        client.EnqueueOk(@"[
            {""description"":""First task references a task that comes later"",""role"":""planner"",""dependsOn"":[1]},
            {""description"":""Second task"",""role"":""planner"",""dependsOn"":[]}
        ]");

        ProjectDto project = await pm.CreateProjectAsync("Goal with a malformed forward reference", CancellationToken.None);

        AgentTaskDto first = tasks.Get(project.TaskIds[0])!;
        Assert.Empty(first.DependsOnTaskIds);
    }

    [Fact]
    public async Task Duplicate_and_negative_dependencies_are_normalized()
    {
        var (pm, tasks, _, client) = Build();
        client.EnqueueOk(@"[
            {""description"":""First"",""role"":""planner"",""dependsOn"":[]},
            {""description"":""Second"",""role"":""coding"",""dependsOn"":[0,0,-1,99]}
        ]");

        ProjectDto project = await pm.CreateProjectAsync("normalize dependencies", CancellationToken.None);
        Assert.Equal(new[] { project.TaskIds[0] }, tasks.Get(project.TaskIds[1])!.DependsOnTaskIds);
    }

    [Fact]
    public async Task Unknown_role_is_replaced_by_authoritative_role_inference_not_left_unclaimable()
    {
        var (pm, tasks, _, client) = Build();
        client.EnqueueOk("[{\"description\":\"Run dotnet test and fix failures\",\"role\":\"wizard\",\"dependsOn\":[]}]");

        ProjectDto project = await pm.CreateProjectAsync("fix tests", CancellationToken.None);

        Assert.Equal("testing", tasks.Get(project.TaskIds[0])!.Role);
    }

    [Fact]
    public async Task An_unparseable_model_response_falls_back_to_a_single_task_not_a_fabricated_plan()
    {
        var (pm, tasks, _, client) = Build();
        client.EnqueueOk("I'm not going to give you JSON, sorry about that.");

        ProjectDto project = await pm.CreateProjectAsync("Do the thing", CancellationToken.None);

        Assert.Single(project.TaskIds);
        Assert.Equal("Do the thing", tasks.Get(project.TaskIds[0])!.Goal);
    }

    [Fact]
    public async Task A_provider_failure_falls_back_to_a_single_task_not_zero_tasks()
    {
        var (pm, tasks, _, client) = Build();
        client.EnqueueFailure("Could not reach the local model provider: connection refused");

        ProjectDto project = await pm.CreateProjectAsync("Do the thing", CancellationToken.None);

        Assert.Single(project.TaskIds);
        Assert.Equal("Do the thing", tasks.Get(project.TaskIds[0])!.Goal);
    }

    [Fact]
    public async Task A_plan_longer_than_the_max_is_truncated_not_rejected_for_parser_fallback_providers()
    {
        var (pm, _, _, client) = Build();
        string longPlan = "[" + string.Join(",", Enumerable.Range(0, 20)
            .Select(i => $"{{\"description\":\"Task {i}\",\"role\":\"planner\",\"dependsOn\":[]}}")) + "]";
        client.EnqueueOk(longPlan);

        ProjectDto project = await pm.CreateProjectAsync("A goal that produces too many tasks", CancellationToken.None);

        Assert.True(project.TaskIds.Count <= ProjectManagerService.MaxTasks);
    }

    [Fact]
    public void SystemPrompt_teaches_distinct_real_execution_roles_and_dependency_order()
    {
        string prompt = ProjectManagerService.SystemPrompt;

        Assert.Contains("\"coding\" role owns repository/file edits", prompt);
        Assert.Contains("sandboxed shell/build/test", prompt);
        Assert.Contains("\"computer-use\" role operates the real Windows GUI", prompt);
        Assert.Contains("\"ui-verification\" role observes the real screen", prompt);
        Assert.Contains("READ-ONLY repository tools", prompt);
        Assert.Contains("bounded isolated child tasks", prompt);
        Assert.Contains("repository mutation stays coding-only", prompt);
        Assert.Contains("verification after implementation", prompt);
    }

    [Fact]
    public async Task A_task_the_model_marks_as_coding_is_created_with_role_coding_end_to_end()
    {
        var (pm, tasks, _, client) = Build();
        client.EnqueueOk("[{\"description\":\"Run `dotnet build` and confirm 0 errors/warnings\",\"role\":\"coding\",\"dependsOn\":[]}]");

        ProjectDto project = await pm.CreateProjectAsync("Verify the build is clean", CancellationToken.None);

        Assert.Equal("coding", tasks.Get(project.TaskIds[0])!.Role);
    }

    [Fact]
    public async Task A_task_the_model_marks_as_computer_use_is_created_for_the_windows_worker()
    {
        var (pm, tasks, _, client) = Build();
        client.EnqueueOk(@"[
            {""description"":""Open the built application and navigate to Settings"",""role"":""computer-use"",""dependsOn"":[]},
            {""description"":""Confirm the Settings screen visibly shows the new toggle"",""role"":""ui-verification"",""dependsOn"":[0]}
        ]");

        ProjectDto project = await pm.CreateProjectAsync("Validate the real Windows Settings UI", CancellationToken.None);

        Assert.Equal(2, project.TaskIds.Count);
        Assert.Equal("computer-use", tasks.Get(project.TaskIds[0])!.Role);
        Assert.Equal("ui-verification", tasks.Get(project.TaskIds[1])!.Role);
        Assert.Equal(new[] { project.TaskIds[0] }, tasks.Get(project.TaskIds[1])!.DependsOnTaskIds);
    }
}
