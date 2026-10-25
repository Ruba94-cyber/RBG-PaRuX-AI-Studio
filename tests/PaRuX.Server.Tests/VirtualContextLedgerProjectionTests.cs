using PaRuX.Contracts;
using System.Diagnostics;
using Xunit;
using Xunit.Abstractions;

namespace PaRuX.Server.Tests;

public sealed class VirtualContextLedgerProjectionTests
{
    private readonly ITestOutputHelper _output;

    public VirtualContextLedgerProjectionTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void Late_supersession_after_more_than_ten_thousand_events_updates_state_and_prompt()
    {
        using TempDirectory temp = new();
        var store = new VirtualContextLedgerStore(temp.Path);
        using Process process = Process.GetCurrentProcess();
        VirtualContextLedgerEntryDto original = store.Append(Request(
            VirtualContextLedgerKind.Constraint,
            "runtime backend must be CPU",
            scopeId: "long-history"));
        store.Append(Request(
            VirtualContextLedgerKind.Constraint,
            "foreign scope must never enter this projection",
            scopeId: "other-history"));

        long allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        long privateBytesBefore = GetPrivateBytes(process);
        long peakPrivateBytes = privateBytesBefore;
        var appendTimer = Stopwatch.StartNew();
        for (int i = 0; i < 10_050; i++)
        {
            store.Append(Request(VirtualContextLedgerKind.Note, "audit note " + i, scopeId: "long-history"));
            if (i % 1_000 == 999)
                peakPrivateBytes = Math.Max(peakPrivateBytes, GetPrivateBytes(process));
        }
        appendTimer.Stop();

        var correctionTimer = Stopwatch.StartNew();
        VirtualContextLedgerEntryDto replacement = store.Append(Request(
            VirtualContextLedgerKind.Constraint,
            "runtime backend must be GPU",
            scopeId: "long-history",
            supersedes: new[] { original.Id }));
        correctionTimer.Stop();

        var queryTimer = Stopwatch.StartNew();
        VirtualContextLedgerStateResponse state = store.GetState("conversation", "long-history", maxEntries: 1_000);
        queryTimer.Stop();
        peakPrivateBytes = Math.Max(peakPrivateBytes, GetPrivateBytes(process));
        long allocatedBytes = Math.Max(0, GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore);
        long privateBytesDelta = Math.Max(0, peakPrivateBytes - privateBytesBefore);

        Assert.Equal(1_000, state.Entries.Count);
        Assert.Equal(replacement.Id, state.Entries[^1].Id);
        Assert.DoesNotContain(state.ActiveConstraints, entry => entry.Id == original.Id);
        Assert.Contains(state.ActiveConstraints, entry => entry.Id == replacement.Id);
        Assert.DoesNotContain(state.ActiveConstraints, entry => entry.ScopeId == "other-history");
        Assert.DoesNotContain("runtime backend must be CPU", state.PromptText, StringComparison.Ordinal);
        Assert.Contains("runtime backend must be GPU", state.PromptText, StringComparison.Ordinal);
        Assert.DoesNotContain("foreign scope", state.PromptText, StringComparison.Ordinal);

        _output.WriteLine(
            "context-ledger-scale scope_events={0} append_ms={1:F1} correction_ms={2:F1} query_ms={3:F1} allocated_bytes={4} observed_process_private_delta_bytes={5} active_constraints={6} prompt_chars={7}",
            10_052,
            appendTimer.Elapsed.TotalMilliseconds,
            correctionTimer.Elapsed.TotalMilliseconds,
            queryTimer.Elapsed.TotalMilliseconds,
            allocatedBytes,
            privateBytesDelta,
            state.ActiveConstraints.Count,
            state.PromptText.Length);
    }

