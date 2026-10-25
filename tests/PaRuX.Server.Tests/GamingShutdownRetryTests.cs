using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// The Gaming Mode shutdown must keep retrying while a local AI runtime still holds VRAM, and must say so.
/// The policy is pure: the clock is passed in, so the retry schedule is checked without waiting.
/// </summary>
public sealed class GamingShutdownRetryTests
{
    private static readonly DateTime Start = new(2026, 10, 19, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void A_shutdown_that_failed_is_retried_after_a_backoff_and_not_recorded_as_handled()
    {
        var retry = new GamingShutdownRetry();
        retry.Begin(Start);
        Assert.True(retry.IsDue(Start));

        retry.Failed("A local AI process did not stop.", Start);

        Assert.True(retry.Pending);
        Assert.False(retry.IsDue(Start.AddSeconds(1)));
        Assert.True(retry.IsDue(Start.AddSeconds(2)));
        Assert.Equal("A local AI process did not stop.", retry.LastFailure);
    }

    [Fact]
    public void The_retry_interval_is_constant_so_a_resident_that_keeps_vram_is_retried_promptly()
    {
        Assert.Equal(TimeSpan.FromSeconds(2), GamingShutdownRetry.RetryInterval);

        var retry = new GamingShutdownRetry();
        retry.Begin(Start);
        DateTime failedAt = Start;
        for (int failure = 1; failure <= 6; failure++)
        {
            retry.Failed("still running", failedAt);
            Assert.False(retry.IsDue(failedAt + GamingShutdownRetry.RetryInterval - TimeSpan.FromMilliseconds(1)));
            Assert.True(retry.IsDue(failedAt + GamingShutdownRetry.RetryInterval));
            failedAt += GamingShutdownRetry.RetryInterval;
        }
        Assert.Equal(6, retry.Failures);
    }

    [Fact]
    public void A_later_success_ends_the_pending_shutdown_and_clears_the_failure()
    {
        var retry = new GamingShutdownRetry();
        retry.Begin(Start);
        retry.Failed("first attempt failed", Start);

        retry.Succeeded();

        Assert.False(retry.Pending);
        Assert.Null(retry.LastFailure);
        Assert.False(retry.IsDue(Start.AddHours(1)));
    }

    [Fact]
    public void Ending_gaming_mode_stops_all_further_attempts()
    {
        var retry = new GamingShutdownRetry();
        retry.Begin(Start);
        retry.Failed("still running", Start);

        retry.Cleared();

        Assert.False(retry.Pending);
        Assert.False(retry.IsDue(Start.AddHours(1)));
        Assert.Null(retry.LastFailure);
    }
}
