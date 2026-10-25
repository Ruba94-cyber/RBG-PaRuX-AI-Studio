using System.Net;
using System.Text.Json;
using PaRuX.Intelligence;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// BIC-11: counts must be bound to the tokenizer that produced them, synthesized summaries must be
/// priced with the real tokenizer rather than an inferred ratio, and a prompt whose size could not
/// be verified must be labelled and given a margin instead of silently trusted.
/// </summary>
public sealed class TokenizerBudgetIntegrityTests
{
    private sealed class CountingHandler : HttpMessageHandler
    {
        private readonly Func<string, int> _tokens;
        public int Requests;

        public CountingHandler(Func<string, int> tokens) => _tokens = tokens;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Requests);
            using JsonDocument body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            int count = _tokens(body.RootElement.GetProperty("content").GetString() ?? string.Empty);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(new { tokens = Enumerable.Range(0, count).ToArray() }),
                    System.Text.Encoding.UTF8, "application/json")
            };
        }
    }

    private const string Endpoint = "http://127.0.0.1:1234";

    [Fact]
    public async Task Unresolved_backend_identity_is_priced_per_call_and_never_shared()
    {
        const string identity = "managed-alias:local-default|model=unresolved|backend=unresolved";
        Assert.False(RuntimeTokenCounter.IsStableTokenizerIdentity(identity));
        var handler = new CountingHandler(text => text.Length);
        var counter = new RuntimeTokenCounter(new HttpClient(handler));

        await counter.PrepareAsync(Endpoint, "local-default", identity, new[] { "same text" }, CancellationToken.None);
        await counter.PrepareAsync(Endpoint, "local-default", identity, new[] { "same text" }, CancellationToken.None);

        Assert.Equal(2, handler.Requests);
    }

    [Fact]
    public async Task Backend_generation_changes_retokenize_but_the_same_generation_is_reused()
    {
        const string gen1 = "managed-alias:local-default|model=unresolved|backend=text-cuda;build=x;pid=100;start=111";
        const string gen1Again = gen1;
        const string restartedPid = "managed-alias:local-default|model=unresolved|backend=text-cuda;build=x;pid=200;start=111";
        const string reusedPidNewStart = "managed-alias:local-default|model=unresolved|backend=text-cuda;build=x;pid=100;start=999";
        var handler = new CountingHandler(text => text.Length);
        var counter = new RuntimeTokenCounter(new HttpClient(handler));
        var texts = new[] { "same text" };

        await counter.PrepareAsync(Endpoint, "local-default", gen1, texts, CancellationToken.None);
        await counter.PrepareAsync(Endpoint, "local-default", gen1Again, texts, CancellationToken.None);
        Assert.Equal(1, handler.Requests);

        await counter.PrepareAsync(Endpoint, "local-default", restartedPid, texts, CancellationToken.None);
        Assert.Equal(2, handler.Requests);
        await counter.PrepareAsync(Endpoint, "local-default", reusedPidNewStart, texts, CancellationToken.None);
        Assert.Equal(3, handler.Requests);
    }

    [Fact]
    public async Task Basis_is_partly_estimated_when_some_texts_were_not_tokenized()
    {
        var counter = new RuntimeTokenCounter(new HttpClient(new CountingHandler(
            text => text.StartsWith("fail", StringComparison.Ordinal) ? 0 : text.Length / 2)));

        var partial = await counter.PrepareWithBasisAsync(
            Endpoint, "m", "provider-model:m", new[] { "hello world", "fail this one" }, CancellationToken.None);

        Assert.Equal(RuntimeTokenCounter.CountBasis.PartlyEstimated, partial.Basis);
        Assert.Contains("some messages", ChatPromptBudget.DescribeUnverifiedCount(partial.Basis, 64, false), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Count_basis_distinguishes_tokenizer_counts_from_the_character_estimate()
    {
        var live = new RuntimeTokenCounter(new HttpClient(new CountingHandler(text => text.Length / 2)));
        var down = new RuntimeTokenCounter(new HttpClient(new CountingHandler(_ => throw new HttpRequestException("down"))));

        var measured = await live.PrepareWithBasisAsync(
            Endpoint, "m", "provider-model:m", new[] { "hello world" }, CancellationToken.None);
        var guessed = await down.PrepareWithBasisAsync(
            Endpoint, "m", "provider-model:m", new[] { "hello world" }, CancellationToken.None);
        var none = await live.PrepareWithBasisAsync(
            string.Empty, "m", "provider-model:m", new[] { "hello world" }, CancellationToken.None);

        Assert.Equal(RuntimeTokenCounter.CountBasis.Tokenizer, measured.Basis);
        Assert.Equal(RuntimeTokenCounter.CountBasis.Estimated, guessed.Basis);
        Assert.Equal(RuntimeTokenCounter.CountBasis.Estimated, none.Basis);
    }

    /// <summary>
    /// The digest is synthesized after the conversation was tokenized. A tokenizer that charges more
    /// for the digest's identifier-dense content than the conversation's ratio predicts must be
    /// reflected once the plan's real messages are tokenized.
    /// </summary>
    [Fact]
    public async Task Synthesized_summary_is_priced_by_the_tokenizer_once_it_is_tokenized()
    {
        // Prose is cheap (4 chars/token); hex-dense text is expensive (1 char/token).
        static int Tokenize(string text) =>
            text.Contains("5cfecc4", StringComparison.Ordinal) ? text.Length : text.Length / 4;
        var counter = new RuntimeTokenCounter(new HttpClient(new CountingHandler(Tokenize)));
        const string identity = "provider-model:m";
        string prose = string.Join(' ', Enumerable.Repeat("the quick brown fox jumps over the lazy dog", 100));
        string digest = "Durable state: " + string.Join(' ', Enumerable.Repeat("5cfecc4a2b2d", 100));

        var first = await counter.PrepareWithBasisAsync(Endpoint, "m", identity, new[] { prose }, CancellationToken.None);
        int inferred = first.Count(digest);
        var second = await counter.PrepareWithBasisAsync(Endpoint, "m", identity, new[] { prose, digest }, CancellationToken.None);
        int exact = second.Count(digest);

        Assert.True(inferred < digest.Length / 3, "unseen digest is priced from the prose ratio");
        Assert.Equal(Tokenize(digest) + RuntimeTokenCounter.PerMessageTemplateOverheadTokens, exact);
        Assert.True(exact > inferred * 2, $"inferred {inferred}, exact {exact}");
    }

    [Fact]
    public void Planned_conversation_priced_with_exact_counts_stays_inside_the_window()
    {
        // Mixed content: the exact per-text counter is what the proxy re-prices the plan with.
        // Additive per word, like a real tokenizer: identifier-dense words cost a token per
        // character, prose words about a quarter of that.
        static int Tokens(string text) => text
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Sum(word => word.StartsWith("5cfecc4", StringComparison.Ordinal) ? word.Length : (word.Length + 3) / 4 + 1);
        Func<string, int> count = text => Tokens(text) + RuntimeTokenCounter.PerMessageTemplateOverheadTokens;
        var turns = new List<ConversationWindowPlanner.Turn> { new("system", "Be concise.") };
        for (int i = 0; i < 40; i++)
        {
            turns.Add(new("user", $"Decision {i}: keep module {i} stable. " + string.Join(' ', Enumerable.Repeat("5cfecc4a2b", 30))));
            turns.Add(new("assistant", string.Join(' ', Enumerable.Repeat("acknowledged and noted for later", 20))));
        }

        var plan = ConversationWindowPlanner.Fit(turns, 4096, 512, count);

        Assert.True(plan.Compacted);
        int priced = plan.Messages.Sum(message => count(message.Content));
        Assert.True(priced <= 4096 - 512, $"priced {priced} planned {plan.EstimatedTokens} kept {plan.KeptTurns} avail {plan.AvailableTokens} msgs {string.Join(',', plan.Messages.Select(m => m.Role + count(m.Content)))}");
    }

    [Theory]
    [InlineData(8192, 1024, 7000, true)]   // estimate inside the margin: tighten
    [InlineData(8192, 1024, 5000, false)]  // estimate far below window: leave alone
    [InlineData(2048, 1024, 7500, false)]  // cannot be tightened without breaking the reply reserve
    public void Unverified_estimates_near_the_window_get_a_scaled_margin(
        int messageContext, int reserve, int estimated, bool expectTightened)
    {
        int maximumRendered = 7168;
        bool tightened = ChatPromptBudget.TryTightenForUnverifiedCount(
            messageContext, estimated, maximumRendered, reserve, out int newContext);

        Assert.Equal(expectTightened, tightened);
        if (tightened)
        {
            int margin = ChatPromptBudget.UnverifiedSafetyMarginTokens(maximumRendered);
            Assert.True(newContext < messageContext);
            Assert.Equal(maximumRendered - margin, estimated - (messageContext - newContext));
            Assert.True(newContext > reserve);
        }
        else
        {
            Assert.Equal(messageContext, newContext);
        }
    }

    [Fact]
    public void Unverified_margin_scales_with_the_window_but_has_a_floor()
    {
        Assert.Equal(ChatPromptBudget.ExactCountSafetyMarginTokens, ChatPromptBudget.UnverifiedSafetyMarginTokens(100));
        Assert.Equal(1000, ChatPromptBudget.UnverifiedSafetyMarginTokens(10_000));
        Assert.Equal(ChatPromptBudget.ExactCountSafetyMarginTokens, ChatPromptBudget.UnverifiedSafetyMarginTokens(-5));
    }

    [Fact]
    public void Unverified_label_states_the_basis_and_never_claims_exactness()
    {
        string estimate = ChatPromptBudget.DescribeUnverifiedCount(
            RuntimeTokenCounter.CountBasis.Estimated, 717, tightened: true);
        string tokenizer = ChatPromptBudget.DescribeUnverifiedCount(
            RuntimeTokenCounter.CountBasis.Tokenizer, 717, tightened: false);

        Assert.Contains("character-based estimate", estimate, StringComparison.Ordinal);
        Assert.Contains("not exact", estimate, StringComparison.Ordinal);
        Assert.Contains("enforced", estimate, StringComparison.Ordinal);
        Assert.Contains("per-message tokenizer counts", tokenizer, StringComparison.Ordinal);
        Assert.Contains("targeted", tokenizer, StringComparison.Ordinal);
    }

    [Fact]
    public void Spacing_json_separators_leaves_string_contents_untouched()
    {
        string spaced = ChatPromptBudget.SpaceJsonSeparators("{\"a\":\"x,y:z\\\",w\",\"b\":[1,2]}");

        Assert.Equal("{\"a\": \"x,y:z\\\",w\", \"b\": [1, 2]}", spaced);
    }

    [Fact]
    public void Tool_fragment_prices_the_template_preamble_and_both_template_shapes()
    {
        var tools = new System.Text.Json.Nodes.JsonArray(System.Text.Json.Nodes.JsonNode.Parse(
            "{\"type\":\"function\",\"function\":{\"name\":\"read_file\",\"description\":\"Read\",\"parameters\":{\"type\":\"object\",\"properties\":{\"path\":{\"type\":\"string\",\"description\":\"p\"}}}}}"));

        string fragment = Assert.Single(ChatPromptBudget.BuildFixedFragments(tools, null));

        Assert.Contains("\"name\": \"read_file\"", fragment, StringComparison.Ordinal);
        Assert.Contains("<parameter>", fragment, StringComparison.Ordinal);
        Assert.Contains("<tool_call>", fragment, StringComparison.Ordinal);
        Assert.True(fragment.Length > tools.ToJsonString().Length + 300);
    }
}
