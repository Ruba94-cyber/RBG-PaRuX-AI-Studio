using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class PromptCacheScopePolicyTests
{
    private const string RuntimeA = "pid=101|model=C:/models/27b.gguf|profile=standard|cuda=b10549";
    private const string RuntimeB = "pid=202|model=C:/models/27b.gguf|profile=standard|cuda=b10549";
    private const string RuntimeOtherModel = "pid=303|model=C:/models/4b.gguf|profile=fast|cuda=b10549";

    [Fact]
    public void First_turn_is_cold_then_same_project_conversation_and_runtime_can_reuse()
    {
        var policy = new PromptCacheScopePolicy();
        ChatCompletionRequest request = Request("conversation-a", "project-a", "system", "hello");

        PromptCacheDecision first = policy.Decide(request, "http://127.0.0.1:1234", RuntimeA);
        PromptCacheDecision second = policy.Decide(request, "http://127.0.0.1:1234", RuntimeA);

        Assert.False(first.Reuse);
        Assert.Equal(0, first.ReuseChunkTokens);
        Assert.True(second.Reuse);
        Assert.Equal(PromptCacheScopePolicy.ReuseChunkTokens, second.ReuseChunkTokens);
    }

    [Fact]
    public void Conversation_switch_invalidates_reuse_until_scope_is_stable_again()
    {
        var policy = new PromptCacheScopePolicy();
        ChatCompletionRequest a = Request("conversation-a", "project-a", "same system", "same first user");
        ChatCompletionRequest b = Request("conversation-b", "project-a", "same system", "same first user");

        policy.Decide(a, "managed", RuntimeA);
        Assert.True(policy.Decide(a, "managed", RuntimeA).Reuse);
        Assert.False(policy.Decide(b, "managed", RuntimeA).Reuse);
        Assert.True(policy.Decide(b, "managed", RuntimeA).Reuse);
    }

    [Fact]
    public void Project_switch_invalidates_same_conversation()
    {
        var policy = new PromptCacheScopePolicy();
        ChatCompletionRequest a = Request("conversation-a", "project-a", "same system", "same first user");
        ChatCompletionRequest b = Request("conversation-a", "project-b", "same system", "same first user");

        policy.Decide(a, "managed", RuntimeA);
        Assert.True(policy.Decide(a, "managed", RuntimeA).Reuse);
        Assert.False(policy.Decide(b, "managed", RuntimeA).Reuse);
        Assert.True(policy.Decide(b, "managed", RuntimeA).Reuse);
    }

    [Fact]
    public void Runtime_restart_or_exact_model_change_invalidates_reuse_even_on_same_port()
    {
        var policy = new PromptCacheScopePolicy();
        ChatCompletionRequest request = Request("conversation-a", "project-a", "system", "hello");

        policy.Decide(request, "http://127.0.0.1:1234", RuntimeA);
        Assert.True(policy.Decide(request, "http://127.0.0.1:1234", RuntimeA).Reuse);
        Assert.False(policy.Decide(request, "http://127.0.0.1:1234", RuntimeB).Reuse);
        Assert.False(policy.Decide(request, "http://127.0.0.1:1234", RuntimeOtherModel).Reuse);
    }

    [Fact]
    public void Provider_change_invalidates_reuse()
    {
        var policy = new PromptCacheScopePolicy();
        ChatCompletionRequest request = Request("conversation-a", "project-a", "system", "hello");

        policy.Decide(request, "http://127.0.0.1:1234", RuntimeA);
        Assert.False(policy.Decide(request, "http://127.0.0.1:1235", RuntimeA).Reuse);
    }

    [Fact]
    public void Legacy_client_without_conversation_id_uses_stable_prefix_hash_with_project_partition()
    {
        var policy = new PromptCacheScopePolicy();
        ChatCompletionRequest first = Request(null, "project-a", "project A context", "initial task");
        ChatCompletionRequest sameThread = Request(null, "project-a", "project A context", "initial task");
        sameThread.Messages.Add(new ChatCompletionMessage { Role = "assistant", Content = "answer" });
        sameThread.Messages.Add(new ChatCompletionMessage { Role = "user", Content = "follow-up" });
        ChatCompletionRequest otherProject = Request(null, "project-b", "project A context", "initial task");

        Assert.False(policy.Decide(first, "managed", RuntimeA).Reuse);
        Assert.True(policy.Decide(sameThread, "managed", RuntimeA).Reuse);
        Assert.False(policy.Decide(otherProject, "managed", RuntimeA).Reuse);
    }

    [Fact]
    public void Scope_key_does_not_contain_prompt_project_or_model_path_text()
    {
        ChatCompletionRequest request = Request(null, "secret-project-name", "private system material", "private first turn");
        string key = PromptCacheScopePolicy.BuildScopeKey(
            request, "managed", "pid=9|model=C:/secret/model.gguf|profile=secret-profile");

        Assert.DoesNotContain("private", key, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", key, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(64, key.Length);
    }

    private static ChatCompletionRequest Request(
        string? conversationId,
        string? projectId,
        string system,
        string firstUser)
    {
        var request = new ChatCompletionRequest
        {
            Model = "local-default",
            ConversationId = conversationId,
            Messages = new List<ChatCompletionMessage>
            {
                new() { Role = "system", Content = system },
                new() { Role = "user", Content = firstUser }
            }
        };
        if (!string.IsNullOrWhiteSpace(projectId))
            request.SkillParameters[PromptCacheScopePolicy.ProjectScopeParameter] = projectId;
        return request;
    }
}
