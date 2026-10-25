using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using PaRuX.Services;
using Xunit;

// Characterisation of the action JSON parser extracted from CodingAgentService in 92788c84.
// LegacyParser is the pre-refactor TryParseAction body from 5ceb95e2, copied verbatim (only the
// method name changed) and used as the oracle. Every input must yield the same action payload and
// the same parse error from the extracted CodingAgentActionJson.TryParse. Remove the oracle once
// the extraction has soaked.
public sealed class CodingAgentActionJsonCharacterizationTests
{
    private static readonly JsonSerializerOptions DumpOptions = new(JsonSerializerDefaults.Web);

    private static readonly string[] Responses =
    {
        "{\"action\":\"read_file\",\"path\":\"src/App.cs\"}",
        "```json\n{\"action\":\"read_file\",\"path\":\"src/App.cs\",\"items\":[1,2,],}\n```",
        "```json\n{\"action\":\"read_file\"}\n```\n{\"action\":\"run_command\",\"command\":\"example\"}",
        "{\"path\":\"src/App.cs\",\"actionName\":\"read_file\"}",
        "{\"action\":\"unknown_action\"}",
        "{\"action\":\"grep_search\",\"text\":\"needle\"}",
        "{\"action\":\"glob_search\",\"search\":\"s-first\",\"find\":\"f-second\"}",
        "{\"action\":\"search_artifact\",\"find\":\"f-only\"}",
        "{\"action\":\"grep_search\",\"pattern\":\"explicit\",\"text\":\"fallback\"}",
        "{\"action\":\"update_plan\",\"plan\":\"step one\"}",
        "{\"action\":\"update_plan\",\"goal\":\"G\",\"children\":[{\"goal\":\"c1\",\"role\":\"r1\"},{\"goal\":\"\",\"role\":\"x\"},null]}",
        "{\"action\":\"update_plan\",\"text\":\"T\"}",
        "{\"action\":\"update_plan\",\"summary\":\"S\"}",
        "{\"action\":\"update_plan\",\"content\":\"C\",\"plan\":\"P\"}",
        "{\"action\":\"update_plan\",\"goal\":\"G2\"}",
        "{\"action\":\"replace_range\",\"search\":\"s\",\"replace\":\"r\"}",
        "{\"action\":\"replace_range\",\"find\":\"f\",\"replacement\":\"rp\"}",
        "{\"action\":\"replace_range\",\"target\":\"t\",\"oldText\":\"keep\",\"newText\":\"n2\"}",
        "{\"action\":\"write_file\",\"path\":\"p\",\"text\":\"body\"}",
        "{\"action\":\"create_file\",\"path\":\"p\",\"content\":null,\"text\":\"x\"}",
        "{\"action\":\"run_command\",\"text\":\"dotnet build\"}",
        "{\"action\":\"RUN_COMMAND\",\"text\":\"dotnet build\",\"command\":\"\"}",
        "{\"action\":\"run_command\",\"command\":\"dotnet test\",\"text\":\"other\"}",
        "{\"action\":\"read_file_lines\",\"path\":\"p\",\"offset\":5}",
        "{\"action\":\"read_file_lines\",\"path\":\"p\",\"offset\":5,\"line\":0}",
        "{\"action\":\"read_file_lines\",\"path\":\"p\",\"offset\":5,\"line\":9}",
        "{\"action\":\"read_file_lines\",\"path\":\"p\",\"offset\":0}",
        "{\"action\":\"read_file_lines\",\"path\":\"p\",\"offset\":-3}",
        "{\"action\":\"read_file_lines\",\"path\":\"p\",\"line\":null,\"offset\":2}",
        "{\"action\":\"read_file\",\"count\":\"nan\"}",
        "{\"action\":\"read_file\",\"regex\":\"yes\"}",
        "{\"action\":\"write_file\",\"path\":\"p\",\"content\":\"x\",\"allowOther\":false,\"options\":[\"a\",\"b\"],\"requests\":null,\"children\":null}",
        "{\"action\":\"ui_invoke\",\"selector\":\"btn-send\"}",
        "{\"action\":\"done\",\"summary\":\"ok\"}",
        "{\"action\":\"ask_user_question\",\"question\":\"Q?\",\"options\":[\"y\",\"n\"],\"allowOther\":true}",
        "{\"action\":\"delegate_tasks\",\"continueParent\":true,\"children\":[{\"goal\":\"g\",\"role\":\"r\",\"capabilities\":[\"read\"],\"contextRefs\":[\"c\"],\"maxRepairAttempts\":2}]}",
        "null",
        "[]",
        "\"text\"",
        "   ",
        "",
        "not json at all",
    };

    public static IEnumerable<object[]> Corpus()
    {
        foreach (string response in Responses)
            yield return new object[] { response };
    }

    [Theory]
    [MemberData(nameof(Corpus))]
    public void Extracted_parser_matches_the_pre_refactor_parser(string response)
    {
        CodingAgentAction? legacy = LegacyParser.Parse(response, out string legacyError);
        CodingAgentAction? extracted = CodingAgentActionJson.TryParse(response, out string extractedError);

        Assert.Equal(legacyError, extractedError);
        Assert.Equal(Dump(legacy), Dump(extracted));
    }

    [Fact]
    public void Single_argument_overload_agrees_with_the_out_parameter_overload()
    {
        foreach (string response in Responses)
        {
            CodingAgentAction? withError = CodingAgentActionJson.TryParse(response, out _);
            Assert.Equal(Dump(withError), Dump(CodingAgentActionJson.TryParse(response)));
        }
    }

    private static string Dump(CodingAgentAction? action) =>
        action == null ? "<null>" : JsonSerializer.Serialize(action, DumpOptions);

    private static class LegacyParser
    {
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

        internal static CodingAgentAction? Parse(string text) => Parse(text, out _);

        internal static CodingAgentAction? Parse(string text, out string parseError)
        {
            if (!CodingAgentActionJsonRepair.TryRepair(text, out string candidate, out parseError))
                return null;
            try
            {
                CodingAgentAction? action = JsonSerializer.Deserialize<CodingAgentAction>(candidate, JsonOptions);
                if (action == null)
                    parseError = "The JSON object did not produce an action payload. Return one object with an action field.";
                if (action != null)
                {
                    parseError = string.Empty;
                    // Small local models often put a search query in the schema's generic "text"
                    // field. For search actions it has the same meaning as "pattern"; accepting it
                    // avoids repeated multi-minute inference retries without broadening file authority.
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
                }
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
