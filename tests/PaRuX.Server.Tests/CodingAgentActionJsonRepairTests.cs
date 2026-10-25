using System.Text.Json;
using PaRuX.Services;
using Xunit;

public sealed class CodingAgentActionJsonRepairTests
{
    [Theory]
    [InlineData("{ action: 'read_file', path: 'src/App.cs', }", "read_file", "src/App.cs")]
    [InlineData("```json\n{\"action\":\"read_file\",\"path\":\"src/App.cs\",}\n```", "read_file", "src/App.cs")]
    [InlineData("I will inspect the file. {action: 'read_file', path: 'src/App.cs'} Then continue.", "read_file", "src/App.cs")]
    public void RepairsOneNearJsonActionWithoutChangingItsValues(string response, string action, string path)
    {
        Assert.True(CodingAgentActionJsonRepair.TryRepair(response, out string json, out string error), error);

        using JsonDocument document = JsonDocument.Parse(json);
        Assert.Equal(action, document.RootElement.GetProperty("action").GetString());
        Assert.Equal(path, document.RootElement.GetProperty("path").GetString());
    }

    [Fact]
    public void SingleQuotedCommandPreservesTheExactCommandText()
    {
        const string command = "Write-Output 'approved'; dotnet test";
        string response = "{action:'run_command', command:'Write-Output \\\'approved\\\'; dotnet test'}";

        Assert.True(CodingAgentActionJsonRepair.TryRepair(response, out string json, out string error), error);
        using JsonDocument document = JsonDocument.Parse(json);
        Assert.Equal(command, document.RootElement.GetProperty("command").GetString());
    }

    [Theory]
    [InlineData("I have two options: {action:'done'} {action:'run_command',command:'x'}")]
    [InlineData("No action object is present.")]
    [InlineData("{action: run_command, command: 'dotnet test'}")]
    [InlineData("{action:'read_file', path:'unfinished}")]
    [InlineData("{\"action\":\"read_file\",\"action\":\"run_command\",\"command\":\"del /q target\"}")]
    [InlineData("{\"action\":\"read_file\",\"Action\":\"run_command\",\"command\":\"del /q target\"}")]
    [InlineData("{action:'batch_read', requests:[{action:'read_file',path:'a',path:'b'},{action:'read_file',path:'c'}]}")]
    public void RejectsAmbiguousOrSemanticallyInventiveRepairWithActionableReason(string response)
    {
        Assert.False(CodingAgentActionJsonRepair.TryRepair(response, out _, out string error));
        Assert.False(string.IsNullOrWhiteSpace(error));
        Assert.True(error.Contains("one", StringComparison.OrdinalIgnoreCase)
            || error.Contains("object", StringComparison.OrdinalIgnoreCase)
            || error.Contains("action", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void DoesNotInventAMissingActionField()
    {
        Assert.True(CodingAgentActionJsonRepair.TryRepair("{path:'src/App.cs'}", out string json, out string error), error);
        using JsonDocument document = JsonDocument.Parse(json);
        Assert.False(document.RootElement.TryGetProperty("action", out _));
    }
}
