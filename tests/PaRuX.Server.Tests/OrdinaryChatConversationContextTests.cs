using Microsoft.Extensions.DependencyInjection;
using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>Proves the ordinary ChatProxy context path uses persisted history only when the
/// request identifies its conversation and does not explicitly opt out. The old no-id path and
/// the old id-without-policy path keep their previous behavior.</summary>
public sealed class OrdinaryChatConversationContextTests : IClassFixture<ApiSecurityTests.PaRuXFactory>
{
    private readonly ApiSecurityTests.PaRuXFactory _factory;
    public OrdinaryChatConversationContextTests(ApiSecurityTests.PaRuXFactory factory) => _factory = factory;

    [Fact]
    public async Task Ordinary_chat_context_includes_only_an_explicit_conversation_id()
    {
        string id = "ordinary-chat-context-" + Guid.NewGuid().ToString("N");
        string marker = "ordinary persisted history marker " + Guid.NewGuid().ToString("N");
        ConversationStore conversations = _factory.Services.GetRequiredService<ConversationStore>();
        ContextEngineService context = _factory.Services.GetRequiredService<ContextEngineService>();
        conversations.Save(id, new ConversationUpsertRequest
        {
            Title = "Ordinary chat history",
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow,
            DocumentJson = "{\"messages\":[{\"role\":\"user\",\"content\":\"" + marker + "\"}]}"
        });

        try
        {
            ChatContextResult legacy = await context.BuildForChatAsync("local ordinary query", skill: null, CancellationToken.None);
            Assert.DoesNotContain(marker, legacy.Pack.PromptText, StringComparison.Ordinal);
            Assert.DoesNotContain(legacy.Pack.Items, item => item.Kind == "conversation" && item.Source == "conversation:" + id);

            ChatContextResult scoped = await context.BuildForChatAsync(
                "local ordinary query", skill: null, conversationId: id, CancellationToken.None);
            Assert.Contains(marker, scoped.Pack.PromptText, StringComparison.Ordinal);
            Assert.Contains(scoped.Pack.Items, item => item.Kind == "conversation" && item.Source == "conversation:" + id);
        }
        finally
        {
            conversations.Delete(id);
        }
    }

    [Fact]
    public async Task Ordinary_chat_context_respects_an_explicit_history_opt_out()
    {
        string id = "ordinary-chat-optout-" + Guid.NewGuid().ToString("N");
        string marker = "must not leak into opted out context " + Guid.NewGuid().ToString("N");
        ConversationStore conversations = _factory.Services.GetRequiredService<ConversationStore>();
        ContextEngineService context = _factory.Services.GetRequiredService<ContextEngineService>();
        conversations.Save(id, new ConversationUpsertRequest
        {
            Title = "Opt-out history",
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow,
            DocumentJson = "{\"messages\":[{\"role\":\"user\",\"content\":\"" + marker + "\"}]}"
        });

        try
        {
            ChatContextResult optedOut = await context.BuildForChatAsync(
                "local ordinary query", skill: null, conversationId: id,
                includeConversationHistory: false, CancellationToken.None);
            Assert.DoesNotContain(marker, optedOut.Pack.PromptText, StringComparison.Ordinal);
            Assert.DoesNotContain(optedOut.Pack.Items,
                item => item.Kind == "conversation" && item.Source == "conversation:" + id);

            ChatContextResult explicitlyIncluded = await context.BuildForChatAsync(
                "local ordinary query", skill: null, conversationId: id,
                includeConversationHistory: true, CancellationToken.None);
            Assert.Contains(marker, explicitlyIncluded.Pack.PromptText, StringComparison.Ordinal);
            Assert.Contains(explicitlyIncluded.Pack.Items,
                item => item.Kind == "conversation" && item.Source == "conversation:" + id);
        }
        finally
        {
            conversations.Delete(id);
        }
    }
}
