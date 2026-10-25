using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// The cost of getting this wrong runs in both directions, so both are pinned. Too narrow - which is
/// what it was, a literal "search the web" phrase - and a local model answers a perishable question
/// from stale training data and sounds confident doing it. Too broad and PaRuX sends the user's
/// ordinary questions to a search engine they did not ask to involve, which is the opposite of what
/// a local-first product is for.
/// </summary>
public sealed class WebIntentDetectorTests
{
    private static readonly DateOnly Today = new(2026, 9, 3);

    [Theory]
    [InlineData("search the web for llama.cpp benchmarks")]
    [InlineData("cerca sul web le novità di llama.cpp")]
    [InlineData("look up online what changed")]
    [InlineData("google it for me")]
    // A pasted link is a request about that page, not about the model's memory of it.
    [InlineData("summarise https://github.com/ggml-org/llama.cpp/releases")]
    [InlineData("what does http://example.com/post say?")]
    public void An_explicit_request_or_a_pasted_link_asks_for_the_web(string prompt)
    {
        Assert.True(WebIntentDetector.IsWebSearchRequest(prompt));
        Assert.True(WebIntentDetector.WantsWebContext(prompt, Today));
    }

    [Theory]
    [InlineData("what is the latest llama.cpp release?")]
    [InlineData("who is the current CEO of that company?")]
    [InlineData("what happened today in the EU AI act rollout")]
    [InlineData("any news about RTX 5080 driver problems?")]
    [InlineData("what's the current version of CUDA?")]
    [InlineData("qual è l'ultima versione di llama.cpp?")]
    [InlineData("ci sono novità sul decreto?")]
    [InlineData("what was announced in 2026?")]
    public void A_question_whose_answer_goes_stale_asks_for_the_web(string prompt)
    {
        Assert.True(WebIntentDetector.IsLikelyTimeSensitive(prompt, Today), prompt);
    }

    [Theory]
    [InlineData("write a python function that reverses a string")]
    [InlineData("explain how attention works in a transformer")]
    [InlineData("refactor this class to use dependency injection")]
    [InlineData("spiegami la ricorsione con un esempio")]
    [InlineData("what is 17 * 23?")]
    [InlineData("summarise the document I attached")]
    public void An_ordinary_question_does_not_reach_for_the_web(string prompt)
    {
        Assert.False(WebIntentDetector.WantsWebContext(prompt, Today), prompt);
    }

    [Theory]
    // A recency word about the user's own machine is not a question about the world. Searching the
    // web for "my latest commit" is useless and a small privacy surprise.
    [InlineData("what does my latest commit change?")]
    [InlineData("show the current directory")]
    [InlineData("what is the current branch?")]
    [InlineData("explain the latest commit in this repository")]
    [InlineData("what does this function in my project do?")]
    public void A_recency_word_about_the_users_own_machine_is_not_a_web_question(string prompt)
    {
        Assert.False(WebIntentDetector.IsLikelyTimeSensitive(prompt, Today), prompt);
    }

    [Fact]
    public void A_year_the_model_may_not_have_seen_counts_but_an_old_one_does_not()
    {
        Assert.True(WebIntentDetector.IsLikelyTimeSensitive("what shipped in 2026?", Today));
        Assert.True(WebIntentDetector.IsLikelyTimeSensitive("what shipped in 2025?", Today));
        Assert.False(WebIntentDetector.IsLikelyTimeSensitive("what shipped in 2019?", Today));
    }

    [Fact]
    public void Whole_words_only_so_a_lookalike_does_not_trigger_a_search()
    {
        Assert.False(WebIntentDetector.IsLikelyTimeSensitive("explain concurrency in Go", Today));
        Assert.False(WebIntentDetector.IsLikelyTimeSensitive("what is a newsletter parser?", Today));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Nothing_to_read_is_not_a_web_question(string? prompt)
    {
        Assert.False(WebIntentDetector.IsWebSearchRequest(prompt!));
        Assert.False(WebIntentDetector.IsLikelyTimeSensitive(prompt!, Today));
    }

    // ---- the gate the chat path actually calls ----

    [Fact]
    public void The_internet_switch_still_decides_whether_the_network_may_be_reached()
    {
        const string perishable = "what is the latest llama.cpp release?";

        Assert.False(ContextEngineService.ShouldIncludeWeb(perishable, "off", Today));
        Assert.True(ContextEngineService.ShouldIncludeWeb(perishable, null, Today));

        // "required" keeps searching for every turn, unchanged.
        Assert.True(ContextEngineService.ShouldIncludeWeb("write a python function", "required", Today));
        Assert.False(ContextEngineService.ShouldIncludeWeb("write a python function", null, Today));
    }
}
