using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class CodingAgentPhaseTimerTests
{
    private sealed class FakeClock { public long Ticks; public long Now() => Ticks; }

    [Fact]
    public void Step_line_attributes_measured_phases_and_the_remainder_to_other()
    {
        var clock = new FakeClock();
        var timer = new CodingAgentPhaseTimer(clock.Now, 1000); // 1 tick = 1 ms
        timer.BeginStep(3);
        timer.Add(CodingAgentPhase.FirstToken, 120);
        timer.Add(CodingAgentPhase.Generate, 80);
        timer.Add(CodingAgentPhase.Read, 40);
        clock.Ticks = 300;
        string line = timer.EndStep();

        Assert.StartsWith("[phase-timing] step=3 total=300ms", line);
        Assert.True(CodingAgentPhaseTimer.TryParse(line, out var v));
        Assert.Equal(120, v["first_token"]);
        Assert.Equal(80, v["generate"]);
        Assert.Equal(40, v["read"]);
        Assert.Equal(60, v["other"]);
        Assert.Equal(300, v["total"]);
    }

    [Fact]
    public void Overlapping_scopes_never_produce_a_negative_other()
    {
        var clock = new FakeClock();
        var timer = new CodingAgentPhaseTimer(clock.Now, 1000);
        timer.BeginStep(1);
        timer.Add(CodingAgentPhase.Command, 500);
        clock.Ticks = 100;
        Assert.True(CodingAgentPhaseTimer.TryParse(timer.EndStep(), out var v));
        Assert.Equal(0, v["other"]);
    }

    [Fact]
    public async Task MeasureAsync_records_elapsed_time_even_when_work_throws()
    {
        var clock = new FakeClock();
        var timer = new CodingAgentPhaseTimer(clock.Now, 1000);
        timer.BeginStep(1);
        await Assert.ThrowsAsync<InvalidOperationException>(() => timer.MeasureAsync<int>(CodingAgentPhase.Command, () =>
        {
            clock.Ticks += 250;
            throw new InvalidOperationException("boom");
        }));
        clock.Ticks += 10;
        Assert.True(CodingAgentPhaseTimer.TryParse(timer.EndStep(), out var v));
        Assert.Equal(250, v["command"]);
    }

    [Fact]
    public void Run_total_sums_closed_steps_and_an_unclosed_step_is_closed_by_the_next_begin()
    {
        var clock = new FakeClock();
        var timer = new CodingAgentPhaseTimer(clock.Now, 1000);
        timer.BeginStep(1); timer.Add(CodingAgentPhase.Read, 10); clock.Ticks = 50;
        timer.BeginStep(2); timer.Add(CodingAgentPhase.Read, 15); clock.Ticks = 90;
        timer.EndStep();
        Assert.Equal(2, timer.Steps);
        string total = timer.RenderRunTotal();
        Assert.StartsWith("[phase-timing-total] steps=2 total=90ms", total);
        Assert.True(CodingAgentPhaseTimer.TryParse(total, out var v));
        Assert.Equal(25, v["read"]);
    }

    [Theory]
    [InlineData("read_file", CodingAgentPhase.Read)]
    [InlineData("batch_read", CodingAgentPhase.Read)]
    [InlineData("grep_search", CodingAgentPhase.Search)]
    [InlineData("git_diff", CodingAgentPhase.Git)]
    [InlineData("replace_range", CodingAgentPhase.Write)]
    [InlineData("run_command", CodingAgentPhase.Command)]
    [InlineData("delegate_tasks", CodingAgentPhase.Coordination)]
    public void Actions_map_to_phases(string action, CodingAgentPhase expected) =>
        Assert.Equal(expected, CodingAgentPhaseTimer.PhaseForAction(action));

    [Theory]
    [InlineData("")]
    [InlineData("Step 3: read_file")]
    [InlineData("[phase-timing] step=3 read=abc")]
    [InlineData("[phase-timing] step=3 read=5ms")] // no total
    public void Unrelated_or_malformed_lines_do_not_parse(string line) =>
        Assert.False(CodingAgentPhaseTimer.TryParse(line, out _));
}

public sealed class CodingAgentReadBatchTests
{
    private static CodingBatchReadItem Read(string path) => new() { Action = "read_file", Path = path };

    [Fact]
    public void Validation_accepts_bounded_read_only_batches()
    {
        Assert.True(CodingAgentReadBatch.TryValidate(new[] { Read("a.cs"), new CodingBatchReadItem { Action = "grep_search", Pattern = "x" } }, out _));
    }

    [Theory]
    [InlineData("write_file")]
    [InlineData("run_command")]
    [InlineData("delete_file")]
    [InlineData("git_diff")]
    [InlineData("delegate_task")]
    public void Validation_rejects_anything_that_is_not_a_pure_read(string action)
    {
        var items = new[] { Read("a.cs"), new CodingBatchReadItem { Action = action, Path = "b.cs", Pattern = "p" } };
        Assert.False(CodingAgentReadBatch.TryValidate(items, out string error));
        Assert.Contains("only contain", error);
    }

