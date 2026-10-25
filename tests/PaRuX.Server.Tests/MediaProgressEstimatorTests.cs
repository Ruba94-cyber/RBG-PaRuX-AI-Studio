using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class MediaProgressEstimatorTests
{
    [Fact]
    public void DuplicateProgressDoesNotMakeEtaGrow()
    {
        var estimator = new MediaProgressEstimator(90);

        Assert.Equal(90, estimator.Estimate(0));
        Assert.Equal(90, estimator.Estimate(20));
        int? firstMovingEstimate = estimator.Estimate(40);
        int? duplicateEstimate = estimator.Estimate(40);

        Assert.True(firstMovingEstimate.HasValue);
        Assert.Equal(firstMovingEstimate, duplicateEstimate);
    }

    [Fact]
    public void EtaIsBoundedAndReachesZeroAtCompletion()
    {
        var estimator = new MediaProgressEstimator(90);

        estimator.Estimate(10);
        estimator.Estimate(50);
        int? complete = estimator.Estimate(100);

        Assert.NotNull(complete);
        Assert.Equal(0, complete.Value);
    }
}
