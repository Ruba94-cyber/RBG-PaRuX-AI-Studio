using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PaRuX.Intelligence;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Opt-in real-runtime check for BIC-11. It runs only when PARUX_BIC11_LLAMA_ENDPOINT points at a
/// llama.cpp server that is already serving a model (it never starts or stops anything), and
/// otherwise returns immediately without asserting anything - the regular suite therefore does
/// not claim live evidence it did not gather. PARUX_BIC11_LOG optionally receives one JSON line
/// of measurements.
///
/// It compares the per-text pricing the chat path uses (tokenizer count + template allowance) with
/// the runtime's own rendered-prompt count for messages, tools and a near-window conversation.
/// </summary>
public sealed class LiveTokenizerBudgetTests
{
    private static readonly string[] UnicodeCorpus =
    {
        "Hello, world",
        "Ciao a tutti: l'affidabilità è più importante della velocità.",
        "日本語のテキストと絵文字 😀🚀 مرحبا привет",
        "commit 5cfecc4a2b2d585741e2a20eb5a87 token=EED4C8642250FD46 status=ok",
        "var x = new Dictionary<string, List<int>> { [\"a\"] = new() { 1, 2, 3 } }; // C#"
    };

    private static JsonArray Tools() => new(
        new JsonObject
        {
            ["type"] = "function",
            ["function"] = new JsonObject
            {
                ["name"] = "read_file",
                ["description"] = "Read a UTF-8 text file from the workspace and return its contents with line numbers.",
                ["parameters"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["path"] = new JsonObject { ["type"] = "string", ["description"] = "Workspace-relative path." },
                        ["startLine"] = new JsonObject { ["type"] = "integer" },
                        ["endLine"] = new JsonObject { ["type"] = "integer" }
                    },
                    ["required"] = new JsonArray("path")
                }
            }
        },
        new JsonObject
        {
            ["type"] = "function",
            ["function"] = new JsonObject
            {
                ["name"] = "search_text",
                ["description"] = "Search workspace files for a regular expression.",
                ["parameters"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject { ["pattern"] = new JsonObject { ["type"] = "string" } },
                    ["required"] = new JsonArray("pattern")
                }
            }
        });

    private static JsonObject Payload(IEnumerable<(string Role, string Content)> messages, JsonArray? tools)
    {
        var array = new JsonArray();
        foreach ((string role, string content) in messages)
            array.Add(new JsonObject { ["role"] = role, ["content"] = content });
        var payload = new JsonObject { ["model"] = "local-default", ["messages"] = array };
        if (tools != null) payload["tools"] = tools.DeepClone();
        return payload;
    }

    [Fact]
    public async Task Per_text_pricing_never_undercounts_the_runtimes_rendered_prompt()
    {
        string? endpoint = Environment.GetEnvironmentVariable("PARUX_BIC11_LLAMA_ENDPOINT");
        if (string.IsNullOrWhiteSpace(endpoint)) return;

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        var counter = new RuntimeTokenCounter(http);
        const string identity = "live-test-identity";

        // 1. Messages: per-text tokenizer count + template allowance vs the rendered prompt.
        var messages = new List<(string, string)> { ("system", "You are a careful assistant.") };
        foreach (string text in UnicodeCorpus)
        {
            messages.Add(("user", text));
            messages.Add(("assistant", "Noted: " + text));
        }
        messages.Add(("user", "Summarise the above in one line."));
        string[] texts = messages.Select(message => message.Item2).ToArray();

        RuntimeTokenCounter.PreparedCounter prepared = await counter.PrepareWithBasisAsync(
            endpoint, "local-default", identity, texts, CancellationToken.None);
        Assert.Equal(RuntimeTokenCounter.CountBasis.Tokenizer, prepared.Basis);

        int? rendered = await counter.CountRenderedChatPromptCoreAsync(
            endpoint, Payload(messages, null), CancellationToken.None);
        Assert.True(rendered.HasValue, "runtime did not answer the rendered-prompt count");
        int priced = texts.Sum(prepared.Count);
        Assert.True(priced >= rendered!.Value,
            $"per-text pricing {priced} undercounts the rendered prompt {rendered} for {messages.Count} messages");
        double overheadPerMessage = (rendered.Value - texts.Sum(text => prepared.Count(text) - RuntimeTokenCounter.PerMessageTemplateOverheadTokens))
            / (double)messages.Count;

        // 2. Tools: fixed fragment priced like ChatPromptBudget does vs the rendered delta.
        JsonArray tools = Tools();
        int? withTools = await counter.CountRenderedChatPromptCoreAsync(
            endpoint, Payload(messages, tools), CancellationToken.None);
        Assert.True(withTools.HasValue);
        int renderedToolDelta = withTools!.Value - rendered.Value;
        IReadOnlyList<string> fixedFragments = ChatPromptBudget.BuildFixedFragments(tools, null);
        RuntimeTokenCounter.PreparedCounter fixedCounter = await counter.PrepareWithBasisAsync(
            endpoint, "local-default", identity, fixedFragments, CancellationToken.None);
        int pricedTools = ChatPromptBudget.CountFixedTokens(fixedFragments, fixedCounter.Count);
        int pricedToolsWithAllowance = pricedTools;

        // 3. Near window: a long history compacted by the planner, re-priced with the real
        // tokenizer, then verified by the runtime. The rendered result must fit the window the
        // plan was built for (default llama-server context is read from /props).
        int window = await ReadContextAsync(http, endpoint);
        int reply = 1024;
        var turns = new List<ConversationWindowPlanner.Turn> { new("system", "You are a careful assistant.") };
        for (int i = 0; i < 400; i++)
        {
            turns.Add(new("user", $"Decision {i}: module {i} must keep commit 5cfecc4a2b{i:x4} stable. " + UnicodeCorpus[i % UnicodeCorpus.Length]));
            turns.Add(new("assistant", "Acknowledged " + i + ". " + string.Join(' ', Enumerable.Repeat("verified and noted", 12))));
        }
        turns.Add(new("user", "Which commit must module 7 keep stable?"));
        string[] corpus = turns.Select(turn => turn.Content).ToArray();
        RuntimeTokenCounter.PreparedCounter first = await counter.PrepareWithBasisAsync(
            endpoint, "local-default", identity, corpus, CancellationToken.None);
        ConversationWindowPlanner.WindowPlan plan = ConversationWindowPlanner.Fit(turns, window, reply, first.Count);
        Assert.True(plan.Compacted);

        RuntimeTokenCounter.PreparedCounter repriced = await counter.PrepareWithBasisAsync(
            endpoint, "local-default", identity, plan.Messages.Select(message => message.Content), CancellationToken.None);
        int repricedTotal = plan.Messages.Sum(message => repriced.Count(message.Content));
        int? finalRendered = await counter.CountRenderedChatPromptCoreAsync(
            endpoint, Payload(plan.Messages.Select(message => (message.Role, message.Content)), null), CancellationToken.None);
        Assert.True(finalRendered.HasValue);
        Assert.True(finalRendered!.Value <= window - reply,
            $"near-window rendered prompt {finalRendered} exceeds {window - reply} (window {window}, reply {reply}); plan priced {plan.EstimatedTokens}, repriced {repricedTotal}");
        Assert.True(repricedTotal >= finalRendered.Value,
            $"re-priced plan {repricedTotal} undercounts rendered {finalRendered}");

        string? log = Environment.GetEnvironmentVariable("PARUX_BIC11_LOG");
        if (!string.IsNullOrWhiteSpace(log))
        {
            File.AppendAllText(log, JsonSerializer.Serialize(new
            {
                at = DateTimeOffset.UtcNow,
                window,
                messages = messages.Count,
                renderedMessages = rendered.Value,
                pricedMessages = priced,
                templateOverheadPerMessageObserved = Math.Round(overheadPerMessage, 2),
                templateAllowancePerMessage = RuntimeTokenCounter.PerMessageTemplateOverheadTokens,
                renderedToolDelta,
                pricedTools,
                pricedToolsWithAllowance,
                plannedTokens = plan.EstimatedTokens,
                repricedTotal,
                finalRendered = finalRendered.Value,
                keptTurns = plan.KeptTurns,
                compactedTurns = plan.CompactedTurns
            }) + Environment.NewLine, Encoding.UTF8);
        }

        // The rendered tool block is the schema plus the template's own tool-use preamble, so the
        // raw schema alone undercounts; the budget adds a fixed allowance for that preamble.
        Assert.True(pricedToolsWithAllowance >= renderedToolDelta,
            $"tool pricing {pricedTools} undercounts the rendered tool delta {renderedToolDelta}");
    }

    private static async Task<int> ReadContextAsync(HttpClient http, string endpoint)
    {
        using JsonDocument props = JsonDocument.Parse(await http.GetStringAsync(endpoint.TrimEnd('/') + "/props"));
        return props.RootElement.GetProperty("default_generation_settings").GetProperty("n_ctx").GetInt32();
    }
}
