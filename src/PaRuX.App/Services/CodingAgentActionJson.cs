using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PaRuX.Services
{
    internal sealed class CodingDelegationActionItem
    {
        [JsonPropertyName("goal")] public string Goal { get; set; } = string.Empty;
        [JsonPropertyName("role")] public string Role { get; set; } = string.Empty;
        [JsonPropertyName("capabilities")] public List<string>? Capabilities { get; set; }
        [JsonPropertyName("contextRefs")] public List<string>? ContextRefs { get; set; }
        [JsonPropertyName("maxRepairAttempts")] public int? MaxRepairAttempts { get; set; }
    }

    internal sealed class CodingAgentAction
    {
        [JsonPropertyName("action")] public string Action { get; set; } = string.Empty;
        [JsonPropertyName("mcpServerId")] public string McpServerId { get; set; } = string.Empty;
        [JsonPropertyName("mcpToolName")] public string McpToolName { get; set; } = string.Empty;
        [JsonPropertyName("mcpArguments")] public JsonElement? McpArguments { get; set; }
        [JsonPropertyName("path")] public string Path { get; set; } = string.Empty;
        [JsonPropertyName("newPath")] public string NewPath { get; set; } = string.Empty;
        [JsonPropertyName("content")] public string Content { get; set; } = string.Empty;
        [JsonPropertyName("oldText")] public string OldText { get; set; } = string.Empty;
        [JsonPropertyName("newText")] public string NewText { get; set; } = string.Empty;
        [JsonPropertyName("command")] public string Command { get; set; } = string.Empty;
        [JsonPropertyName("pattern")] public string Pattern { get; set; } = string.Empty;
        [JsonPropertyName("regex")] public bool Regex { get; set; }
        [JsonPropertyName("count")] public int? Count { get; set; }
        [JsonPropertyName("offset")] public int? Offset { get; set; }
        [JsonPropertyName("length")] public int? Length { get; set; }
        [JsonPropertyName("line")] public int? Line { get; set; }
        [JsonPropertyName("artifactId")] public string ArtifactId { get; set; } = string.Empty;
        [JsonPropertyName("summary")] public string Summary { get; set; } = string.Empty;
        [JsonPropertyName("url")] public string Url { get; set; } = string.Empty;
        [JsonPropertyName("expression")] public string Expression { get; set; } = string.Empty;
        [JsonPropertyName("selector")] public string Selector { get; set; } = string.Empty;
        [JsonPropertyName("text")] public string Text { get; set; } = string.Empty;
        [JsonPropertyName("goal")] public string Goal { get; set; } = string.Empty;
        [JsonPropertyName("role")] public string Role { get; set; } = string.Empty;
        [JsonPropertyName("ecosystem")] public string Ecosystem { get; set; } = string.Empty;
        [JsonPropertyName("capabilities")] public List<string>? Capabilities { get; set; }
        [JsonPropertyName("contextRefs")] public List<string>? ContextRefs { get; set; }
        [JsonPropertyName("maxRepairAttempts")] public int? MaxRepairAttempts { get; set; }
        [JsonPropertyName("children")] public List<CodingDelegationActionItem>? Children { get; set; }
        /// <summary>
        /// On delegate_task/delegate_tasks: keep this parent running while the (read-only) children
        /// run instead of pausing it. Core re-validates that every child is read-only.
        /// </summary>
        [JsonPropertyName("continueParent")] public bool ContinueParent { get; set; }
        [JsonPropertyName("requests")] public List<CodingBatchReadItem>? Requests { get; set; }
        [JsonPropertyName("question")] public string Question { get; set; } = string.Empty;
        [JsonPropertyName("options")] public List<string>? Options { get; set; }
        [JsonPropertyName("allowOther")] public bool AllowOther { get; set; } = true;
        [JsonPropertyName("plan")] public string Plan { get; set; } = string.Empty;
        [JsonPropertyName("search")] public string Search { get; set; } = string.Empty;
        [JsonPropertyName("find")] public string Find { get; set; } = string.Empty;
        [JsonPropertyName("target")] public string Target { get; set; } = string.Empty;
        [JsonPropertyName("replace")] public string Replace { get; set; } = string.Empty;
        [JsonPropertyName("replacement")] public string Replacement { get; set; } = string.Empty;
    }

    /// <summary>
    /// Parses one coding action. Syntax repair is delegated exactly once to the bounded JSON
    /// repair helper; this layer only deserializes the resulting object and applies existing
    /// action-specific compatibility aliases. It does not authorize action names.
    /// </summary>
    internal static class CodingAgentActionJson
    {
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

        internal static CodingAgentAction? TryParse(string? response) => TryParse(response, out _);

        internal static CodingAgentAction? TryParse(string? response, out string parseError)
        {
            if (!CodingAgentActionJsonRepair.TryRepair(response, out string candidate, out parseError))
                return null;

            try
            {
                CodingAgentAction? action = JsonSerializer.Deserialize<CodingAgentAction>(candidate, JsonOptions);
                if (action == null)
                {
                    parseError = "The JSON object did not produce an action payload. Return one object with an action field.";
                    return null;
                }

                parseError = string.Empty;
                // Small local models often put a search query in the schema's generic "text"
                // field. For search actions it has the same meaning as "pattern"; retain this
                // compatibility without changing the JSON field names or action authority.
                if (string.IsNullOrWhiteSpace(action.Pattern)
                    && action.Action is "search_artifact" or "grep_search" or "glob_search")
                {
                    if (!string.IsNullOrWhiteSpace(action.Text)) action.Pattern = action.Text;
                    else if (!string.IsNullOrWhiteSpace(action.Search)) action.Pattern = action.Search;
                    else if (!string.IsNullOrWhiteSpace(action.Find)) action.Pattern = action.Find;
                }

                // update_plan: local models often emit text, summary, plan or goal instead of content
                if (string.Equals(action.Action, "update_plan", StringComparison.OrdinalIgnoreCase))
                {
                    if (string.IsNullOrWhiteSpace(action.Content) && !string.IsNullOrWhiteSpace(action.Plan))
                        action.Content = action.Plan;
                    else if (string.IsNullOrWhiteSpace(action.Content) && !string.IsNullOrWhiteSpace(action.Goal))
                    {
                        action.Content = action.Goal;
                        if (action.Children != null && action.Children.Count > 0)
                        {
                            action.Content += "\n" + string.Join("\n", action.Children.Where(c => c != null && !string.IsNullOrWhiteSpace(c.Goal)).Select(c => $"- [{c.Role ?? "task"}] {c.Goal}"));
                        }
                    }
                    else if (string.IsNullOrWhiteSpace(action.Content) && !string.IsNullOrWhiteSpace(action.Text))
                        action.Content = action.Text;
                    else if (string.IsNullOrWhiteSpace(action.Content) && !string.IsNullOrWhiteSpace(action.Summary))
                        action.Content = action.Summary;
                }

                // replace_range: local models often emit search/find/target instead of oldText, and replace/replacement instead of newText
                if (string.Equals(action.Action, "replace_range", StringComparison.OrdinalIgnoreCase))
                {
                    if (string.IsNullOrWhiteSpace(action.OldText))
                    {
                        if (!string.IsNullOrWhiteSpace(action.Search)) action.OldText = action.Search;
                        else if (!string.IsNullOrWhiteSpace(action.Find)) action.OldText = action.Find;
                        else if (!string.IsNullOrWhiteSpace(action.Target)) action.OldText = action.Target;
                    }
                    if (string.IsNullOrWhiteSpace(action.NewText))
                    {
                        if (!string.IsNullOrWhiteSpace(action.Replace)) action.NewText = action.Replace;
                        else if (!string.IsNullOrWhiteSpace(action.Replacement)) action.NewText = action.Replacement;
                    }
                }

                // write_file / create_file: map text -> content if content is omitted
                if (action.Action is "write_file" or "create_file" && action.Content == null && !string.IsNullOrWhiteSpace(action.Text))
                    action.Content = action.Text;

                // run_command: map text -> command if command is omitted
                if (string.Equals(action.Action, "run_command", StringComparison.OrdinalIgnoreCase)
                    && string.IsNullOrWhiteSpace(action.Command) && !string.IsNullOrWhiteSpace(action.Text))
                    action.Command = action.Text;

                // read_file_lines: models sometimes pass the target line in offset instead of line
                if (string.Equals(action.Action, "read_file_lines", StringComparison.OrdinalIgnoreCase)
                    && (action.Line is null or < 1) && action.Offset != null && action.Offset > 0)
                    action.Line = action.Offset;

                return action;
            }
            catch (JsonException)
            {
                parseError = "The JSON object parsed, but its action fields had invalid value types. Follow the advertised schema.";
                return null;
            }
        }
    }
}
