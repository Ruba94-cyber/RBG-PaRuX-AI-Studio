using PaRuX.Contracts;
using PaRuX.Server;
using PaRuX.Server.ArtifactWorkspace;
using PaRuX.Server.ContextGraph;
using PaRuX.Server.Orchestration;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class AgentParentContinuationPolicyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parux-parent-continuation-" + Guid.NewGuid().ToString("N"));

    public AgentParentContinuationPolicyTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public void Independent_read_only_action_runs_before_a_slow_child_and_dependent_action_waits()
    {
        Stores stores = CreateStores();
        AgentTaskDto parent = CreateRunningParent(stores, workspaceRoot: null);
        var client = new ScriptedCompletionClient();
        client.EnqueueAction("""
            {"action":"delegate_tasks","children":[
              {"childGoal":"Map test coverage","role":"review","capabilities":["context.read"]},
              {"childGoal":"Map the code paths","role":"planner","capabilities":["context.read"]}],"readsChildOutput":false}
            """);
        client.EnqueueAction("""{"action":"context_search","query":"independent context lookup","readsChildOutput":false}""");
        client.EnqueueAction("""{"action":"answer","answer":"Finished with child evidence.","readsChildOutput":true}""");
        client.OnStructuredCall = (call, _) =>
        {
            if (call == 2)
            {
                AgentTaskDto[] children = stores.Tasks.List()
                    .Where(task => string.Equals(task.ParentId, parent.Id, StringComparison.Ordinal))
                    .OrderBy(task => task.CreatedUtc)
                    .ToArray();
                Assert.Equal(2, children.Length);
                stores.Tasks.UpdateStatus(children[0].Id, new UpdateAgentTaskStatusRequest { Status = AgentTaskStatus.Running });
                stores.Tasks.UpdateStatus(children[0].Id, new UpdateAgentTaskStatusRequest
                {
                    Status = AgentTaskStatus.Completed,
                    Stage = "Done",
                    ResultJson = "{\"response\":\"first child result\"}"
                });
                stores.Tasks.UpdateStatus(children[1].Id, new UpdateAgentTaskStatusRequest { Status = AgentTaskStatus.Running });
            }
            else if (call == 3)
            {
                AgentTaskDto[] children = stores.Tasks.List()
                    .Where(task => string.Equals(task.ParentId, parent.Id, StringComparison.Ordinal))
                    .OrderBy(task => task.CreatedUtc)
                    .ToArray();
                Assert.Equal(AgentTaskStatus.Completed, children[0].Status);
                Assert.Equal(AgentTaskStatus.Running, children[1].Status);
                Assert.Equal(AgentTaskStatus.Running, stores.Tasks.Get(parent.Id)!.Status);
                Assert.Contains(stores.Tasks.GetLogs(parent.Id), log =>
                    log.Message == "Specialist tool step 2: executing context_search.");
            }
            return null;
        };

        var worker = CreateWorker(stores, client, CreateContextEngine());
        using TaskExecutionLease lease = Claim(stores.Tasks, parent.Id);
        CompletionResult paused = worker.RunCapabilityToolLoopAsync(
            parent, "planner", "parent prompt", Budget(), lease, lease.StopToken).GetAwaiter().GetResult();

        Assert.True(paused.Success);
        AgentTaskDto waitingParent = stores.Tasks.Get(parent.Id)!;
        Assert.Equal(AgentTaskStatus.Waiting, waitingParent.Status);
        Assert.StartsWith("Waiting for delegated child ", waitingParent.Stage, StringComparison.Ordinal);
        AgentTaskDto[] pendingChildren = stores.Tasks.List()
            .Where(task => string.Equals(task.ParentId, parent.Id, StringComparison.Ordinal))
            .OrderBy(task => task.CreatedUtc)
            .ToArray();
        Assert.Equal(AgentTaskStatus.Completed, pendingChildren[0].Status);
        Assert.Equal(AgentTaskStatus.Running, pendingChildren[1].Status);

        string[] firstPassSteps = stores.Tasks.GetLogs(parent.Id)
            .Select(log => log.Message)
            .Where(message => message.StartsWith("Specialist tool step ", StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(new[]
        {
            "Specialist tool step 1: executing delegate_tasks.",
            "Specialist tool step 2: executing context_search.",
            "Specialist tool step 2: context_search returned 0 item(s), 0 chars.",
            "Specialist tool step 3: deferred action 'answer' until delegated children are terminal; waiting for 1 child task(s)."
        }, firstPassSteps);

        stores.Tasks.UpdateStatus(pendingChildren[1].Id, new UpdateAgentTaskStatusRequest
        {
            Status = AgentTaskStatus.Completed,
            Stage = "Done",
            ResultJson = "{\"response\":\"slow child result\"}"
        });
        Assert.Contains(parent.Id, worker.ResumeWaitingDelegators());
        Assert.Equal(AgentTaskStatus.Running, stores.Tasks.Get(parent.Id)!.Status);

        client.EnqueueAction("""{"action":"answer","answer":"Finished with child evidence.","readsChildOutput":true}""");
        using TaskExecutionLease resumedLease = Claim(stores.Tasks, parent.Id);
        CompletionResult finished = worker.RunCapabilityToolLoopAsync(
            stores.Tasks.Get(parent.Id)!,
            "planner",
            "parent prompt",
            Budget(),
            resumedLease,
            resumedLease.StopToken,
            initialChildEvidence: "DELEGATED CHILD EVIDENCE: slow child result").GetAwaiter().GetResult();

        Assert.True(finished.Success);
        Assert.Contains("Specialist tool step 1: executing answer.",
            stores.Tasks.GetLogs(parent.Id).Select(log => log.Message));
        Assert.Contains("slow child result", client.StructuredCalls[^1].UserPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stop_cancels_continuing_parent_action_and_live_children()
    {
        Stores stores = CreateStores();
        AgentTaskDto parent = CreateRunningParent(stores, workspaceRoot: null);
        var client = new ScriptedCompletionClient();
        client.EnqueueAction("""{"action":"delegate_task","childGoal":"Review the plan","role":"review","capabilities":["context.read"],"readsChildOutput":false}""");
        client.BlockStructuredCallNumber = 2;
        var worker = CreateWorker(stores, client, CreateContextEngine());
        using TaskExecutionLease lease = Claim(stores.Tasks, parent.Id);

        Task<CompletionResult> running = worker.RunCapabilityToolLoopAsync(
            parent, "planner", "parent prompt", Budget(), lease, lease.StopToken);
        await client.BlockedCallStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        AgentTaskDto child = Assert.Single(stores.Tasks.List().Where(task => task.ParentId == parent.Id));
        Assert.Equal(AgentTaskStatus.Queued, child.Status);
        Assert.True(stores.Tasks.UpdateStatus(parent.Id, new UpdateAgentTaskStatusRequest
        {
            Status = AgentTaskStatus.Cancelled,
            Stage = "Stopped"
        }));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        Assert.Equal(AgentTaskStatus.Cancelled, stores.Tasks.Get(parent.Id)!.Status);
        Assert.Equal(AgentTaskStatus.Cancelled, stores.Tasks.Get(child.Id)!.Status);
        Assert.DoesNotContain(stores.Tasks.GetLogs(parent.Id), log =>
            log.Message == "Specialist tool step 2: executing context_search.");
    }

    [Fact]
    public void Reclaimed_parent_skips_repository_prefetch_when_live_child_shares_the_workspace()
    {
        Stores stores = CreateStores();
        string workspaceRoot = Path.Combine(_root, "shared-workspace");
        Directory.CreateDirectory(workspaceRoot);
        AgentTaskDto parent = CreateRunningParent(stores, workspaceRoot);
        AgentTaskDto child = stores.Tasks.Create(new CreateAgentTaskRequest
        {
            ParentId = parent.Id,
            Goal = "Review the assigned workspace",
            Role = "review",
            GrantedCapabilities = new List<string> { "repository.read" }
        });
        stores.Workspaces.Set(child.Id, workspaceRoot, child);
        Assert.True(stores.Tasks.UpdateStatus(child.Id, new UpdateAgentTaskStatusRequest
        {
            Status = AgentTaskStatus.Running,
            Stage = "Reviewing"
        }));

        var client = new ScriptedCompletionClient();
        client.EnqueueAction("""{"action":"context_search","query":"independent lookup","readsChildOutput":false}""");
        client.EnqueueAction("""{"action":"answer","answer":"Waiting for the workspace review.","readsChildOutput":true}""");
        var worker = CreateWorker(stores, client, CreateContextEngine());
        using TaskExecutionLease lease = Claim(stores.Tasks, parent.Id);

        CompletionResult result = worker.RunCapabilityToolLoopAsync(
            parent, "planner", "parent prompt", Budget(), lease, lease.StopToken).GetAwaiter().GetResult();

        Assert.True(result.Success);
        Assert.Equal(AgentTaskStatus.Waiting, stores.Tasks.Get(parent.Id)!.Status);
        Assert.DoesNotContain("parallel repository snapshot", client.StructuredCalls[0].UserPrompt, StringComparison.Ordinal);
        Assert.Contains(stores.Tasks.GetLogs(parent.Id), log =>
            log.Message == "Execution Profile skipped repository prefetch because a live delegated child shares or may share the workspace.");
        Assert.Contains(stores.Tasks.GetLogs(parent.Id), log =>
            log.Message == "Specialist tool step 1: executing context_search.");
    }

    [Fact]
    public void Missing_dependency_declaration_is_rejected_while_a_child_is_live()
    {
        Stores stores = CreateStores();
        AgentTaskDto parent = CreateRunningParent(stores, workspaceRoot: null);
        AgentTaskDto child = stores.Tasks.Create(new CreateAgentTaskRequest
        {
            ParentId = parent.Id,
            Goal = "Review independent context",
            Role = "review",
            GrantedCapabilities = new List<string> { "context.read" }
        });
        Assert.True(stores.Tasks.UpdateStatus(child.Id, new UpdateAgentTaskStatusRequest
        {
            Status = AgentTaskStatus.Running,
            Stage = "Reviewing"
        }));

        var client = new ScriptedCompletionClient();
        client.EnqueueAction("""{"action":"context_search","query":"missing dependency declaration"}""");
        client.EnqueueAction("""{"action":"context_search","query":"independent lookup","readsChildOutput":false}""");
        client.EnqueueAction("""{"action":"answer","answer":"Waiting for the child.","readsChildOutput":false}""");
        var worker = CreateWorker(stores, client, CreateContextEngine());
        using TaskExecutionLease lease = Claim(stores.Tasks, parent.Id);

        CompletionResult result = worker.RunCapabilityToolLoopAsync(
            parent, "planner", "parent prompt", Budget(), lease, lease.StopToken).GetAwaiter().GetResult();

        Assert.True(result.Success);
        AgentTaskDto waitingParent = stores.Tasks.Get(parent.Id)!;
        Assert.Equal(AgentTaskStatus.Waiting, waitingParent.Status);
        string[] messages = stores.Tasks.GetLogs(parent.Id).Select(log => log.Message).ToArray();
        Assert.Contains("Specialist tool step 1: protocol error: readsChildOutput must be explicitly true or false.", messages);
        Assert.DoesNotContain("Specialist tool step 1: executing context_search.", messages);
        Assert.Contains("Specialist tool step 2: executing context_search.", messages);
    }

    [Fact]
    public void Dependent_action_rechecks_child_evidence_when_child_finishes_during_model_call()
    {
        Stores stores = CreateStores();
        AgentTaskDto parent = CreateRunningParent(stores, workspaceRoot: null);
        var client = new ScriptedCompletionClient();
        client.EnqueueAction("""
            {"action":"delegate_tasks","children":[
              {"childGoal":"Inspect the first area","role":"review","capabilities":["context.read"]},
              {"childGoal":"Inspect the second area","role":"planner","capabilities":["context.read"]}],"readsChildOutput":false}
            """);
        client.EnqueueAction("""{"action":"context_search","query":"independent lookup","readsChildOutput":false}""");
        client.EnqueueAction("""{"action":"answer","answer":"Answer from an old snapshot.","readsChildOutput":true}""");
        client.EnqueueAction("""{"action":"answer","answer":"Answer using the completed child result.","readsChildOutput":true}""");
        client.OnStructuredCall = (call, _) =>
        {
            AgentTaskDto[] children = stores.Tasks.List()
                .Where(task => string.Equals(task.ParentId, parent.Id, StringComparison.Ordinal))
                .OrderBy(task => task.CreatedUtc)
                .ToArray();

            if (call == 2)
            {
                Assert.Equal(2, children.Length);
                stores.Tasks.UpdateStatus(children[0].Id, new UpdateAgentTaskStatusRequest { Status = AgentTaskStatus.Running });
                stores.Tasks.UpdateStatus(children[0].Id, new UpdateAgentTaskStatusRequest
                {
                    Status = AgentTaskStatus.Completed,
                    Stage = "Done",
                    ResultJson = "{\"response\":\"first child result\"}"
                });
                stores.Tasks.UpdateStatus(children[1].Id, new UpdateAgentTaskStatusRequest { Status = AgentTaskStatus.Running });
            }
            else if (call == 3)
            {
                Assert.Equal(AgentTaskStatus.Running, children[1].Status);
                stores.Tasks.UpdateStatus(children[1].Id, new UpdateAgentTaskStatusRequest
                {
                    Status = AgentTaskStatus.Completed,
                    Stage = "Done",
                    ResultJson = "{\"response\":\"newly completed child result\"}"
                });
            }
            else if (call == 4)
            {
                Assert.Contains("newly completed child result", client.StructuredCalls[3].UserPrompt, StringComparison.Ordinal);
                Assert.DoesNotContain(stores.Tasks.GetLogs(parent.Id), log =>
                    log.Message == "Specialist tool step 3: executing answer.");
            }

            return null;
        };

        var worker = CreateWorker(stores, client, CreateContextEngine());
        using TaskExecutionLease lease = Claim(stores.Tasks, parent.Id);
        CompletionResult result = worker.RunCapabilityToolLoopAsync(
            parent, "planner", "parent prompt", Budget(), lease, lease.StopToken).GetAwaiter().GetResult();

        Assert.True(result.Success);
        Assert.Equal("Answer using the completed child result.", result.Content);
        string[] stepMessages = stores.Tasks.GetLogs(parent.Id)
            .Select(log => log.Message)
            .Where(message => message.StartsWith("Specialist tool step ", StringComparison.Ordinal))
            .ToArray();
        Assert.Contains("Specialist tool step 3: refreshed child evidence; requesting a new action before executing 'answer'.", stepMessages);
        Assert.Contains("Specialist tool step 4: executing answer.", stepMessages);
    }

    [Fact]
    public void Dependent_action_rechecks_when_a_new_child_result_was_truncated_behind_older_evidence()
    {
        Stores stores = CreateStores();
        AgentTaskDto parent = CreateRunningParent(stores, workspaceRoot: null);
        var client = new ScriptedCompletionClient();
        client.EnqueueAction("""
            {"action":"delegate_tasks","children":[
              {"childGoal":"Inspect the first area","role":"review","capabilities":["context.read"]},
              {"childGoal":"Inspect the second area","role":"planner","capabilities":["context.read"]}],"readsChildOutput":false}
            """);
        client.EnqueueAction("""{"action":"context_search","query":"independent lookup","readsChildOutput":false}""");
        client.EnqueueAction("""{"action":"answer","answer":"Answer from an incomplete evidence snapshot.","readsChildOutput":true}""");
        client.EnqueueAction("""{"action":"answer","answer":"Answer using the latest child result.","readsChildOutput":true}""");
        client.OnStructuredCall = (call, prompt) =>
        {
            AgentTaskDto[] children = stores.Tasks.List()
                .Where(task => string.Equals(task.ParentId, parent.Id, StringComparison.Ordinal))
                .OrderBy(task => task.CreatedUtc)
                .ToArray();

            if (call == 2)
            {
                Assert.Equal(2, children.Length);
                stores.Tasks.UpdateStatus(children[0].Id, new UpdateAgentTaskStatusRequest { Status = AgentTaskStatus.Running });
                stores.Tasks.UpdateStatus(children[0].Id, new UpdateAgentTaskStatusRequest
                {
                    Status = AgentTaskStatus.Completed,
                    Stage = "Done",
                    ResultJson = "{\"response\":\"" + new string('x', 8_000) + "\"}"
                });
                stores.Tasks.UpdateStatus(children[1].Id, new UpdateAgentTaskStatusRequest { Status = AgentTaskStatus.Running });
            }
            else if (call == 3)
            {
                Assert.Equal(AgentTaskStatus.Running, children[1].Status);
                stores.Tasks.UpdateStatus(children[1].Id, new UpdateAgentTaskStatusRequest
                {
                    Status = AgentTaskStatus.Completed,
                    Stage = "Done",
                    ResultJson = "{\"response\":\"new child result marker\"}"
                });
            }
            else if (call == 4)
            {
                Assert.Contains("new child result marker", prompt, StringComparison.Ordinal);
                Assert.DoesNotContain(stores.Tasks.GetLogs(parent.Id), log =>
                    log.Message == "Specialist tool step 3: executing answer.");
            }

            return null;
        };

        var worker = CreateWorker(stores, client, CreateContextEngine());
        using TaskExecutionLease lease = Claim(stores.Tasks, parent.Id);
        CompletionResult result = worker.RunCapabilityToolLoopAsync(
            parent,
            "planner",
            "parent prompt",
            Budget(toolObservationCharacters: 2_000),
            lease,
            lease.StopToken).GetAwaiter().GetResult();

        Assert.True(result.Success);
        Assert.Equal("Answer using the latest child result.", result.Content);
        Assert.Contains(stores.Tasks.GetLogs(parent.Id), log =>
            log.Message == "Specialist tool step 3: refreshed child evidence; requesting a new action before executing 'answer'.");
        Assert.Contains(stores.Tasks.GetLogs(parent.Id), log =>
            log.Message == "Specialist tool step 4: executing answer.");
    }

    [Fact]
    public void Dependent_action_rechecks_when_a_new_child_failure_was_truncated_behind_older_evidence()
    {
        Stores stores = CreateStores();
        AgentTaskDto parent = CreateRunningParent(stores, workspaceRoot: null);
        var client = new ScriptedCompletionClient();
        client.EnqueueAction("""
            {"action":"delegate_tasks","children":[
              {"childGoal":"Inspect the first area","role":"review","capabilities":["context.read"]},
              {"childGoal":"Inspect the second area","role":"planner","capabilities":["context.read"]}],"readsChildOutput":false}
            """);
        client.EnqueueAction("""{"action":"context_search","query":"independent lookup","readsChildOutput":false}""");
        client.EnqueueAction("""{"action":"answer","answer":"Answer before the child failure was visible.","readsChildOutput":true}""");
        client.EnqueueAction("""{"action":"answer","answer":"Answer after reviewing the child failure.","readsChildOutput":true}""");
        client.OnStructuredCall = (call, prompt) =>
        {
            AgentTaskDto[] children = stores.Tasks.List()
                .Where(task => string.Equals(task.ParentId, parent.Id, StringComparison.Ordinal))
                .OrderBy(task => task.CreatedUtc)
                .ToArray();

            if (call == 2)
            {
                Assert.Equal(2, children.Length);
                stores.Tasks.UpdateStatus(children[0].Id, new UpdateAgentTaskStatusRequest { Status = AgentTaskStatus.Running });
                stores.Tasks.UpdateStatus(children[0].Id, new UpdateAgentTaskStatusRequest
                {
                    Status = AgentTaskStatus.Completed,
                    Stage = "Done",
                    ResultJson = "{\"response\":\"" + new string('x', 8_000) + "\"}"
                });
                stores.Tasks.UpdateStatus(children[1].Id, new UpdateAgentTaskStatusRequest { Status = AgentTaskStatus.Running });
            }
            else if (call == 3)
            {
                Assert.Equal(AgentTaskStatus.Running, children[1].Status);
                stores.Tasks.UpdateStatus(children[1].Id, new UpdateAgentTaskStatusRequest
                {
                    Status = AgentTaskStatus.Failed,
                    Stage = "Failed",
                    Error = "new child failure marker"
                });
            }
            else if (call == 4)
            {
                Assert.Contains("new child failure marker", prompt, StringComparison.Ordinal);
                Assert.DoesNotContain(stores.Tasks.GetLogs(parent.Id), log =>
                    log.Message == "Specialist tool step 3: executing answer.");
            }

            return null;
        };

        var worker = CreateWorker(stores, client, CreateContextEngine());
        using TaskExecutionLease lease = Claim(stores.Tasks, parent.Id);
        CompletionResult result = worker.RunCapabilityToolLoopAsync(
            parent,
            "planner",
            "parent prompt",
            Budget(toolObservationCharacters: 2_000),
            lease,
            lease.StopToken).GetAwaiter().GetResult();

        Assert.True(result.Success);
        Assert.Equal("Answer after reviewing the child failure.", result.Content);
        Assert.Contains(stores.Tasks.GetLogs(parent.Id), log =>
            log.Message == "Specialist tool step 3: refreshed child evidence; requesting a new action before executing 'answer'.");
        Assert.Contains(stores.Tasks.GetLogs(parent.Id), log =>
            log.Message == "Specialist tool step 4: executing answer.");
    }

    [Fact]
    public void Latest_child_evidence_stays_in_the_prompt_after_tool_observations_fill_the_window()
    {
        Stores stores = CreateStores();
        AgentTaskDto parent = CreateRunningParent(stores, workspaceRoot: null);
        var client = new ScriptedCompletionClient();
        client.EnqueueAction("""
            {"action":"delegate_tasks","children":[
              {"childGoal":"Inspect first area","role":"review","capabilities":["context.read"]},
              {"childGoal":"Inspect second area","role":"planner","capabilities":["context.read"]}],"readsChildOutput":false}
            """);
        for (int call = 2; call <= 11; call++)
            client.EnqueueAction("""{"action":"context_search","query":"independent lookup","readsChildOutput":false}""");
        client.EnqueueAction("""{"action":"answer","answer":"Final answer with child evidence.","readsChildOutput":true}""");
        client.OnStructuredCall = (call, prompt) =>
        {
            AgentTaskDto[] children = stores.Tasks.List()
                .Where(task => string.Equals(task.ParentId, parent.Id, StringComparison.Ordinal))
                .OrderBy(task => task.CreatedUtc)
                .ToArray();
            if (call == 2)
            {
                stores.Tasks.UpdateStatus(children[0].Id, new UpdateAgentTaskStatusRequest { Status = AgentTaskStatus.Running });
                stores.Tasks.UpdateStatus(children[0].Id, new UpdateAgentTaskStatusRequest
                {
                    Status = AgentTaskStatus.Completed,
                    Stage = "Done",
                    ResultJson = "{\"response\":\"first completed result\"}"
                });
                stores.Tasks.UpdateStatus(children[1].Id, new UpdateAgentTaskStatusRequest { Status = AgentTaskStatus.Running });
            }
            else if (call == 3)
            {
                stores.Tasks.UpdateStatus(children[1].Id, new UpdateAgentTaskStatusRequest
                {
                    Status = AgentTaskStatus.Completed,
                    Stage = "Done",
                    ResultJson = "{\"response\":\"latest child evidence survives\"}"
                });
            }
            else if (call == 12)
            {
                Assert.Contains("latest child evidence survives", prompt, StringComparison.Ordinal);
            }

            return null;
        };

        var worker = CreateWorker(stores, client, CreateContextEngine());
        using TaskExecutionLease lease = Claim(stores.Tasks, parent.Id);
        CompletionResult result = worker.RunCapabilityToolLoopAsync(
            parent, "planner", "parent prompt", Budget(maxToolSteps: 12), lease, lease.StopToken).GetAwaiter().GetResult();

        Assert.True(result.Success);
        Assert.Equal("Final answer with child evidence.", result.Content);
    }

    [Fact]
    public void Policy_blocks_writes_child_output_and_shared_workspace_but_allows_independent_reads()
    {
        var children = new AgentParentChildSnapshot[]
        {
            new("fast", IsTerminal: true, WorkspaceKnown: true, WorkspaceRoot: "D:/fixtures/fast"),
            new("slow", IsTerminal: false, WorkspaceKnown: true, WorkspaceRoot: "D:/fixtures/shared")
        };

        Assert.Equal(AgentParentContinuationDecision.Run,
            AgentParentContinuationPolicy.Decide(
                AgentParentContinuationPolicy.ForToolAction("web_research", readsChildOutput: false, workspaceRoot: null), children));
        Assert.Equal(AgentParentContinuationDecision.WaitForChildren,
            AgentParentContinuationPolicy.Decide(
                new AgentParentActionIntent(false, false, false, false, null), children));
        Assert.Equal(AgentParentContinuationDecision.WaitForChildren,
            AgentParentContinuationPolicy.Decide(
                AgentParentContinuationPolicy.ForToolAction("repo_read", readsChildOutput: true, workspaceRoot: "D:/fixtures/other"), children));
        Assert.Equal(AgentParentContinuationDecision.WaitForChildren,
            AgentParentContinuationPolicy.Decide(
                AgentParentContinuationPolicy.ForToolAction("context_search", readsChildOutput: null, workspaceRoot: null), children));
        Assert.Equal(AgentParentContinuationDecision.WaitForChildren,
            AgentParentContinuationPolicy.Decide(
                AgentParentContinuationPolicy.ForToolAction("repo_list", readsChildOutput: false, workspaceRoot: "d:\\fixtures\\shared\\"), children));
        Assert.Equal(AgentParentContinuationDecision.WaitForChildren,
            AgentParentContinuationPolicy.Decide(
                AgentParentContinuationPolicy.ForToolAction("answer", readsChildOutput: false, workspaceRoot: null), children));
    }

    [Fact]
    public void Only_read_only_advisory_children_can_use_existing_core_continuation_gate()
    {
        Assert.True(AgentParentContinuationPolicy.CanContinueWithChild("review", new[] { "context.read", "repository.read" }));
        Assert.True(AgentParentContinuationPolicy.CanContinueWithChild("planner", capabilities: null));
        Assert.False(AgentParentContinuationPolicy.CanContinueWithChild("testing", new[] { "repository.read" }));
        Assert.False(AgentParentContinuationPolicy.CanContinueWithChild("review", new[] { "web.research" }));
    }

    [Fact]
    public void Repository_read_waits_when_a_live_child_workspace_is_unknown()
    {
        AgentParentActionIntent read = AgentParentContinuationPolicy.ForToolAction(
            "repo_read", readsChildOutput: false, workspaceRoot: "C:/workspace/repo");

        Assert.Equal(AgentParentContinuationDecision.WaitForChildren, AgentParentContinuationPolicy.Decide(
            read,
            new[] { new AgentParentChildSnapshot("child-a", IsTerminal: false, WorkspaceKnown: false, WorkspaceRoot: null) }));
        Assert.Equal(AgentParentContinuationDecision.Run, AgentParentContinuationPolicy.Decide(
            read,
            new[] { new AgentParentChildSnapshot("child-b", IsTerminal: false, WorkspaceKnown: true, WorkspaceRoot: "C:/workspace/other") }));
    }

    [Fact]
    public async Task Invalid_protocol_responses_end_within_the_tool_step_budget()
    {
        Stores stores = CreateStores();
        AgentTaskDto parent = CreateRunningParent(stores, workspaceRoot: null);
        var client = new ScriptedCompletionClient();
        for (int index = 0; index < 50; index++)
            client.EnqueueAction("""{"action":"context_search","query":"missing the readsChildOutput flag"}""");
        var worker = CreateWorker(stores, client, CreateContextEngine());
        using TaskExecutionLease lease = Claim(stores.Tasks, parent.Id);
        AgentExecutionBudget budget = Budget(maxToolSteps: 3);

        CompletionResult result = await worker.RunCapabilityToolLoopAsync(
            parent, "planner", "parent prompt", budget, lease, lease.StopToken).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(result.Success);
        Assert.True(client.StructuredCalls.Count <= budget.MaxToolSteps,
            "Each invalid response must consume one tool step; calls were " + client.StructuredCalls.Count);
        Assert.Contains(stores.Tasks.GetLogs(parent.Id), log =>
            log.Message.Contains("readsChildOutput must be explicitly true or false", StringComparison.Ordinal));
    }

    [Fact]
    public void Child_and_tool_evidence_share_the_configured_character_budget()
    {
        string[] observations = Enumerable.Repeat(new string('o', 150), 8).ToArray();
        const int maximumCharacters = 800;

        var rendered = AgentWorkerService.BuildToolEvidencePrompt(
            observations,
            "latest child result",
            maximumCharacters);

        Assert.True(rendered.PromptText.Length <= maximumCharacters);
        Assert.Equal("latest child result", rendered.ChildEvidenceText);
        Assert.Contains("latest child result", rendered.PromptText, StringComparison.Ordinal);
    }

    // ---- BIC-09: declared file sets carried end to end (task -> store -> snapshot -> parent action) ----

    [Fact]
    public void Declared_files_are_normalized_persisted_and_survive_a_store_restart()
    {
        string state = Path.Combine(_root, "declared-restart");
        Directory.CreateDirectory(state);
        var first = new TaskStore(state);
        AgentTaskDto created = first.Create(new CreateAgentTaskRequest
        {
            Goal = "Edit billing",
            Role = "review",
            DeclaredFiles = new List<string> { " ./src\\Billing/ ", "docs/a.md", "SRC/billing/", "" }
        });

        Assert.Equal(new[] { "src/Billing/", "docs/a.md" }, created.DeclaredFiles);
        AgentTaskDto reloaded = new TaskStore(state).Get(created.Id)!;
        Assert.Equal(created.DeclaredFiles, reloaded.DeclaredFiles);
        Assert.Empty(first.Create(new CreateAgentTaskRequest { Goal = "No files" }).DeclaredFiles);
    }

    [Theory]
    [InlineData("../outside.cs")]
    [InlineData("src/../../x")]
    [InlineData("/etc/passwd")]
    [InlineData("C:\\Windows\\x")]
    public void Declared_files_reject_traversal_and_absolute_paths(string path)
    {
        Stores stores = CreateStores();
        Assert.Throws<InvalidDataException>(() => stores.Tasks.Create(new CreateAgentTaskRequest
        {
            Goal = "bad", DeclaredFiles = new List<string> { path }
        }));
    }

    [Fact]
    public void Delegation_payload_declared_files_reach_the_child_task_and_the_snapshot()
    {
        Stores stores = CreateStores();
        AgentTaskDto parent = CreateRunningParent(stores, workspaceRoot: null);
        var client = new ScriptedCompletionClient();
        client.EnqueueAction("""
            {"action":"delegate_tasks","children":[
              {"childGoal":"Review billing","role":"review","capabilities":["context.read"],"declaredFiles":["src/Billing"]},
              {"childGoal":"Review docs","role":"planner","capabilities":["context.read"],"declaredFiles":["docs/a.md"]}],"readsChildOutput":false}
            """);
        client.EnqueueAction("""{"action":"answer","answer":"x","readsChildOutput":true}""");
        var worker = CreateWorker(stores, client, CreateContextEngine());
        using TaskExecutionLease lease = Claim(stores.Tasks, parent.Id);

        worker.RunCapabilityToolLoopAsync(parent, "planner", "p", Budget(), lease, lease.StopToken).GetAwaiter().GetResult();

        AgentTaskDto[] children = stores.Tasks.List().Where(t => t.ParentId == parent.Id).OrderBy(t => t.CreatedUtc).ToArray();
        Assert.Equal(new[] { "src/Billing" }, children[0].DeclaredFiles);
        Assert.Equal(new[] { "docs/a.md" }, children[1].DeclaredFiles);
    }

    [Fact]
    public void Parent_repo_read_runs_when_files_are_disjoint_from_live_child_and_waits_on_overlap()
    {
        Stores stores = CreateStores();
        string workspaceRoot = Path.Combine(_root, "declared-workspace");
        Directory.CreateDirectory(Path.Combine(workspaceRoot, "src", "Ui"));
        File.WriteAllText(Path.Combine(workspaceRoot, "src", "Ui", "View.cs"), "class View {}");
        AgentTaskDto parent = CreateRunningParent(stores, workspaceRoot);
        AgentTaskDto child = stores.Tasks.Create(new CreateAgentTaskRequest
        {
            ParentId = parent.Id,
            Goal = "Review billing",
            Role = "review",
            GrantedCapabilities = new List<string> { "repository.read" },
            DeclaredFiles = new List<string> { "src/Billing" }
        });
        string childWorktree = Path.Combine(_root, "child-worktree");
        Directory.CreateDirectory(childWorktree);
        stores.Workspaces.Set(child.Id, childWorktree, child);
        Assert.True(stores.Tasks.UpdateStatus(child.Id, new UpdateAgentTaskStatusRequest { Status = AgentTaskStatus.Running }));

        var client = new ScriptedCompletionClient();
        client.EnqueueAction("""{"action":"repo_read","path":"src/Ui/View.cs","readsChildOutput":false}""");
        client.EnqueueAction("""{"action":"repo_read","path":"src/Billing/Invoice.cs","readsChildOutput":false}""");
        var worker = CreateWorker(stores, client, CreateContextEngine());
        using TaskExecutionLease lease = Claim(stores.Tasks, parent.Id);

        worker.RunCapabilityToolLoopAsync(parent, "planner", "p", Budget(), lease, lease.StopToken).GetAwaiter().GetResult();

        string[] logs = stores.Tasks.GetLogs(parent.Id).Select(l => l.Message).ToArray();
        Assert.Contains("Specialist tool step 1: executing repo_read.", logs);
        Assert.Contains(logs, m => m.StartsWith("Specialist tool step 2: deferred action 'repo_read'", StringComparison.Ordinal));
        Assert.Equal(AgentTaskStatus.Waiting, stores.Tasks.Get(parent.Id)!.Status);
    }

    [Fact]
    public void Child_without_a_declared_file_set_keeps_the_conservative_rule_and_files_never_override_it()
    {
        // One live child declares files, the other declares none: the file-set shortcut must not apply.
        AgentParentActionIntent read = AgentParentContinuationPolicy.ForToolAction(
            "repo_read", false, "C:/w", AgentParentContinuationPolicy.FilesForToolAction("repo_read", "src/Ui/View.cs", null));
        Assert.Equal(new[] { "src/Ui/View.cs" }, read.Files);
        var children = new[]
        {
            new AgentParentChildSnapshot("a", false, true, "C:/other", new[] { "src/Billing" }),
            new AgentParentChildSnapshot("b", false, false, null)
        };
        Assert.Equal(AgentParentContinuationDecision.WaitForChildren, AgentParentContinuationPolicy.Decide(read, children));
        Assert.Null(AgentParentContinuationPolicy.FilesForToolAction("repo_grep", "x", null));
        Assert.Null(AgentParentContinuationPolicy.FilesForToolAction("repo_read", "  ", null));
    }

    private Stores CreateStores()
    {
        string state = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(state);
        var tasks = new TaskStore(state);
        var workspaces = new AgentWorkspaceAssignmentStore(state);
        var authoritySets = new AgentRepositoryAuthoritySetStore(state);
        var legacySets = new AgentRepositorySetStore(state, workspaces);
        var coordinator = new AgentDelegationCommitCoordinator(tasks, workspaces, authoritySets, legacySets);
        return new Stores(tasks, workspaces, coordinator);
    }

    private static AgentTaskDto CreateRunningParent(Stores stores, string? workspaceRoot)
    {
        AgentTaskDto parent = stores.Tasks.Create(new CreateAgentTaskRequest
        {
            Goal = "Review a complex architecture and coordinate independent work",
            Role = "planner",
            GrantedCapabilities = new List<string> { "agent.delegate", "context.read", "repository.read" }
        });
        if (workspaceRoot != null) stores.Workspaces.Set(parent.Id, workspaceRoot, parent);
        Assert.True(stores.Tasks.UpdateStatus(parent.Id, new UpdateAgentTaskStatusRequest
        {
            Status = AgentTaskStatus.Running,
            Stage = "Planning"
        }));
        return stores.Tasks.Get(parent.Id)!;
    }

    private static AgentWorkerService CreateWorker(
        Stores stores,
        ScriptedCompletionClient client,
        ContextEngineService context) =>
        new(stores.Tasks, client, new FakeRoleRouter(),
            plannerMemory: null,
            context: context,
            research: null,
            internet: null,
            workspaceAssignments: stores.Workspaces,
            delegationCoordinator: stores.Coordinator);

    private ContextEngineService CreateContextEngine()
    {
        string state = Path.Combine(_root, "context-engine");
        Directory.CreateDirectory(state);
        var embeddings = new EmbeddingService(Path.Combine(state, "embeddings"));
        var memory = new MemoryStore(embeddings, Path.Combine(state, "memory"));
        var rag = new RagStore(embeddings, Path.Combine(state, "rag"));
        var graph = new ContextGraphStore(Path.Combine(state, "graph"));
        var artifacts = new ArtifactStore(Path.Combine(state, "artifacts"));
        var conversations = new ConversationStore(Path.Combine(state, "conversations"));
        var internet = new InternetSettingsService(Path.Combine(state, "internet"));
        var web = new WebSearchService(internet);
        var mcp = new McpClientRegistry(Path.Combine(state, "mcp"));
        var repoIndex = new RepoIndexService(graph);
        return new ContextEngineService(memory, rag, graph, artifacts, conversations, web, internet, mcp, repoIndex);
    }

    private static TaskExecutionLease Claim(TaskStore tasks, string id) =>
        tasks.TryClaimExecution(id) ?? throw new Xunit.Sdk.XunitException("Expected a current execution lease.");

    private static AgentExecutionBudget Budget(int maxToolSteps = 8, int toolObservationCharacters = 12_000) => AgentExecutionBudget.From(new ExecutionProfileStatus
    {
        ActiveQualityProfile = ExecutionQualityProfile.Balanced,
        Effective = new ModelExecutionProfile
        {
            ModelKey = "test-model",
            QualityProfile = ExecutionQualityProfile.Balanced,
            ToolObservationCharacters = toolObservationCharacters,
            RepositoryEvidenceCharacters = 96_000,
            PreferredContextTokens = 65_536,
            DelegationThreshold = 0,
            ToolStrategy = "serial-evidence",
            StructuredOutputStrategy = "schema-first",
            Budget = new ExecutionResourceBudget
            {
                MaxWallClockMinutes = 60,
                MaxToolSteps = maxToolSteps,
                ContextFraction = 0.5,
                MaxDirectSubagents = 2,
                MaxDelegationDepth = 2
            }
        }
    });

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private sealed record Stores(
        TaskStore Tasks,
        AgentWorkspaceAssignmentStore Workspaces,
        AgentDelegationCommitCoordinator Coordinator);

    private sealed class ScriptedCompletionClient : ILocalCompletionClient
    {
        private readonly Queue<string> _actions = new();

        public List<FakeLocalCompletionClient.StructuredCall> StructuredCalls { get; } = new();
        public TaskCompletionSource BlockedCallStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Func<int, string, Task<CompletionResult>?>? OnStructuredCall { get; set; }
        public int? BlockStructuredCallNumber { get; set; }

        public void EnqueueAction(string json) => _actions.Enqueue(json);

        public Task<CompletionResult> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken) =>
            Task.FromResult(CompletionResult.Failure("Unexpected unstructured completion in the focused tool-loop test."));

        public async Task<CompletionResult> CompleteStructuredAsync(
            string systemPrompt,
            string userPrompt,
            LocalStructuredOutputRequest structuredOutput,
            CancellationToken cancellationToken)
        {
            StructuredCalls.Add(new FakeLocalCompletionClient.StructuredCall(systemPrompt, userPrompt, structuredOutput));
            int call = StructuredCalls.Count;
            Task<CompletionResult>? intercepted = OnStructuredCall?.Invoke(call, userPrompt);
            if (intercepted != null) return await intercepted.ConfigureAwait(false);
            if (BlockStructuredCallNumber == call)
            {
                BlockedCallStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            }

            if (_actions.Count == 0)
                return CompletionResult.Failure("No structured action remains in the scripted client.");
            return CompletionResult.Ok(_actions.Dequeue(), structuredOutputApplied: true);
        }
    }
}
