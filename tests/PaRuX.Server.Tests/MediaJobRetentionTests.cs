using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class MediaJobRetentionTests
{
    [Fact]
    public void Polling_snapshot_keeps_asset_metadata_but_strips_large_payload()
    {
        var status = new MediaJobStatus
        {
            Id = "job-1",
            Type = "video",
            Status = "completed",
            ProgressPercent = 100,
            Asset = new MediaAsset
            {
                FileName = "clip.mp4",
                MediaType = "video/mp4",
                Base64Data = new string('x', 1_000_000),
                Width = 1280,
                Height = 720,
                DurationSeconds = 8,
                Fps = 24
            }
        };

        MediaJobStatus polling = MediaJobService.SnapshotStatus(status, includePayload: false);
        MediaJobStatus detail = MediaJobService.SnapshotStatus(status, includePayload: true);

        Assert.NotNull(polling.Asset);
        Assert.Equal("clip.mp4", polling.Asset!.FileName);
        Assert.Equal(1280, polling.Asset.Width);
        Assert.Equal(string.Empty, polling.Asset.Base64Data);
        Assert.Equal(status.Asset.Base64Data, detail.Asset!.Base64Data);
        Assert.NotSame(status.Asset, polling.Asset);
    }

    [Fact]
    public void Retention_is_bounded_by_count_for_small_terminal_jobs()
    {
        DateTime now = DateTime.UtcNow;
        var jobs = Enumerable.Range(0, MediaJobService.MaximumRetainedTerminalJobs + 10)
            .Select(index => ($"job-{index}", now.AddSeconds(index), 0L))
            .ToList();

        HashSet<string> keep = MediaJobService.SelectTerminalJobsToKeep(jobs);

        Assert.Equal(MediaJobService.MaximumRetainedTerminalJobs, keep.Count);
        Assert.Contains($"job-{jobs.Count - 1}", keep);
        Assert.DoesNotContain("job-0", keep);
    }

    [Fact]
    public void Retention_prunes_old_payloads_when_aggregate_memory_budget_is_exceeded()
    {
        DateTime now = DateTime.UtcNow;
        long halfBudgetPlusOne = MediaJobService.MaximumRetainedPayloadCharacters / 2 + 1;
        var jobs = new[]
        {
            ("old", now, halfBudgetPlusOne),
            ("new", now.AddMinutes(1), halfBudgetPlusOne)
        };

        HashSet<string> keep = MediaJobService.SelectTerminalJobsToKeep(jobs);

        Assert.Single(keep);
        Assert.Contains("new", keep);
    }

    [Fact]
    public void Newest_oversized_result_is_kept_so_the_creator_can_fetch_it_once()
    {
        DateTime now = DateTime.UtcNow;
        var jobs = new[]
        {
            ("older", now, 1L),
            ("oversized", now.AddMinutes(1), MediaJobService.MaximumRetainedPayloadCharacters + 1)
        };

        HashSet<string> keep = MediaJobService.SelectTerminalJobsToKeep(jobs);

        Assert.Single(keep);
        Assert.Contains("oversized", keep);
    }
}
