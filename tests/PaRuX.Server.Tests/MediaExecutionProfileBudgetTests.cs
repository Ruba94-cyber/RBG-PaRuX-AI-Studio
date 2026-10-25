using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class MediaExecutionProfileBudgetTests
{
    [Theory]
    [InlineData(0, 1, true)]
    [InlineData(1, 1, true)]
    [InlineData(2, 1, false)]
    [InlineData(1024, 2048, true)]
    [InlineData(2049, 2048, false)]
    public void Artifact_budget_is_fail_closed(long artifactBytes, long maxArtifactBytes, bool expected)
    {
        Assert.Equal(expected, MediaGenerationService.FitsArtifactBudget(artifactBytes, maxArtifactBytes));
    }

    [Theory]
    [InlineData(-1, 1)]
    [InlineData(0, 0)]
    [InlineData(1, 0)]
    public void Invalid_budget_inputs_are_rejected(long artifactBytes, long maxArtifactBytes)
    {
        Assert.False(MediaGenerationService.FitsArtifactBudget(artifactBytes, maxArtifactBytes));
    }
}
