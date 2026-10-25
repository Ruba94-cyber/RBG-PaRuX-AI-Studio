using System.Text.Json.Nodes;
using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// LM Studio-style "edit and continue": the client edits an assistant reply and asks the model to
/// resume generating from exactly that text. ChatProxyService.ApplyContinuation is the boundary
/// that turns ChatCompletionRequest.ContinueFinalMessage into the llama.cpp server wire fields
/// (continue_final_message/add_generation_prompt), or refuses when continuation cannot honestly be
/// honored - a wrong prefix or a silently-discarded edit would be worse than an explicit error.
/// </summary>
public sealed class ChatContinuationTests
{
    private static ChatCompletionRequest Request(bool continueFinalMessage, params ChatCompletionMessage[] messages) =>
        new() { ContinueFinalMessage = continueFinalMessage, Messages = new List<ChatCompletionMessage>(messages) };

    [Fact]
    public void A_normal_turn_is_unaffected_when_continuation_is_not_requested()
    {
        var payload = new JsonObject();
        var request = Request(false, new ChatCompletionMessage { Role = "user", Content = "hi" });

        ChatProxyService.ApplyContinuation(payload, request, usesExternalProvider: false);

        Assert.False(payload.ContainsKey("continue_final_message"));
        Assert.False(payload.ContainsKey("add_generation_prompt"));
    }

    [Fact]
    public void Continuation_sets_the_llama_cpp_wire_fields_when_the_last_message_is_the_edited_assistant_turn()
    {
        var payload = new JsonObject();
        var request = Request(true,
            new ChatCompletionMessage { Role = "user", Content = "Name three fruits." },
            new ChatCompletionMessage { Role = "assistant", Content = "1) apple, 2)" });

        ChatProxyService.ApplyContinuation(payload, request, usesExternalProvider: false);

        Assert.True(payload["continue_final_message"]!.GetValue<bool>());
        Assert.False(payload["add_generation_prompt"]!.GetValue<bool>());
    }

    /// <summary>The one case this exists to prevent: continuing when the last message is not the
    /// edited assistant text would resume from the wrong prefix, or silently start a fresh turn -
    /// both worse than refusing outright.</summary>
    [Fact]
    public void Continuation_is_refused_when_the_final_message_is_not_the_edited_assistant_turn()
    {
        var payload = new JsonObject();
        var request = Request(true,
            new ChatCompletionMessage { Role = "assistant", Content = "1) apple, 2)" },
            new ChatCompletionMessage { Role = "user", Content = "keep going" });

        Assert.Throws<InvalidDataException>(() => ChatProxyService.ApplyContinuation(payload, request, usesExternalProvider: false));
    }

    [Fact]
    public void Continuation_is_refused_on_an_empty_message_list()
    {
        var payload = new JsonObject();
        var request = Request(true);

        Assert.Throws<InvalidDataException>(() => ChatProxyService.ApplyContinuation(payload, request, usesExternalProvider: false));
    }

    /// <summary>llama.cpp's continuation extension has no guaranteed equivalent on an arbitrary
    /// USER_MANAGED endpoint - refusing beats silently discarding the user's edit.</summary>
    [Fact]
    public void Continuation_is_refused_on_an_external_provider()
    {
        var payload = new JsonObject();
        var request = Request(true, new ChatCompletionMessage { Role = "assistant", Content = "1) apple, 2)" });

        Assert.Throws<InvalidOperationException>(() => ChatProxyService.ApplyContinuation(payload, request, usesExternalProvider: true));
    }

    /// <summary>A well-formed continuation request must still be rejected on an external provider,
    /// not silently accepted because the message shape happened to be valid.</summary>
    [Fact]
    public void External_provider_refusal_takes_priority_even_with_a_valid_message_shape()
    {
        var payload = new JsonObject();
        var request = Request(true,
            new ChatCompletionMessage { Role = "user", Content = "Name three fruits." },
            new ChatCompletionMessage { Role = "assistant", Content = "1) apple, 2)" });

        Assert.Throws<InvalidOperationException>(() => ChatProxyService.ApplyContinuation(payload, request, usesExternalProvider: true));
    }
}
