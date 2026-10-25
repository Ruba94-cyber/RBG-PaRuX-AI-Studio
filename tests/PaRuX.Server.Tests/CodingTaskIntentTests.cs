using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class CodingTaskIntentTests
{
    private const string RealPrompt = "controlla questa applicazione \"C:\\Users\\PaRuX-Test\\Desktop\\Test App - Copia\", "
        + "correggi eventuali bug e usa compute use per testarla realmente. non toccare git.";

    [Fact]
    public void Explicit_quoted_workspace_overrides_stale_configured_repository()
    {
        string? path = CodingTaskIntent.ExtractExplicitWindowsPath(RealPrompt);
        Assert.Equal(@"C:\Users\PaRuX-Test\Desktop\Test App - Copia", path);
    }

    [Theory]
    [InlineData("usa computer use per testarla realmente")]
    [InlineData("usa compute use per testarla realmente")]
    [InlineData("testa realmente la UI")]
    [InlineData("run real GUI tests")]
    public void Real_ui_requests_require_computer_use_evidence(string prompt) =>
        Assert.True(CodingTaskIntent.RequiresComputerUse(prompt));

    [Fact]
    public void No_git_instruction_forces_isolation_from_original_git_metadata() =>
        Assert.True(CodingTaskIntent.PreserveOriginalGit(RealPrompt));

    [Fact]
    public void Execution_requirement_demands_verified_computer_use_child()
    {
        string goal = CodingTaskIntent.AppendExecutionRequirements("Fix it", RealPrompt);
        Assert.Contains(CodingTaskIntent.ComputerUseEvidenceMarker, goal);
        Assert.Contains("role=computer-use", goal);
        Assert.False(CodingTaskIntent.HasVerifiedComputerUseEvidence(goal));
        Assert.True(CodingTaskIntent.HasVerifiedComputerUseEvidence(
            goal + "\n[DEPENDENCY RESULT child · role=computer-use · verification=pass]"));
    }
}
