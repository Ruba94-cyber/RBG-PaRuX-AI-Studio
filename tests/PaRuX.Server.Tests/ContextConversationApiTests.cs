using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class ContextConversationApiTests : IClassFixture<ApiSecurityTests.PaRuXFactory>
{
    private readonly ApiSecurityTests.PaRuXFactory _factory;
    public ContextConversationApiTests(ApiSecurityTests.PaRuXFactory factory) => _factory = factory;

    private HttpClient AuthedClient()
    {
        HttpClient client = _factory.CreateClient();
        ServerState state = _factory.Services.GetRequiredService<ServerState>();
        client.DefaultRequestHeaders.Add("X-PaRuX-Token", state.Token);
        return client;
    }

    [Fact]
    public async Task Context_build_can_include_only_the_explicit_persisted_conversation()
    {
        string id = "context-conversation-" + Guid.NewGuid().ToString("N");
        ConversationStore store = _factory.Services.GetRequiredService<ConversationStore>();
        store.Save(id, new ConversationUpsertRequest
        {
            Title = "Scoped chat",
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow,
            DocumentJson = "{\"messages\":[{\"role\":\"user\",\"content\":\"private scoped marker 7391\"}]}"
        });

        using HttpClient client = AuthedClient();
        try
        {
            HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/context/build", new ContextBuildRequest
            {
                Query = "marker",
                IncludeMemory = false,
                IncludeRag = false,
                IncludeGraph = false,
                IncludeArtifacts = false,
                IncludeWeb = false,
                IncludeConversationHistory = true,
                ConversationId = id
            });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            ContextPackDto? pack = await response.Content.ReadFromJsonAsync<ContextPackDto>();
            Assert.NotNull(pack);
            ContextItemDto item = Assert.Single(pack!.Items);
            Assert.Equal("conversation", item.Kind);
            Assert.Equal("conversation:" + id, item.Source);
            Assert.Contains("private scoped marker 7391", item.Content);
        }
        finally { store.Delete(id); }
    }

    [Fact]
    public async Task Context_build_rejects_history_without_an_explicit_conversation_id()
    {
        using HttpClient client = AuthedClient();
        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/context/build", new ContextBuildRequest
        {
            Query = "anything",
            IncludeMemory = false,
            IncludeRag = false,
            IncludeGraph = false,
            IncludeArtifacts = false,
            IncludeConversationHistory = true
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Context_build_rejects_unknown_conversation_instead_of_leaking_another_chat()
    {
        using HttpClient client = AuthedClient();
        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/context/build", new ContextBuildRequest
        {
            Query = "anything",
            IncludeMemory = false,
            IncludeRag = false,
            IncludeGraph = false,
            IncludeArtifacts = false,
            IncludeConversationHistory = true,
            ConversationId = "does-not-exist-" + Guid.NewGuid().ToString("N")
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>Regression coverage for a real, 100%-reproducible bug found via live Desktop app
    /// testing: HistoryStore.Save's fire-and-forget ConversationSyncClient.PushAsync (deliberately
    /// never awaited, so a slow/offline server can't stall a local save) races the chat completion
    /// request that follows it. Every brand-new "New chat" first message hit this race and failed
    /// with a 503 "conversation not found", because the internal chat-turn context path used to
    /// propagate the same hard failure the direct /api/v1/context/build API deliberately keeps (see
    /// Context_build_rejects_unknown_conversation_instead_of_leaking_another_chat above). The
    /// internal path must instead degrade to no-history for that one turn rather than fail it.</summary>
    [Fact]
    public async Task BuildForChatAsync_degrades_to_no_history_instead_of_failing_the_turn_when_the_conversation_has_not_synced_yet()
    {
        ContextEngineService context = _factory.Services.GetRequiredService<ContextEngineService>();
        string unsyncedConversationId = "unsynced-" + Guid.NewGuid().ToString("N");

        ChatContextResult result = await context.BuildForChatAsync(
            "hello", skill: null, unsyncedConversationId, includeConversationHistory: true, CancellationToken.None);

        Assert.DoesNotContain(result.Pack.Items, item => item.Kind == "conversation");
    }
}
