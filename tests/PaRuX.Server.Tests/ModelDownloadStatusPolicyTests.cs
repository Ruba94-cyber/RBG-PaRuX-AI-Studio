using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class ModelDownloadStatusPolicyTests
{
    [Theory]
    [InlineData("cancelled")]
    [InlineData("failed")]
    [InlineData("paused")]
    public void Paused_download_rows_can_resume(string status)
    {
        Assert.True(ModelDownloadStatusPolicy.CanResume(status));
    }

    [Theory]
    [InlineData("queued")]
    [InlineData("downloading")]
    public void Active_download_rows_can_pause(string status)
    {
        Assert.True(ModelDownloadStatusPolicy.CanPause(status));
    }

    [Fact]
    public void Download_waiting_for_automatic_retry_can_be_paused()
    {
        Assert.True(ModelDownloadStatusPolicy.CanPause("retrying"));
        Assert.True(ModelDownloadStatusPolicy.IsActive("retrying"));
    }

    [Theory]
    [InlineData("completed")]
    [InlineData("running")]
    public void Other_download_states_do_not_offer_resume(string status)
    {
        Assert.False(ModelDownloadStatusPolicy.CanResume(status));
    }

    [Theory]
    [InlineData("cancelled")]
    [InlineData("failed")]
    [InlineData("paused")]
    public void Only_terminal_or_paused_server_rows_offer_remove(string status)
    {
        Assert.True(ModelDownloadStatusPolicy.CanRemove(status, "server-job-id"));
        Assert.False(ModelDownloadStatusPolicy.CanRemove(status, ModelDownloadResumeCatalog.SyntheticIdPrefix + "partial-id"));
    }

    [Theory]
    [InlineData("queued")]
    [InlineData("downloading")]
    [InlineData("completed")]
    public void Active_and_completed_downloads_do_not_offer_remove(string status)
    {
        Assert.False(ModelDownloadStatusPolicy.CanRemove(status, "server-job-id"));
    }

    [Fact]
    public void Download_rows_read_every_one_and_a_half_seconds()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(1500), ModelDownloadStatusPolicy.Follow.Interval);
    }

    [Fact]
    public void Download_rows_give_up_after_eight_failed_reads_in_a_row()
    {
        Assert.Equal(8, ModelDownloadStatusPolicy.Follow.MaxConsecutiveFailures);
    }

    [Fact]
    public void Failed_read_waits_double_from_one_and_a_half_seconds_and_stop_at_thirty()
    {
        StatusPollPolicy policy = ModelDownloadStatusPolicy.Follow;

        Assert.Equal(
            new[] { 1500, 3000, 6000, 12000, 24000, 30000, 30000 }.Select(ms => TimeSpan.FromMilliseconds(ms)).ToArray(),
            Enumerable.Range(1, 7).Select(failures => BoundedStatusPoller.BackoffAfter(policy, failures)).ToArray());
    }

    [Fact]
    public void Following_a_download_is_bounded_to_24_hours_of_reads()
    {
        StatusPollPolicy policy = ModelDownloadStatusPolicy.Follow;

        Assert.Equal(TimeSpan.FromHours(24), TimeSpan.FromTicks(policy.Interval.Ticks * policy.MaxPolls));
    }
}
