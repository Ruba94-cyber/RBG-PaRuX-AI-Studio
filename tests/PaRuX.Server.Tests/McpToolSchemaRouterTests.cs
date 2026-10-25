using System.Text.Json.Nodes;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>Shares the process-wide McpToolUseTelemetry store with McpToolUseTelemetryTests -
/// every Route call records into it - so the two must not run in parallel.</summary>
[Collection("McpTelemetrySerial")]
public sealed class McpToolSchemaRouterTests
{
    private static JsonObject Tool(string name, string description) => new()
    {
        ["type"] = "function",
        ["function"] = new JsonObject
        {
            ["name"] = name,
            ["description"] = description,
            ["parameters"] = new JsonObject { ["type"] = "object" }
        }
    };

    [Fact]
    public void Small_allowed_tool_sets_are_preserved_exactly()
    {
        var tools = new JsonArray(
            Tool("github_search", "Search GitHub repositories"),
            Tool("calendar_list", "List calendar events"));

        McpToolSchemaRouter.Decision decision = McpToolSchemaRouter.Route("search github", tools);

        Assert.False(decision.Routed);
        Assert.Equal(2, decision.SelectedCount);
        Assert.Equal("github_search", decision.Schemas[0]!["function"]!["name"]!.GetValue<string>());
        Assert.Equal("calendar_list", decision.Schemas[1]!["function"]!["name"]!.GetValue<string>());
    }

    [Fact]
    public void Large_tool_sets_are_narrowed_when_the_query_has_a_high_confidence_match()
    {
        var tools = new JsonArray();
        for (int i = 0; i < 10; i++) tools.Add(Tool("generic_tool_" + i, "General utility number " + i));
        tools.Add(Tool("github_search_repositories", "Search GitHub repositories, source code and project metadata"));
        tools.Add(Tool("github_open_pull_request", "Open a GitHub pull request"));

        McpToolSchemaRouter.Decision decision = McpToolSchemaRouter.Route(
            "Search GitHub repositories for the parser implementation", tools,
            activationToolCount: 4, maximumSelectedTools: 6, maximumSelectedCharacters: 4_000);

        Assert.True(decision.Routed);
        Assert.True(decision.SelectedCount < tools.Count);
        Assert.Contains(decision.Schemas, node =>
            node?["function"]?["name"]?.GetValue<string>() == "github_search_repositories");
    }

    [Fact]
    public void No_high_confidence_signal_fails_open_to_preserve_correctness()
    {
        var tools = new JsonArray();
        for (int i = 0; i < 12; i++) tools.Add(Tool("service_action_" + i, "Remote capability " + i));

        McpToolSchemaRouter.Decision decision = McpToolSchemaRouter.Route(
            "Do the thing we discussed earlier", tools, activationToolCount: 4, maximumSelectedTools: 6);

        Assert.False(decision.Routed);
        Assert.Equal(tools.Count, decision.SelectedCount);
    }

    [Fact]
    public void Routing_respects_tool_and_character_budgets()
    {
        var tools = new JsonArray();
        for (int i = 0; i < 20; i++)
            tools.Add(Tool("database_query_" + i, "Query the database for records and structured data " + new string('x', 120)));

        McpToolSchemaRouter.Decision decision = McpToolSchemaRouter.Route(
            "query database records", tools,
            activationToolCount: 2, maximumSelectedTools: 5, maximumSelectedCharacters: 1_500);

        Assert.True(decision.Routed);
        Assert.InRange(decision.SelectedCount, 1, 5);
        Assert.True(decision.SelectedCharacters <= 1_500 || decision.SelectedCount == 1);
    }

    [Fact]
    public void Returned_nodes_are_clones_and_do_not_reparent_the_original_array()
    {
        var tools = new JsonArray();
        for (int i = 0; i < 10; i++) tools.Add(Tool("calendar_event_" + i, "Read and manage calendar events"));

        McpToolSchemaRouter.Decision decision = McpToolSchemaRouter.Route(
            "calendar events", tools, activationToolCount: 2, maximumSelectedTools: 4);

        Assert.True(decision.Routed);
        Assert.NotSame(tools[0], decision.Schemas[0]);
        Assert.Equal(10, tools.Count);
    }
}
