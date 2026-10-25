using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// The coding agent now keeps durable, task-scoped memory in the Virtual Context ledger.
///
/// Before this, <c>ContextEngineService</c> built Virtual Context only for scope "conversation"
/// and <c>CodingAgentService</c> never touched it, so the agent ran on the raw physical window.
/// Measured on the physical PC (2026-09-21) across four real runs against an external .NET
/// repository: it re-read the same 154-line file until its repeat guard and exploration budget
/// stopped it, producing no edit - and a verified 4x larger physical window did not break the
/// loop, because recall was the binding constraint.
/// </summary>
public sealed class CodingTaskMemoryTests : IDisposable
{
    /// <summary>
    /// Mirrors <c>CodingTaskMemoryClient.Scope</c> in the desktop app. This project links
    /// individual app sources and does not link that client, which would pull in the WinUI
    /// server controller; the constant is asserted here because the scope token is the contract
    /// between the two sides.
    /// </summary>
    private const string CodingTaskScope = "coding-task";

    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "parux-task-memory-" + Guid.NewGuid().ToString("N"));

    public CodingTaskMemoryTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { }
    }

    private VirtualContextLedgerStore Store() => new(_directory);

    private static AppendVirtualContextLedgerRequest Entry(string scopeId, string kind, string text) => new()
    {
        Scope = CodingTaskScope,
        ScopeId = scopeId,
        Kind = kind,
        Text = text,
        Source = "coding-agent"
    };

    [Fact]
    public void A_coding_task_scope_is_accepted_and_replayed_as_prompt_text()
    {
        VirtualContextLedgerStore store = Store();
        const string taskId = "cfcc3ad7464343a8b7fedb517fb589f5";

        store.Append(Entry(taskId, VirtualContextLedgerKind.Decision,
            "Implementation plan: key the statistics dictionaries on IsExtended as well."));
        store.Append(Entry(taskId, VirtualContextLedgerKind.Constraint,
            "read_file src/RbgCanInsight.Analysis/TraceStatistics.cs - Compute keys on (Channel, Identifier)."));
        store.Append(Entry(taskId, VirtualContextLedgerKind.Note, "note kinds are stored but never projected"));

        VirtualContextLedgerStateResponse state = store.GetState(CodingTaskScope, taskId);

        Assert.Equal(3, state.Entries.Count);
        Assert.False(string.IsNullOrWhiteSpace(state.PromptText),
            "Durable memory that renders to nothing puts the agent back on the physical window alone.");
        Assert.Contains("IsExtended", state.PromptText, StringComparison.Ordinal);
        Assert.Contains("Compute keys on", state.PromptText, StringComparison.Ordinal);

        // The projection renders only contradictions, constraints and decisions. Recording the
        // agent's evidence as notes would store it and never show it to the model - memory that
        // changes nothing. This assertion is why CodingAgentService writes constraints.
        Assert.DoesNotContain("never projected", state.PromptText, StringComparison.Ordinal);
    }

    /// <summary>
    /// One scope per task. Memory bleeding between unrelated coding tasks would be worse than no
    /// memory at all - the agent would act on another repository's findings.
    /// </summary>
    [Fact]
    public void Memory_does_not_leak_between_tasks()
    {
        VirtualContextLedgerStore store = Store();
        store.Append(Entry("task-alpha", VirtualContextLedgerKind.Constraint, "alpha read TraceStatistics.cs"));
        store.Append(Entry("task-beta", VirtualContextLedgerKind.Constraint, "beta read DbcParser.cs"));

        VirtualContextLedgerStateResponse alpha = store.GetState(CodingTaskScope, "task-alpha");

        Assert.Single(alpha.Entries);
        Assert.DoesNotContain("DbcParser", alpha.PromptText, StringComparison.Ordinal);
    }

    /// <summary>A resumed run must pick its own history back up, not start blind.</summary>
    [Fact]
    public void A_resumed_task_still_sees_what_it_learned_before_the_interruption()
    {
        const string taskId = "resumed-task";
        Store().Append(Entry(taskId, VirtualContextLedgerKind.Constraint, "Compute merges standard and extended ids."));

        // A fresh store over the same directory is what a relaunched process sees.
        VirtualContextLedgerStateResponse state = Store().GetState(CodingTaskScope, taskId);

        Assert.Contains("extended ids", state.PromptText, StringComparison.Ordinal);
    }
}
