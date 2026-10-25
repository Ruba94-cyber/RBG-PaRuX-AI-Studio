using PaRuX.Services;
using Xunit;

public sealed class CodingAgentObservationPolicyTests
{
    [Fact]
    public void OversizedNewestObservationStillAppearsWithArtifactReference()
    {
        string large = "read_file source\n" + new string('x', 4_000) + "\nartifactId=abc123";
        var recent = CodingAgentObservationPolicy.Recent(
            new[] { "earlier", large }, maxEntries: 12, maxCharacters: 2_500);

        Assert.Equal(2, recent.Count);
        Assert.StartsWith("read_file source", recent[1]);
        Assert.Contains("artifactId=abc123", recent[1]);
        Assert.True(recent[1].Length <= 900);
    }
    [Theory]
    [InlineData(0, 2_500)]
    [InlineData(12, 0)]
    [InlineData(12, -100)]
    [InlineData(-1, 2_500)]
    public void EmptyReplayBudgetNeverAddsDelimiterOnlyObservations(int maxEntries, int maxCharacters)
    {
        var recent = CodingAgentObservationPolicy.Recent(
            new[] { "read_file a.cs", "run_command dotnet test" },
            maxEntries, maxCharacters);

        Assert.Empty(recent);
    }

}
