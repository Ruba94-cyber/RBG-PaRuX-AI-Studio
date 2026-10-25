using System.Net;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using PaRuX.Contracts;
using PaRuX.Server;
using PaRuX.Server.Orchestration;
using PaRuX.Server.Workflow;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>Covers Workflow Builder: template validation and Run's real instantiation into the
/// shared TaskStore. These tests exercise persisted dependencies and the resource/role bounds
/// enforced before a workflow can reach the scheduler.</summary>
public sealed class WorkflowTests : IClassFixture<ApiSecurityTests.PaRuXFactory>, IDisposable
{
    private readonly ApiSecurityTests.PaRuXFactory _factory;
    private readonly string _dbDirectory = Path.Combine(Path.GetTempPath(), "parux-workflow-tests-" + Guid.NewGuid().ToString("N"));

    public WorkflowTests(ApiSecurityTests.PaRuXFactory factory)
    {
        _factory = factory;
        Directory.CreateDirectory(_dbDirectory);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dbDirectory, true); } catch { }
    }

    private (WorkflowService workflows, TaskStore tasks) NewService()
    {
        var tasks = new TaskStore(_dbDirectory);
        return (new WorkflowService(new WorkflowStore(_dbDirectory), tasks), tasks);
    }

    [Fact]
    public void Create_rejects_a_workflow_with_no_steps()
    {
        (WorkflowService workflows, _) = NewService();
        Assert.Throws<InvalidDataException>(() => workflows.Create(new CreateWorkflowRequest { Name = "x", Steps = new List<WorkflowStepTemplate>() }));
    }

    [Fact]
    public void Create_rejects_a_step_with_an_empty_goal()
    {
        (WorkflowService workflows, _) = NewService();
        var request = new CreateWorkflowRequest { Name = "x", Steps = new List<WorkflowStepTemplate> { new() { Goal = "" } } };
        Assert.Throws<InvalidDataException>(() => workflows.Create(request));
    }

    [Fact]
    public void Create_rejects_a_step_depending_on_itself_or_a_later_step()
    {
        (WorkflowService workflows, _) = NewService();
        var selfDependency = new CreateWorkflowRequest
        {
            Name = "x", Steps = new List<WorkflowStepTemplate> { new() { Goal = "a", DependsOnStepIndexes = new List<int> { 0 } } },
        };
        Assert.Throws<InvalidDataException>(() => workflows.Create(selfDependency));

        var forwardDependency = new CreateWorkflowRequest
        {
            Name = "x",
            Steps = new List<WorkflowStepTemplate>
            {
                new() { Goal = "a", DependsOnStepIndexes = new List<int> { 1 } },
                new() { Goal = "b" },
            },
        };
        Assert.Throws<InvalidDataException>(() => workflows.Create(forwardDependency));
    }

    [Fact]
    public void Create_rejects_an_out_of_range_dependency_index()
    {
        (WorkflowService workflows, _) = NewService();
        var request = new CreateWorkflowRequest
        {
            Name = "x", Steps = new List<WorkflowStepTemplate> { new() { Goal = "a", DependsOnStepIndexes = new List<int> { 5 } } },
        };
        Assert.Throws<InvalidDataException>(() => workflows.Create(request));
    }

    [Fact]
    public void Create_rejects_unknown_agent_roles()
    {
        (WorkflowService workflows, _) = NewService();
        var request = new CreateWorkflowRequest
        {
            Name = "x", Steps = new List<WorkflowStepTemplate> { new() { Goal = "a", Role = "magic-super-agent" } }
        };
        InvalidDataException error = Assert.Throws<InvalidDataException>(() => workflows.Create(request));
        Assert.Contains("unknown or non-executable", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Create_accepts_every_real_server_and_desktop_agent_role()
    {
        foreach (string role in new[]
        {
            "planner", "research", "review", "documentation", "testing", "security",
            "coding", "binary-analysis", "ui-verification", "computer-use"
        })
        {
            (WorkflowService workflows, _) = NewService();
            WorkflowDto created = workflows.Create(new CreateWorkflowRequest
            {
                Name = role, Steps = new List<WorkflowStepTemplate> { new() { Goal = "do work", Role = role } }
            });
            Assert.Equal(role, created.Steps[0].Role);
        }
    }

    [Fact]
    public void Create_bounds_step_count_and_goal_size()
    {
        (WorkflowService workflows, _) = NewService();
        var tooMany = Enumerable.Range(0, WorkflowService.MaximumSteps + 1)
            .Select(i => new WorkflowStepTemplate { Goal = "step " + i }).ToList();
        Assert.Throws<InvalidDataException>(() => workflows.Create(new CreateWorkflowRequest { Name = "large", Steps = tooMany }));

        Assert.Throws<InvalidDataException>(() => workflows.Create(new CreateWorkflowRequest
        {
            Name = "large-goal",
            Steps = new List<WorkflowStepTemplate> { new() { Goal = new string('x', WorkflowService.MaximumGoalCharacters + 1) } }
        }));
    }

    [Fact]
    public void Create_deduplicates_dependency_indexes_before_persisting()
    {
        (WorkflowService workflows, _) = NewService();
        WorkflowDto created = workflows.Create(new CreateWorkflowRequest
        {
            Name = "canonical",
            Steps = new List<WorkflowStepTemplate>
            {
                new() { Goal = "a" },
                new() { Goal = "b", DependsOnStepIndexes = new List<int> { 0, 0, 0 } }
            }
        });
        Assert.Equal(new[] { 0 }, created.Steps[1].DependsOnStepIndexes);
        Assert.Equal(new[] { 0 }, workflows.Get(created.Id)!.Steps[1].DependsOnStepIndexes);
    }

    [Fact]
    public void Create_accepts_a_valid_multi_step_template_and_persists_it()
    {
        (WorkflowService workflows, _) = NewService();
        var request = new CreateWorkflowRequest
        {
            Name = "Research then summarize",
            Steps = new List<WorkflowStepTemplate>
            {
                new() { Name = "Research", Goal = "Research {{input}}", Role = "research" },
                new() { Name = "Summarize", Goal = "Summarize the research", Role = "documentation", DependsOnStepIndexes = new List<int> { 0 } },
            },
        };

        WorkflowDto created = workflows.Create(request);

        Assert.Equal(2, created.Steps.Count);
        Assert.Contains(workflows.List(), w => w.Id == created.Id);
        Assert.Equal("Research then summarize", workflows.Get(created.Id)?.Name);
    }

    [Fact]
    public void Run_instantiates_a_real_dependency_respecting_task_graph()
    {
        (WorkflowService workflows, TaskStore tasks) = NewService();
        WorkflowDto workflow = workflows.Create(new CreateWorkflowRequest
        {
            Name = "Two-step",
            Steps = new List<WorkflowStepTemplate>
            {
                new() { Goal = "Step A about {{input}}" },
                new() { Goal = "Step B", DependsOnStepIndexes = new List<int> { 0 } },
            },
        });

        WorkflowRunResult result = workflows.Run(workflow.Id, new RunWorkflowRequest { Input = "widgets" });

        Assert.Equal(2, result.CreatedTaskIds.Count);
        AgentTaskDto? taskA = tasks.Get(result.CreatedTaskIds[0]);
        AgentTaskDto? taskB = tasks.Get(result.CreatedTaskIds[1]);

        Assert.Equal("Step A about widgets", taskA?.Goal);
        Assert.Empty(taskA!.DependsOnTaskIds);
        Assert.Equal("Step B", taskB?.Goal);
        Assert.Equal(new List<string> { result.CreatedTaskIds[0] }, taskB!.DependsOnTaskIds);
    }

    [Fact]
    public void Run_rejects_oversized_input_before_creating_tasks()
    {
        (WorkflowService workflows, TaskStore tasks) = NewService();
        WorkflowDto workflow = workflows.Create(new CreateWorkflowRequest
        {
            Name = "input", Steps = new List<WorkflowStepTemplate> { new() { Goal = "{{input}}" } }
        });

        Assert.Throws<InvalidDataException>(() => workflows.Run(workflow.Id,
            new RunWorkflowRequest { Input = new string('x', WorkflowService.MaximumInputCharacters + 1) }));
        Assert.Empty(tasks.List());
    }

    [Fact]
    public void Run_rejects_an_unknown_workflow_id()
    {
        (WorkflowService workflows, _) = NewService();
        Assert.Throws<InvalidDataException>(() => workflows.Run("does-not-exist", new RunWorkflowRequest()));
    }

    [Fact]
    public async Task Workflows_list_endpoint_rejects_missing_token()
    {
        using HttpClient client = _factory.CreateClient();
        HttpResponseMessage response = await client.GetAsync("/api/v1/workflows");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Workflows_list_endpoint_accepts_the_desktop_session_token()
    {
        using HttpClient client = _factory.CreateClient();
        ServerState state = _factory.Services.GetRequiredService<ServerState>();
        client.DefaultRequestHeaders.Add("X-PaRuX-Token", state.Token);

        HttpResponseMessage response = await client.GetAsync("/api/v1/workflows");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
