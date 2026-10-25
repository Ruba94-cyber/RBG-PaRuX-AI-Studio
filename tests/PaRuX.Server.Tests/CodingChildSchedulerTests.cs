using PaRuX.Server.Orchestration;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class CodingChildSchedulerTests
{
    private sealed class FakeClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 25, 10, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class FakeRunner : ICodingChildRunner
    {
        public List<string> Cancelled { get; } = new();
        public HashSet<string> Throwing { get; } = new();
        public Task CancelAsync(string childId, CancellationToken ct)
        {
            if (Throwing.Contains(childId)) throw new InvalidOperationException("boom");
            Cancelled.Add(childId);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public void Independent_step_runs_while_child_works_but_overlapping_step_waits_until_done()
    {
        var clock = new FakeClock();
        var s = new CodingChildScheduler(new FakeRunner(), clock);
        s.RegisterChild("c1", new[] { "src/Billing/", "docs/a.md" });

        Assert.True(s.DecideStep("s1", new[] { "src/Ui/View.cs" }, false).CanRun);
        var wait = s.DecideStep("s2", new[] { "src/Billing/Invoice.cs" }, false);
        Assert.False(wait.CanRun);
        Assert.Equal(new[] { "c1" }, wait.BlockingChildIds);
        Assert.False(s.DecideStep("s3", new[] { "SRC\\billing" }, false).CanRun); // case/sep-insensitive
        Assert.False(s.DecideStep("s4", Array.Empty<string>(), true).CanRun);     // reads child output

        clock.Advance(TimeSpan.FromSeconds(30));
        s.ReportChildState("c1", CodingChildState.Completed);
        Assert.True(s.DecideStep("s2-retry", new[] { "src/Billing/Invoice.cs" }, false).CanRun);
    }

    [Fact]
    public void Child_without_declared_files_blocks_every_file_step_but_not_fileless_steps()
    {
        var s = new CodingChildScheduler(new FakeRunner(), new FakeClock());
        s.RegisterChild("c1", null);
        Assert.False(s.DecideStep("w", new[] { "a.txt" }, false).CanRun);
        Assert.True(s.DecideStep("think", null, false).CanRun);
    }

    [Fact]
    public async Task Stop_cancels_all_live_children_even_when_one_cancel_fails_and_refuses_new_work()
    {
        var runner = new FakeRunner();
        runner.Throwing.Add("c2");
        var clock = new FakeClock();
        var s = new CodingChildScheduler(runner, clock);
        s.RegisterChild("c1", new[] { "a" });
        s.RegisterChild("c2", new[] { "b" });
        s.RegisterChild("c3", new[] { "c" });
        s.ReportChildState("c3", CodingChildState.Completed);

        clock.Advance(TimeSpan.FromMinutes(1));
        await s.StopAsync();

        Assert.Equal(new[] { "c1" }, runner.Cancelled);
        Assert.True(s.IsStopped);
        Assert.False(s.RegisterChild("c4", null));
        Assert.False(s.DecideStep("x", null, false).CanRun);
        Assert.Contains(s.Timeline, e => e.Kind == "child-cancel-failed" && e.Subject == "c2");
        Assert.Contains(s.Timeline, e => e.Kind == "child-cancelled" && e.Subject == "c1");
    }

    [Fact]
    public void Timeline_records_ordered_timestamped_child_and_step_events()
    {
        var clock = new FakeClock();
        var s = new CodingChildScheduler(new FakeRunner(), clock);
        s.RegisterChild("c1", new[] { "a.cs" });
        clock.Advance(TimeSpan.FromSeconds(5));
        s.DecideStep("s1", new[] { "a.cs" }, false);
        clock.Advance(TimeSpan.FromSeconds(5));
        s.ReportChildState("c1", CodingChildState.Failed);
        s.ReportChildState("c1", CodingChildState.Completed); // terminal: ignored

        var kinds = s.Timeline.Select(e => e.Kind).ToArray();
        Assert.Equal(new[] { "child-started", "step-wait", "child-failed" }, kinds);
        Assert.Equal(TimeSpan.FromSeconds(10), s.Timeline[2].AtUtc - s.Timeline[0].AtUtc);
    }
}
