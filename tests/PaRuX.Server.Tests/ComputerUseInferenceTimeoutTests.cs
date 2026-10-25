using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// A UI verification worker waits on a local vision model. Before this, that call had no time limit, so a stalled model kept
/// its task Running and its execution slot occupied until the user cancelled it. The limit is tested with a stream that
/// never completes, and a user stop must still be reported as cancellation rather than as a timeout.
/// </summary>
public sealed class ComputerUseInferenceTimeoutTests
{
    [Fact]
    public async Task An_inference_that_never_completes_is_reported_as_timed_out_instead_of_hanging()
    {
        var clock = Stopwatch.StartNew();

        bool completed = await ComputerUseInferenceTimeout.CompletesWithinAsync(
            token => Task.Delay(Timeout.Infinite, token),
            TimeSpan.FromMilliseconds(100),
            CancellationToken.None);

        Assert.False(completed);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), "A stalled inference must end at its limit.");
    }

    [Fact]
    public async Task An_inference_that_finishes_within_the_limit_is_reported_as_completed()
    {
        bool completed = await ComputerUseInferenceTimeout.CompletesWithinAsync(
            _ => Task.CompletedTask,
            TimeSpan.FromSeconds(5),
            CancellationToken.None);

        Assert.True(completed);
    }

    [Fact]
    public async Task A_user_stop_is_cancellation_and_never_reported_as_a_timeout()
    {
        using var stop = new CancellationTokenSource();
        Task<bool> run = ComputerUseInferenceTimeout.CompletesWithinAsync(
            token => Task.Delay(Timeout.Infinite, token),
            TimeSpan.FromMinutes(5),
            stop.Token);

        stop.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }
}
