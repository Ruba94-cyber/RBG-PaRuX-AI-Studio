using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class BoundedStatusPollerTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(100);

    private static StatusPollPolicy Policy(int maxPolls = 50, int maxFailures = 3, int backoffCapMs = 1000) =>
        new(Interval, maxPolls, maxFailures, TimeSpan.FromMilliseconds(backoffCapMs));

    [Fact]
    public async Task Run_that_reaches_a_final_state_reports_every_reading_and_stops_there()
    {
        string[] states = { "queued", "running", "completed" };
        int next = 0;
        var seen = new List<string>();

        StatusPollResult<string> result = await BoundedStatusPoller.RunAsync<string>(
            _ => Task.FromResult<string?>(states[next++]),
            state => state == "completed",
            Policy(),
            onStatus: seen.Add,
            onFailure: null,
            delay: (_, _) => Task.CompletedTask,
            CancellationToken.None);

        Assert.Equal(StatusPollEnd.Terminal, result.End);
        Assert.Equal("completed", result.Last);
        Assert.Equal(new[] { "queued", "running", "completed" }, seen);
    }

    [Fact]
    public async Task Run_that_never_finishes_stops_at_the_read_limit_instead_of_polling_forever()
    {
        int reads = 0;

        StatusPollResult<string> result = await BoundedStatusPoller.RunAsync<string>(
            _ =>
            {
                reads++;
                return Task.FromResult<string?>("running");
            },
            state => state == "completed",
            Policy(maxPolls: 7),
            onStatus: null,
            onFailure: null,
            delay: (_, _) => Task.CompletedTask,
            CancellationToken.None);

        Assert.Equal(StatusPollEnd.TimedOut, result.End);
        Assert.Equal(7, reads);
        Assert.Equal("running", result.Last);
    }

    [Fact]
    public async Task Transient_failure_is_retried_and_the_run_still_completes()
    {
        var failures = new List<int>();
        int reads = 0;

        StatusPollResult<string> result = await BoundedStatusPoller.RunAsync<string>(
            _ =>
            {
                reads++;
                if (reads == 2) throw new HttpRequestException("connection reset");
                return Task.FromResult<string?>(reads >= 4 ? "completed" : "running");
            },
            state => state == "completed",
            Policy(maxFailures: 3),
            onStatus: null,
            onFailure: (_, count) => failures.Add(count),
            delay: (_, _) => Task.CompletedTask,
            CancellationToken.None);

        Assert.Equal(StatusPollEnd.Terminal, result.End);
        Assert.Equal(new[] { 1 }, failures);
    }

    [Fact]
    public async Task Failures_separated_by_good_reads_never_add_up_to_a_give_up()
    {
        int reads = 0;

        StatusPollResult<string> result = await BoundedStatusPoller.RunAsync<string>(
            _ =>
            {
                reads++;
                if (reads % 2 == 1) throw new HttpRequestException("blip #" + reads);
                return Task.FromResult<string?>(reads >= 10 ? "completed" : "running");
            },
            state => state == "completed",
            Policy(maxPolls: 20, maxFailures: 2),
            onStatus: null,
            onFailure: null,
            delay: (_, _) => Task.CompletedTask,
            CancellationToken.None);

        Assert.Equal(StatusPollEnd.Terminal, result.End);
        Assert.Equal(10, reads);
        Assert.Equal("completed", result.Last);
    }

    [Fact]
    public async Task Timeout_after_a_recovered_failure_reports_no_error()
    {
        int reads = 0;

        StatusPollResult<string> result = await BoundedStatusPoller.RunAsync<string>(
            _ =>
            {
                reads++;
                if (reads == 1) throw new HttpRequestException("connection reset");
                return Task.FromResult<string?>("running");
            },
            state => state == "completed",
            Policy(maxPolls: 4),
            onStatus: null,
            onFailure: null,
            delay: (_, _) => Task.CompletedTask,
            CancellationToken.None);

        Assert.Equal(StatusPollEnd.TimedOut, result.End);
        Assert.Equal(4, reads);
        Assert.Equal("running", result.Last);
        Assert.Null(result.LastError);
    }

    [Fact]
    public async Task Consecutive_failures_past_the_limit_give_up_and_keep_the_last_error()
    {
        int reads = 0;

        StatusPollResult<string> result = await BoundedStatusPoller.RunAsync<string>(
            _ =>
            {
                reads++;
                throw new HttpRequestException("core unreachable #" + reads);
            },
            state => state == "completed",
            Policy(maxFailures: 3),
            onStatus: null,
            onFailure: null,
            delay: (_, _) => Task.CompletedTask,
            CancellationToken.None);

        Assert.Equal(StatusPollEnd.GaveUp, result.End);
        Assert.Equal(3, reads);
        Assert.Equal("core unreachable #3", result.LastError);
    }

    [Fact]
    public async Task Waits_after_failures_double_up_to_the_cap_and_do_not_wait_after_the_last_one()
    {
        var waits = new List<TimeSpan>();

        StatusPollResult<string> result = await BoundedStatusPoller.RunAsync<string>(
            _ => throw new HttpRequestException("down"),
            _ => false,
            Policy(maxFailures: 5, backoffCapMs: 350),
            onStatus: null,
            onFailure: null,
            delay: (duration, _) =>
            {
                waits.Add(duration);
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.Equal(StatusPollEnd.GaveUp, result.End);
        Assert.Equal(
            new[] { 100, 200, 350, 350 }.Select(ms => TimeSpan.FromMilliseconds(ms)).ToArray(),
            waits.ToArray());
    }

    [Fact]
    public async Task Run_that_is_no_longer_listed_ends_as_not_listed()
    {
        StatusPollResult<string> result = await BoundedStatusPoller.RunAsync<string>(
            _ => Task.FromResult<string?>(null),
            _ => false,
            Policy(),
            onStatus: null,
            onFailure: null,
            delay: (_, _) => Task.CompletedTask,
            CancellationToken.None);

        Assert.Equal(StatusPollEnd.NotListed, result.End);
        Assert.Null(result.Last);
    }

    [Fact]
    public async Task Cancelling_during_a_read_ends_the_poll_as_cancelled()
    {
        using var cts = new CancellationTokenSource();

        StatusPollResult<string> result = await BoundedStatusPoller.RunAsync<string>(
            ct =>
            {
                cts.Cancel();
                ct.ThrowIfCancellationRequested();
                return Task.FromResult<string?>("running");
            },
            _ => false,
            Policy(),
            onStatus: null,
            onFailure: null,
            delay: (_, _) => Task.CompletedTask,
            cts.Token);

        Assert.Equal(StatusPollEnd.Cancelled, result.End);
    }

    [Fact]
    public async Task Cancelling_during_the_wait_ends_the_poll_as_cancelled_without_another_read()
    {
        using var cts = new CancellationTokenSource();
        int reads = 0;

        StatusPollResult<string> result = await BoundedStatusPoller.RunAsync<string>(
            _ =>
            {
                reads++;
                return Task.FromResult<string?>("running");
            },
            _ => false,
            Policy(),
            onStatus: null,
            onFailure: null,
            delay: (_, ct) =>
            {
                cts.Cancel();
                ct.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            },
            cts.Token);

        Assert.Equal(StatusPollEnd.Cancelled, result.End);
        Assert.Equal(1, reads);
    }

    [Fact]
    public async Task Policy_without_room_for_a_read_is_refused()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => BoundedStatusPoller.RunAsync<string>(
            _ => Task.FromResult<string?>("running"),
            _ => false,
            new StatusPollPolicy(Interval, MaxPolls: 0, MaxConsecutiveFailures: 1, MaxFailureBackoff: Interval),
            onStatus: null,
            onFailure: null,
            delay: (_, _) => Task.CompletedTask,
            CancellationToken.None));
    }
}
