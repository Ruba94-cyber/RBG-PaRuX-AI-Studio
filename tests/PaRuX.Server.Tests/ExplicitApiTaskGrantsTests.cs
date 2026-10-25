using System.Security.Cryptography;
using System.Text;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class ExplicitApiTaskGrantsTests
{
    private static readonly Uri DefaultEndpoint = new("https://api.example.org/v1");

    private static ExplicitApiTaskBinding Binding(
        string providerId = "openai",
        Uri? endpoint = null,
        string apiKey = "key-a",
        string model = "model-a",
        string taskId = "task-1",
        string? payloadSha256 = null,
        decimal? pricePerToken = null,
        int? maxOutputTokens = null) =>
        ExplicitApiTaskBinding.Create(providerId, endpoint ?? DefaultEndpoint, apiKey, model, taskId,
            payloadSha256, pricePerToken, maxOutputTokens);

    private static string Digest(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    private sealed class FixedClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 18, 9, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public void GrantCanBeConsumedExactlyOnce()
    {
        var grants = new ExplicitApiTaskGrants();
        string token = grants.Issue(Binding(), TimeSpan.FromMinutes(1));
        Assert.True(grants.TryConsume(token, Binding(), out _));
        Assert.False(grants.TryConsume(token, Binding(), out _));
    }

    [Fact]
    public void WrongProviderConsumesGrantWithoutAuthorizing()
    {
        var grants = new ExplicitApiTaskGrants();
        string token = grants.Issue(Binding(), TimeSpan.FromMinutes(1));
        Assert.False(grants.TryConsume(token, Binding(providerId: "other"), out _));
        Assert.False(grants.TryConsume(token, Binding(), out _));
    }

    [Fact]
    public void WrongModelCannotReuseGrant()
    {
        var grants = new ExplicitApiTaskGrants();
        string token = grants.Issue(Binding(), TimeSpan.FromMinutes(1));
        Assert.False(grants.TryConsume(token, Binding(model: "model-b"), out _));
        Assert.False(grants.TryConsume(token, Binding(), out _));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(11)]
    public void InvalidLifetimeIsRejected(int minutes)
    {
        var grants = new ExplicitApiTaskGrants();
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            grants.Issue(Binding(), TimeSpan.FromMinutes(minutes)));
    }

    [Fact]
    public async Task SimultaneousConsumptionAuthorizesOnlyOneCaller()
    {
        var grants = new ExplicitApiTaskGrants();
        string token = grants.Issue(Binding(), TimeSpan.FromMinutes(1));
        var attempts = Enumerable.Range(0, 32)
            .Select(attempt => Task.Run(() => grants.TryConsume(token, Binding(), out decimal _)));
        bool[] results = await Task.WhenAll(attempts);
        Assert.Equal(1, results.Count(allowed => allowed));
    }

    [Fact]
    public void ReplayedGrantCannotRecoverApprovedBudget()
    {
        var grants = new ExplicitApiTaskGrants();
        string token = grants.Issue(Binding(), TimeSpan.FromMinutes(1), 2m);
        Assert.True(grants.TryConsume(token, Binding(), out decimal budget));
        Assert.Equal(2m, budget);
        Assert.False(grants.TryConsume(token, Binding(), out decimal replayBudget));
        Assert.Equal(0m, replayBudget);
    }

    [Fact]
    public void OversizedProviderAndModelIdentifiersAreRejected()
    {
        Assert.Throws<ArgumentException>(() => Binding(providerId: new string('p', 257)));
        Assert.Throws<ArgumentException>(() => Binding(model: new string('m', 257)));
    }

    [Fact]
    public void UnknownTokenIsRejected()
    {
        var grants = new ExplicitApiTaskGrants();
        Assert.False(grants.TryConsume("unknown", Binding(), out _));
    }

    [Theory]
    [InlineData("other", "https://api.example.org/v1", "key-a", "model-a", "task-1")]
    [InlineData("openai", "https://other.example.org/v1", "key-a", "model-a", "task-1")]
    [InlineData("openai", "https://api.example.org:8443/v1", "key-a", "model-a", "task-1")]
    [InlineData("openai", "https://api.example.org/v2", "key-a", "model-a", "task-1")]
    [InlineData("openai", "https://api.example.org", "key-a", "model-a", "task-1")]
    [InlineData("openai", "https://api.example.org/v1", "key-b", "model-a", "task-1")]
    [InlineData("openai", "https://api.example.org/v1", "key-a", "model-y", "task-1")]
    [InlineData("openai", "https://api.example.org/v1", "key-a", "model-a", "task-2")]
    public void GrantRefusesAnyFieldThatDiffersFromTheIssuedBinding(
        string providerId, string endpoint, string apiKey, string model, string taskId)
    {
        var grants = new ExplicitApiTaskGrants();
        string token = grants.Issue(Binding(), TimeSpan.FromMinutes(1));

        Assert.False(grants.TryConsume(token, Binding(providerId, new Uri(endpoint), apiKey, model, taskId), out _));
        // The mismatch consumed the grant: the rightful caller gets no second attempt with the same token.
        Assert.False(grants.TryConsume(token, Binding(), out _));
    }

    [Theory]
    [InlineData("https://API.Example.ORG/v1")]
    [InlineData("https://api.example.org:443/v1/")]
    public void EquivalentSpellingsOfTheSameAuthorityMatch(string spelling)
    {
        var grants = new ExplicitApiTaskGrants();
        string token = grants.Issue(Binding(), TimeSpan.FromMinutes(1), 1m);
        Assert.True(grants.TryConsume(token, Binding(endpoint: new Uri(spelling)), out decimal budget));
        Assert.Equal(1m, budget);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("short")]
    public void MalformedTokensAreRefusedWithoutBurningAValidGrant(string? malformed)
    {
        var grants = new ExplicitApiTaskGrants();
        string token = grants.Issue(Binding(), TimeSpan.FromMinutes(1));
        Assert.False(grants.TryConsume(malformed, Binding(), out _));
        Assert.True(grants.TryConsume(token, Binding(), out _));
    }

    [Fact]
    public void PayloadDigestMustMatchExactlyWhenBound()
    {
        string approved = Digest("approved payload");
        var grants = new ExplicitApiTaskGrants();

        string exact = grants.Issue(Binding(payloadSha256: approved), TimeSpan.FromMinutes(1));
        Assert.True(grants.TryConsume(exact, Binding(payloadSha256: approved), out _));

        string mismatch = grants.Issue(Binding(payloadSha256: approved), TimeSpan.FromMinutes(1));
        Assert.False(grants.TryConsume(mismatch, Binding(payloadSha256: Digest("other payload")), out _));
        Assert.False(grants.TryConsume(mismatch, Binding(payloadSha256: approved), out _));

        string unbound = grants.Issue(Binding(), TimeSpan.FromMinutes(1));
        Assert.False(grants.TryConsume(unbound, Binding(payloadSha256: approved), out _));
    }

    [Fact]
    public void GrantIsUsableOnlyForTheApprovedPriceAndCompletionLimit()
    {
        var grants = new ExplicitApiTaskGrants();

        string exact = grants.Issue(Binding(pricePerToken: 0.001m, maxOutputTokens: 16), TimeSpan.FromMinutes(1), 1m);
        Assert.True(grants.TryConsume(exact, Binding(pricePerToken: 0.001m, maxOutputTokens: 16), out _));

        string cheaper = grants.Issue(Binding(pricePerToken: 0.001m, maxOutputTokens: 16), TimeSpan.FromMinutes(1), 1m);
        Assert.False(grants.TryConsume(cheaper, Binding(pricePerToken: 0.0001m, maxOutputTokens: 16), out _));

        string longer = grants.Issue(Binding(pricePerToken: 0.001m, maxOutputTokens: 16), TimeSpan.FromMinutes(1), 1m);
        Assert.False(grants.TryConsume(longer, Binding(pricePerToken: 0.001m, maxOutputTokens: 17), out _));

        string unpriced = grants.Issue(Binding(pricePerToken: 0.001m, maxOutputTokens: 16), TimeSpan.FromMinutes(1), 1m);
        Assert.False(grants.TryConsume(unpriced, Binding(), out _));

        string withoutTerms = grants.Issue(Binding(), TimeSpan.FromMinutes(1), 1m);
        Assert.False(grants.TryConsume(withoutTerms, Binding(pricePerToken: 0.001m, maxOutputTokens: 16), out _));
    }

    [Fact]
    public void RevokedGrantCanNoLongerAuthorizeItsTask()
    {
        var grants = new ExplicitApiTaskGrants();
        string token = grants.Issue(Binding(), TimeSpan.FromMinutes(1), 1m);

        Assert.True(grants.Revoke(token));
        Assert.False(grants.TryConsume(token, Binding(), out _));
        Assert.False(grants.Revoke(token));
        Assert.False(grants.Revoke("unknown"));
    }

    [Fact]
    public void ExpiredGrantIsRefused()
    {
        var clock = new FixedClock();
        var grants = new ExplicitApiTaskGrants(clock);
        string token = grants.Issue(Binding(), TimeSpan.FromMinutes(1), 1m);

        clock.Now = clock.Now.AddMinutes(2);

        Assert.False(grants.TryConsume(token, Binding(), out _));
    }

    [Fact]
    public void BindingKeepsOnlyTheApiKeyFingerprintAndNormalizedAuthority()
    {
        ExplicitApiTaskBinding binding = Binding(apiKey: "test-key-material", endpoint: new Uri("https://API.Example.ORG/v1"));

        Assert.Equal(Digest("test-key-material"), binding.ApiKeyFingerprint);
        Assert.DoesNotContain("test-key-material", binding.ApiKeyFingerprint, StringComparison.Ordinal);
        Assert.Equal("api.example.org:443", binding.EndpointAuthority);
    }
}
