using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class CodingReviewReconciliationTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 25, 10, 0, 0, TimeSpan.Zero);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parux-bic09-" + Guid.NewGuid().ToString("N"));

    public CodingReviewReconciliationTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private const string Original = "using System;\nclass A\n{\n    int Div(int a, int b)\n    {\n        return a / b + 1;\n    }\n}\n";

    private static IReadOnlyDictionary<string, string?> Files(string text) =>
        new Dictionary<string, string?> { ["src/A.cs"] = text };

    private static (CodingReviewFindingLedger Ledger, CodingReviewFinding Finding) Seed()
    {
        var ledger = new CodingReviewFindingLedger { TaskId = "t1" };
        CodingReviewFinding f = ledger.Add("src\\A.cs", 6, 6, "high", "Off by one", Original, T0);
        return (ledger, f);
    }

    [Fact]
    public void Unchanged_hunk_is_kept_at_same_line()
    {
        var (ledger, f) = Seed();
        var r = ledger.Reconcile(Files(Original), T0.AddMinutes(1));
        Assert.Equal(CodingFindingAnchorOutcome.Kept, r.Single().Outcome);
        Assert.Equal(6, f.StartLine);
        Assert.Equal(CodingFindingState.Open, f.State);
    }

    [Fact]
    public void Moved_hunk_is_relocated_with_audit_and_state_preserved()
    {
        var (ledger, f) = Seed();
        ledger.SetState(f.Id, CodingFindingState.Accepted, T0);
        string moved = "// header\n// header2\n" + Original;
        var r = ledger.Reconcile(Files(moved), T0.AddMinutes(1));
        Assert.Equal(CodingFindingAnchorOutcome.Relocated, r.Single().Outcome);
        Assert.Equal(8, f.StartLine);
        Assert.Equal(CodingFindingState.Accepted, f.State);
        Assert.Contains(f.Audit, a => a.Event == "relocated" && a.Detail.Contains("6-6 -> 8-8"));
    }

    [Fact]
    public void Slightly_edited_hunk_relocates_fuzzily()
    {
        var ledger = new CodingReviewFindingLedger();
        string before = "a1\na2\nvar x = Compute(alpha, beta);\nvar y = x * 2;\nreturn y;\nz1\n";
        CodingReviewFinding f = ledger.Add("src/A.cs", 3, 5, "medium", "Check", before, T0);
        string after = "new1\nnew2\na1\na2\nvar x = Compute(alpha, beta);\nvar y = x * 3;\nreturn y;\nz1\n";
        var r = ledger.Reconcile(Files(after), T0.AddMinutes(1));
        Assert.Equal(CodingFindingAnchorOutcome.RelocatedFuzzy, r.Single().Outcome);
        Assert.Equal(5, f.StartLine);
        Assert.Equal(CodingFindingState.Open, f.State);
    }

    [Fact]
    public void Removed_hunk_is_marked_fixed_or_outdated_and_never_dropped()
    {
        var (ledger, f) = Seed();
        string fixedText = Original.Replace("return a / b + 1;", "return a / b;").Replace("int Div", "double Quotient");
        var r = ledger.Reconcile(Files(fixedText + "// unrelated\n"), T0.AddMinutes(1));
        Assert.Equal(CodingFindingAnchorOutcome.FixedOrOutdated, r.Single().Outcome);
        Assert.Single(ledger.Findings);
        Assert.Equal(CodingFindingState.Fixed, f.State);
        Assert.StartsWith("fixed or outdated", f.OutdatedReason);
        Assert.Contains(f.Audit, a => a.Event == "fixed-or-outdated");
        Assert.Equal(6, f.StartLine); // last known location retained
    }

    [Fact]
    public void Deleted_file_marks_fixed_and_second_reconcile_is_stable()
    {
        var (ledger, f) = Seed();
        ledger.Reconcile(new Dictionary<string, string?>(), T0.AddMinutes(1));
        Assert.Equal(CodingFindingState.Fixed, f.State);
        int audit = f.Audit.Count;
        var r = ledger.Reconcile(new Dictionary<string, string?>(), T0.AddMinutes(2));
        Assert.Equal(CodingFindingAnchorOutcome.AlreadyFixed, r.Single().Outcome);
        Assert.Equal(audit, f.Audit.Count);
    }

    [Fact]
    public void Duplicate_text_picks_the_copy_with_matching_neighbours()
    {
        var ledger = new CodingReviewFindingLedger();
        string before = "x\nTARGET();\ny\n";
        CodingReviewFinding f = ledger.Add("src/A.cs", 2, 2, "low", "m", before, T0);
        string after = "p\nq\nTARGET();\nr\nx\nTARGET();\ny\n";
        ledger.Reconcile(Files(after), T0);
        Assert.Equal(6, f.StartLine);
    }

    [Fact]
    public void Ledger_round_trips_through_disk_with_states_and_audit()
    {
        var (ledger, f) = Seed();
        ledger.SetState(f.Id, CodingFindingState.Rejected, T0);
        string path = Path.Combine(_root, "task", "findings.json");
        ledger.Save(path);
        var loaded = CodingReviewFindingLedger.Load(path);
        Assert.Equal("t1", loaded.TaskId);
        CodingReviewFinding g = Assert.Single(loaded.Findings);
        Assert.Equal(CodingFindingState.Rejected, g.State);
        Assert.Equal(f.Audit.Count, g.Audit.Count);
        var r = loaded.Reconcile(Files("// moved\n" + Original), T0);
        Assert.Equal(CodingFindingAnchorOutcome.Relocated, r.Single().Outcome);
        File.WriteAllText(path, "{corrupt");
        Assert.Empty(CodingReviewFindingLedger.Load(path).Findings);
    }

    [Fact]
    public void Add_is_idempotent_and_rejects_out_of_range()
    {
        var (ledger, f) = Seed();
        Assert.Same(f, ledger.Add("src/A.cs", 6, 6, "high", "Off by one", Original, T0));
        Assert.Throws<ArgumentOutOfRangeException>(() => ledger.Add("src/A.cs", 99, 99, "low", "m", Original, T0));
    }
}
