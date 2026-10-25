using System.Reflection;
using PaRuX.Server;
using PaRuX.Server.Orchestration;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class AgentWorkerDelegationSchemaTests
{
    [Fact]
    public void Specialist_tool_schema_exposes_single_and_parallel_delegation_with_bounded_children()
    {
        FieldInfo field = typeof(AgentWorkerService).GetField(
            "ToolActionSchema",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("AgentWorkerService.ToolActionSchema not found.");
        LocalStructuredOutputRequest schema = Assert.IsType<LocalStructuredOutputRequest>(field.GetValue(null));

        Assert.Contains("\"delegate_task\"", schema.JsonSchema, StringComparison.Ordinal);
        Assert.Contains("\"delegate_tasks\"", schema.JsonSchema, StringComparison.Ordinal);
        Assert.Contains("\"children\"", schema.JsonSchema, StringComparison.Ordinal);
        Assert.Contains("\"minItems\":2", schema.JsonSchema, StringComparison.Ordinal);
        Assert.Contains("\"maxItems\":6", schema.JsonSchema, StringComparison.Ordinal);
        Assert.Contains("\"required\":[\"childGoal\"]", schema.JsonSchema, StringComparison.Ordinal);
        Assert.Contains("\"additionalProperties\":false", schema.JsonSchema, StringComparison.Ordinal);
    }
}
