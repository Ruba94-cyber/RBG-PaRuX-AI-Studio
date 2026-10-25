using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class ConversationContextExtractorTests
{
    [Fact]
    public void Extract_reads_messages_and_attachment_names_without_base64_payloads()
    {
        string hugeBase64 = new string('A', 20_000);
        string json = $$"""
        {
          "messages": [
            { "role": "user", "content": "inspect this image", "images": [
              { "fileName": "photo.png", "base64Data": "{{hugeBase64}}" }
            ] },
            { "role": "assistant", "content": "done" }
          ]
        }
        """;

        string result = ConversationContextExtractor.Extract(json);

        Assert.Contains("user: inspect this image", result);
        Assert.Contains("photo.png", result);
        Assert.Contains("assistant: done", result);
        Assert.DoesNotContain(hugeBase64, result);
        Assert.True(result.Length < 1_000);
    }

    [Fact]
    public void Extract_prefers_recent_messages_when_history_exceeds_the_dedicated_limit()
    {
        string messages = string.Join(',', Enumerable.Range(0, 40)
            .Select(i => "{\"role\":\"user\",\"content\":\"message-" + i + " " + new string('x', 400) + "\"}"));
        string result = ConversationContextExtractor.Extract("{\"messages\":[" + messages + "]}");

        Assert.True(result.Length <= ConversationContextExtractor.MaxTotalCharacters);
        Assert.Contains("message-23", result);
        Assert.Contains("message-39", result);
        Assert.DoesNotContain("message-0 ", result);
    }

    [Fact]
    public void ExtractRelevant_recalls_an_ancient_decision_from_far_beyond_the_recent_window()
    {
        var messages = new List<string>
        {
            "{\"role\":\"user\",\"content\":\"Decision for ORBIT-552: NEVER change the on-disk frame checksum from CRC32C.\"}"
        };
        for (int i = 0; i < 600; i++)
            messages.Add("{\"role\":\"assistant\",\"content\":\"routine historical filler " + i + "\"}");
        messages.Add("{\"role\":\"user\",\"content\":\"What checksum did we decide for ORBIT-552?\"}");

        string json = "{\"messages\":[" + string.Join(',', messages) + "]}";
        string result = ConversationContextExtractor.ExtractRelevant(json, "What checksum did we decide for ORBIT-552?");

        Assert.True(result.Length <= ConversationContextExtractor.MaxTotalCharacters);
        Assert.Contains("ORBIT-552", result, StringComparison.Ordinal);
        Assert.Contains("CRC32C", result, StringComparison.Ordinal);
        Assert.Contains("Virtual Context", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Default_extract_uses_virtual_context_for_persisted_long_history_too()
    {
        var messages = new List<string>
        {
            "{\"role\":\"user\",\"content\":\"MUST keep sentinel PERSISTED_1M_STATE in all future release checks.\"}"
        };
        for (int i = 0; i < 500; i++)
            messages.Add("{\"role\":\"assistant\",\"content\":\"old batch " + i + " irrelevant material\"}");
        messages.Add("{\"role\":\"user\",\"content\":\"Recall PERSISTED_1M_STATE for this release check.\"}");

        string result = ConversationContextExtractor.Extract(
            "{\"messages\":[" + string.Join(',', messages) + "]}");

        Assert.Contains("PERSISTED_1M_STATE", result, StringComparison.Ordinal);
        Assert.True(result.Length <= ConversationContextExtractor.MaxTotalCharacters);
    }

    [Fact]
    public void Persisted_system_messages_are_historical_evidence_not_unbounded_live_system_prompts()
    {
        var messages = new List<string>();
        for (int i = 0; i < 100; i++)
            messages.Add("{\"role\":\"system\",\"content\":\"historic-system-" + i + " " + new string('z', 500) + "\"}");
        messages.Add("{\"role\":\"user\",\"content\":\"current request\"}");

        string result = ConversationContextExtractor.Extract(
            "{\"messages\":[" + string.Join(',', messages) + "]}");

        Assert.True(result.Length <= ConversationContextExtractor.MaxTotalCharacters);
        Assert.Contains("current request", result);
    }

    [Fact]
    public void Extreme_history_guardrail_keeps_oldest_middle_and_newest_regions()
    {
        IReadOnlyList<int> indexes = ConversationContextExtractor.SelectMessageIndexes(100_000, 20_000);

        Assert.Equal(20_000, indexes.Count);
        Assert.Equal(0, indexes[0]);
        Assert.Equal(99_999, indexes[^1]);
        Assert.Contains(indexes, index => index is > 30_000 and < 70_000);
        Assert.Equal(indexes.Count, indexes.Distinct().Count());
        Assert.True(indexes.SequenceEqual(indexes.OrderBy(index => index)));
    }

    [Fact]
    public void Extract_accepts_one_level_wrapped_chat_documents()
    {
        string result = ConversationContextExtractor.Extract(
            "{\"chat\":{\"messages\":[{\"Role\":\"user\",\"Text\":\"wrapped value\"}]}}");
        Assert.Equal("user: wrapped value", result);
    }

    [Fact]
    public void Extract_returns_empty_for_invalid_or_unrelated_json()
    {
        Assert.Equal(string.Empty, ConversationContextExtractor.Extract("not-json"));
        Assert.Equal(string.Empty, ConversationContextExtractor.Extract("{\"title\":\"no messages\"}"));
    }

    [Fact]
    public void Extract_never_treats_arbitrary_arrays_as_chat_messages()
    {
        string json = "{\"attachments\":[{\"role\":\"user\",\"content\":\"should not leak\"}]}";
        Assert.Equal(string.Empty, ConversationContextExtractor.Extract(json));
    }
}
