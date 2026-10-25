using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using PaRuX.Contracts;

namespace PaRuX.Server.Orchestration;

public sealed record AgentMcpToolDefinition(
    string ServerId,
    string Name,
    string Description,
    JsonElement InputSchema);

public sealed class AgentMcpToolCallRequest
{
    public string ServerId { get; set; } = string.Empty;
    public string ToolName { get; set; } = string.Empty;
    public JsonElement Arguments { get; set; }
}

/// <summary>
/// Authority rules for exposing configured MCP tools to the desktop coding agent. A task must
/// carry an explicit capability grant, and external tools keep their server identity so routing
/// never falls back to the chat path's ambiguous bare-name lookup.
/// </summary>
public static class AgentMcpToolAuthority
{
    public const string RequiredCapability = "mcp.external";

    private static readonly string[] ReservedActionNameArray =
    {
        "update_plan", "list_directory", "read_file", "read_file_range", "read_file_lines",
        "grep_search", "glob_search", "list_artifacts", "read_artifact", "search_artifact",
        "git_status", "git_diff", "git_log", "replace_range", "write_file", "create_file",
        "delete_file", "rename_file", "run_command", "restore_packages", "web_search", "mcp_tool_call",
        "browser_navigate", "browser_dom", "browser_console", "browser_network", "browser_eval",
        "browser_click", "browser_type", "browser_assert", "browser_screenshot", "browser_reset",
        "ask_user", "delegate_task", "delegate_tasks", "collect_children", "batch_read", "done"
    };

    private static readonly HashSet<string> ReservedActionNames =
        new(ReservedActionNameArray, StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<string> ReservedCodingActionNames => Array.AsReadOnly(ReservedActionNameArray);

    public static bool CanUse(AgentTaskDto? task) =>
        task != null
        && string.Equals(task.Role, "coding", StringComparison.OrdinalIgnoreCase)
        && string.Equals(task.Status, AgentTaskStatus.Running, StringComparison.OrdinalIgnoreCase)
        && task.GrantedCapabilities?.Contains(RequiredCapability, StringComparer.OrdinalIgnoreCase) == true;

    public static bool IsReservedCodingActionName(string? name) =>
        !string.IsNullOrWhiteSpace(name) && ReservedActionNames.Contains(name);

    /// <summary>
    /// A tool name is plain printable ASCII without spaces. A lookalike of a built-in action (trailing
    /// space, zero-width or homoglyph characters) renders like the built-in name, so it is refused.
    /// </summary>
    public static bool IsPlainToolName(string? name) =>
        !string.IsNullOrEmpty(name) && name.All(character => character is >= '!' and <= '~');

    public static bool CanRequest(AgentTaskDto? task, string? serverId, string? toolName) =>
        CanUse(task)
        && !string.IsNullOrWhiteSpace(serverId)
        && serverId.Length <= 64
        && !serverId.Any(char.IsControl)
        && !string.IsNullOrWhiteSpace(toolName)
        && toolName.Length <= 128
        && IsPlainToolName(toolName)
        && !IsReservedCodingActionName(toolName);

    public static IReadOnlyList<AgentMcpToolDefinition> FilterAvailableTools(
        AgentTaskDto? task,
        IEnumerable<AgentMcpToolDefinition>? candidates)
    {
        if (!CanUse(task) || candidates == null) return Array.Empty<AgentMcpToolDefinition>();

        const int maximumTools = 32;
        const int maximumSerializedCharacters = 16_384;
        var result = new List<AgentMcpToolDefinition>(maximumTools);
        var identities = new HashSet<string>(StringComparer.Ordinal);
        int serializedCharacters = 2;

        foreach (AgentMcpToolDefinition? candidate in candidates)
        {
            if (candidate == null
                || string.IsNullOrWhiteSpace(candidate.ServerId)
                || candidate.ServerId.Length > 64
                || candidate.ServerId.Any(char.IsControl)
                || string.IsNullOrWhiteSpace(candidate.Name)
                || candidate.Name.Length > 128
                || !IsPlainToolName(candidate.Name)
                || IsReservedCodingActionName(candidate.Name)
                || candidate.Description == null
                || candidate.Description.Length > 2_048
                || candidate.InputSchema.ValueKind != JsonValueKind.Object
                || candidate.InputSchema.GetRawText().Length > 8_192)
                continue;

            string identity = candidate.ServerId + "\0" + candidate.Name;
            if (!identities.Add(identity)) continue;

            int length = JsonSerializer.Serialize(candidate).Length + (result.Count == 0 ? 0 : 1);
            if (result.Count >= maximumTools || serializedCharacters + length > maximumSerializedCharacters)
                break;

            result.Add(candidate);
            serializedCharacters += length;
        }

        return result;
    }

    public static bool CanCall(
        AgentTaskDto? task,
        string? serverId,
        string? toolName,
        IEnumerable<AgentMcpToolDefinition>? currentlyAvailableTools) =>
        CanRequest(task, serverId, toolName)
        && currentlyAvailableTools?.Any(tool =>
            string.Equals(tool.ServerId, serverId, StringComparison.Ordinal)
            && string.Equals(tool.Name, toolName, StringComparison.Ordinal)) == true;
}
