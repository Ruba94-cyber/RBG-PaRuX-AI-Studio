using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Serialized against McpToolSchemaRouterTests because both drive the same process-wide
/// McpToolUseTelemetry store. These tests call ClearForTests and then assert on the exact contents
/// of that store, so any routing recorded by another class running in parallel makes them fail -
/// which is exactly what happened during a full-suite run under heavy CPU load, producing a
/// failure that passed on its own re-run. A flaky test is worse than a missing one: it makes every
/// green regression result arguable.
/// </summary>
[CollectionDefinition("McpTelemetrySerial", DisableParallelization = true)]
public sealed class McpTelemetrySerialCollection { }

[Collection("McpTelemetrySerial")]
public sealed class McpToolUseTelemetryTests
{
    [Fact]
    public void Snapshot_reports_schema_reduction_execution_success_and_repair_burden()
    {
        McpToolUseTelemetry.ClearForTests();
        McpToolUseTelemetry.RecordRouting(
            routed: true,
            originalCount: 20,
            selectedCount: 5,
            originalCharacters: 10_000,
            selectedCharacters: 2_000,
            reason: "test",
            elapsedMilliseconds: 3);
        McpToolUseTelemetry.RecordExecution(
            "repo_search", advertisedToolCount: 5, success: true,
            outcome: "success", argumentParseFallback: false, elapsedMilliseconds: 12);
        McpToolUseTelemetry.RecordExecution(
            "repo_open", advertisedToolCount: 5, success: false,
            outcome: "tool-error", argumentParseFallback: true, elapsedMilliseconds: 7);

        McpToolUseTelemetry.Snapshot snapshot = McpToolUseTelemetry.GetSnapshot();

        Assert.Equal(1, snapshot.RoutingEventCount);
        Assert.Equal(2, snapshot.ExecutionEventCount);
        Assert.Equal(0.75d, snapshot.SchemaCountReductionRatio, 3);
        Assert.Equal(0.8d, snapshot.SchemaCharacterReductionRatio, 3);
        Assert.Equal(0.5d, snapshot.ExecutionSuccessRate, 3);
        Assert.Equal(0.5d, snapshot.ArgumentParseFallbackRate, 3);
        Assert.Equal(5d, snapshot.AverageAdvertisedToolsAtExecution, 3);
    }

    [Fact]
    public void Router_records_real_selection_metrics_without_prompt_content()
    {
        McpToolUseTelemetry.ClearForTests();
        var tools = new System.Text.Json.Nodes.JsonArray();
        for (int i = 0; i < 12; i++)
        {
            tools.Add(new System.Text.Json.Nodes.JsonObject
            {
                ["type"] = "function",
                ["function"] = new System.Text.Json.Nodes.JsonObject
                {
                    ["name"] = i == 11 ? "github_search_repositories" : "generic_tool_" + i,
                    ["description"] = i == 11 ? "Search GitHub repositories and source code" : "General utility",
                    ["parameters"] = new System.Text.Json.Nodes.JsonObject { ["type"] = "object" }
                }
            });
        }

        _ = McpToolSchemaRouter.Route(
            "search github repositories for parser",
            tools,
            activationToolCount: 4,
            maximumSelectedTools: 6,
            maximumSelectedCharacters: 4_000);

        McpToolUseTelemetry.Snapshot snapshot = McpToolUseTelemetry.GetSnapshot();
        McpToolUseTelemetry.RoutingEvent item = Assert.Single(snapshot.RecentRouting);
        Assert.True(item.Routed);
        Assert.Equal(12, item.OriginalCount);
        Assert.True(item.SelectedCount < item.OriginalCount);
        Assert.True(item.SelectedCharacters < item.OriginalCharacters);
        Assert.DoesNotContain("parser", item.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Store_is_bounded_for_long_running_local_sessions()
    {
        McpToolUseTelemetry.ClearForTests();
        for (int i = 0; i < 1_200; i++)
        {
            McpToolUseTelemetry.RecordExecution(
                "tool-" + i,
                advertisedToolCount: 4,
                success: true,
                outcome: "success",
                argumentParseFallback: false,
                elapsedMilliseconds: 1);
        }

        McpToolUseTelemetry.Snapshot snapshot = McpToolUseTelemetry.GetSnapshot(recentLimit: 100);
        Assert.Equal(1_024, snapshot.ExecutionEventCount);
        Assert.Equal(100, snapshot.RecentExecutions.Count);
        Assert.Equal("tool-1199", snapshot.RecentExecutions[^1].ToolName);
    }
}
