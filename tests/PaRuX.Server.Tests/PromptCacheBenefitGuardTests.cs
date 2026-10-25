using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class PromptCacheBenefitGuardTests
{
    [Fact]
    public void Explicit_negative_measurement_vetoes_same_scope_reuse()
    {
        var policy = new PromptCacheScopePolicy();
        ChatCompletionRequest request = Request();

        Assert.False(policy.Decide(request, "managed", "runtime-a", empiricallyRecommended: false).Reuse);
        PromptCacheDecision second = policy.Decide(request, "managed", "runtime-a", empiricallyRecommended: false);

        Assert.False(second.Reuse);
        Assert.Equal(0, second.ReuseChunkTokens);
        Assert.False(second.EmpiricallyRecommended);
    }

    [Fact]
    public void Missing_measurement_preserves_same_scope_reuse()
    {
        var policy = new PromptCacheScopePolicy();
        ChatCompletionRequest request = Request();

        Assert.False(policy.Decide(request, "managed", "runtime-a", empiricallyRecommended: null).Reuse);
        PromptCacheDecision second = policy.Decide(request, "managed", "runtime-a", empiricallyRecommended: null);

        Assert.True(second.Reuse);
        Assert.Equal(PromptCacheScopePolicy.ReuseChunkTokens, second.ReuseChunkTokens);
        Assert.Null(second.EmpiricallyRecommended);
    }

    [Fact]
    public void Positive_measurement_still_requires_exact_same_scope()
    {
        var policy = new PromptCacheScopePolicy();
        ChatCompletionRequest first = Request("conversation-a");
        ChatCompletionRequest second = Request("conversation-b");

        policy.Decide(first, "managed", "runtime-a", empiricallyRecommended: true);
        Assert.False(policy.Decide(second, "managed", "runtime-a", empiricallyRecommended: true).Reuse);
        Assert.True(policy.Decide(second, "managed", "runtime-a", empiricallyRecommended: true).Reuse);
    }

    private static ChatCompletionRequest Request(string conversationId = "conversation-a") => new()
    {
        Model = "local-default",
        ConversationId = conversationId,
        Messages = new List<ChatCompletionMessage>
        {
            new() { Role = "system", Content = "system" },
            new() { Role = "user", Content = "hello" }
        }
    };
}
