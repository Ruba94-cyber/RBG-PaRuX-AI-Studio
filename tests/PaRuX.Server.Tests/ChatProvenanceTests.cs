using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// The App records where each reply ran from Core's control events. These tests pin the parser and the label the user
/// sees, including the cases that must stay blank: an older Core that reports no locality, and malformed events.
/// </summary>
public sealed class ChatProvenanceTests
{
    [Fact]
    public void A_local_route_maps_to_a_local_provenance_without_any_path()
    {
        ChatProvenance? provenance = ChatProvenance.TryParseModelRouted("""
            {"parux_event":"model_routed","selectedModel":"C:\\models\\qwen.gguf","provider":"local","locality":"local","mode":"auto"}
            """);

        Assert.NotNull(provenance);
        Assert.Equal("qwen.gguf", provenance.ModelId);
        Assert.Equal("local", provenance.Provider);
        Assert.Equal("local", provenance.Locality);
        Assert.Null(provenance.FallbackReason);
    }

    [Fact]
    public void A_remote_route_keeps_its_provider_and_the_reason_it_was_not_the_first_choice()
    {
        ChatProvenance? provenance = ChatProvenance.TryParseModelRouted("""
            {"parux_event":"model_routed","selectedModel":"gpt-model","provider":"external-openai-compatible","locality":"remote","endpointHost":"models.example.com","fallbackReason":"The direct engine failed its readiness check."}
            """);

        Assert.NotNull(provenance);
        Assert.Equal("remote", provenance.Locality);
        Assert.Equal("external-openai-compatible", provenance.Provider);
        Assert.Equal("The direct engine failed its readiness check.", provenance.FallbackReason);
    }

    [Fact]
    public void An_older_core_that_reports_no_locality_gets_no_label_rather_than_a_guess()
    {
        ChatProvenance? provenance = ChatProvenance.TryParseModelRouted("""
            {"parux_event":"model_routed","selectedModel":"qwen.gguf","mode":"auto"}
            """);

        Assert.NotNull(provenance);
        Assert.Null(provenance.Locality);
        Assert.Null(provenance.Provider);
        Assert.Null(ChatProvenance.FormatLabel(provenance.ModelId, provenance.Provider, provenance.Locality, provenance.FallbackReason));
    }

    [Fact]
    public void A_cloud_fallback_names_the_provider_that_answered_and_the_local_failure_behind_it()
    {
        ChatProvenance? provenance = ChatProvenance.TryParseCloudFallback("""
            {"parux_event":"cloud_fallback","provider":"OpenRouter","providerId":"openrouter","model":"openai/gpt-4o","reason":"llama-server exited unexpectedly"}
            """);

        Assert.NotNull(provenance);
        Assert.Equal("remote", provenance.Locality);
        Assert.Equal("openrouter", provenance.Provider);
        Assert.Equal("openai/gpt-4o", provenance.ModelId);
        Assert.Equal("llama-server exited unexpectedly", provenance.FallbackReason);
    }

    [Theory]
    [InlineData("{\"parux_event\":\"token\"}")]
    [InlineData("not-json")]
    [InlineData("[]")]
    [InlineData("{\"choices\":[{\"delta\":{\"content\":\"cloud_fallback\"}}]}")]
    public void Other_and_malformed_events_are_ignored(string payload)
    {
        Assert.Null(ChatProvenance.TryParseModelRouted(payload));
        Assert.Null(ChatProvenance.TryParseCloudFallback(payload));
    }

    [Fact]
    public void A_non_text_model_name_is_dropped_but_the_turn_is_still_recorded()
    {
        ChatProvenance? provenance = ChatProvenance.TryParseModelRouted(
            "{\"parux_event\":\"model_routed\",\"selectedModel\":42,\"locality\":\"local\"}");

        Assert.NotNull(provenance);
        Assert.Null(provenance.ModelId);
        Assert.Equal("local", provenance.Locality);
    }

    [Fact]
    public void Control_characters_are_removed_and_long_names_are_capped_so_the_label_stays_small()
    {
        ChatProvenance? named = ChatProvenance.TryParseModelRouted(
            "{\"parux_event\":\"model_routed\",\"selectedModel\":\"bad\\u0007name.gguf\",\"locality\":\"local\"}");
        ChatProvenance? capped = ChatProvenance.TryParseCloudFallback(
            "{\"parux_event\":\"cloud_fallback\",\"model\":\"" + new string('m', 400) + "\"}");

        Assert.Equal("badname.gguf", named!.ModelId);
        Assert.Equal(160, capped!.ModelId!.Length);
    }

    [Theory]
    [InlineData("qwen.gguf", "local", "local", "Local · qwen.gguf")]
    [InlineData(null, "local", "local", "Local")]
    [InlineData("gpt-4o", "openrouter", "remote", "Remote · openrouter · gpt-4o")]
    [InlineData(null, "openrouter", "remote", "Remote · openrouter")]
    public void The_label_says_where_the_reply_ran(string? model, string? provider, string locality, string expected) =>
        Assert.Equal(expected, ChatProvenance.FormatLabel(model, provider, locality, fallbackReason: null));

    [Fact]
    public void A_fallback_is_marked_in_the_label_and_an_unknown_locality_gets_no_label()
    {
        Assert.Equal("Local · qwen.gguf · fallback",
            ChatProvenance.FormatLabel("qwen.gguf", "local", "local", "The direct engine failed."));
        Assert.Null(ChatProvenance.FormatLabel("qwen.gguf", "local", "somewhere", fallbackReason: null));
    }
}
