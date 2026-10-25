using System.Text.Json;
using PaRuX.Models;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// A reply that ran under a manual model lock must still say so after the conversation is reloaded. These tests pin the
/// lock flag in Core's model_routed event and its round trip through the saved assistant message.
/// </summary>
public sealed class ChatProvenanceManualLockTests
{
    [Fact]
    public void A_manual_lock_in_the_model_routed_event_is_recorded()
    {
        ChatProvenance? provenance = ChatProvenance.TryParseModelRouted("""
            {"parux_event":"model_routed","selectedModel":"qwen.gguf","provider":"local","locality":"local","mode":"manual","manualLock":true}
            """);

        Assert.NotNull(provenance);
        Assert.True(provenance.ManualLock);
    }

    [Fact]
    public void Auto_routing_and_older_events_are_not_recorded_as_locked()
    {
        ChatProvenance? auto = ChatProvenance.TryParseModelRouted("""
            {"parux_event":"model_routed","selectedModel":"qwen.gguf","provider":"local","locality":"local","mode":"auto","manualLock":false}
            """);
        ChatProvenance? older = ChatProvenance.TryParseModelRouted("""
            {"parux_event":"model_routed","selectedModel":"qwen.gguf","provider":"local","locality":"local"}
            """);
        ChatProvenance? notABoolean = ChatProvenance.TryParseModelRouted("""
            {"parux_event":"model_routed","selectedModel":"qwen.gguf","manualLock":"yes"}
            """);

        Assert.NotNull(auto);
        Assert.False(auto.ManualLock);
        Assert.NotNull(older);
        Assert.False(older.ManualLock);
        Assert.NotNull(notABoolean);
        Assert.False(notABoolean.ManualLock);
    }

    [Fact]
    public void The_lock_flag_survives_saving_and_reloading_the_assistant_message()
    {
        var reply = new ChatMessage { Role = "assistant", Content = "answer", ModelId = "qwen.gguf", ManualLock = true };

        string json = JsonSerializer.Serialize(reply);
        ChatMessage? reloaded = JsonSerializer.Deserialize<ChatMessage>(json);

        Assert.NotNull(reloaded);
        Assert.True(reloaded.ManualLock);
    }

    [Fact]
    public void Conversations_saved_before_the_flag_existed_load_as_not_locked()
    {
        ChatMessage? legacy = JsonSerializer.Deserialize<ChatMessage>("{\"Role\":\"assistant\",\"Content\":\"answer\",\"ModelId\":\"qwen.gguf\"}");

        Assert.NotNull(legacy);
        Assert.False(legacy.ManualLock);
    }
}
