using System.Text.Json.Nodes;
using PaRuX.Intelligence;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class ChatPromptBudgetTests
{
    [Fact]
    public void Tool_and_structured_output_definitions_are_both_fixed_prompt_material()
    {
        var tools = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = "read_file",
                    ["description"] = "Read a repository file",
                    ["parameters"] = new JsonObject { ["type"] = "object" }
                }
            }
        };
        var structured = new LocalStructuredOutputRequest(
            "answer",
            """{"type":"object","properties":{"result":{"type":"string"}},"required":["result"]}""");

        IReadOnlyList<string> fragments = ChatPromptBudget.BuildFixedFragments(tools, structured);

        Assert.Equal(2, fragments.Count);
        Assert.Contains("read_file", fragments[0], StringComparison.Ordinal);
        Assert.Contains("json_schema", fragments[1], StringComparison.Ordinal);
        Assert.Contains("result", fragments[1], StringComparison.Ordinal);
        Assert.True(ChatPromptBudget.CountFixedTokens(fragments, text => text.Length) > 0);
    }

    [Fact]
    public void Fixed_prompt_tokens_reduce_the_message_context_before_reply_reserve_is_applied()
    {
        Assert.Equal(6_000, ChatPromptBudget.EffectiveMessageContextTokens(
            physicalContextTokens: 8_192,
            fixedPromptTokens: 2_192,
            reservedForReplyTokens: 1_024));
    }

    [Fact]
    public void Fixed_prompt_that_leaves_no_safe_reply_room_fails_explicitly()
    {
        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            ChatPromptBudget.EffectiveMessageContextTokens(
                physicalContextTokens: 8_192,
                fixedPromptTokens: 7_500,
                reservedForReplyTokens: 1_024));

        Assert.Contains("no safe room", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Near_limit_conversation_compacts_when_tool_schema_cost_is_subtracted()
    {
        static int Count(string value) => value.Length;
        var turns = new List<ConversationWindowPlanner.Turn>
        {
            new("system", new string('s', 80)),
            new("user", new string('u', 120)),
            new("assistant", new string('a', 100))
        };

        ConversationWindowPlanner.WindowPlan withoutFixed = ConversationWindowPlanner.Fit(
            turns, contextTokens: 500, reservedForReplyTokens: 100, countTokens: Count);
        int effective = ChatPromptBudget.EffectiveMessageContextTokens(
            physicalContextTokens: 500, fixedPromptTokens: 180, reservedForReplyTokens: 100);
        ConversationWindowPlanner.WindowPlan withFixed = ConversationWindowPlanner.Fit(
            turns, contextTokens: effective, reservedForReplyTokens: 100, countTokens: Count);

        Assert.False(withoutFixed.Compacted);
        Assert.True(withFixed.Compacted);
        Assert.True(withFixed.EstimatedTokens <= withFixed.AvailableTokens);
    }

    [Fact]
    public void Exact_rendered_overage_tightens_message_budget_by_overage_and_margin()
    {
        int tightened = ChatPromptBudget.TightenMessageContextTokens(
            currentMessageContextTokens: 7_000,
            exactRenderedPromptTokens: 7_400,
            maximumRenderedPromptTokens: 7_168,
            reservedForReplyTokens: 1_024);

        Assert.Equal(7_000 - (7_400 - 7_168) - ChatPromptBudget.ExactCountSafetyMarginTokens, tightened);
        Assert.True(tightened < 7_000);
    }

    [Fact]
    public void Exact_rendered_count_that_already_fits_never_expands_or_changes_budget()
    {
        Assert.Equal(6_000, ChatPromptBudget.TightenMessageContextTokens(
            currentMessageContextTokens: 6_000,
            exactRenderedPromptTokens: 5_900,
            maximumRenderedPromptTokens: 6_000,
            reservedForReplyTokens: 1_024));
    }

    [Fact]
    public void Exact_rendered_overflow_that_cannot_preserve_reply_reserve_fails_explicitly()
    {
        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            ChatPromptBudget.TightenMessageContextTokens(
                currentMessageContextTokens: 1_300,
                exactRenderedPromptTokens: 4_000,
                maximumRenderedPromptTokens: 3_000,
                reservedForReplyTokens: 1_024));

        Assert.Contains("cannot compact", error.Message, StringComparison.OrdinalIgnoreCase);
    }
}
