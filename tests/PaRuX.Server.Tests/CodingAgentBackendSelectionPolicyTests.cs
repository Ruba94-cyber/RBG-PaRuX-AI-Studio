using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class CodingAgentBackendSelectionPolicyTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("native")]
    [InlineData(" NATIVE ")]
    public void Native_agent_remains_default_without_any_new_permission(string? selection)
    {
        var result = CodingAgentBackendSelectionPolicy.Select(selection);
        Assert.True(result.Allowed);
        Assert.Equal("native", result.Backend);
    }

    [Theory]
    [InlineData("opencode")]
    [InlineData("OPENCODE")]
    public void Third_party_backend_cannot_bypass_windows_appcontainer(string requested)
    {
        var result = CodingAgentBackendSelectionPolicy.Select(requested);
        Assert.False(result.Allowed);
        Assert.Equal("opencode", result.Backend);
        Assert.Contains("OS-isolated", result.Reason);
    }

    [Theory]
    [InlineData("shell")]
    [InlineData("arbitrary-executor")]
    [InlineData("opencode --auto")]
    public void Unknown_backends_cannot_launch_an_arbitrary_process(string requested)
    {
        var result = CodingAgentBackendSelectionPolicy.Select(requested);
        Assert.False(result.Allowed);
        Assert.Equal(string.Empty, result.Backend);
    }
}
