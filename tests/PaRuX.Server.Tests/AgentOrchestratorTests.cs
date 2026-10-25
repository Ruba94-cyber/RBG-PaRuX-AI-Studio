using PaRuX.Contracts;
using PaRuX.Server.Orchestration;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Covers the vNext Phase 4 scheduler: which queued tasks a pass dispatches, and - the
/// property that actually matters for a DAG-based orchestrator - that a dependent task is never
/// dispatched before every task it depends on has reached "completed".
/// </summary>
public sealed class AgentOrchestratorTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "parux-orchestrator-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    [Theory]
    [InlineData("Analyze this executable binary and inspect its PE sections", "binary-analysis")]
    [InlineData("Decompile this DLL for reverse engineering research", "binary-analysis")]
    [InlineData("Disassemble firmware image and identify interesting functions", "binary-analysis")]
    [InlineData("Fix the authentication vulnerability in the login flow", "security")]
    [InlineData("Click Settings and navigate to the Runtime page", "computer-use")]
    [InlineData("Control the PC and open the app", "computer-use")]
    [InlineData("Confirm the screen visibly shows the new toggle", "ui-verification")]
    [InlineData("Inspect screenshot and verify the UI", "ui-verification")]
    [InlineData("Write regression tests for the new endpoint", "testing")]
    [InlineData("Review the pull request for the parser rewrite", "review")]
    [InlineData("Update the README with the new setup steps", "documentation")]
    [InlineData("Update README.md with the new setup steps", "coding")]
    [InlineData("Review changes to README.md", "review")]
    [InlineData("Write a review of changes to README.md", "review")]
    [InlineData("Please write a review of changes to README.md", "review")]
    [InlineData("In the repository, write a review of changes to README.md", "review")]
    [InlineData("Create a plan to update README.md", "planner")]
    [InlineData("Create plan.md", "coding")]
    [InlineData("Create review.md", "coding")]
    [InlineData("Test that the update to src/slug.js handles accents", "testing")]
    [InlineData("Write regression tests covering src/PaRuX.Server/FirstModelAdvisor.cs", "coding")]
    [InlineData("Write unit tests for src/slug.js", "coding")]
    [InlineData("In the isolated repository assigned to this Project, update only README.md", "coding")]
    [InlineData("Research alternative embedding models and compare benchmarks", "research")]
    [InlineData("Break down this goal into milestones", "planner")]
    [InlineData("Implement the new settings screen", "coding")]
    [InlineData("Do a completely unrelated freeform thing", "coding")]
    public void InferRole_matches_the_expected_keyword_category(string goal, string expectedRole) =>
        Assert.Equal(expectedRole, AgentOrchestrator.InferRole(goal));

    [Theory]
    // Observed for real: this goal was routed to "testing", a role with no repository authority,
    // where it could only fail - because the word "test" appears inside "tests must still pass".
    [InlineData("Fix the CS8602 warnings in src/PaRuX.Server/DesktopIntegration/WorkbookPatchStylePreserver.cs. "
        + "The existing tests must still pass.")]
    [InlineData("Refactor src/PaRuX.App/Views/ChatView.xaml.cs and document the change afterwards")]
    [InlineData("Correggi il null check in src/PaRuX.Server/FirstModelAdvisor.cs senza rompere i test")]
    public void A_goal_that_edits_a_named_source_file_stays_a_coding_task(string goal) =>
        Assert.Equal("coding", AgentOrchestrator.InferRole(goal));

    [Theory]
    // Substring matching read these keywords inside ordinary words and silently changed which
    // specialist - and therefore which tool grants - the task was given.
    [InlineData("Ship the latest build to the release channel", "testing")]
    [InlineData("Write an explanation of the routing heuristics", "planner")]
    public void A_keyword_buried_inside_another_word_no_longer_decides_the_role(string goal, string wrongRole) =>
        Assert.NotEqual(wrongRole, AgentOrchestrator.InferRole(goal));

    [Fact]
    public void A_genuine_testing_goal_is_still_a_testing_task()
    {
        // Test execution/verification stays in the read-only testing role; creating files needs coding authority.
        Assert.Equal("testing", AgentOrchestrator.InferRole(
            "Test that the update to src/PaRuX.Server/FirstModelAdvisor.cs handles accents"));
    }

    [Fact]
    public void Repository_provisioning_wait_does_not_consume_a_scheduler_slot()
    {
        var store = new TaskStore(_tempDir);
        var profile = new ModelExecutionProfile
        {
            Budget = new ExecutionResourceBudget { MaxParallelTasks = 1 }
        };
        var orchestrator = new AgentOrchestrator(store, () => profile);
        AgentTaskDto provisioning = store.CreateDeferred(
            new CreateAgentTaskRequest { Goal = "Implement src/slug.js", Role = "coding" },
            TaskStore.ProjectRepositoryProvisioningStage);
        AgentTaskDto queued = store.Create(new CreateAgentTaskRequest { Goal = "A separate queued task" });

        List<string> dispatched = orchestrator.RunSchedulingPass();

        Assert.Contains(queued.Id, dispatched);
        Assert.Equal(AgentTaskStatus.Waiting, store.Get(provisioning.Id)!.Status);
    }

    [Fact]
    public void A_task_with_no_dependencies_is_dispatched_on_the_first_pass()
    {
        var store = new TaskStore(_tempDir);
        var orchestrator = new AgentOrchestrator(store);
        AgentTaskDto task = store.Create(new CreateAgentTaskRequest { Goal = "Implement the settings screen" });

        List<string> dispatched = orchestrator.RunSchedulingPass();

        Assert.Contains(task.Id, dispatched);
        AgentTaskDto? fetched = store.Get(task.Id);
        Assert.Equal(AgentTaskStatus.Running, fetched!.Status);
        Assert.Equal("coding", fetched.Role);
        Assert.Contains(CodingTaskPermissionProfile.AgentDelegateCapability, fetched.GrantedCapabilities);
        Assert.Contains("Dispatched to coding role", fetched.Stage);
    }

    [Fact]
    public void An_explicit_root_coding_task_gets_bounded_delegation_before_scheduling()
    {
        var store = new TaskStore(_tempDir);

        AgentTaskDto task = store.Create(new CreateAgentTaskRequest
        {
            Goal = "Implement the feature",
            Role = "coding"
        });

        Assert.Equal("coding", task.Role);
        Assert.Contains(CodingTaskPermissionProfile.AgentDelegateCapability, task.GrantedCapabilities);
        Assert.DoesNotContain(CodingTaskPermissionProfile.SafeRestoreCapability, task.GrantedCapabilities);
        Assert.DoesNotContain(CodingTaskPermissionProfile.ScriptedRestoreCapability, task.GrantedCapabilities);
    }

    [Fact]
    public void An_auto_coding_child_is_not_widened_beyond_its_parent_capabilities()
    {
        var store = new TaskStore(_tempDir);
        var orchestrator = new AgentOrchestrator(store);
        AgentTaskDto parent = store.Create(new CreateAgentTaskRequest
        {
            Goal = "Plan the implementation",
            Role = "planner"
        });
        AgentTaskDto child = store.Create(new CreateAgentTaskRequest
        {
            ParentId = parent.Id,
            Goal = "Implement the feature"
        });

        orchestrator.RunSchedulingPass();

        AgentTaskDto fetched = store.Get(child.Id)!;
        Assert.Equal("coding", fetched.Role);
        Assert.Empty(fetched.GrantedCapabilities);
    }

    [Fact]
    public void A_task_keeps_its_explicitly_requested_role_instead_of_the_inferred_one()
    {
        var store = new TaskStore(_tempDir);
        var orchestrator = new AgentOrchestrator(store);
        AgentTaskDto task = store.Create(new CreateAgentTaskRequest { Goal = "Implement the settings screen", Role = "lead" });

        orchestrator.RunSchedulingPass();

        Assert.Equal("lead", store.Get(task.Id)!.Role);
    }

    [Fact]
    public void A_dependent_task_is_never_dispatched_before_its_dependency_completes()
    {
        var store = new TaskStore(_tempDir);
        var orchestrator = new AgentOrchestrator(store);
        AgentTaskDto backend = store.Create(new CreateAgentTaskRequest { Goal = "Backend change" });
        AgentTaskDto integrationTest = store.Create(new CreateAgentTaskRequest
        {
            Goal = "Integration test the backend change",
            DependsOnTaskIds = new List<string> { backend.Id }
        });

        List<string> firstPass = orchestrator.RunSchedulingPass();
        Assert.Contains(backend.Id, firstPass);
        Assert.DoesNotContain(integrationTest.Id, firstPass);
        Assert.Equal(AgentTaskStatus.Queued, store.Get(integrationTest.Id)!.Status);

        List<string> secondPass = orchestrator.RunSchedulingPass();
        Assert.DoesNotContain(integrationTest.Id, secondPass);

        store.UpdateStatus(backend.Id, new UpdateAgentTaskStatusRequest { Status = AgentTaskStatus.Completed });

        List<string> thirdPass = orchestrator.RunSchedulingPass();
        Assert.Contains(integrationTest.Id, thirdPass);
        Assert.Equal(AgentTaskStatus.Running, store.Get(integrationTest.Id)!.Status);
    }

    [Fact]
    public void A_task_already_dispatched_is_not_dispatched_again_on_a_later_pass()
    {
        var store = new TaskStore(_tempDir);
        var orchestrator = new AgentOrchestrator(store);
        AgentTaskDto task = store.Create(new CreateAgentTaskRequest { Goal = "Implement the settings screen" });

        orchestrator.RunSchedulingPass();
        List<string> secondPass = orchestrator.RunSchedulingPass();

        Assert.DoesNotContain(task.Id, secondPass);
    }

    [Fact]
    public void A_dependent_task_cascades_to_blocked_when_its_direct_dependency_permanently_fails()
    {
        var store = new TaskStore(_tempDir);
        var orchestrator = new AgentOrchestrator(store);
        AgentTaskDto backend = store.Create(new CreateAgentTaskRequest { Goal = "Backend change" });
        AgentTaskDto integrationTest = store.Create(new CreateAgentTaskRequest
        {
            Goal = "Integration test the backend change",
            DependsOnTaskIds = new List<string> { backend.Id }
        });

        store.UpdateStatus(backend.Id, new UpdateAgentTaskStatusRequest { Status = AgentTaskStatus.Failed });

        orchestrator.RunSchedulingPass();

        AgentTaskDto? fetched = store.Get(integrationTest.Id);
        Assert.Equal(AgentTaskStatus.Blocked, fetched!.Status);
        Assert.Contains(backend.Id, fetched.Stage);
    }

    [Fact]
    public void A_two_hop_transitive_dependency_failure_cascades_in_a_single_pass()
    {
        var store = new TaskStore(_tempDir);
        var orchestrator = new AgentOrchestrator(store);
        AgentTaskDto taskC = store.Create(new CreateAgentTaskRequest { Goal = "Task C" });
        AgentTaskDto taskB = store.Create(new CreateAgentTaskRequest
        {
            Goal = "Task B",
            DependsOnTaskIds = new List<string> { taskC.Id }
        });
        AgentTaskDto taskA = store.Create(new CreateAgentTaskRequest
        {
            Goal = "Task A",
            DependsOnTaskIds = new List<string> { taskB.Id }
        });

        store.UpdateStatus(taskC.Id, new UpdateAgentTaskStatusRequest { Status = AgentTaskStatus.Failed });

        orchestrator.RunSchedulingPass();

        AgentTaskDto? fetchedA = store.Get(taskA.Id);
        Assert.Equal(AgentTaskStatus.Blocked, fetchedA!.Status);
        Assert.Contains(taskC.Id, fetchedA.Stage);
    }

    [Fact]
    public void A_dependent_task_cascades_to_blocked_when_its_dependency_is_cancelled()
    {
        var store = new TaskStore(_tempDir);
        var orchestrator = new AgentOrchestrator(store);
        AgentTaskDto backend = store.Create(new CreateAgentTaskRequest { Goal = "Backend change" });
        AgentTaskDto integrationTest = store.Create(new CreateAgentTaskRequest
        {
            Goal = "Integration test the backend change",
            DependsOnTaskIds = new List<string> { backend.Id }
        });

        store.UpdateStatus(backend.Id, new UpdateAgentTaskStatusRequest { Status = AgentTaskStatus.Cancelled });

        orchestrator.RunSchedulingPass();

        AgentTaskDto? fetched = store.Get(integrationTest.Id);
        Assert.Equal(AgentTaskStatus.Blocked, fetched!.Status);
        Assert.Contains(backend.Id, fetched.Stage);
    }

    [Fact]
    public void A_task_depending_on_a_supervised_held_task_is_not_cascaded_and_still_dispatches_after_approval()
    {
        var store = new TaskStore(_tempDir);
        var orchestrator = new AgentOrchestrator(store);
        AgentTaskDto held = store.Create(new CreateAgentTaskRequest { Goal = "Held for Autopilot SUPERVISED approval" });
        AgentTaskDto dependent = store.Create(new CreateAgentTaskRequest
        {
            Goal = "Depends on the held task",
            DependsOnTaskIds = new List<string> { held.Id }
        });

        store.UpdateStatus(held.Id, new UpdateAgentTaskStatusRequest
        {
            Status = AgentTaskStatus.Blocked,
            Stage = "Held for Autopilot SUPERVISED approval"
        });

        List<string> passWhileHeld = orchestrator.RunSchedulingPass();
        Assert.DoesNotContain(dependent.Id, passWhileHeld);
        Assert.Equal(AgentTaskStatus.Queued, store.Get(dependent.Id)!.Status);

        store.UpdateStatus(held.Id, new UpdateAgentTaskStatusRequest { Status = AgentTaskStatus.Queued });
        List<string> passAfterApproval = orchestrator.RunSchedulingPass();
        Assert.Contains(held.Id, passAfterApproval);

        store.UpdateStatus(held.Id, new UpdateAgentTaskStatusRequest { Status = AgentTaskStatus.Completed });
        List<string> finalPass = orchestrator.RunSchedulingPass();

        Assert.Contains(dependent.Id, finalPass);
        Assert.Equal(AgentTaskStatus.Running, store.Get(dependent.Id)!.Status);
    }

    [Fact]
    public void A_task_with_a_still_in_flight_dependency_is_not_cascaded_and_stays_queued()
    {
        var store = new TaskStore(_tempDir);
        var orchestrator = new AgentOrchestrator(store);
        AgentTaskDto backend = store.Create(new CreateAgentTaskRequest { Goal = "Backend change" });
        AgentTaskDto integrationTest = store.Create(new CreateAgentTaskRequest
        {
            Goal = "Integration test the backend change",
            DependsOnTaskIds = new List<string> { backend.Id }
        });

        List<string> firstPass = orchestrator.RunSchedulingPass();
        Assert.Contains(backend.Id, firstPass);
        Assert.DoesNotContain(integrationTest.Id, firstPass);
        Assert.Equal(AgentTaskStatus.Queued, store.Get(integrationTest.Id)!.Status);

        List<string> secondPass = orchestrator.RunSchedulingPass();
        Assert.DoesNotContain(integrationTest.Id, secondPass);
        Assert.Equal(AgentTaskStatus.Queued, store.Get(integrationTest.Id)!.Status);
    }
}
