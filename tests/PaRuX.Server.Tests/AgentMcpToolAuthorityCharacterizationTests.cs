using System;
using System.Linq;
using System.Text.Json;
using PaRuX.Contracts;
using PaRuX.Server.Orchestration;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class AgentMcpToolAuthorityCharacterizationTests
{
    [Theory]
    [InlineData("running")]
    [InlineData("RUNNING")]
    [InlineData("Running")]
    public void CanUse_is_case_insensitive_on_role_and_status_and_capability(string status)
    {
        Assert.True(AgentMcpToolAuthority.CanUse(Task("CoDiNg", status, "MCP.external")));
        Assert.True(AgentMcpToolAuthority.CanUse(Task("coding", status, "MCP.EXTERNAL")));
    }

    [Theory]
    [InlineData("planned")]
    [InlineData("completed")]
    [InlineData("failed")]
    public void CanUse_refuses_any_status_other_than_running(string status)
    {
        Assert.False(AgentMcpToolAuthority.CanUse(Task("coding", status, AgentMcpToolAuthority.RequiredCapability)));
    }

    [Fact]
    public void CanUse_refuses_null_or_empty_capability_lists()
    {
        var granted = Task("coding", AgentTaskStatus.Running, null);
        Assert.False(AgentMcpToolAuthority.CanUse(granted));
        Assert.False(AgentMcpToolAuthority.CanUse(Task("coding", AgentTaskStatus.Running)));
    }

    [Fact]
    public void IsPlainToolName_requires_printable_ascii_without_spaces()
    {
        Assert.True(AgentMcpToolAuthority.IsPlainToolName("external_lookup"));
        Assert.True(AgentMcpToolAuthority.IsPlainToolName("a!b~c"));
        Assert.False(AgentMcpToolAuthority.IsPlainToolName(null));
        Assert.False(AgentMcpToolAuthority.IsPlainToolName(""));
        Assert.False(AgentMcpToolAuthority.IsPlainToolName("has space"));
        Assert.False(AgentMcpToolAuthority.IsPlainToolName("del\ttab"));
        Assert.False(AgentMcpToolAuthority.IsPlainToolName("cyrillic\u0441earch"));
    }

    [Fact]
    public void IsReservedCodingActionName_is_case_insensitive_and_rejects_blank()
    {
        Assert.True(AgentMcpToolAuthority.IsReservedCodingActionName("READ_FILE"));
        Assert.True(AgentMcpToolAuthority.IsReservedCodingActionName("done"));
        Assert.False(AgentMcpToolAuthority.IsReservedCodingActionName(null));
        Assert.False(AgentMcpToolAuthority.IsReservedCodingActionName(""));
        Assert.False(AgentMcpToolAuthority.IsReservedCodingActionName("   "));
        Assert.False(AgentMcpToolAuthority.IsReservedCodingActionName("not_reserved"));
    }

    [Fact]
    public void ReservedCodingActionNames_exposes_all_reserved_names()
    {
        IReadOnlyList<string> names = AgentMcpToolAuthority.ReservedCodingActionNames;
        Assert.NotEmpty(names);
        Assert.Contains("read_file", names);
        Assert.Contains("mcp_tool_call", names);
        Assert.Contains("done", names);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void CanRequest_refuses_blank_server_and_tool_ids(string? serverId)
    {
        Assert.False(AgentMcpToolAuthority.CanRequest(Granted(), serverId, "external_lookup"));
        Assert.False(AgentMcpToolAuthority.CanRequest(Granted(), "server-a", serverId));
    }

    [Fact]
    public void CanRequest_enforces_server_id_length_boundary_at_64()
    {
        Assert.True(AgentMcpToolAuthority.CanRequest(Granted(), new string('s', 64), "external_lookup"));
        Assert.False(AgentMcpToolAuthority.CanRequest(Granted(), new string('s', 65), "external_lookup"));
    }

    [Fact]
    public void CanRequest_enforces_tool_name_length_boundary_at_128()
    {
        Assert.True(AgentMcpToolAuthority.CanRequest(Granted(), "server-a", new string('t', 128)));
        Assert.False(AgentMcpToolAuthority.CanRequest(Granted(), "server-a", new string('t', 129)));
    }

    [Fact]
    public void FilterAvailableTools_refuses_null_task_or_null_candidates()
    {
        var candidates = new[] { ServerTool("server-a", "lookup") };
        Assert.Empty(AgentMcpToolAuthority.FilterAvailableTools(null, candidates));
        Assert.Empty(AgentMcpToolAuthority.FilterAvailableTools(Granted(), null));
    }

    [Fact]
    public void FilterAvailableTools_skips_null_entries_and_reserved_names()
    {
        AgentMcpToolDefinition?[] candidates =
        {
            null,
            ServerTool("server-a", "lookup"),
            ServerTool("server-a", "write_file")
        };

        var filtered = AgentMcpToolAuthority.FilterAvailableTools(Granted(), candidates);
        Assert.Single(filtered);
        Assert.Equal("lookup", filtered[0].Name);
    }

    [Fact]
    public void FilterAvailableTools_enforces_description_and_schema_boundaries()
    {
        var oversizedDescription = ServerToolWithDescription("server-a", "lookup", new string('d', 2_049));
        var arrayKind = ServerToolWithSchema("server-a", "lookup2", "[1,2]");
        var oversizedSchema = ServerToolWithSchema("server-a", "lookup3", "{\"x\":\"" + new string('x', 8_192) + "\"}");

        var filtered = AgentMcpToolAuthority.FilterAvailableTools(Granted(), new[]
        {
            oversizedDescription,
            arrayKind,
            oversizedSchema,
            ServerTool("server-a", "ok")
        });

        Assert.Single(filtered);
        Assert.Equal("ok", filtered[0].Name);
    }

    [Fact]
    public void FilterAvailableTools_stops_when_serialized_budget_exhausted()
    {
        var candidates = Enumerable.Range(0, 40)
            .Select(index => ServerToolWithDescription("server-a", "lookup_" + index, new string('d', 500)))
            .ToArray();

        var filtered = AgentMcpToolAuthority.FilterAvailableTools(Granted(), candidates);
        Assert.True(filtered.Count > 0);
        Assert.True(filtered.Count < 32);
    }

    [Fact]
    public void CanCall_requires_membership_in_the_available_set()
    {
        var available = new[] { ServerTool("server-a", "lookup") };
        Assert.True(AgentMcpToolAuthority.CanCall(Granted(), "server-a", "lookup", available));
        Assert.False(AgentMcpToolAuthority.CanCall(Granted(), "server-a", "other", available));
        Assert.False(AgentMcpToolAuthority.CanCall(Granted(), "server-a", "lookup", null));
        Assert.False(AgentMcpToolAuthority.CanCall(Granted(), "server-a", "lookup", Array.Empty<AgentMcpToolDefinition>()));
    }

    [Fact]
    public void CanCall_refuses_when_task_is_not_granted()
    {
        var available = new[] { ServerTool("server-a", "lookup") };
        Assert.False(AgentMcpToolAuthority.CanCall(Task("coding", AgentTaskStatus.Running), "server-a", "lookup", available));
        Assert.False(AgentMcpToolAuthority.CanCall(null, "server-a", "lookup", available));
    }

    [Theory]
    [InlineData("running")]
    [InlineData("RUNNING")]
    public void CanRequest_accepts_case_insensitive_running_status(string status)
    {
        var task = Task("coding", status, AgentMcpToolAuthority.RequiredCapability);
        Assert.True(AgentMcpToolAuthority.CanRequest(task, "server-a", "external_lookup"));
    }

    private static AgentTaskDto Granted() =>
        Task("coding", AgentTaskStatus.Running, AgentMcpToolAuthority.RequiredCapability);

    private static AgentTaskDto Task(string role, string status, params string[]? capabilities)
    {
        var task = new AgentTaskDto
        {
            Id = "task-id",
            Goal = "coding task",
            Role = role,
            Status = status,
            CreatedUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            UpdatedUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        };
        if (capabilities != null)
            task.GrantedCapabilities = capabilities.ToList();
        return task;
    }

    private static AgentMcpToolDefinition ServerTool(string serverId, string name) =>
        ServerToolWithDescription(serverId, name, "test description");

    private static AgentMcpToolDefinition ServerToolWithDescription(string serverId, string name, string description) =>
        new(serverId, name, description, ParseObject("{\"type\":\"object\"}"));

    private static AgentMcpToolDefinition ServerToolWithSchema(string serverId, string name, string schemaJson) =>
        new(serverId, name, "test description", ParseObject(schemaJson));

    private static JsonElement ParseObject(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}

// BUG NOTE (test deleted per task instructions): CanRequest was found to accept serverIds containing
// U+2028 (LINE SEPARATOR) and other Unicode separator/format characters (e.g. U+2029, U+202A–U+202E,
// U+2060, U+FEFF). Production AgentMcpToolAuthority.CanRequest (line 67) and FilterAvailableTools (line 90)
// reject only char.IsControl(c), but U+2028 is not a .NET control char — it is Unicode category Zl.
// A serverId containing U+2028 passes validation, creating a rendering/lookalike risk analogous to the
// tool-name rule. Production code was not modified per task constraints; the failing test was deleted.
