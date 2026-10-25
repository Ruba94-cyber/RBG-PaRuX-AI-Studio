using System.Text;
using System.Text.Json.Nodes;

namespace PaRuX.Server;

/// <summary>
/// Prices prompt material that is not represented by ChatCompletionRequest.Messages but is still
/// rendered into the model context by the provider. Tool definitions and structured-output schemas
/// are the important cases: treating them as HTTP metadata rather than prompt tokens lets a request
/// that looked safe at the message layer overflow the physical context after chat-template
/// rendering. The helper is pure so near-limit behavior can be tested without a live runtime.
/// </summary>
internal static class ChatPromptBudget
{
    internal const int ExactCountSafetyMarginTokens = 64;

    /// <summary>
    /// Stand-in for the tool-use preamble and wrapper tags a chat template renders around a
    /// non-empty tool list. It is appended to the tool fragment so the runtime tokenizer prices it
    /// like real prompt text. Measured on Qwen3/Qwen3.5 GGUF through llama-server's rendered-prompt
    /// count, the schema JSON alone undercounted the rendered tool block by roughly 100 tokens.
    /// </summary>
    internal const string ToolTemplatePreamble =
        "# Tools\n\nYou may call one or more functions to assist with the user query.\n\n"
        + "You are provided with function signatures within <tools></tools> XML tags:\n<tools>\n</tools>\n\n"
        + "For each function call, return a json object with function name and arguments within "
        + "<tool_call></tool_call> XML tags:\n<tool_call>\n{\"name\": <function-name>, \"arguments\": <args-json-object>}\n</tool_call>";

    internal static IReadOnlyList<string> BuildFixedFragments(
        JsonArray? toolSchemas,
        LocalStructuredOutputRequest? structuredOutput)
    {
        var fragments = new List<string>(2);
        if (toolSchemas is { Count: > 0 })
        {
            // Chat templates render each tool through a spaced JSON filter (", " and ": "), one tool
            // per line, which costs more tokens than the compact transport JSON.
            var rendered = new StringBuilder();
            foreach (JsonNode? tool in toolSchemas)
                rendered.Append(SpaceJsonSeparators(tool?.ToJsonString() ?? "null")).Append('\n')
                    .Append(RenderXmlStyleTool(tool)).Append('\n');
            fragments.Add(rendered + ToolTemplatePreamble);
        }
        if (structuredOutput != null)
            fragments.Add(StructuredOutputJson.BuildOpenAiResponseFormat(structuredOutput).ToJsonString());
        return fragments;
    }

    /// <summary>
    /// Some templates (Qwen3.5) render tools as XML-tagged function/parameter blocks instead of
    /// JSON. Pricing that shape as well makes the fixed estimate an upper bound across both
    /// template families: it over-counts for JSON templates, which is the safe direction, and the
    /// runtime's rendered count remains authoritative whenever it is available.
    /// </summary>
    internal static string RenderXmlStyleTool(JsonNode? tool)
    {
        JsonNode? function = tool?["function"] ?? tool;
        var builder = new StringBuilder("<function>\n<name>")
            .Append(function?["name"]?.ToString()).Append("</name>\n<description>")
            .Append(function?["description"]?.ToString()).Append("</description>\n<parameters>\n");
        if (function?["parameters"]?["properties"] is JsonObject properties)
        {
            foreach (KeyValuePair<string, JsonNode?> property in properties)
            {
                builder.Append("<parameter>\n<name>").Append(property.Key).Append("</name>\n<type>")
                    .Append(property.Value?["type"]?.ToString()).Append("</type>\n<description>")
                    .Append(property.Value?["description"]?.ToString()).Append("</description>\n</parameter>\n");
            }
        }
        return builder.Append("</parameters>\n</function>").ToString();
    }

