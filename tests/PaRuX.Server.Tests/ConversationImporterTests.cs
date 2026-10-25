using System.IO.Compression;
using System.Text;
using System.Text.Json;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class ConversationImporterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "parux-convimport-" + Guid.NewGuid().ToString("N"));

    public ConversationImporterTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private const string ChatGptJson = """
    [
      {"conversation_id":"c1","title":"Sorting","create_time":1700000000,"update_time":1700000100,"current_node":"n3","unknown":{"x":1},
       "mapping":{
         "root":{"id":"root","parent":null,"children":["n1"],"message":null},
         "n1":{"id":"n1","parent":"root","children":["n2"],"message":{"author":{"role":"system"},"content":{"parts":["sys"]}}},
         "n2":{"id":"n2","parent":"n1","children":["n3"],"message":{"author":{"role":"user"},"create_time":1700000010,"content":{"parts":["How to sort?"]}}},
         "n3":{"id":"n3","parent":"n2","children":[],"message":{"author":{"role":"assistant"},"create_time":1700000020,"content":{"parts":["Use Array.Sort", {"asset":"img"}]}}}
       }},
      {"conversation_id":"c2","title":"Empty","mapping":{}},
      "garbage"
    ]
    """;

    private const string ClaudeJson = """
    [{"uuid":"u-1","name":"Plan","created_at":"2024-05-01T10:00:00Z","updated_at":"2024-05-01T10:05:00Z",
      "chat_messages":[
        {"sender":"human","text":"Hello","created_at":"2024-05-01T10:00:00Z"},
        {"sender":"assistant","text":"","content":[{"type":"text","text":"Hi there"},{"type":"tool_use"}],"created_at":"2024-05-01T10:00:05Z"},
        {"sender":"other","text":"ignored"}
      ]}]
    """;

    private const string GeminiJson = """
    [{"header":"Gemini Apps","title":"Prompted What is 2+2?","time":"2024-06-01T09:00:00.000Z",
      "safeHtmlItem":[{"html":"<p>It is <b>4</b>.</p><script>alert(1)</script>"}]},
     {"header":"Gemini Apps","title":"Prompted Only a prompt","time":"2024-06-02T09:00:00Z"},
     {"header":"Search","note":"no time"}]
    """;

    private ConversationStore NewStore() => new(Path.Combine(_dir, "store-" + Guid.NewGuid().ToString("N")));
    private static byte[] Bytes(string s) => Encoding.UTF8.GetBytes(s);

    [Fact]
    public void ChatGpt_follows_active_branch_keeps_order_roles_and_timestamps_and_skips_bad_entries()
    {
        var report = ConversationImporter.Parse(Bytes(ChatGptJson), "conversations.json");
        Assert.Equal("ChatGPT", report.Source);
        var c = Assert.Single(report.Conversations);
        Assert.Equal("Sorting", c.Title);
        Assert.Equal(new[] { "user", "assistant" }, c.Messages.Select(m => m.Role));
        Assert.Equal("Use Array.Sort", c.Messages[1].Content);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1700000010).UtcDateTime, c.Messages[0].TimestampUtc);
        Assert.Equal(2, report.Skipped); // empty mapping + non-object entry
        Assert.Contains("malformed entry x1", report.Summary());
    }

    [Fact]
    public void Claude_reads_flat_text_and_content_blocks_and_ignores_unknown_senders()
    {
        var report = ConversationImporter.Parse(Bytes(ClaudeJson), "conversations.json");
        var c = Assert.Single(report.Conversations);
        Assert.Equal("Claude", c.Source);
        Assert.Equal(new[] { "Hello", "Hi there" }, c.Messages.Select(m => m.Content));
    }

    [Fact]
    public void Gemini_takeout_strips_html_and_skips_unrelated_entries()
    {
        var report = ConversationImporter.Parse(Bytes(GeminiJson), "MyActivity.json");
        Assert.Equal("Gemini", report.Source);
        Assert.Equal(2, report.Conversations.Count);
        var first = report.Conversations.Single(c => c.Title == "What is 2+2?");
        Assert.Equal("It is 4.", first.Messages[1].Content);
        Assert.DoesNotContain("alert", first.Messages[1].Content);
        Assert.Equal(1, report.Skipped);
    }

    [Fact]
    public void Reimport_is_idempotent_and_documents_are_tagged_with_source()
    {
        ConversationStore store = NewStore();
        var first = ConversationImporter.ImportInto(store, Bytes(ClaudeJson), "conversations.json");
        var second = ConversationImporter.ImportInto(store, Bytes(ClaudeJson), "conversations.json");
        Assert.Equal(1, first.Imported);
        Assert.Equal(0, second.Imported);
        Assert.Equal("Imported 0, skipped 1 (already imported x1)", second.Summary());
        Assert.Single(store.List());
        using JsonDocument doc = JsonDocument.Parse(store.Get(first.Conversations[0].Id)!.DocumentJson);
        Assert.Equal("Claude", doc.RootElement.GetProperty("ImportSource").GetString());
        Assert.Equal("user", doc.RootElement.GetProperty("Messages")[0].GetProperty("Role").GetString());
    }

    [Fact]
    public void Zip_is_read_in_memory_and_traversal_entry_names_are_skipped()
    {
        byte[] zip = Zip(("../../evil/conversations.json", ClaudeJson), ("export/conversations.json", ClaudeJson));
        var report = ConversationImporter.Parse(zip, "export.zip");
        Assert.Null(report.Error);
        Assert.Single(report.Conversations);
        Assert.Equal(1, report.SkipReasons["unsafe archive entry name"]);
        Assert.False(File.Exists(Path.Combine(_dir, "evil", "conversations.json")));
    }

    [Fact]
    public void Zip_without_a_conversation_file_fails_honestly()
    {
        var report = ConversationImporter.Parse(Zip(("readme.txt", "hi")), "export.zip");
        Assert.NotNull(report.Error);
        Assert.StartsWith("Nothing imported", report.Summary());
    }

    [Fact]
    public void Zip_with_too_many_entries_is_refused()
    {
        var entries = Enumerable.Range(0, ConversationImporter.MaximumZipEntries + 1).Select(i => ("f" + i + ".txt", "x")).ToArray();
        var report = ConversationImporter.Parse(Zip(entries), "big.zip");
        Assert.Contains("more than", report.Error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"a\":1}")]
    [InlineData("[{\"foo\":1}]")]
    public void Unrecognised_or_malformed_input_reports_an_error_without_throwing(string text)
    {
        var report = ConversationImporter.Parse(Bytes(text), "x.json");
        Assert.NotNull(report.Error);
        Assert.Empty(report.Conversations);
    }

    [Fact]
    public void Oversized_input_is_refused()
    {
        var report = ConversationImporter.Parse(new byte[ConversationImporter.MaximumInputBytes + 1], "x.json");
        Assert.Contains("limit", report.Error);
    }

    [Fact]
    public void Cyclic_chatgpt_parent_links_terminate()
    {
        const string json = """
        [{"conversation_id":"c","current_node":"a","mapping":{
          "a":{"parent":"b","children":[],"message":{"author":{"role":"user"},"content":{"parts":["hi"]}}},
          "b":{"parent":"a","children":["a"],"message":{"author":{"role":"assistant"},"content":{"parts":["yo"]}}}}}]
        """;
        var report = ConversationImporter.Parse(Bytes(json), "conversations.json");
        Assert.Equal(2, Assert.Single(report.Conversations).Messages.Count);
    }

    private static byte[] Zip(params (string Name, string Content)[] entries)
    {
        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, true))
            foreach (var (name, content) in entries)
            {
                using var writer = new StreamWriter(archive.CreateEntry(name).Open());
                writer.Write(content);
            }
        return ms.ToArray();
    }
}
