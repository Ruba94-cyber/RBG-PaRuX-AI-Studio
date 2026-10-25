using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class MediaVramReservationEstimatorTests
{
    [Fact]
    public void Video_and_higher_resolution_reserve_more_than_baseline_image()
    {
        long image = MediaVramReservationEstimator.EstimateBytes(new MediaJobRequest
        {
            Type = "image", Width = 1024, Height = 1024, Prompt = "x"
        });
        long largeImage = MediaVramReservationEstimator.EstimateBytes(new MediaJobRequest
        {
            Type = "image", Width = 2048, Height = 2048, Prompt = "x"
        });
        long video = MediaVramReservationEstimator.EstimateBytes(new MediaJobRequest
        {
            Type = "video", Width = 1280, Height = 720, DurationSeconds = 10, Prompt = "x"
        });

        Assert.True(largeImage > image);
        Assert.True(video > image);
    }

    [Fact]
    public void Estimates_are_positive_bounded_and_enter_the_shared_residency_ledger()
    {
        GpuResidencyReservationStore.ClearForTests();
        long bytes = MediaVramReservationEstimator.EstimateBytes(new MediaJobRequest
        {
            Type = "custom", Width = 3840, Height = 3840, CustomWorkflowId = "workflow"
        });

        Assert.InRange(bytes, 2L * 1024 * 1024 * 1024, 24L * 1024 * 1024 * 1024);
        GpuResidencyReservationStore.Set("media-test", "media", bytes);
        Assert.Equal(bytes, GpuResidencyReservationStore.GetReservedBytes());
        GpuResidencyReservationStore.Release("media-test");
        Assert.Equal(0, GpuResidencyReservationStore.GetReservedBytes());
    }
}
