using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// CodingAgentService is not compiled into this project (it needs the desktop runtime). These guards
/// pin the BIC-09 S2b wiring from source so the safety properties cannot be silently lost: STOP
/// cancels continued children, done can never complete over a live child, and the schema/validator
/// honour the feature flags. Behaviour of the pieces they call is covered by real tests.
/// </summary>
public sealed class CodingAgentParentContinuationWiringSentinelTests
{
    private static string Source(params string[] relative)
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            string path = Path.Combine(new[] { dir.FullName }.Concat(relative).ToArray());
            if (File.Exists(path)) return File.ReadAllText(path);
        }
        throw new FileNotFoundException(Path.Combine(relative));
    }

    private static string Service() => Source("src", "PaRuX.App", "Services", "CodingAgentService.cs");

    // The action schema enumeration moved to the prompt builder when the coding-agent prompt was split out of the service.
    private static string Builder() => Source("src", "PaRuX.App", "Services", "CodingAgentPromptBuilder.cs");

    [Fact]
    public void Schema_advertises_collect_children_and_continue_parent_and_flags_can_remove_them()
    {
        string s = Service();
        Assert.Contains("\\\"collect_children\\\",\\\"batch_read\\\",\\\"done\\\"", Builder());
        Assert.Contains("\\\"continueParent\\\":{\\\"type\\\":\\\"boolean\\\"}", Builder());
        Assert.Contains("internal static string ActionSchemaFor(bool batchRead, bool parentContinuation, bool mcpToolsAvailable = false)", s);
        Assert.Contains("ActionSchemaFor(batchReadEnabled, continuationEnabled, mcpTools.Count > 0)", s);
        Assert.Contains("CodingAgentFeatureFlags.BatchReadEnabled", s);
        Assert.Contains("CodingAgentFeatureFlags.ParentContinuationEnabled", s);
    }

    [Fact]
    public void Continuing_delegation_is_validated_client_side_and_passed_to_Core()
    {
        string s = Service();
        Assert.Contains("CodingAgentContinuation.TryValidateRequest(children, out string error)", s);
        Assert.Contains("continueParent: action.ContinueParent", s);
        Assert.Contains("ContinuedChildTaskIds: delegated.ChildTaskIds", s);
        // A continued child must never route through the pausing outcome fields.
        int continued = s.IndexOf("(parent continues)", StringComparison.Ordinal);
        Assert.True(continued > 0);
        Assert.DoesNotContain("DelegatedChildTaskId:", s.Substring(continued, 420));
    }

    [Fact]
    public void Done_asks_core_whether_any_child_is_live_before_the_checkpoint_is_cleared()
    {
        string s = Service();
        int guard = s.IndexOf("checkpoint.ContinuationUsed ||", StringComparison.Ordinal);
        int pause = s.IndexOf("AwaitDelegatedChildrenAsync(orchestratorTaskId!", StringComparison.Ordinal);
        int clear = s.IndexOf("CodingAgentCheckpointStore.Clear(resolvedSessionId)", StringComparison.Ordinal);
        Assert.True(guard > 0 && pause > guard && clear > pause, "done must consult Core about children before completing");
        Assert.Contains("WaitingForDelegatedChild = true", s.Substring(pause, 2600));
        Assert.Contains("childHoldRejections >= 5", s);
        // A failed or unreachable Core lookup holds completion instead of letting it through.
        Assert.Contains("if (hold == null)", s);
    }

    [Fact]
    public void Stop_cancels_continued_children_and_the_pausing_path_marks_them_collected()
    {
        string s = Service();
        int cancel = s.IndexOf("catch (OperationCanceledException)\n            {\n                SaveCheckpoint(Math.Max(checkpoint.StepsTaken, 0));", StringComparison.Ordinal);
        if (cancel < 0)
            cancel = s.IndexOf("catch (OperationCanceledException)\r\n            {\r\n                SaveCheckpoint(Math.Max(checkpoint.StepsTaken, 0));", StringComparison.Ordinal);
        Assert.True(cancel > 0);
        Assert.Contains("CancelLiveContinuedChildrenAsync(checkpoint.ContinuedChildTaskIds", s.Substring(cancel, 600));
        Assert.Contains("checkpoint.CollectedChildTaskIds = checkpoint.ContinuedChildTaskIds.ToList();", s);
    }

    [Fact]
    public void Every_run_result_goes_through_Finish_so_executed_checks_reach_the_verifier()
    {
        string s = Service();
        int start = s.IndexOf("public static async Task<CodingAgentResult> RunAsync(", StringComparison.Ordinal);
        int end = s.IndexOf("private static string BuildPrompt(", start, StringComparison.Ordinal);
        string run = s.Substring(start, end - start);
        Assert.DoesNotContain("return new CodingAgentResult", run);
        Assert.True(System.Text.RegularExpressions.Regex.Matches(run, @"return Finish\(new CodingAgentResult").Count >= 10);
        Assert.Contains("CodingAgentResult Finish(CodingAgentResult result)", run);
        Assert.Contains("checkpoint.ExecutedChecks.Clear();", run);
        Assert.Contains("RecordExecutedCheck(checkpoint", run);
    }

    [Fact]
    public void Validator_rejects_batch_read_when_disabled_and_accepts_collect_children()
    {
        string s = Service();
        Assert.Contains("batch_read is disabled for this session", s);
        Assert.Contains("case \"collect_children\":", s);
    }

    [Fact]
    public void Core_exposes_a_desktop_only_await_children_route_that_cannot_resurrect_a_parent()
    {
        string endpoints = Source("src", "PaRuX.Server", "MobileControlEndpoints.cs");
        int route = endpoints.IndexOf("/api/v1/orchestrator/tasks/{parentId}/await-children", StringComparison.Ordinal);
        Assert.True(route > 0);
        string body = endpoints.Substring(route, 700);
        Assert.Contains("IsDesktopOwner(context)", body);
        Assert.Contains("PauseParentForLiveDelegatedChildren(parentId)", body);
        Assert.Contains("request.ContinueParent", endpoints);
    }

    [Fact]
    public void Coding_runner_uses_execution_lease_and_typed_exit_code()
    {
        string service = Service();
        Assert.Contains("RecordExecutedCheck(checkpoint, action.Command ?? string.Empty, outcome.CommandExitCode.Value)", service);
        Assert.Contains("RecordExecutedCheck(CodingAgentCheckpoint checkpoint, string command, int exitCode)", service);

        string worker = Source("src", "PaRuX.App", "Services", "AgentCodingWorkerService.cs");
        Assert.Contains("_desktopExecutionLease", worker);
        Assert.Contains("HasActiveExecutionLease", worker);
        Assert.Contains("await _desktopExecutionLease.WaitAsync(", worker);
        Assert.Contains("_desktopExecutionLease.Release()", worker);
    }
}
