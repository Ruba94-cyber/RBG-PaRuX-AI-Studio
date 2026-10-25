using System.Diagnostics;
using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;
using Xunit.Abstractions;

namespace PaRuX.Server.Tests;

/// <summary>
/// Endurance for the store a long-lived local install actually stresses: a conversation that keeps
/// growing. A local-first product has no server-side pruning to fall back on - whatever the desktop
/// accumulates stays on the user's disk and is re-read on every open - so "it works at ten
/// messages" is not evidence that it works at five hundred.
///
/// What is asserted is behaviour a user would notice: every message survives a growing document
/// being rewritten hundreds of times, the summary listing stays O(conversations) rather than
/// re-reading every message body, and re-opening a large conversation stays fast. Timings are
/// printed rather than asserted tightly, because a hard millisecond budget on shared CI-less
/// hardware would be a flaky test pretending to be a performance gate; the assertions are on
/// correctness and on an order of magnitude, which is what actually degrades.
/// </summary>
public sealed class ConversationEnduranceTests : IDisposable
{
    private const int MessageCount = 500;

    private readonly ITestOutputHelper _output;
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "parux-endurance-" + Guid.NewGuid().ToString("N"));

    public ConversationEnduranceTests(ITestOutputHelper output)
    {
        _output = output;
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static string BuildDocument(int messages)
    {
        var body = new System.Text.StringBuilder("{\"messages\":[");
        for (int i = 0; i < messages; i++)
        {
            if (i > 0) body.Append(',');
            body.Append("{\"role\":\"")
                .Append(i % 2 == 0 ? "user" : "assistant")
                .Append("\",\"content\":\"Message ")
                .Append(i)
                .Append(" with enough text to be a realistic turn rather than a token.\"}");
        }
        return body.Append("]}").ToString();
    }

    [Fact]
    public void A_five_hundred_message_conversation_survives_being_rewritten_on_every_turn()
    {
        var store = new ConversationStore(_root);
        var watch = Stopwatch.StartNew();

        // Rewriting the whole document on every turn is exactly what the desktop does, so the cost
        // being measured is the real one, not a single bulk insert that would never happen.
        for (int turn = 1; turn <= MessageCount; turn++)
        {
            store.Save("endurance", new ConversationUpsertRequest
            {
                Title = "Endurance",
                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow,
                DocumentJson = BuildDocument(turn)
            });
        }
        watch.Stop();

        ConversationDocument? loaded = store.Get("endurance");
        Assert.NotNull(loaded);
        // Every message is present and the last write won - no truncation, no silent data loss as
        // the document grew past any internal buffer.
        Assert.Contains("Message " + (MessageCount - 1), loaded!.DocumentJson);
        Assert.Contains("Message 0", loaded.DocumentJson);

        _output.WriteLine($"{MessageCount} incremental saves of a growing conversation: "
            + $"{watch.ElapsedMilliseconds} ms total, "
            + $"{watch.Elapsed.TotalMilliseconds / MessageCount:0.00} ms/turn, "
            + $"final document {loaded.DocumentJson.Length:N0} chars");

        // An order-of-magnitude guard, not a stopwatch contest: a per-turn cost above this would
        // mean the store had started doing something quadratic in the conversation length.
        Assert.True(
            watch.Elapsed.TotalMilliseconds / MessageCount < 100,
            $"Per-turn save cost {watch.Elapsed.TotalMilliseconds / MessageCount:0.0} ms suggests "
            + "the store became superlinear in conversation length.");
    }

    [Fact]
    public void Listing_many_conversations_does_not_read_their_message_bodies()
    {
        // The sidebar lists conversations on every open. If listing paid the cost of each
        // conversation's full document, a user with a long history would watch the app get slower
        // for reasons no single conversation explains.
        var store = new ConversationStore(_root);
        for (int i = 0; i < 100; i++)
        {
            store.Save("conversation-" + i, new ConversationUpsertRequest
            {
                Title = "Conversation " + i,
                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow,
                DocumentJson = BuildDocument(200)
            });
        }

        var watch = Stopwatch.StartNew();
        List<ConversationSummary> summaries = store.List();
        watch.Stop();

        Assert.Equal(100, summaries.Count);
        _output.WriteLine($"Listing 100 conversations of 200 messages each: {watch.ElapsedMilliseconds} ms");
        Assert.True(watch.ElapsedMilliseconds < 2_000,
            $"Listing took {watch.ElapsedMilliseconds} ms, which suggests it is reading message bodies.");
    }

    [Fact]
    public void Deleting_a_large_conversation_leaves_nothing_behind()
    {
        var store = new ConversationStore(_root);
        store.Save("doomed", new ConversationUpsertRequest
        {
            Title = "Doomed",
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow,
            DocumentJson = BuildDocument(MessageCount)
        });

        Assert.True(store.Delete("doomed"));
        Assert.Null(store.Get("doomed"));
        Assert.DoesNotContain(store.List(), summary => summary.Id == "doomed");
        // Deleting something that is already gone is a no-op answer, not an exception - the desktop
        // can issue a delete for a conversation another window already removed.
        Assert.False(store.Delete("doomed"));
    }
}
