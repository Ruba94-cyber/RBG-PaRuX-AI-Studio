using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class CodingOwnChangeReverterTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parux-bic09r-" + Guid.NewGuid().ToString("N"));

    public CodingOwnChangeReverterTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private string P(string rel) => Path.Combine(_root, rel);

    private static void AgentWrite(CodingOwnChangeReverter r, string full, string rel, string content)
    {
        r.BeforeWrite(rel);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        r.AfterWrite(rel);
    }

    [Fact]
    public void Decision_matrix()
    {
        Assert.Equal(CodingRevertDecision.NotNeeded, CodingOwnChangeReverter.DecideAfterVerification(true, true, true));
        Assert.Equal(CodingRevertDecision.NotNeeded, CodingOwnChangeReverter.DecideAfterVerification(false, false, true));
        Assert.Equal(CodingRevertDecision.Offer, CodingOwnChangeReverter.DecideAfterVerification(false, true, false));
        Assert.Equal(CodingRevertDecision.Perform, CodingOwnChangeReverter.DecideAfterVerification(false, true, true));
    }

    [Fact]
    public void Revert_restores_modified_and_deletes_created_agent_files_only()
    {
        File.WriteAllText(P("a.txt"), "orig-a");
        File.WriteAllText(P("user.txt"), "user-work");
        var r = new CodingOwnChangeReverter(_root);
        AgentWrite(r, P("a.txt"), "a.txt", "agent-a");
        AgentWrite(r, P("sub/new.txt"), "sub/new.txt", "agent-new");
        AgentWrite(r, P("a.txt"), "a.txt", "agent-a-2"); // second write keeps original snapshot

        var items = r.Revert();

        Assert.Equal("orig-a", File.ReadAllText(P("a.txt")));
        Assert.False(File.Exists(P("sub/new.txt")));
        Assert.Equal("user-work", File.ReadAllText(P("user.txt")));
        Assert.Contains(items, i => i.Path == "a.txt" && i.Outcome == CodingRevertOutcome.Restored);
        Assert.Contains(items, i => i.Path == "sub/new.txt" && i.Outcome == CodingRevertOutcome.Deleted);
        Assert.Equal(0, r.TrackedCount);
    }

    [Fact]
    public void File_edited_by_user_after_agent_write_is_not_touched()
    {
        File.WriteAllText(P("a.txt"), "orig");
        var r = new CodingOwnChangeReverter(_root);
        AgentWrite(r, P("a.txt"), "a.txt", "agent");
        File.WriteAllText(P("a.txt"), "agent + user edit");

        var items = r.Revert();

        Assert.Equal("agent + user edit", File.ReadAllText(P("a.txt")));
        Assert.Equal(CodingRevertOutcome.SkippedModifiedByUser, items.Single().Outcome);
    }

    [Fact]
    public void Agent_created_file_deleted_then_recreated_by_user_is_kept()
    {
        var r = new CodingOwnChangeReverter(_root);
        AgentWrite(r, P("n.txt"), "n.txt", "agent");
        File.WriteAllText(P("n.txt"), "user rewrote");
        Assert.Equal(CodingRevertOutcome.SkippedModifiedByUser, r.Revert().Single().Outcome);
        Assert.Equal("user rewrote", File.ReadAllText(P("n.txt")));
    }

    [Fact]
    public void Untracked_files_are_never_touched_and_unwritten_snapshots_are_skipped()
    {
        File.WriteAllText(P("keep.txt"), "k");
        File.WriteAllText(P("pending.txt"), "p");
        var r = new CodingOwnChangeReverter(_root);
        r.BeforeWrite("pending.txt"); // write never completed (no AfterWrite)
        var items = r.Revert();
        Assert.Equal(CodingRevertOutcome.SkippedNotWritten, items.Single().Outcome);
        Assert.Equal("k", File.ReadAllText(P("keep.txt")));
        Assert.Equal("p", File.ReadAllText(P("pending.txt")));
    }

    [Fact]
    public void Paths_outside_workspace_or_in_git_are_refused()
    {
        var r = new CodingOwnChangeReverter(_root);
        Assert.Throws<InvalidOperationException>(() => r.BeforeWrite("../escape.txt"));
        Assert.Throws<InvalidOperationException>(() => r.BeforeWrite(".git/config"));
        Assert.Throws<InvalidOperationException>(() => r.BeforeWrite(Path.Combine(Path.GetTempPath(), "abs.txt")));
        Assert.Equal(0, r.TrackedCount);
    }
}
