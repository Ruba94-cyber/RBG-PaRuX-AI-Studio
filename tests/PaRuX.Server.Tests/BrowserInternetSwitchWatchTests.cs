using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// A developer browser launched while PaRuX Internet is ON has no egress sink. The watch that closes it when the switch
/// goes OFF is driven here with fakes for the switch read, the close and the poll interval. The real browser and the
/// real server status read are not exercised by these tests.
/// </summary>
public sealed class BrowserInternetSwitchWatchTests
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(1);

    [Theory]
    [InlineData(true, true, false)]    // launched ON and still ON: keep the browser
    [InlineData(true, false, true)]    // launched ON and switched OFF: close it
    [InlineData(true, null, true)]     // launched ON and unreadable: fail closed
    [InlineData(false, true, false)]   // launched OFF: it already runs with the sink
    [InlineData(false, false, false)]
    [InlineData(false, null, false)]
    public void Only_a_browser_launched_with_internet_on_is_closed_unless_the_switch_reads_on(
        bool launchedWithInternetOn, bool? internetEnabledNow, bool expected) =>
        Assert.Equal(expected, BrowserInternetSwitchWatch.MustClose(launchedWithInternetOn, internetEnabledNow));

    [Fact]
    public async Task The_browser_is_closed_on_the_first_poll_that_reads_off()
    {
        var readings = new Queue<bool?>(new bool?[] { true, true, false });
        int closes = 0;
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await BrowserInternetSwitchWatch.RunAsync(
            _ => Task.FromResult(readings.Dequeue()),
            () => { closes++; return Task.CompletedTask; },
            PollInterval,
            bound.Token);

        Assert.Equal(1, closes);
        Assert.Empty(readings);   // polling stopped at the OFF reading, not after it
    }

    [Fact]
    public async Task A_switch_that_cannot_be_read_closes_the_browser()
    {
        int closes = 0;
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await BrowserInternetSwitchWatch.RunAsync(
            _ => Task.FromException<bool?>(new InvalidOperationException("PaRuX Server is not available.")),
            () => { closes++; return Task.CompletedTask; },
            PollInterval,
            bound.Token);

        Assert.Equal(1, closes);
    }

    [Fact]
    public async Task A_failed_close_is_reported_and_retried_while_the_switch_still_reads_off()
    {
        int attempts = 0;
        var reported = new List<Exception>();
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await BrowserInternetSwitchWatch.RunAsync(
            _ => Task.FromResult<bool?>(false),
            () =>
            {
                attempts++;
                if (attempts == 1) throw new InvalidOperationException("Dedicated developer browser did not exit.");
                return Task.CompletedTask;
            },
            PollInterval,
            bound.Token,
            reported.Add);

        Assert.Equal(2, attempts);
        Assert.Single(reported);
    }

    [Fact]
    public async Task The_browser_stays_open_while_the_switch_reads_on_and_the_watch_ends_when_cancelled()
    {
        int reads = 0;
        int closes = 0;
        using var cancel = new CancellationTokenSource();

        Task watch = BrowserInternetSwitchWatch.RunAsync(
            _ =>
            {
                reads++;
                if (reads == 3) cancel.Cancel();
                return Task.FromResult<bool?>(true);
            },
            () => { closes++; return Task.CompletedTask; },
            PollInterval,
            cancel.Token);

        await watch.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(3, reads);
        Assert.Equal(0, closes);
    }

    [Fact]
    public async Task A_watch_cancelled_by_another_action_never_closes_the_browser_even_when_the_switch_reads_off()
    {
        int reads = 0;
        int closes = 0;
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();

        await BrowserInternetSwitchWatch.RunAsync(
            _ =>
            {
                reads++;
                return Task.FromResult<bool?>(false);
            },
            () => { closes++; return Task.CompletedTask; },
            PollInterval,
            cancel.Token);

        Assert.Equal(0, reads);
        Assert.Equal(0, closes);
    }
}
