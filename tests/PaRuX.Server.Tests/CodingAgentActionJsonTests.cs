using System;
using System.Text.Json;
using PaRuX.Services;
using Xunit;

public sealed class CodingAgentActionJsonTests
{
    [Fact]
    public void ParsesOneFencedObjectAndRemovesTrailingCommasFromObjectsAndArrays()
    {
        const string response = "```json\n{\"action\":\"read_file\",\"path\":\"src/App.cs\",\"items\":[1,2,],}\n```";

        CodingAgentAction? action = CodingAgentActionJson.TryParse(response);

        Assert.NotNull(action);
        Assert.Equal("read_file", action.Action);
        Assert.Equal("src/App.cs", action.Path);
    }

    [Fact]
    public void RejectsTwoCompleteObjectsAsAmbiguous()
    {
        CodingAgentAction? action = CodingAgentActionJson.TryParse(
            "```json\n{\"action\":\"read_file\"}\n```\n{\"action\":\"run_command\",\"command\":\"example\"}");

        Assert.Null(action);
    }

    [Fact]
    public void DoesNotInventOrRenameJsonFields()
    {
        const string response = "{\"path\":\"src/App.cs\",\"actionName\":\"read_file\"}";
        Assert.True(CodingAgentActionJsonRepair.TryRepair(response, out string repaired, out string error), error);

        using JsonDocument document = JsonDocument.Parse(repaired);
        Assert.Collection(document.RootElement.EnumerateObject(),
            property => Assert.Equal("path", property.Name),
            property => Assert.Equal("actionName", property.Name));

        CodingAgentAction? action = CodingAgentActionJson.TryParse(response);
        Assert.NotNull(action);
        Assert.Equal(string.Empty, action.Action);
        Assert.Equal("src/App.cs", action.Path);
    }

    [Fact]
    public void LeavesUnknownActionNameForCallerValidation()
    {
        CodingAgentAction? action = CodingAgentActionJson.TryParse("{\"action\":\"unknown_action\"}");

        Assert.NotNull(action);
        Assert.Equal("unknown_action", action.Action);
    }

    [Theory]
    [InlineData("grep_search", "text", "needle from text")]
    [InlineData("search_artifact", "search", "needle from search")]
    [InlineData("glob_search", "find", "needle from find")]
    public void PreservesSearchAliasFallbacks(string actionName, string aliasName, string expected)
    {
        string response = "{\"action\":\"" + actionName + "\",\"" + aliasName + "\":\"" + expected + "\"}";

        CodingAgentAction? action = CodingAgentActionJson.TryParse(response);

        Assert.NotNull(action);
        Assert.Equal(expected, action.Pattern);
    }

    [Fact]
    public void ExistingPatternTakesPrecedenceOverSearchAliases()
    {
        CodingAgentAction? action = CodingAgentActionJson.TryParse(
            "{\"action\":\"grep_search\",\"pattern\":\"explicit\",\"text\":\"fallback\"}");

        Assert.NotNull(action);
        Assert.Equal("explicit", action.Pattern);
    }
}