    [Fact]
    public void Validation_rejects_wrong_sizes_duplicates_and_missing_fields()
    {
        Assert.False(CodingAgentReadBatch.TryValidate(null, out _));
        Assert.False(CodingAgentReadBatch.TryValidate(new[] { Read("a") }, out _));
        Assert.False(CodingAgentReadBatch.TryValidate(Enumerable.Range(0, 7).Select(i => Read("f" + i)).ToList(), out _));
        Assert.False(CodingAgentReadBatch.TryValidate(new[] { Read("a"), Read("a") }, out string dup));
        Assert.Contains("duplicate", dup);
        Assert.False(CodingAgentReadBatch.TryValidate(new[] { Read("a"), new CodingBatchReadItem { Action = "read_file" } }, out _));
        Assert.False(CodingAgentReadBatch.TryValidate(new[] { Read("a"), new CodingBatchReadItem { Action = "read_file_lines", Path = "b" } }, out _));
        Assert.False(CodingAgentReadBatch.TryValidate(new[] { Read("a"), new CodingBatchReadItem { Action = "glob_search" } }, out _));
    }

    [Fact]
    public async Task Execution_preserves_order_is_bounded_and_actually_overlaps()
    {
        var items = Enumerable.Range(0, 6).Select(i => Read("f" + i)).ToList();
        int active = 0, peak = 0;
        var results = await CodingAgentReadBatch.ExecuteAsync(items, async (item, ct) =>
        {
            int now = Interlocked.Increment(ref active);
            int seen; while ((seen = Volatile.Read(ref peak)) < now && Interlocked.CompareExchange(ref peak, now, seen) != seen) { }
            // Earlier items finish later so order preservation is not accidental.
            await Task.Delay(item.Path == "f0" ? 120 : 30, ct);
            Interlocked.Decrement(ref active);
            return new CodingBatchReadResult(item.Path, "body-" + item.Path);
        }, CancellationToken.None);

        Assert.Equal(items.Select(i => i.Path), results.Select(r => r.Description));
        Assert.InRange(peak, 2, CodingAgentReadBatch.MaxConcurrency);
    }

    [Fact]
    public async Task One_failing_item_does_not_hide_the_others()
    {
        var items = new[] { Read("ok"), Read("bad"), Read("ok2") };
        var results = await CodingAgentReadBatch.ExecuteAsync(items, (item, ct) =>
            item.Path == "bad" ? throw new IOException("locked") : Task.FromResult(new CodingBatchReadResult(item.Path, "fine")),
            CancellationToken.None);
        Assert.Equal("fine", results[0].Observation);
        Assert.Contains("(failed)", results[1].Description);
        Assert.Contains("locked", results[1].Observation);
        Assert.Equal("fine", results[2].Observation);
    }

    [Fact]
    public async Task Cancellation_propagates_instead_of_becoming_a_result()
    {
        using var cts = new CancellationTokenSource();
        var items = new[] { Read("a"), Read("b") };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CodingAgentReadBatch.ExecuteAsync(items, async (item, ct) =>
        {
            cts.Cancel();
            await Task.Delay(5000, ct);
            return new CodingBatchReadResult(item.Path, "x");
        }, cts.Token));
    }

    [Fact]
    public void Render_keeps_a_trailing_FULL_marker_for_every_item_at_six_items()
    {
        var results = Enumerable.Range(1, 6).Select(i => new CodingBatchReadResult(
            "read_file \"f" + i + "\"",
            new string('x', 4000) + "\n[FULL EVIDENCE artifactId=art" + i + "; chars=90000; sha256=abc. Use read_artifact.]")).ToList();
        string text = CodingAgentReadBatch.Render(results);
        for (int i = 1; i <= 6; i++)
            Assert.Contains("[FULL EVIDENCE artifactId=art" + i + ";", text);
        Assert.Contains("Use read_artifact.]", text);
    }

    [Fact]
    public async Task Synchronous_blocking_items_really_run_in_parallel()
    {
        var items = Enumerable.Range(0, 4).Select(i => Read("f" + i)).ToList();
        int active = 0, peak = 0;
        await CodingAgentReadBatch.ExecuteAsync(items, (item, ct) =>
        {
            int now = Interlocked.Increment(ref active);
            int seen; while ((seen = Volatile.Read(ref peak)) < now && Interlocked.CompareExchange(ref peak, now, seen) != seen) { }
            Thread.Sleep(200); // synchronous, like grep/glob/list/range reads
            Interlocked.Decrement(ref active);
            return Task.FromResult(new CodingBatchReadResult(item.Path, "ok"));
        }, CancellationToken.None);
        Assert.True(peak >= 2, "peak concurrency " + peak);
    }

    [Fact]
    public void Render_bounds_total_output_and_labels_each_item()
    {
        var results = Enumerable.Range(1, 3).Select(i => new CodingBatchReadResult("read_file \"f" + i + "\"", new string('x', 50_000))).ToList();
        string text = CodingAgentReadBatch.Render(results, 3_000);
        Assert.True(text.Length < 4_000);
        Assert.Contains("[1/3]", text);
        Assert.Contains("[3/3]", text);
        Assert.Contains("truncated", text);
    }
}