    [Fact]
    public void State_query_keeps_ten_thousand_live_constraints_within_an_explicit_allocation_budget()
    {
        const int liveConstraintCount = 10_000;
        const long maxQueryAllocationBytes = 128L * 1024 * 1024;
        using TempDirectory temp = new();
        var store = new VirtualContextLedgerStore(temp.Path);
        using Process process = Process.GetCurrentProcess();

        string lastConstraintId = string.Empty;
        var appendTimer = Stopwatch.StartNew();
        for (int i = 0; i < liveConstraintCount; i++)
        {
            VirtualContextLedgerEntryDto entry = store.Append(Request(
                VirtualContextLedgerKind.Constraint,
                "active constraint " + i.ToString("D5"),
                scopeId: "large-active-state"));
            lastConstraintId = entry.Id;
        }
        appendTimer.Stop();

        long allocatedBeforeQuery = GC.GetTotalAllocatedBytes(precise: true);
        long privateBytesBeforeQuery = GetPrivateBytes(process);
        var queryTimer = Stopwatch.StartNew();
        VirtualContextLedgerStateResponse state = store.GetState("conversation", "large-active-state", maxEntries: 100);
        queryTimer.Stop();
        long queryAllocatedBytes = Math.Max(0, GC.GetTotalAllocatedBytes(precise: true) - allocatedBeforeQuery);
        long observedPrivateBytesDelta = Math.Max(0, GetPrivateBytes(process) - privateBytesBeforeQuery);

        Assert.Equal(100, state.Entries.Count);
        Assert.Equal(liveConstraintCount, state.ActiveConstraints.Count);
        Assert.Contains(state.ActiveConstraints, entry => entry.Id == lastConstraintId);
        Assert.True(state.PromptText.Length <= 16_000);
        Assert.InRange(queryAllocatedBytes, 0, maxQueryAllocationBytes);

        _output.WriteLine(
            "context-ledger-live-state constraints={0} append_ms={1:F1} query_ms={2:F1} query_allocated_bytes={3} observed_query_private_delta_bytes={4} prompt_chars={5} allocation_budget_bytes={6}",
            liveConstraintCount,
            appendTimer.Elapsed.TotalMilliseconds,
            queryTimer.Elapsed.TotalMilliseconds,
            queryAllocatedBytes,
            observedPrivateBytesDelta,
            state.PromptText.Length,
            maxQueryAllocationBytes);
    }

    private static long GetPrivateBytes(Process process)
    {
        process.Refresh();
        return Math.Max(0, process.PrivateMemorySize64);
    }

    [Fact]
    public void Repeated_supersessions_contradictions_and_tombstones_survive_restart_without_losing_audit_history()
    {
        using TempDirectory temp = new();
        var store = new VirtualContextLedgerStore(temp.Path);
        VirtualContextLedgerEntryDto first = store.Append(Request(
            VirtualContextLedgerKind.Decision, "use model A", scopeId: "project-a"));
        VirtualContextLedgerEntryDto second = store.Append(Request(
            VirtualContextLedgerKind.Decision, "use model B", scopeId: "project-a", supersedes: new[] { first.Id }));
        VirtualContextLedgerEntryDto third = store.Append(Request(
            VirtualContextLedgerKind.Decision, "use model C", scopeId: "project-a", supersedes: new[] { second.Id }));
        VirtualContextLedgerEntryDto contradiction = store.Append(Request(
            VirtualContextLedgerKind.Contradiction,
            "hardware inventory says model C cannot fit",
            scopeId: "project-a",
            contradicts: new[] { third.Id }));

        VirtualContextLedgerStateResponse conflicted = store.GetState("conversation", "project-a");
        Assert.Single(conflicted.ActiveDecisions);
        Assert.Equal(third.Id, conflicted.ActiveDecisions[0].Id);
        Assert.Single(conflicted.UnresolvedContradictions);
        Assert.Equal(contradiction.Id, conflicted.UnresolvedContradictions[0].Id);

        store.Append(Request(
            VirtualContextLedgerKind.Tombstone,
            "remove obsolete decision and resolved contradiction",
            scopeId: "project-a",
            supersedes: new[] { third.Id, contradiction.Id }));

        var restarted = new VirtualContextLedgerStore(temp.Path);
        VirtualContextLedgerStateResponse state = restarted.GetState("conversation", "project-a");

        Assert.Empty(state.ActiveDecisions);
        Assert.Empty(state.UnresolvedContradictions);
        Assert.NotNull(restarted.Get(first.Id));
        Assert.NotNull(restarted.Get(second.Id));
        Assert.NotNull(restarted.Get(third.Id));
        Assert.NotNull(restarted.Get(contradiction.Id));
        Assert.DoesNotContain("use model C", restarted.BuildPromptText("conversation", "project-a"), StringComparison.Ordinal);
    }

