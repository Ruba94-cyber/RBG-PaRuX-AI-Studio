using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Live parity run: a coding task's stage stayed at "Preparing warm coding environment" for its
/// whole run, so the real UI showed no movement while the agent worked.
/// </summary>
public sealed class CodingStepStageReporterTests
{
    private static readonly DateTime T0 = new(2026, 9, 29, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Step_lines_become_throttled_monotonic_stages_below_the_verification_band()
    {
        var reporter = new CodingStepStageReporter(TimeSpan.FromSeconds(2));

        Assert.True(reporter.TryBuild("Step 1: implementation plan updated.", T0, out string first, out int p1));
        Assert.Equal("Coding step 1: implementation plan updated.", first);
        Assert.False(reporter.TryBuild("Step 2: read_file x", T0.AddSeconds(1), out _, out _));
        Assert.True(reporter.TryBuild("Step 3: run_command y", T0.AddSeconds(3), out _, out int p3));
        Assert.False(reporter.TryBuild("Warm environment ready", T0.AddSeconds(10), out _, out _));

        Assert.True(p3 > p1);
        Assert.InRange(CodingStepStageReporter.PercentForStep(10_000), 6, 85);
    }

    [Fact]
    public void Late_token_callback_cannot_regress_visible_step_or_progress()
    {
        var reporter = new CodingStepStageReporter(TimeSpan.Zero);
        Assert.True(reporter.TryBuild("Step 17: wrote file", T0, out _, out int firstPercent));
        Assert.False(reporter.TryBuild("Step 16: delayed read output",
            T0.AddSeconds(5), out _, out _));
        Assert.True(reporter.TryBuild("Step 18: verify", T0.AddSeconds(6),
            out string stage, out int laterPercent));
        Assert.StartsWith("Coding step 18:", stage);
        Assert.True(laterPercent >= firstPercent);
    }

    [Fact]
    public void Long_or_multiline_text_is_bounded()
    {
        var reporter = new CodingStepStageReporter(TimeSpan.Zero);
        Assert.True(reporter.TryBuild("Step 4: " + new string('x', 500) + "\nsecond", T0, out string stage, out _));
        Assert.DoesNotContain('\n', stage);
        Assert.True(stage.Length < 170);
    }

    [Fact]
    public async Task Queued_updates_run_in_order_and_none_after_drain()
    {
        var reporter = new CodingStepStageReporter(TimeSpan.Zero);
        var order = new List<int>();
        var gate = new TaskCompletionSource();
        reporter.Enqueue(async () => { await gate.Task; lock (order) order.Add(1); });
        reporter.Enqueue(() => { lock (order) order.Add(2); return Task.CompletedTask; });
        Task drained = reporter.DrainAsync();
        reporter.Enqueue(() => { lock (order) order.Add(3); return Task.CompletedTask; });
        Assert.False(reporter.TryBuild("Step 9: late", T0, out _, out _));

        gate.SetResult();
        await drained;

        Assert.Equal(new[] { 1, 2 }, order);
    }
}
