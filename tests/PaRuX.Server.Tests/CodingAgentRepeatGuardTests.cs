using PaRuX.Services;
using Xunit;

public sealed class CodingAgentRepeatGuardTests
{
    [Fact]
    public void SecondIdenticalReadSteersAndThirdStopsUntilWorkspaceMutationStartsANewEpoch()
    {
        var guard = new CodingAgentRepeatGuard();
        Assert.Equal(CodingAgentRepeatDecision.Allow, guard.Evaluate("read_file|src/A.cs", out _));
        guard.Record("read_file|src/A.cs", "file contents");
        Assert.Equal(CodingAgentRepeatDecision.Steer,
            guard.Evaluate("read_file|src/A.cs", out string evidence));
        Assert.Equal("file contents", evidence);
        Assert.Equal(CodingAgentRepeatDecision.Stop,
            guard.Evaluate("read_file|src/A.cs", out evidence));
        Assert.Equal(1, guard.AdvanceEpoch());
        Assert.Equal(CodingAgentRepeatDecision.Allow,
            guard.Evaluate("read_file|src/A.cs", out _));
    }
}