    [Fact]
    public void Equal_timestamps_use_append_order_deterministically_across_restart()
    {
        using TempDirectory temp = new();
        DateTimeOffset fixedUtc = new(2026, 9, 10, 8, 0, 0, TimeSpan.Zero);
        var store = new VirtualContextLedgerStore(temp.Path, new FrozenTimeProvider(fixedUtc));
        VirtualContextLedgerEntryDto first = store.Append(Request(
            VirtualContextLedgerKind.Constraint, "first", scopeId: "same-time"));
        VirtualContextLedgerEntryDto second = store.Append(Request(
            VirtualContextLedgerKind.Constraint, "second", scopeId: "same-time", supersedes: new[] { first.Id }));

        Assert.Equal(first.CreatedUtc, second.CreatedUtc);
        VirtualContextLedgerStateResponse beforeRestart = store.GetState("conversation", "same-time", maxEntries: 10);
        Assert.Equal(new[] { first.Id, second.Id }, beforeRestart.Entries.Select(x => x.Id));
        Assert.Single(beforeRestart.ActiveConstraints);
        Assert.Equal(second.Id, beforeRestart.ActiveConstraints[0].Id);

        var restarted = new VirtualContextLedgerStore(temp.Path);
        VirtualContextLedgerStateResponse afterRestart = restarted.GetState("conversation", "same-time", maxEntries: 10);
        Assert.Equal(new[] { first.Id, second.Id }, afterRestart.Entries.Select(x => x.Id));
        Assert.Single(afterRestart.ActiveConstraints);
        Assert.Equal(second.Id, afterRestart.ActiveConstraints[0].Id);
    }

    [Fact]
    public void Relations_cannot_cross_scope_boundaries_and_tombstones_require_targets()
    {
        using TempDirectory temp = new();
        var store = new VirtualContextLedgerStore(temp.Path);
        VirtualContextLedgerEntryDto otherScope = store.Append(Request(
            VirtualContextLedgerKind.Constraint, "scope A fact", scopeId: "scope-a"));

        Assert.Throws<InvalidDataException>(() => store.Append(Request(
            VirtualContextLedgerKind.Tombstone,
            "invalid cross-scope delete",
            scopeId: "scope-b",
            supersedes: new[] { otherScope.Id })));
        Assert.Throws<InvalidDataException>(() => store.Append(Request(
            VirtualContextLedgerKind.Tombstone,
            "targetless delete",
            scopeId: "scope-a")));
    }

    [Fact]
    public void Prompt_projection_is_explicitly_bounded_and_keeps_newest_facts_first()
    {
        using TempDirectory temp = new();
        var store = new VirtualContextLedgerStore(temp.Path);
        for (int i = 0; i < 80; i++)
        {
            store.Append(Request(
                VirtualContextLedgerKind.Constraint,
                "constraint-" + i.ToString("D2") + " " + new string('x', 180),
                scopeId: "prompt-budget"));
        }

        string prompt = store.BuildPromptText("conversation", "prompt-budget", maxCharacters: 1_000);

        Assert.True(prompt.Length <= 1_000, "Prompt must honor the caller's hard character budget.");
        Assert.Contains("constraint-79", prompt, StringComparison.Ordinal);
        Assert.Contains("bounded", prompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Store_recreates_schema_when_database_file_is_removed_after_initialization()
    {
        using TempDirectory temp = new();
        var store = new VirtualContextLedgerStore(temp.Path);
        store.Append(Request(VirtualContextLedgerKind.Note, "before removal", scopeId: "recreate"));

        // Backup restore, data-root swaps and cleanup can remove the file under a long-lived
        // singleton; the per-process initialized flag must not outlive the file.
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (string suffix in new[] { "", "-wal", "-shm" })
            File.Delete(System.IO.Path.Combine(temp.Path, "virtual-context-v2.db" + suffix));

        VirtualContextLedgerStateResponse state = store.GetState("conversation", "recreate");
        Assert.Empty(state.Entries);
        store.Append(Request(VirtualContextLedgerKind.Note, "after removal", scopeId: "recreate"));
        Assert.Single(store.GetState("conversation", "recreate").Entries);
    }

    private static AppendVirtualContextLedgerRequest Request(
        string kind,
        string text,
        string scopeId,
        IEnumerable<string>? supersedes = null,
        IEnumerable<string>? contradicts = null) => new()
    {
        Scope = "conversation",
        ScopeId = scopeId,
        Kind = kind,
        Text = text,
        Source = "test",
        SupersedesIds = (supersedes ?? Array.Empty<string>()).ToList(),
        ContradictsIds = (contradicts ?? Array.Empty<string>()).ToList()
    };

    private sealed class FrozenTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _value;
        public FrozenTimeProvider(DateTimeOffset value) => _value = value;
        public override DateTimeOffset GetUtcNow() => _value;
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "parux-vc-projection-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch { }
        }
    }
}
