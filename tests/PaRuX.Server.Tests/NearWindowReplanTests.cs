using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using PaRuX.Contracts;
using PaRuX.Intelligence;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// BIC-11: near-window re-planning against the runtime's exact rendered-prompt count, the 413 (not
/// 503) outcome when no plan can fit, direct-engine pricing of tool/schema blocks, and a live model
/// swap behind the stable local-default alias never reusing the other model's tokenizer counts. All
/// tokenizers are fakes; no runtime, model or network is involved.
/// </summary>
public sealed class NearWindowReplanTests
{
    private const string Endpoint = "http://127.0.0.1:1234";
    private const int Window = 4_000;                 // reply reserve is 1,024 -> 2,976 prompt tokens
    private const int MaxPrompt = Window - 1_024;
    private const int TemplateSlack = 400;            // chat template + role markers the per-text counts miss

    private sealed class FakeTokenizerHandler : HttpMessageHandler
    {
        public Func<string, int> Tokenizer = text => text.Length;
        public int Requests;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Requests);
            using JsonDocument body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            int count = Tokenizer(body.RootElement.GetProperty("content").GetString() ?? string.Empty);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(new { tokens = Enumerable.Range(0, count).ToArray() }),
                    System.Text.Encoding.UTF8, "application/json")
            };
        }
    }

    private static ChatCompletionRequest Conversation(params int[] lengths)
    {
        var messages = new List<ChatCompletionMessage>();
        for (int i = 0; i < lengths.Length; i++)
        {
            bool last = i == lengths.Length - 1;
            messages.Add(new ChatCompletionMessage
            {
                Role = last || i % 2 == 0 ? "user" : "assistant",
                Content = new string((char)('a' + (i % 26)), lengths[i])
            });
        }
        return new ChatCompletionRequest { Model = "local-default", Messages = messages };
    }

    private static int Rendered(ChatCompletionRequest request, Func<string, int> tokenizer) =>
        request.Messages.Sum(m => tokenizer(m.Content ?? string.Empty) + RuntimeTokenCounter.PerMessageTemplateOverheadTokens)
        + TemplateSlack;

    private static Task<ConversationWindowPlanner.WindowPlan?> Plan(
        ChatCompletionRequest request,
        RuntimeTokenCounter counter,
        Func<string> identity,
        Func<CancellationToken, Task<int?>> countFinal,
        JsonArray? tools = null,
        LocalStructuredOutputRequest? structured = null,
        bool directEngine = false,
        Func<IEnumerable<string>, CancellationToken, Task<RuntimeTokenCounter.PreparedCounter>>? prepare = null) =>
        ChatProxyService.FitConversationWindowCoreAsync(
            request, tools ?? new JsonArray(), structured, Window, directEngine,
            prepare ?? ((texts, ct) => counter.PrepareWithBasisAsync(Endpoint, "local-default", identity(), texts, ct)),
            countFinal, CancellationToken.None);

    private const string IdentityA = "managed|model=A|backend=text-cuda;build=x;pid=100;start=1";
    private const string IdentityB = "managed|model=B|backend=text-cuda;build=x;pid=200;start=2";

    [Fact]
    public async Task Prompt_that_fits_by_the_character_bound_but_not_once_rendered_is_replanned_until_the_exact_count_fits()
    {
        var handler = new FakeTokenizerHandler();
        var counter = new RuntimeTokenCounter(new HttpClient(handler));
        // 2,640 characters: provably under the 2,976 limit by the character bound, over it once the
        // runtime renders the template. The planner's own estimator prices this at ~900 tokens, so
        // only calibration to the exact count can make a re-plan converge.
        ChatCompletionRequest request = Conversation(500, 540, 500, 560, 540);
        Assert.True(request.Messages.Sum(m => m.Content!.Length) < MaxPrompt);
        Assert.True(Rendered(request, handler.Tokenizer) > MaxPrompt);

        var rendered = new List<int>();
        ConversationWindowPlanner.WindowPlan? plan = await Plan(request, counter, () => IdentityA, _ =>
        {
            int count = Rendered(request, handler.Tokenizer);
            rendered.Add(count);
            return Task.FromResult<int?>(count);
        });

        Assert.NotNull(plan);
        Assert.True(rendered.Count >= 2, "the exact count must be re-taken after the plan was tightened");
        Assert.True(rendered[0] > MaxPrompt);
        Assert.True(Rendered(request, handler.Tokenizer) <= MaxPrompt, "the shipped prompt must fit the window");
        Assert.True(plan!.Compacted);
        Assert.Equal(new string((char)('a' + 4), 540), request.Messages[^1].Content); // newest request is never dropped
    }

    [Fact]
    public async Task Prompt_that_cannot_fit_even_after_replanning_is_a_typed_413_never_a_503()
    {
        var handler = new FakeTokenizerHandler();
        var counter = new RuntimeTokenCounter(new HttpClient(handler));
        ChatCompletionRequest request = Conversation(820, 820, 820); // near the window, so it is verified exactly

        // The runtime renders 2,900 tokens more than any history the planner can drop.
        PromptExceedsContextException failure = await Assert.ThrowsAsync<PromptExceedsContextException>(() =>
            Plan(request, counter, () => IdentityA, _ => Task.FromResult<int?>(Rendered(request, handler.Tokenizer) + 2_900)));
        Assert.Contains("fully rendered", failure.Message, StringComparison.OrdinalIgnoreCase);
        await AssertIs413Async(failure);
    }

    [Fact]
    public async Task Overflow_without_an_exact_count_is_still_a_413_labelled_as_an_estimate()
    {
        var handler = new FakeTokenizerHandler();
        var counter = new RuntimeTokenCounter(new HttpClient(handler));
        // A system prompt that alone fills the window: nothing can be dropped to make room.
        var request = new ChatCompletionRequest
        {
            Model = "local-default",
            Messages = new List<ChatCompletionMessage>
            {
                new() { Role = "system", Content = new string('s', 6_000) },
                new() { Role = "user", Content = "hello" }
            }
        };

        PromptExceedsContextException failure = await Assert.ThrowsAsync<PromptExceedsContextException>(() =>
            Plan(request, counter, () => IdentityA, _ => Task.FromResult<int?>(null)));
        Assert.Contains("estimated", failure.Message, StringComparison.OrdinalIgnoreCase);
        await AssertIs413Async(failure);
    }

    [Fact]
    public async Task Tools_that_exhaust_the_window_are_a_413_not_a_503()
    {
        var handler = new FakeTokenizerHandler();
        var counter = new RuntimeTokenCounter(new HttpClient(handler));
        ChatCompletionRequest request = Conversation(200);
        JsonArray tools = HugeToolSchemas(Window);

        PromptExceedsContextException failure = await Assert.ThrowsAsync<PromptExceedsContextException>(() =>
            Plan(request, counter, () => IdentityA, _ => Task.FromResult<int?>(null), tools));
        await AssertIs413Async(failure);
    }

    [Fact]
    public async Task Direct_engine_prices_the_schema_block_and_lets_the_engines_exact_count_decide()
    {
        var handler = new FakeTokenizerHandler();
        var counter = new RuntimeTokenCounter(new HttpClient(handler));
        Func<IEnumerable<string>, CancellationToken, Task<RuntimeTokenCounter.PreparedCounter>> noProviderTokenizer =
            (_, _) => throw new InvalidOperationException("a direct engine is counted by the engine, never the provider tokenizer");
        const int schemaTokensInRenderedPrompt = 1_000;

        // 2,000 characters of history fits comfortably on its own.
        ChatCompletionRequest plain = Conversation(500, 500, 500, 500);
        int plainCalls = 0;
        ConversationWindowPlanner.WindowPlan? withoutSchema = await Plan(plain, counter, () => IdentityA,
            _ => { plainCalls++; return Task.FromResult<int?>(Rendered(plain, handler.Tokenizer)); },
            directEngine: true, prepare: noProviderTokenizer);
        Assert.False(withoutSchema!.Compacted);
        Assert.Equal(0, handler.Requests);

        // The same history plus a rendered response-format block: the engine counts the schema, so the
        // exact total exceeds the window and the history must be re-planned down.
        ChatCompletionRequest withSchema = Conversation(500, 500, 500, 500);
        var schema = new LocalStructuredOutputRequest("answer", HugeObjectSchema(100));
        int schemaCalls = 0;
        ConversationWindowPlanner.WindowPlan? planned = await Plan(withSchema, counter, () => IdentityA,
            _ => { schemaCalls++; return Task.FromResult<int?>(Rendered(withSchema, handler.Tokenizer) + schemaTokensInRenderedPrompt); },
            structured: schema, directEngine: true, prepare: noProviderTokenizer);

        Assert.True(schemaCalls >= 1, "the engine's exact rendered count must be consulted when a schema is rendered");
        Assert.True(planned!.Compacted, "the schema block must shrink the history budget");
        Assert.True(Rendered(withSchema, handler.Tokenizer) + schemaTokensInRenderedPrompt <= MaxPrompt);
        Assert.Equal(0, plainCalls); // far from the window with no fixed blocks: no exact count is needed
    }

    [Fact]
    public async Task Model_swap_behind_the_local_default_alias_never_reuses_the_other_models_counts()
    {
        var handler = new FakeTokenizerHandler();
        var counter = new RuntimeTokenCounter(new HttpClient(handler));
        string identity = IdentityA;
        Func<string, int> tokenizerA = text => text.Length / 2;   // an efficient vocabulary
        Func<string, int> tokenizerB = text => text.Length;       // 1 token per character
        Func<string, int> active = tokenizerA;
        handler.Tokenizer = text => active(text);

        // 3,040 characters: over the character bound, so the runtime tokenizer is consulted.
        // Model A prices it at 1,520 tokens -> fits uncompacted.
        ChatCompletionRequest underA = Conversation(760, 760, 760, 760);
        ConversationWindowPlanner.WindowPlan? planA = await Plan(underA, counter, () => identity,
            _ => Task.FromResult<int?>(Rendered(underA, active)));
        Assert.False(planA!.Compacted);
        int tokenizeCallsAfterA = handler.Requests;
        Assert.True(tokenizeCallsAfterA > 0, "the near-window prompt must be priced by the live tokenizer");

        // Live swap to model B under the same alias with the same text: B needs 3,040 tokens.
        identity = IdentityB;
        active = tokenizerB;
        ChatCompletionRequest underB = Conversation(760, 760, 760, 760);
        ConversationWindowPlanner.WindowPlan? planB = await Plan(underB, counter, () => identity,
            _ => Task.FromResult<int?>(Rendered(underB, active)));

        Assert.True(handler.Requests > tokenizeCallsAfterA, "model B's text must be tokenized by model B, not served from A's cache");
        Assert.True(planB!.Compacted, "a stale 1,520-token count from model A would have shipped a 3,040-token prompt");
        Assert.True(Rendered(underB, tokenizerB) <= MaxPrompt);

        // Swapping back is priced by A's own generation again (its counts are still valid).
        identity = IdentityA;
        active = tokenizerA;
        ChatCompletionRequest backToA = Conversation(760, 760, 760, 760);
        ConversationWindowPlanner.WindowPlan? replanA = await Plan(backToA, counter, () => identity,
            _ => Task.FromResult<int?>(Rendered(backToA, active)));
        Assert.False(replanA!.Compacted);
    }

    [Fact]
    public async Task Unresolved_backend_identity_is_not_cached_across_a_swap_with_the_same_alias()
    {
        var handler = new FakeTokenizerHandler();
        var counter = new RuntimeTokenCounter(new HttpClient(handler));
        const string unresolved = "managed-alias:local-default|model=unresolved|backend=unresolved";
        Func<string, int> active = text => text.Length / 2;
        handler.Tokenizer = text => active(text);

        ChatCompletionRequest first = Conversation(760, 760, 760, 760);
        ConversationWindowPlanner.WindowPlan? planFirst = await Plan(first, counter, () => unresolved,
            _ => Task.FromResult<int?>(Rendered(first, active)));
        Assert.False(planFirst!.Compacted);

        active = text => text.Length;
        ChatCompletionRequest second = Conversation(760, 760, 760, 760);
        ConversationWindowPlanner.WindowPlan? planSecond = await Plan(second, counter, () => unresolved,
            _ => Task.FromResult<int?>(Rendered(second, active)));
        Assert.True(planSecond!.Compacted);
    }

    private static async Task AssertIs413Async(PromptExceedsContextException failure)
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        await ChatProxyFailureResponse.WriteExceptionAsync(context.Response, failure, CancellationToken.None);
        Assert.Equal(StatusCodes.Status413PayloadTooLarge, context.Response.StatusCode);
        context.Response.Body.Position = 0;
        using JsonDocument body = await JsonDocument.ParseAsync(context.Response.Body);
        Assert.Equal("prompt_exceeds_context", body.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    private static JsonArray HugeToolSchemas(int approximateTokens)
    {
        var properties = new JsonObject();
        for (int i = 0; i < approximateTokens / 2; i++)
            properties["parameter_number_" + i] = new JsonObject { ["type"] = "string", ["description"] = "A documented parameter." };
        return new JsonArray(new JsonObject
        {
            ["type"] = "function",
            ["function"] = new JsonObject
            {
                ["name"] = "huge",
                ["description"] = "A tool whose schema alone exhausts the window.",
                ["parameters"] = new JsonObject { ["type"] = "object", ["properties"] = properties }
            }
        });
    }

    private static string HugeObjectSchema(int properties)
    {
        var props = new JsonObject();
        for (int i = 0; i < properties; i++) props["field_" + i] = new JsonObject { ["type"] = "string" };
        return new JsonObject { ["type"] = "object", ["properties"] = props }.ToJsonString();
    }
}
