using System.Net;
using System.Text.Json;
using PaRuX.Intelligence;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// The chat path counts prompt tokens with the served model's own tokenizer instead of the 3.0
/// characters-per-token heuristic, because measurement on the physical PC showed the heuristic is
/// wrong in both directions: 5.66 characters per token for English prose, 1.16 for git SHAs and hex
/// tokens. These tests pin the properties that make that safe to run on the chat path: counts are
/// real, scoped to the actual tokenizer/runtime generation, cached only inside that scope, and an
/// unreachable tokenizer degrades to the estimate instead of breaking Chat.
/// </summary>
public sealed class RuntimeTokenCounterTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<string, HttpResponseMessage> _respond;
        public int Requests { get; private set; }

        public StubHandler(Func<string, HttpResponseMessage> respond) => _respond = respond;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            string body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            using JsonDocument document = JsonDocument.Parse(body);
            return _respond(document.RootElement.GetProperty("content").GetString() ?? string.Empty);
        }
    }

    private static HttpResponseMessage Tokens(int count) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(
            JsonSerializer.Serialize(new { tokens = Enumerable.Range(0, count).ToArray() }),
            System.Text.Encoding.UTF8,
            "application/json")
    };

    [Fact]
    public async Task Exact_rendered_chat_count_includes_coding_json_schema_without_running_inference()
    {
        Uri? requested = null;
        string? sentBody = null;
        using var client = new HttpClient(new RenderedChatStub(async request =>
        {
            requested = request.RequestUri;
            sentBody = await request.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"input_tokens":4032}""")
            };
        }));
        var counter = new RuntimeTokenCounter(client);
        var prepared = System.Text.Json.Nodes.JsonNode.Parse(
            CodingRenderedPromptCountContract.BuildProviderPayload(new CodingRenderedPromptCountRequest
            {
                SystemPrompt = "Follow this schema",
                UserPrompt = "Inspect the repo",
                SchemaName = "parux_coding_action",
                JsonSchema = """{"type":"object","properties":{"action":{"type":"string"}}}"""
            }))!.AsObject();

        int? result = await counter.TryCountRenderedChatPromptAsync(
            LocalRuntimeService.CurrentManagedProviderEndpoint,
            prepared, CancellationToken.None);

        Assert.Equal(4032, result);
        Assert.EndsWith("/v1/chat/completions/input_tokens", requested!.AbsolutePath);
        Assert.Contains("parux_coding_action", sentBody);
        Assert.Contains("response_format", sentBody);
    }

    private sealed class RenderedChatStub(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            responder(request);
    }

    [Fact]
    public async Task The_runtime_count_is_used_rather_than_the_character_heuristic()
    {
        // 1.16 characters per token: the densest content measured, where the heuristic under-counts.
        var handler = new StubHandler(content => Tokens((int)(content.Length / 1.16)));
        var counter = new RuntimeTokenCounter(new HttpClient(handler));
        string hashes = string.Join(' ', Enumerable.Repeat("5cfecc4 EED4C8642250FD46", 40));

        Func<string, int> count = await counter.PrepareAsync(
            "http://127.0.0.1:1234", "qwen", new[] { hashes }, CancellationToken.None);

        Assert.True(
            count(hashes) > ConversationWindowPlanner.EstimateTokens(hashes),
            $"Runtime said {count(hashes):N0}, the heuristic {ConversationWindowPlanner.EstimateTokens(hashes):N0}.");
        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public async Task A_repeated_message_is_tokenized_once_so_a_long_conversation_pays_for_its_newest_turn()
    {
        var handler = new StubHandler(content => Tokens(content.Length));
        var counter = new RuntimeTokenCounter(new HttpClient(handler));
        var history = new[] { "first turn", "second turn", "third turn" };

        const string identity = "managed|model=m|backend=text-vulkan;build=one";
        await counter.PrepareAsync("http://127.0.0.1:1234", "qwen", identity, history, CancellationToken.None);
        await counter.PrepareAsync(
            "http://127.0.0.1:1234", "qwen", identity, history.Append("a brand new turn"), CancellationToken.None);

        Assert.Equal(history.Length + 1, handler.Requests);
    }

    [Fact]
    public async Task Counts_are_not_reused_across_models_because_the_tokenizer_changes_with_them()
    {
        var handler = new StubHandler(content => Tokens(content.Length));
        var counter = new RuntimeTokenCounter(new HttpClient(handler));

        await counter.PrepareAsync("http://127.0.0.1:1234", "qwen", new[] { "same text" }, CancellationToken.None);
        await counter.PrepareAsync("http://127.0.0.1:1234", "llama", new[] { "same text" }, CancellationToken.None);

        Assert.Equal(2, handler.Requests);
    }

    [Fact]
    public async Task Same_local_default_alias_does_not_reuse_counts_after_actual_tokenizer_identity_changes()
    {
        var handler = new StubHandler(content => Tokens(content.Length));
        var counter = new RuntimeTokenCounter(new HttpClient(handler));
        const string endpoint = "http://127.0.0.1:1234";
        const string alias = "local-default";
        const string text = "same prompt behind a stable API alias";

        await counter.PrepareAsync(endpoint, alias,
            "managed|model=qwen-rev-a|backend=text-vulkan;build=one", new[] { text }, CancellationToken.None);
        await counter.PrepareAsync(endpoint, alias,
            "managed|model=llama-rev-b|backend=text-vulkan;build=one", new[] { text }, CancellationToken.None);

        Assert.Equal(2, handler.Requests);
    }

    [Fact]
    public async Task Same_actual_tokenizer_identity_keeps_cache_reuse_even_when_called_again()
    {
        var handler = new StubHandler(content => Tokens(content.Length));
        var counter = new RuntimeTokenCounter(new HttpClient(handler));
        const string endpoint = "http://127.0.0.1:1234";
        const string alias = "local-default";
        const string identity = "managed|path=qwen.gguf|length=123|created=1|written=2|backend=text-vulkan;build=x";
        const string text = "repeat me";

        await counter.PrepareAsync(endpoint, alias, identity, new[] { text }, CancellationToken.None);
        await counter.PrepareAsync(endpoint, alias, identity, new[] { text }, CancellationToken.None);

        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public void Non_managed_provider_identity_remains_bound_to_its_provider_model_name()
    {
        string first = RuntimeTokenCounter.ResolveRuntimeTokenizerIdentity("http://localhost:9999", "model-a");
        string second = RuntimeTokenCounter.ResolveRuntimeTokenizerIdentity("http://localhost:9999", "model-b");

        Assert.Contains("model-a", first, StringComparison.Ordinal);
        Assert.Contains("model-b", second, StringComparison.Ordinal);
        Assert.NotEqual(first, second);
    }

    /// <summary>
    /// Regression for a live failure on the physical PC. A 19,676-character hex-dense document was
    /// correctly priced at 14,616 tokens and correctly compacted - and the model still answered
    /// with nothing, because the digest the planner built out of it was a string the runtime had
    /// never been asked about, so it was priced at the global 3.0 characters per token. The plan
    /// claimed 5,840 of 7,168 usable tokens while really sending some 13,000 into an 8,192 window.
    /// Text the runtime has not seen is now priced at the ratio this conversation showed.
    /// </summary>
    [Fact]
    public async Task Text_the_runtime_never_saw_is_priced_at_the_ratio_this_conversation_showed()
    {
        var handler = new StubHandler(content => Tokens((int)(content.Length / 1.35)));
        var counter = new RuntimeTokenCounter(new HttpClient(handler));
        string document = string.Join(' ', Enumerable.Repeat("5cfecc4a2b2d585741e2a20eb5a87 token=EED4C8642250FD46 status=ok", 200));

        Func<string, int> count = await counter.PrepareAsync(
            "http://127.0.0.1:1234", "qwen", new[] { document }, CancellationToken.None);

        // A digest built out of that document - exactly what the planner synthesises and then
        // measures its own plan against.
        string digest = document[..5000];
        int atObservedRatio = (int)(digest.Length / 1.35);

        Assert.InRange(count(digest), (int)(atObservedRatio * 0.9), (int)(atObservedRatio * 1.1));
        Assert.True(
            count(digest) > ConversationWindowPlanner.EstimateTokens(digest),
            $"Priced at {count(digest):N0}, the global heuristic says {ConversationWindowPlanner.EstimateTokens(digest):N0}.");
    }

    [Fact]
    public async Task An_unreachable_tokenizer_falls_back_to_the_estimate_instead_of_breaking_chat()
    {
        var handler = new StubHandler(_ => throw new HttpRequestException("no runtime"));
        var counter = new RuntimeTokenCounter(new HttpClient(handler));

        Func<string, int> count = await counter.PrepareAsync(
            "http://127.0.0.1:1234", "qwen", new[] { "hello" }, CancellationToken.None);

        Assert.Equal(ConversationWindowPlanner.EstimateTokens("hello"), count("hello"));
    }

    [Fact]
    public async Task A_runtime_with_no_endpoint_is_not_called_at_all()
    {
        var handler = new StubHandler(_ => Tokens(1));
        var counter = new RuntimeTokenCounter(new HttpClient(handler));

        Func<string, int> count = await counter.PrepareAsync(
            string.Empty, "qwen", new[] { "hello" }, CancellationToken.None);

        Assert.Equal(0, handler.Requests);
        Assert.Equal(ConversationWindowPlanner.EstimateTokens("hello"), count("hello"));
    }

    [Fact]
    public void A_response_without_a_token_array_is_treated_as_no_answer()
    {
        using JsonDocument malformed = JsonDocument.Parse("""{"error":"model not loaded"}""");
        using JsonDocument counted = JsonDocument.Parse("""{"tokens":[1,2,3]}""");

        Assert.Equal(0, RuntimeTokenCounter.ParseTokenCount(malformed.RootElement));
        Assert.Equal(3, RuntimeTokenCounter.ParseTokenCount(counted.RootElement));
    }
}
