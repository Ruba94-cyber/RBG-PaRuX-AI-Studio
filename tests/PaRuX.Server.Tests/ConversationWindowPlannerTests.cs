using PaRuX.Intelligence;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class ConversationWindowPlannerTests
{
    private static ConversationWindowPlanner.Turn System(string content) => new("system", content);
    private static ConversationWindowPlanner.Turn User(string content) => new("user", content);
    private static ConversationWindowPlanner.Turn Assistant(string content) => new("assistant", content);

    private static int Words(string text) =>
        string.IsNullOrWhiteSpace(text) ? 0 : text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;

    private static string Words(int count, string word) => string.Join(' ', Enumerable.Repeat(word, count));

    [Fact]
    public void A_conversation_that_fits_is_sent_unchanged()
    {
        var turns = new[] { System("be helpful"), User("hello there"), Assistant("hi") };

        ConversationWindowPlanner.WindowPlan plan = ConversationWindowPlanner.Fit(
            turns, contextTokens: 4096, reservedForReplyTokens: 512, countTokens: Words);

        Assert.False(plan.Compacted);
        Assert.Equal(0, plan.CompactedTurns);
        Assert.Equal(turns.Length, plan.Messages.Count);
        Assert.Contains("whole conversation fits", plan.Explanation);
    }

    [Fact]
    public void The_system_prompt_survives_compaction()
    {
        var turns = new List<ConversationWindowPlanner.Turn> { System("YOU ARE PARUX") };
        for (int index = 0; index < 40; index++) turns.Add(User(Words(50, "filler")));

        ConversationWindowPlanner.WindowPlan plan = ConversationWindowPlanner.Fit(
            turns, contextTokens: 400, reservedForReplyTokens: 100, countTokens: Words);

        Assert.True(plan.Compacted);
        // The digest is now folded into the single leading system message rather than added as a
        // second one, because Qwen's chat template rejects any system message that is not first -
        // which made every compacted turn return empty. The system prompt still survives verbatim
        // inside it, which is what this test is actually about.
        Assert.True(plan.Messages[0].IsSystem, "The system prompt must lead the compacted plan.");
        Assert.Contains("YOU ARE PARUX", plan.Messages[0].Content, StringComparison.Ordinal);
    }

    [Fact]
    public void The_most_recent_messages_are_kept_whole_and_the_oldest_are_the_ones_compacted()
    {
        var turns = new List<ConversationWindowPlanner.Turn> { System("sys") };
        for (int index = 0; index < 30; index++) turns.Add(User("message-" + index + " " + Words(20, "x")));

        ConversationWindowPlanner.WindowPlan plan = ConversationWindowPlanner.Fit(
            turns, contextTokens: 300, reservedForReplyTokens: 60, countTokens: Words);

        Assert.True(plan.Compacted);
        Assert.Contains(plan.Messages, message => message.Content.StartsWith("message-29", StringComparison.Ordinal));
        Assert.DoesNotContain(plan.Messages, message => message.Content.StartsWith("message-0 ", StringComparison.Ordinal));
    }

    [Fact]
    public void What_was_dropped_is_compacted_and_the_digest_says_so()
    {
        var turns = new List<ConversationWindowPlanner.Turn> { System("sys") };
        turns.Add(User("the deployment key is stored in the vault under project alpha"));
        for (int index = 0; index < 30; index++) turns.Add(User(Words(20, "filler")));

        ConversationWindowPlanner.WindowPlan plan = ConversationWindowPlanner.Fit(
            turns, contextTokens: 400, reservedForReplyTokens: 80, countTokens: Words);

        Assert.True(plan.Compacted);
        Assert.Contains(plan.Messages, message =>
            message.Content.Contains("compacted because it no longer fits", StringComparison.Ordinal));
        Assert.Contains(plan.Messages, message =>
            message.Content.Contains("not verbatim wording", StringComparison.Ordinal));
    }

    [Fact]
    public void Compaction_is_reported_with_real_counts_rather_than_happening_silently()
    {
        var turns = new List<ConversationWindowPlanner.Turn>();
        for (int index = 0; index < 25; index++) turns.Add(User(Words(30, "filler")));

        ConversationWindowPlanner.WindowPlan plan = ConversationWindowPlanner.Fit(
            turns, contextTokens: 300, reservedForReplyTokens: 60, countTokens: Words);

        Assert.True(plan.Compacted);
        Assert.True(plan.CompactedTurns > 0);
        Assert.True(plan.KeptTurns > 0);
        Assert.Equal(25, plan.KeptTurns + plan.CompactedTurns);
        Assert.Contains("longer than the model's physical context", plan.Explanation);
        Assert.Contains("loss-aware", plan.Explanation);
        Assert.Contains("Virtual Context", plan.Explanation);
    }

    [Fact]
    public void The_result_actually_fits_the_budget_it_was_given()
    {
        var turns = new List<ConversationWindowPlanner.Turn> { System(Words(20, "sys")) };
        for (int index = 0; index < 60; index++) turns.Add(User(Words(40, "filler")));

        const int context = 800;
        const int reserve = 200;
        ConversationWindowPlanner.WindowPlan plan = ConversationWindowPlanner.Fit(turns, context, reserve, Words);

        int actual = plan.Messages.Sum(message => Words(message.Content));
        Assert.True(actual <= context - reserve,
            $"The plan came back at {actual} tokens against a {context - reserve} token budget.");
        Assert.Equal(actual, plan.EstimatedTokens);
    }

    [Fact]
    public void The_newest_message_is_kept_even_when_it_alone_exceeds_the_window()
    {
        var turns = new List<ConversationWindowPlanner.Turn>
        {
            User(Words(50, "old")),
            User("HUGE " + Words(500, "current"))
        };

        ConversationWindowPlanner.WindowPlan plan = ConversationWindowPlanner.Fit(
            turns, contextTokens: 200, reservedForReplyTokens: 50, countTokens: Words);

        Assert.Contains(plan.Messages, message => message.Content.StartsWith("HUGE", StringComparison.Ordinal));
        Assert.Equal(1, plan.KeptTurns);
    }

    [Fact]
    public void A_system_prompt_that_fills_the_context_is_reported_instead_of_producing_an_empty_send()
    {
        var turns = new List<ConversationWindowPlanner.Turn>
        {
            System(Words(500, "instructions")),
            User("what now")
        };

        ConversationWindowPlanner.WindowPlan plan = ConversationWindowPlanner.Fit(
            turns, contextTokens: 300, reservedForReplyTokens: 50, countTokens: Words);

        Assert.True(plan.Compacted);
        Assert.Contains("system prompt alone fills", plan.Explanation);
    }

    [Fact]
    public void An_unknown_context_sends_the_conversation_unchanged_rather_than_inventing_a_limit()
    {
        var turns = new[] { User("hello") };

        ConversationWindowPlanner.WindowPlan plan = ConversationWindowPlanner.Fit(
            turns, contextTokens: 0, reservedForReplyTokens: 512, countTokens: Words);

        Assert.False(plan.Compacted);
        Assert.Single(plan.Messages);
        Assert.Contains("not known", plan.Explanation);
    }

    // Measured on the physical PC against the served Qwen tokenizer: 5.66 characters per token for
    // English prose, 4.24 Italian, 4.43 C#, 2.23 JSON, 2.15 logs, 1.16 git SHAs and hex tokens.
    // The 3.0 assumption is therefore wrong in both directions, which is the whole reason the chat
    // path injects the runtime's own tokenizer. These two tests pin the real behaviour so nobody
    // "restores" the old belief that the heuristic only ever fails safe.
    [Fact]
    public void The_default_heuristic_over_counts_prose()
    {
        string prose = string.Join(' ', Enumerable.Repeat("the assistant remembers the conversation", 40));
        int measuredAtProseRatio = (int)(prose.Length / 5.66);

        Assert.True(ConversationWindowPlanner.EstimateTokens(prose) > measuredAtProseRatio);
    }

    [Fact]
    public void The_default_heuristic_under_counts_identifier_dense_text()
    {
        string hashes = string.Join(' ', Enumerable.Repeat("5cfecc4 2b2d585 741e2a2 EED4C8642250FD46", 40));
        int measuredAtHexRatio = (int)(hashes.Length / 1.16);

        Assert.True(ConversationWindowPlanner.EstimateTokens(hashes) < measuredAtHexRatio);
    }

    /// <summary>
    /// A tokenizer as dense as the densest content measured on the physical PC: one token per
    /// non-space character, i.e. about 1.16 characters per token. The slicing helpers used to turn
    /// a token budget into characters by multiplying by the assumed 3.0, and returned that slice
    /// without ever checking it, so against this counter every edge they cut was roughly three
    /// times the budget it was cut to respect.
    /// </summary>
    private static int Dense(string text) =>
        string.IsNullOrEmpty(text) ? 0 : text.Count(character => character != ' ');

    [Fact]
    public void An_oversized_message_stays_inside_the_window_when_the_tokenizer_is_denser_than_the_heuristic()
    {
        string document = string.Join(' ', Enumerable.Repeat("5cfecc4 2b2d585 741e2a2 EED4C8642250FD46", 400));
        var turns = new[] { System("sys"), User(document) };

        ConversationWindowPlanner.WindowPlan plan = ConversationWindowPlanner.Fit(
            turns, contextTokens: 4096, reservedForReplyTokens: 512, countTokens: Dense);

        Assert.True(plan.Compacted);
        Assert.True(
            plan.EstimatedTokens <= plan.AvailableTokens,
            $"The plan sends about {plan.EstimatedTokens:N0} tokens into {plan.AvailableTokens:N0} usable ones.");
        Assert.Equal(plan.EstimatedTokens, plan.Messages.Sum(turn => Dense(turn.Content)));
    }

    [Fact]
    public void A_long_history_stays_inside_the_window_when_the_tokenizer_is_denser_than_the_heuristic()
    {
        var turns = new List<ConversationWindowPlanner.Turn> { System("sys") };
        for (int index = 0; index < 120; index++)
        {
            turns.Add(User($"step {index}: 5cfecc4 2b2d585 741e2a2 EED4C8642250FD46 {new string('x', 200)}"));
            turns.Add(Assistant($"done {index}: 0eb5a87 73f0306 667daf0 {new string('y', 200)}"));
        }

        ConversationWindowPlanner.WindowPlan plan = ConversationWindowPlanner.Fit(
            turns, contextTokens: 8192, reservedForReplyTokens: 1024, countTokens: Dense);

        Assert.True(plan.Compacted);
        Assert.True(
            plan.EstimatedTokens <= plan.AvailableTokens,
            $"The plan sends about {plan.EstimatedTokens:N0} tokens into {plan.AvailableTokens:N0} usable ones.");
    }

    /// <summary>
    /// The production counter charges a fixed per-message overhead on top of the tokenizer's count,
    /// so a slice can be over budget even at one character per token. The slicing must converge on
    /// what the counter actually says rather than trusting any ratio, and must terminate.
    /// </summary>
    [Fact]
    public void Slicing_converges_on_the_counter_even_when_it_charges_a_per_message_overhead()
    {
        static int DenseWithOverhead(string text) =>
            string.IsNullOrEmpty(text) ? 0 : text.Count(character => character != ' ') + 8;

        string document = string.Join(' ', Enumerable.Repeat("5cfecc4 EED4C8642250FD46", 500));
        var turns = new[] { System("sys"), User(document) };

        ConversationWindowPlanner.WindowPlan plan = ConversationWindowPlanner.Fit(
            turns, contextTokens: 2048, reservedForReplyTokens: 256, countTokens: DenseWithOverhead);

        Assert.True(plan.Compacted);
        Assert.True(
            plan.EstimatedTokens <= plan.AvailableTokens,
            $"The plan sends about {plan.EstimatedTokens:N0} tokens into {plan.AvailableTokens:N0} usable ones.");
    }

    [Fact]
    public void A_sparse_tokenizer_keeps_more_of_the_conversation_verbatim_than_the_heuristic_would()
    {
        // 5.66 characters per token, the measured English-prose end of the range. The heuristic
        // would price this conversation at nearly twice its real cost and compact it for nothing.
        static int Sparse(string text) => (int)Math.Ceiling((text ?? string.Empty).Length / 5.66);

        var turns = new List<ConversationWindowPlanner.Turn> { System("be helpful") };
        for (int index = 0; index < 40; index++)
            turns.Add(User(string.Join(' ', Enumerable.Repeat("the assistant remembers the conversation", 30))));

        ConversationWindowPlanner.WindowPlan heuristic = ConversationWindowPlanner.Fit(
            turns, contextTokens: 8192, reservedForReplyTokens: 1024);
        ConversationWindowPlanner.WindowPlan measured = ConversationWindowPlanner.Fit(
            turns, contextTokens: 8192, reservedForReplyTokens: 1024, countTokens: Sparse);

        Assert.True(
            measured.KeptTurns > heuristic.KeptTurns,
            $"The real tokenizer kept {measured.KeptTurns} turns, the heuristic {heuristic.KeptTurns}.");
    }

    [Fact]
    public void Loss_aware_digest_preserves_a_critical_middle_constraint_instead_of_only_the_opening()
    {
        var turns = new List<ConversationWindowPlanner.Turn> { System("sys") };
        turns.Add(User(
            Words(90, "intro") + ". MUST preserve Android offline mode and never use a cloud fallback. " +
            Words(90, "outro")));
        for (int index = 0; index < 28; index++) turns.Add(Assistant(Words(24, "filler")));
        turns.Add(User("current question"));

        ConversationWindowPlanner.WindowPlan plan = ConversationWindowPlanner.Fit(
            turns, contextTokens: 430, reservedForReplyTokens: 80, countTokens: Words);

        string digest = Assert.Single(plan.Messages.Where(message =>
            message.Role == "system" && message.Content.Contains("loss-aware", StringComparison.Ordinal))).Content;
        Assert.Contains("MUST preserve Android offline mode", digest, StringComparison.Ordinal);
        Assert.Contains("never use a cloud fallback", digest, StringComparison.Ordinal);
    }

    [Fact]
    public void Loss_aware_digest_preserves_paths_failures_and_verification_evidence()
    {
        var turns = new List<ConversationWindowPlanner.Turn> { System("sys") };
        turns.Add(Assistant("Changed src/PaRuX.Server/ChatProxyService.cs. dotnet test failed: 2 tests failed. Next step: fix ContextWindowPlannerTests."));
        for (int index = 0; index < 24; index++) turns.Add(User(Words(24, "noise")));
        turns.Add(User("continue"));

        ConversationWindowPlanner.WindowPlan plan = ConversationWindowPlanner.Fit(
            turns, contextTokens: 390, reservedForReplyTokens: 70, countTokens: Words);

        string digest = string.Join("\n", plan.Messages.Where(message => message.Role == "system").Select(message => message.Content));
        Assert.Contains("ChatProxyService.cs", digest, StringComparison.Ordinal);
        Assert.Contains("test failed", digest, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Next step", digest, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Repeated_old_evidence_is_deduplicated_so_it_does_not_consume_the_digest()
    {
        var turns = new List<ConversationWindowPlanner.Turn> { System("sys") };
        for (int index = 0; index < 8; index++)
            turns.Add(Assistant("Build failed in src/App.cs: error CS1002; next step fix parser."));
        for (int index = 0; index < 24; index++) turns.Add(User(Words(20, "filler")));
        turns.Add(User("continue"));

        ConversationWindowPlanner.WindowPlan plan = ConversationWindowPlanner.Fit(
            turns, contextTokens: 390, reservedForReplyTokens: 70, countTokens: Words);

        string digest = string.Join("\n", plan.Messages.Where(message => message.Role == "system").Select(message => message.Content));
        Assert.Equal(1, CountOccurrences(digest, "Build failed in src/App.cs"));
    }

    [Fact]
    public void Virtual_context_recalls_a_relevant_ancient_page_instead_of_only_recent_archived_turns()
    {
        var turns = new List<ConversationWindowPlanner.Turn> { System("sys") };
        turns.Add(User("Decision: project NEBULA-771 must keep the legacy binary protocol framing exactly unchanged."));
        for (int index = 0; index < 180; index++)
            turns.Add(Assistant("historical filler batch " + index + " " + Words(18, "noise")));
        turns.Add(User("For NEBULA-771, what protocol constraint did we decide earlier?"));

        ConversationWindowPlanner.WindowPlan plan = ConversationWindowPlanner.Fit(
            turns, contextTokens: 520, reservedForReplyTokens: 90, countTokens: Words);

        string virtualMemory = string.Join("\n", plan.Messages
            .Where(message => message.Role == "system")
            .Select(message => message.Content));
        Assert.True(plan.Compacted);
        Assert.Contains("NEBULA-771", virtualMemory, StringComparison.Ordinal);
        Assert.Contains("legacy binary protocol framing", virtualMemory, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("historical page", virtualMemory, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Virtual_context_can_represent_more_than_one_million_logical_tokens_under_a_small_native_window()
    {
        var turns = new List<ConversationWindowPlanner.Turn> { System("sys") };
        turns.Add(User("MUST retain marker ONE_MILLION_CONTEXT_SENTINEL for the final acceptance answer."));
        for (int index = 0; index < 1_000; index++)
            turns.Add(Assistant("bulk-" + index + " routine archived observation"));
        turns.Add(User("Return the ONE_MILLION_CONTEXT_SENTINEL requirement from the old history."));

        static int LogicalTokens(string text)
        {
            if (text.StartsWith("bulk-", StringComparison.Ordinal)) return 1_100;
            return Words(text);
        }

        const int physicalContext = 8_192;
        const int replyReserve = 1_024;
        ConversationWindowPlanner.WindowPlan plan = ConversationWindowPlanner.Fit(
            turns, physicalContext, replyReserve, LogicalTokens);

        Assert.True(plan.Compacted);
        Assert.True(plan.CompactedTurns >= 900);
        Assert.True(plan.EstimatedTokens <= physicalContext - replyReserve);
        Assert.Contains("ONE_MILLION_CONTEXT_SENTINEL",
            string.Join("\n", plan.Messages.Select(message => message.Content)), StringComparison.Ordinal);
        Assert.Contains("logical history may be far larger", plan.Explanation, StringComparison.OrdinalIgnoreCase);
    }

    private static int CountOccurrences(string text, string value)
    {
        int count = 0;
        int start = 0;
        while ((start = text.IndexOf(value, start, StringComparison.Ordinal)) >= 0)
        {
            count++;
            start += value.Length;
        }
        return count;
    }

    /// <summary>
    /// One message larger than the physical window must still be answerable. PaRuX's promise is a
    /// logical context beyond the native one, and that used to cover only older conversation turns:
    /// measured on the physical PC, a single 74,000-character message was handed to llama.cpp whole
    /// and came back with zero content tokens. It now goes through Virtual Context - verbatim edges,
    /// paged and recalled middle - and the plan says so rather than pretending it was sent intact.
    /// </summary>
    [Fact]
    public void A_single_oversized_message_is_served_through_virtual_context_not_refused()
    {
        string instruction = "Answer with the single word ACKNOWLEDGED.";
        string ending = "That was the document; remember the requirement above.";
        string middle = string.Join(' ', Enumerable.Repeat("filler", 40_000));
        var turns = new List<ConversationWindowPlanner.Turn>
        {
            System("You are PaRuX."),
            User(instruction + " " + middle + " " + ending)
        };

        ConversationWindowPlanner.WindowPlan plan = ConversationWindowPlanner.Fit(
            turns, contextTokens: 8192, reservedForReplyTokens: 1024);

        Assert.True(plan.Compacted, "A lossy representation must always be reported.");
        Assert.True(plan.EstimatedTokens <= plan.AvailableTokens,
            "The rebuilt turn must fit the window: " + plan.EstimatedTokens + " > " + plan.AvailableTokens);

        string sent = string.Join(" ", plan.Messages.Select(turn => turn.Content));
        Assert.Contains("ACKNOWLEDGED", sent, StringComparison.Ordinal);
        Assert.Contains("remember the requirement", sent, StringComparison.Ordinal);
        Assert.Contains("Virtual Context", plan.Explanation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_oversized_message_that_still_fits_after_paging_keeps_the_system_prompt()
    {
        var turns = new List<ConversationWindowPlanner.Turn>
        {
            System("SYSTEM_MARKER stays."),
            User(string.Join(' ', Enumerable.Repeat("filler", 30_000)))
        };

        ConversationWindowPlanner.WindowPlan plan = ConversationWindowPlanner.Fit(
            turns, contextTokens: 8192, reservedForReplyTokens: 1024);

        Assert.Contains(plan.Messages, turn => turn.Content.Contains("SYSTEM_MARKER", StringComparison.Ordinal));
        Assert.True(plan.EstimatedTokens <= plan.AvailableTokens);
    }

    /// <summary>
    /// Qwen's chat template raises "System message must be at the beginning" for any system message
    /// that is not first, and llama.cpp turns that into HTTP 500. Emitting the Virtual Context
    /// digest as its own system turn therefore made every compacted turn - long conversations and
    /// oversized single messages alike - come back empty, while short turns worked. Exactly one
    /// system message, at the front, is a hard requirement of the feature working at all.
    /// </summary>
    [Fact]
    public void A_compacted_plan_keeps_exactly_one_leading_system_message()
    {
        var turns = new List<ConversationWindowPlanner.Turn> { System("SYSTEM_MARKER.") };
        for (int i = 0; i < 40; i++)
        {
            turns.Add(User(string.Join(' ', Enumerable.Repeat("history", 200))));
            turns.Add(Assistant(string.Join(' ', Enumerable.Repeat("reply", 200))));
        }
        turns.Add(User("What was the marker?"));

        ConversationWindowPlanner.WindowPlan plan = ConversationWindowPlanner.Fit(
            turns, contextTokens: 8192, reservedForReplyTokens: 1024);

        Assert.True(plan.Compacted, "This conversation must exceed the window for the test to mean anything.");
        Assert.Single(plan.Messages.Where(turn => turn.IsSystem));
        Assert.True(plan.Messages[0].IsSystem, "The only system message must be first.");
        Assert.Contains("SYSTEM_MARKER", plan.Messages[0].Content, StringComparison.Ordinal);
    }

    [Fact]
    public void An_oversized_single_message_also_keeps_one_leading_system_message()
    {
        var turns = new List<ConversationWindowPlanner.Turn>
        {
            System("SYSTEM_MARKER."),
            User(string.Join(' ', Enumerable.Repeat("filler", 40_000)))
        };

        ConversationWindowPlanner.WindowPlan plan = ConversationWindowPlanner.Fit(
            turns, contextTokens: 8192, reservedForReplyTokens: 1024);

        Assert.True(plan.Compacted);
        Assert.Single(plan.Messages.Where(turn => turn.IsSystem));
        Assert.True(plan.Messages[0].IsSystem);
    }
}
