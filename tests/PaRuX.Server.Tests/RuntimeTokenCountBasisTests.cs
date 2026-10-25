using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using PaRuX.Intelligence;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class RuntimeTokenCountBasisTests
{
    private const string StableIdentity = "managed|path=example.gguf|backend=text-vulkan;build=fixture;pid=42";

    [Theory]
    [InlineData(StableIdentity, true)]
    [InlineData("managed|backend=unresolved", false)]
    [InlineData("managed|backend=unknown", false)]
    [InlineData("managed|identity=unresolved", false)]
    [InlineData("managed|BACKEND=UNKNOWN", true)]
    [InlineData("", false)]
    [InlineData(" \t\r\n", false)]
    [InlineData(null, false)]
    public void Stable_identity_classification_matches_the_runtime_generation_rule(
        string? identity,
        bool expectedStable)
    {
        Assert.Equal(expectedStable, RuntimeTokenCounter.IsStableTokenizerIdentity(identity));
    }

    [Fact]
    public async Task Estimated_counts_are_not_written_to_the_shared_cache_for_a_stable_identity()
    {
        int attempts = 0;
        var handler = new TokenizeHandler(_ =>
            Interlocked.Increment(ref attempts) == 1 ? Failure() : Tokens(4));
        using var http = new HttpClient(handler);
        var cache = new RecordingTokenCountCache();
        var counter = new RuntimeTokenCounter(http, cache);

        RuntimeTokenCounter.PreparedCounter estimated = await counter.PrepareWithBasisAsync(
            "http://fixture", "model", StableIdentity, new[] { "retry this text" }, CancellationToken.None);

        Assert.Equal(RuntimeTokenCounter.CountBasis.Estimated, estimated.Basis);
        Assert.Empty(cache.Writes);

        RuntimeTokenCounter.PreparedCounter measured = await counter.PrepareWithBasisAsync(
            "http://fixture", "model", StableIdentity, new[] { "retry this text" }, CancellationToken.None);

        Assert.Equal(2, handler.Requests);
        Assert.Equal(RuntimeTokenCounter.CountBasis.Tokenizer, measured.Basis);
        Assert.Single(cache.Writes);
        Assert.Equal(12, measured.Count("retry this text"));
    }

    [Fact]
    public async Task Partly_estimated_basis_is_returned_on_the_context_window_plan()
    {
        var handler = new TokenizeHandler(text => text == "measured text" ? Tokens(3) : Failure());
        using var http = new HttpClient(handler);
        var counter = new RuntimeTokenCounter(http, new RecordingTokenCountCache());

        RuntimeTokenCounter.PreparedCounter prepared = await counter.PrepareWithBasisAsync(
            "http://fixture",
            "model",
            StableIdentity,
            new[] { "measured text", "estimated text" },
            CancellationToken.None);

        Assert.Equal(RuntimeTokenCounter.CountBasis.PartlyEstimated, prepared.Basis);
        Assert.Equal(ConversationWindowPlanner.TokenCountBasis.PartlyEstimated, prepared.PlanBasis);

        ConversationWindowPlanner.WindowPlan report = ConversationWindowPlanner.Fit(
            new[] { new ConversationWindowPlanner.Turn("user", "estimated text") },
            contextTokens: 2048,
            reservedForReplyTokens: 32,
            countTokens: prepared.Count,
            countBasisProvider: () => prepared.PlanBasis);

        Assert.Equal(nameof(ConversationWindowPlanner.TokenCountBasis.PartlyEstimated), report.CountBasis);
        Assert.Contains("\"CountBasis\":\"PartlyEstimated\"", JsonSerializer.Serialize(report), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Estimated_synthetic_compaction_text_downgrades_the_report_basis()
    {
        string original = new('x', 5000);
        var handler = new TokenizeHandler(content => Tokens(Math.Max(1, content.Length / 2)));
        using var http = new HttpClient(handler);
        var counter = new RuntimeTokenCounter(http, new RecordingTokenCountCache());
        RuntimeTokenCounter.PreparedCounter prepared = await counter.PrepareWithBasisAsync(
            "http://fixture", "model", StableIdentity, new[] { original }, CancellationToken.None);

        Assert.Equal(RuntimeTokenCounter.CountBasis.Tokenizer, prepared.Basis);

        ConversationWindowPlanner.WindowPlan report = ConversationWindowPlanner.Fit(
            new[] { new ConversationWindowPlanner.Turn("user", original) },
            contextTokens: 512,
            reservedForReplyTokens: 32,
            countTokens: prepared.Count,
            countBasisProvider: () => prepared.PlanBasis);

        Assert.True(report.Compacted);
        Assert.Equal(nameof(ConversationWindowPlanner.TokenCountBasis.PartlyEstimated), report.CountBasis);
        Assert.Equal(RuntimeTokenCounter.CountBasis.PartlyEstimated, prepared.Basis);
    }

    [Fact]
    public async Task Estimated_empty_text_count_downgrades_the_report_basis()
    {
        var handler = new TokenizeHandler(_ => Tokens(2));
        using var http = new HttpClient(handler);
        var counter = new RuntimeTokenCounter(http, new RecordingTokenCountCache());
        RuntimeTokenCounter.PreparedCounter prepared = await counter.PrepareWithBasisAsync(
            "http://fixture", "model", StableIdentity, new[] { "measured text" }, CancellationToken.None);

        Assert.Equal(RuntimeTokenCounter.CountBasis.Tokenizer, prepared.Basis);

        ConversationWindowPlanner.WindowPlan report = ConversationWindowPlanner.Fit(
            new[]
            {
                new ConversationWindowPlanner.Turn("user", string.Empty),
                new ConversationWindowPlanner.Turn("user", "measured text")
            },
            contextTokens: 2048,
            reservedForReplyTokens: 32,
            countTokens: prepared.Count,
            countBasisProvider: () => prepared.PlanBasis);

        Assert.Equal(nameof(ConversationWindowPlanner.TokenCountBasis.PartlyEstimated), report.CountBasis);
    }

    private static HttpResponseMessage Failure() => new(HttpStatusCode.ServiceUnavailable);

    private static HttpResponseMessage Tokens(int count) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(
            JsonSerializer.Serialize(new { tokens = Enumerable.Range(0, count).ToArray() }),
            Encoding.UTF8,
            "application/json")
    };

    private sealed class TokenizeHandler(Func<string, HttpResponseMessage> respond) : HttpMessageHandler
    {
        private int _requests;
        public int Requests => Volatile.Read(ref _requests);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requests);
            string body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            using JsonDocument document = JsonDocument.Parse(body);
            string content = document.RootElement.GetProperty("content").GetString() ?? string.Empty;
            return respond(content);
        }
    }

    private sealed class RecordingTokenCountCache : IRuntimeTokenCountCache
    {
        private readonly ConcurrentDictionary<string, int> _entries = new(StringComparer.Ordinal);
        private readonly ConcurrentQueue<KeyValuePair<string, int>> _writes = new();

        public KeyValuePair<string, int>[] Writes => _writes.ToArray();

        public bool ContainsKey(string key) => _entries.ContainsKey(key);

        public bool TryGetValue(string key, out int value) => _entries.TryGetValue(key, out value);

        public bool TryAdd(string key, int value)
        {
            if (!_entries.TryAdd(key, value)) return false;
            _writes.Enqueue(new KeyValuePair<string, int>(key, value));
            return true;
        }

        public bool TryRemove(string key, out int value) => _entries.TryRemove(key, out value);

        public void Clear() => _entries.Clear();
    }
}
