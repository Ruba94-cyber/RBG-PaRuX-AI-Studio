using System.Text.Json;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class ConfigurableRootsAndSyncContractTests
{
    [Fact]
    public void Extra_roots_are_added_to_defaults_and_relative_entries_are_ignored()
    {
        string extra = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "gguf-extra"));
        string defaultRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "gguf-default"));
        var roots = ExternalModelStoreImporter.Extra("X", new[] { defaultRoot },
            _ => extra + Path.PathSeparator + "relative" + Path.PathSeparator + extra);
        Assert.Equal(new[] { defaultRoot, extra }, roots);
        Assert.Equal(new[] { defaultRoot }, ExternalModelStoreImporter.Extra("X", new[] { defaultRoot }, _ => null));
    }

    // PaRuX.App's ConversationSyncClient.PullNewerAsync stores DocumentJson via HistoryStore.DeserializeDocument
    // (System.Text.Json, default PascalCase, into Conversation/ChatMessage). Pin the property names it needs.
    private sealed class Msg { public string Role { get; set; } = ""; public string Content { get; set; } = ""; public DateTime TimestampUtc { get; set; } }
    private sealed class Conv
    {
        public string Id { get; set; } = ""; public string Title { get; set; } = "";
        public DateTime CreatedUtc { get; set; } public DateTime UpdatedUtc { get; set; }
        public List<Msg> Messages { get; set; } = new();
    }

    [Fact]
    public void Imported_document_round_trips_through_the_server_store_into_the_app_conversation_shape()
    {
        string dir = Path.Combine(Path.GetTempPath(), "parux-sync-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ConversationStore(dir);
            byte[] export = System.Text.Encoding.UTF8.GetBytes(
                "[{\"uuid\":\"u9\",\"name\":\"T\",\"created_at\":\"2024-05-01T10:00:00Z\",\"chat_messages\":[{\"sender\":\"human\",\"text\":\"Hi\"},{\"sender\":\"assistant\",\"text\":\"Yo\"}]}]");
            var report = ConversationImporter.ImportInto(store, export, "conversations.json");
            // The same list + get calls PullNewerAsync makes:
            var summary = Assert.Single(store.List());
            Conv conv = JsonSerializer.Deserialize<Conv>(store.Get(summary.Id)!.DocumentJson)!;
            Assert.Equal(summary.Id, conv.Id);
            Assert.Equal("T", conv.Title);
            Assert.Equal(new[] { "user", "assistant" }, conv.Messages.Select(m => m.Role));
            Assert.Equal("Yo", conv.Messages[1].Content);
            Assert.Equal(1, report.Imported);
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }
}
