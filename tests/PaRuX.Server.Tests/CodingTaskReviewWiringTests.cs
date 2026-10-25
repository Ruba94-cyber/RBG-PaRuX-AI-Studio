using System.Text.Json;
using PaRuX.Contracts;
using PaRuX.Server.Orchestration;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Worker-level integration of the BIC-09 review/continuation/revert components. The desktop worker's
/// logic lives in CodingTaskChangeTracker (called by AgentCodingWorkerService and the agent's file writes);
/// the Core side is the real TaskStore. The sink is the only fake: it writes to the real store like the
/// Core endpoints do.
/// </summary>
public sealed class CodingTaskReviewWiringTests : IDisposable
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parux-bic09w-" + Guid.NewGuid().ToString("N"));
    private readonly string _work;
    private readonly TaskStore _store;
    private readonly CodingTaskChangeTracker _tracker;
    private readonly AgentTaskDto _task;
    private readonly StoreSink _sink;

    private sealed class StoreSink : ICodingReviewStatusSink
    {
        private readonly TaskStore _store;
        public List<string> Logs { get; } = new();
        public StoreSink(TaskStore store) => _store = store;
        public Task PutSectionAsync(string taskId, string section, object value, CancellationToken ct)
        {
            _store.SetReviewStatusSection(taskId, section, JsonSerializer.SerializeToNode(value, Web));
            return Task.CompletedTask;
        }
        public Task AppendLogAsync(string taskId, string message, CancellationToken ct)
        {
            Logs.Add(message);
            return Task.CompletedTask;
        }
    }

    public CodingTaskReviewWiringTests()
    {
        _work = Path.Combine(_root, "work");
        Directory.CreateDirectory(_work);
        Directory.CreateDirectory(Path.Combine(_root, "state"));
        _store = new TaskStore(Path.Combine(_root, "state"));
        _task = _store.Create(new CreateAgentTaskRequest { Goal = "fix", Role = "coding" });
        _sink = new StoreSink(_store);
        _tracker = new CodingTaskChangeTracker(Path.Combine(_root, "ledger"), _sink);
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private string W(string rel) => Path.Combine(_work, rel);

    private AgentTaskReviewStatusDto Status() =>
        JsonSerializer.Deserialize<AgentTaskReviewStatusDto>(_store.Get(_task.Id)!.ReviewStatusJson!, Web)!;

    private void AgentWrite(string rel, string content)
    {
        _tracker.BeforeWrite(_task.Id, _work, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(W(rel))!);
        File.WriteAllText(W(rel), content);
        _tracker.AfterWrite(_task.Id, _work, rel);
    }

    [Fact]
    public async Task Failed_verification_offers_revert_then_owner_request_reverts_only_agent_files()
    {
        File.WriteAllText(W("a.cs"), "orig");
        File.WriteAllText(W("user.cs"), "user");
        AgentWrite("a.cs", "agent edit");
        AgentWrite("b.cs", "agent new");

        CodingRevertDecision d = await _tracker.OnVerificationOutcomeAsync(_task.Id, false, autoRevert: false, default);

        Assert.Equal(CodingRevertDecision.Offer, d);
        Assert.Equal("agent edit", File.ReadAllText(W("a.cs"))); // offer only, nothing touched
        AgentOwnChangeRevertStateDto offered = Status().Revert!;
        Assert.True(offered.Offered);
        Assert.Equal(2, offered.OwnChangedFiles);

        // User edits one of the agent's files afterwards; the desktop owner clicks "Revert my changes".
        File.WriteAllText(W("b.cs"), "agent new + user typing");
        Assert.True(_store.RequestOwnChangeRevert(_task.Id).Ok);
        bool handled = await _tracker.HandleRevertRequestAsync(_task.Id, _store.Get(_task.Id)!.ReviewStatusJson, default);

        Assert.True(handled);
        Assert.Equal("orig", File.ReadAllText(W("a.cs")));
        Assert.Equal("user", File.ReadAllText(W("user.cs")));
        Assert.Equal("agent new + user typing", File.ReadAllText(W("b.cs")));
        AgentOwnChangeRevertStateDto done = Status().Revert!;
        Assert.True(done.Completed);
        Assert.Contains(done.Items, i => i.Path == "a.cs" && i.Outcome == "Restored");
        Assert.Contains(done.Items, i => i.Path == "b.cs" && i.Outcome == "SkippedModifiedByUser");

        // Idempotent: a second poll does nothing and a second request is refused.
        Assert.False(await _tracker.HandleRevertRequestAsync(_task.Id, _store.Get(_task.Id)!.ReviewStatusJson, default));
        Assert.False(_store.RequestOwnChangeRevert(_task.Id).Ok);
    }

    [Fact]
    public async Task Passing_verification_or_no_agent_changes_offers_nothing()
    {
        Assert.Equal(CodingRevertDecision.NotNeeded, await _tracker.OnVerificationOutcomeAsync(_task.Id, false, false, default));
        AgentWrite("a.cs", "x");
        Assert.Equal(CodingRevertDecision.NotNeeded, await _tracker.OnVerificationOutcomeAsync(_task.Id, true, true, default));
        Assert.Null(_store.Get(_task.Id)!.ReviewStatusJson);
        Assert.False(_store.RequestOwnChangeRevert(_task.Id).Ok);
    }

    [Fact]
    public async Task Authorized_mode_reverts_without_asking_but_default_modes_never_do()
    {
        foreach (string mode in new[] { CodingRunPermissionModes.Bypass, CodingRunPermissionModes.Ask, CodingRunPermissionModes.Plan })
            Assert.False(CodingTaskChangeTracker.IsRevertAuthorizedByMode(mode));

        File.WriteAllText(W("a.cs"), "orig");
        AgentWrite("a.cs", "agent");
        CodingRevertDecision d = await _tracker.OnVerificationOutcomeAsync(_task.Id, false, autoRevert: true, default);
        Assert.Equal(CodingRevertDecision.Perform, d);
        Assert.Equal("orig", File.ReadAllText(W("a.cs")));
        Assert.True(Status().Revert!.AutoAuthorized);
        Assert.Contains(_sink.Logs, l => l.Contains("reverted the agent's own changes"));
    }

    [Fact]
    public async Task Revert_request_after_restart_fails_honestly_without_touching_files()
    {
        File.WriteAllText(W("a.cs"), "agent");
        _store.SetReviewStatusSection(_task.Id, "revert", JsonSerializer.SerializeToNode(
            new AgentOwnChangeRevertStateDto { Offered = true, OwnChangedFiles = 1 }, Web));
        Assert.True(_store.RequestOwnChangeRevert(_task.Id).Ok);
        await _tracker.HandleRevertRequestAsync(_task.Id, _store.Get(_task.Id)!.ReviewStatusJson, default);
        Assert.Equal("agent", File.ReadAllText(W("a.cs")));
        Assert.Contains(Status().Revert!.Items, i => i.Outcome == "no-recorded-changes");
    }

    [Fact]
    public async Task Findings_from_repair_context_are_reconciled_after_each_patch_update_and_published_with_state()
    {
        File.WriteAllText(W("Calc.cs"), "class C\n{\n    int D(int a,int b)\n    {\n        return a / b + 1;\n    }\n}\n");
        string context = "FAIL\n- high: Calc.cs:5 off by one in division\nnot a finding line";

        await _tracker.ReconcileAndPublishAsync(_task.Id, _work, context, default);
        AgentReviewFindingStatusDto f = Assert.Single(Status().Findings);
        Assert.Equal(("open", 5, "high"), (f.State, f.StartLine, f.Severity));

        // Worker updates the patch: code moves down two lines (context is not re-seeded).
        File.WriteAllText(W("Calc.cs"), "// a\n// b\nclass C\n{\n    int D(int a,int b)\n    {\n        return a / b + 1;\n    }\n}\n");
        await _tracker.ReconcileAndPublishAsync(_task.Id, _work, context, default);
        f = Assert.Single(Status().Findings);
        Assert.Equal(7, f.StartLine);
        Assert.Equal("open", f.State);

        // The fix lands: finding is kept as fixed with the reason, never removed.
        File.WriteAllText(W("Calc.cs"), "// a\n// b\nclass C\n{\n    int D(int a,int b) => a / b;\n}\n");
        await _tracker.ReconcileAndPublishAsync(_task.Id, _work, context, default);
        f = Assert.Single(Status().Findings);
        Assert.Equal("fixed", f.State);
        Assert.StartsWith("fixed or outdated", f.OutdatedReason);

        // Persisted with the task state: a fresh tracker (restart) restores the ledger and its audit trail.
        var restarted = new CodingTaskChangeTracker(Path.Combine(_root, "ledger"), _sink);
        CodingReviewFinding restored = Assert.Single(restarted.LedgerFor(_task.Id, _work).Findings);
        Assert.Equal(CodingFindingState.Fixed, restored.State);
        Assert.True(restored.Audit.Count >= 3);
    }

    [Fact]
    public void Findings_pointing_outside_the_workspace_or_missing_lines_are_ignored()
    {
        File.WriteAllText(W("a.cs"), "one\ntwo");
        int added = _tracker.SeedFindingsFromText(_task.Id, _work,
            "high: ../secret.cs:1 x\nhigh: a.cs:99 beyond end\nlow: missing.cs:1 nope\nmedium: a.cs:2 ok");
        Assert.Equal(1, added);
    }

    [Fact]
    public void Child_timeline_is_surfaced_in_the_task_status_document_and_capped()
    {
        for (int i = 0; i < TaskStore.MaxChildTimelineLines + 5; i++)
            Assert.True(_store.AppendChildTimeline(_task.Id, "step-wait", "step " + i, "waiting for c1"));
        AgentTaskReviewStatusDto status = Status();
        Assert.Equal(TaskStore.MaxChildTimelineLines, status.ChildTimeline.Count);
        Assert.Equal("step 104", status.ChildTimeline[^1].Subject);
        Assert.False(_store.AppendChildTimeline("missing", "k", "s", "d"));
        Assert.Throws<ArgumentException>(() => _store.SetReviewStatusSection(_task.Id, "bogus", null));
    }

    [Fact]
    public void Parent_continuation_policy_uses_declared_file_sets_and_keeps_workspace_fallback()
    {
        var children = new[]
        {
            new AgentParentChildSnapshot("c1", false, true, "w", new[] { "src/Billing/" })
        };
        AgentParentActionIntent Intent(string[] files, bool reads = false, bool end = false) =>
            new(IsReadOnly: false, ReadsChildOutput: reads, RequiresWorkspace: true, EndsParentTurn: end, WorkspaceRoot: "w", Files: files);

        Assert.Equal(AgentParentContinuationDecision.Run,
            AgentParentContinuationPolicy.Decide(Intent(new[] { "src/Ui/View.cs" }), children));
        Assert.Equal(AgentParentContinuationDecision.WaitForChildren,
            AgentParentContinuationPolicy.Decide(Intent(new[] { "SRC\\billing\\Invoice.cs" }), children));
        Assert.Equal(AgentParentContinuationDecision.WaitForChildren,
            AgentParentContinuationPolicy.Decide(Intent(new[] { "src/Ui/View.cs" }, reads: true), children));
        Assert.Equal(AgentParentContinuationDecision.WaitForChildren,
            AgentParentContinuationPolicy.Decide(Intent(new[] { "src/Ui/View.cs" }, end: true), children));
        // Child without declared files, or action without files: previous conservative behaviour.
        var unknown = new[] { new AgentParentChildSnapshot("c2", false, true, "w") };
        Assert.Equal(AgentParentContinuationDecision.WaitForChildren,
            AgentParentContinuationPolicy.Decide(Intent(new[] { "src/Ui/View.cs" }), unknown));
        Assert.Equal(AgentParentContinuationDecision.WaitForChildren,
            AgentParentContinuationPolicy.Decide(Intent(Array.Empty<string>()), children));
    }
}