    internal static string SpaceJsonSeparators(string compactJson)
    {
        var builder = new StringBuilder(compactJson.Length + compactJson.Length / 8);
        bool inString = false, escaped = false;
        foreach (char c in compactJson)
        {
            builder.Append(c);
            if (inString)
            {
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == '"') inString = false;
                continue;
            }
            if (c == '"') inString = true;
            else if (c == ',' || c == ':') builder.Append(' ');
        }
        return builder.ToString();
    }

    internal static int CountFixedTokens(
        IEnumerable<string> fragments,
        Func<string, int> countTokens)
    {
        ArgumentNullException.ThrowIfNull(fragments);
        ArgumentNullException.ThrowIfNull(countTokens);
        long total = 0;
        foreach (string fragment in fragments)
        {
            if (string.IsNullOrEmpty(fragment)) continue;
            total += Math.Max(0, countTokens(fragment));
            if (total >= int.MaxValue) return int.MaxValue;
        }
        return (int)total;
    }

    /// <summary>
    /// Returns the context window that remains for chat messages after fixed prompt material. Reply
    /// reserve is validated here rather than silently passed a negative/zero message budget. A tool
    /// or schema set that cannot coexist with the requested reply must be explicit to the user.
    /// </summary>
    internal static int EffectiveMessageContextTokens(
        int physicalContextTokens,
        int fixedPromptTokens,
        int reservedForReplyTokens)
    {
        if (physicalContextTokens <= 0) return physicalContextTokens;
        fixedPromptTokens = Math.Max(0, fixedPromptTokens);
        reservedForReplyTokens = Math.Max(0, reservedForReplyTokens);
        int remaining = Math.Max(0, physicalContextTokens - fixedPromptTokens);
        if (remaining <= reservedForReplyTokens)
        {
            throw new InvalidDataException(
                "The selected tool/structured-output definitions consume about " + fixedPromptTokens.ToString("N0")
                + " tokens of a " + physicalContextTokens.ToString("N0")
                + "-token model context, leaving no safe room for both the conversation and the "
                + reservedForReplyTokens.ToString("N0") + "-token reply reserve. Reduce enabled tools/schema size or use a model with a larger context window.");
        }
        return remaining;
    }

    /// <summary>
    /// Tightens the planner's message-context ceiling after the serving engine has rendered the real
    /// chat template and reported a larger prompt than the estimate. The exact overage is removed,
    /// plus a small fixed margin so a newly synthesized digest/template boundary does not oscillate
    /// by a handful of tokens on the next verification pass. This method never enlarges a budget.
    /// </summary>
    internal static int TightenMessageContextTokens(
        int currentMessageContextTokens,
        int exactRenderedPromptTokens,
        int maximumRenderedPromptTokens,
        int reservedForReplyTokens)
    {
        if (currentMessageContextTokens <= 0)
            throw new ArgumentOutOfRangeException(nameof(currentMessageContextTokens));
        reservedForReplyTokens = Math.Max(0, reservedForReplyTokens);
        if (exactRenderedPromptTokens <= maximumRenderedPromptTokens)
            return currentMessageContextTokens;

        long overage = (long)exactRenderedPromptTokens - Math.Max(0, maximumRenderedPromptTokens);
        long tightened = (long)currentMessageContextTokens - overage - ExactCountSafetyMarginTokens;
        if (tightened <= reservedForReplyTokens)
        {
            throw new InvalidDataException(
                "The fully rendered prompt still requires " + exactRenderedPromptTokens.ToString("N0")
                + " tokens, but this model can safely accept only " + Math.Max(0, maximumRenderedPromptTokens).ToString("N0")
                + " input tokens while preserving the reply reserve. PaRuX cannot compact this request further without discarding required current context; reduce the prompt/tool schema or use a larger-context model.");
        }
        return (int)Math.Min(currentMessageContextTokens, tightened);
    }

    /// <summary>
    /// Safety margin reserved when a near-window prompt could not be verified against the serving
    /// runtime's rendered-prompt count (counter missing, failed, or the runtime has no exact
    /// counting path). Estimates can be off by far more than the fixed exact-count margin, so the
    /// margin scales with the window: 10% of the largest prompt the model may accept.
    /// </summary>
    internal static int UnverifiedSafetyMarginTokens(int maximumRenderedPromptTokens) =>
        Math.Max(ExactCountSafetyMarginTokens, Math.Max(0, maximumRenderedPromptTokens) / 10);

    /// <summary>
    /// Lowers the message-context ceiling so an unverified estimate lands at least the safety margin
    /// below the largest acceptable prompt. Returns false when the estimate already leaves that
    /// margin, or when no tighter ceiling can still hold the reply reserve (the caller keeps the
    /// plan and says honestly that it is unverified). Never enlarges a budget.
    /// </summary>
    internal static bool TryTightenForUnverifiedCount(
        int currentMessageContextTokens,
        int estimatedPromptTokens,
        int maximumRenderedPromptTokens,
        int reservedForReplyTokens,
        out int tightenedMessageContextTokens)
    {
        tightenedMessageContextTokens = currentMessageContextTokens;
        if (currentMessageContextTokens <= 0) return false;
        long ceiling = (long)maximumRenderedPromptTokens - UnverifiedSafetyMarginTokens(maximumRenderedPromptTokens);
        long overshoot = estimatedPromptTokens - ceiling;
        if (overshoot <= 0) return false;
        long tightened = currentMessageContextTokens - overshoot;
        if (tightened <= Math.Max(0, reservedForReplyTokens)) return false;
        tightenedMessageContextTokens = (int)Math.Min(currentMessageContextTokens, tightened);
        return tightenedMessageContextTokens < currentMessageContextTokens;
    }

    /// <summary>User-visible statement of how the final prompt size was obtained when it was not exact.</summary>
    internal static string DescribeUnverifiedCount(
        RuntimeTokenCounter.CountBasis basis, int marginTokens, bool tightened)
    {
        string source = basis switch
        {
            RuntimeTokenCounter.CountBasis.Tokenizer => "per-message tokenizer counts plus a template allowance",
            RuntimeTokenCounter.CountBasis.PartlyEstimated =>
                "tokenizer counts for some messages and a character-ratio estimate for the rest",
            _ => "a character-based estimate"
        };
        return "The final rendered prompt could not be verified by the runtime, so its size is based on "
            + source + " and is not exact; " + marginTokens.ToString("N0") + " tokens of safety margin are "
            + (tightened ? "enforced" : "targeted")
            + " against the context window.";
    }
}
