using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// The lexical half of retrieval ranked by counting how many distinct query words a chunk
/// contained. That treats every term as equally informative, which is the one thing lexical
/// ranking must not do: for "how do I configure the CUDA runtime", a chunk containing "configure"
/// and "runtime" outranked one containing only "CUDA" - the term that actually decides which chunk
/// answers the question. Open WebUI and AnythingLLM both use BM25 for exactly this reason.
///
/// Each test below pins one of the three properties the count did not have, stated as the
/// retrieval outcome it changes rather than as a formula.
/// </summary>
public sealed class Bm25ScorerTests
{
    private static readonly string[] CudaRuntimeQuery = { "configure", "cuda", "runtime" };

    [Fact]
    public void A_rare_term_outweighs_two_common_ones()
    {
        // "configure" and "runtime" appear in most of the corpus; "cuda" appears in one chunk.
        var documents = new List<string?>
        {
            "To configure the runtime you configure the runtime settings.",           // 2 common terms
            "The CUDA device is selected here.",                                      // 1 rare term
            "Configure the runtime for playback.",
            "Configure the runtime for logging.",
            "Configure the runtime for exports."
        };
        var scorer = new Bm25Scorer(CudaRuntimeQuery, documents);

        Assert.True(
            scorer.Score(1) > scorer.Score(0),
            "The chunk with the discriminative term must rank above the one with two ubiquitous terms.");
        // The old ranking is the opposite way round, which is the defect being pinned.
        Assert.True(scorer.DistinctTermsMatched(0) > scorer.DistinctTermsMatched(1));
    }

    [Fact]
    public void Repeating_one_term_saturates_instead_of_winning_outright()
    {
        var documents = new List<string?>
        {
            "cuda cuda cuda cuda cuda cuda cuda cuda cuda cuda",
            "cuda"
        };
        var scorer = new Bm25Scorer(new[] { "cuda" }, documents);

        // More occurrences still score higher, but nothing like ten times higher - a chunk cannot
        // win a query by repeating the word.
        Assert.True(scorer.Score(0) > scorer.Score(1));
        Assert.True(scorer.Score(0) < scorer.Score(1) * 3);
    }

    [Fact]
    public void A_short_precise_chunk_beats_a_long_one_that_merely_contains_the_term()
    {
        var documents = new List<string?>
        {
            "The CUDA runtime is configured here.",
            "The CUDA runtime is configured here. " + string.Join(' ', Enumerable.Repeat("filler words about unrelated subjects", 60))
        };
        var scorer = new Bm25Scorer(CudaRuntimeQuery, documents);

        Assert.True(
            scorer.Score(0) > scorer.Score(1),
            "Length normalisation must stop padding from earning relevance.");
    }

    [Fact]
    public void A_chunk_with_none_of_the_query_terms_scores_zero()
    {
        // Callers gate relevance on > 0, so this must behave exactly like the old overlap count's
        // zero, or chunks that used to be excluded would start appearing.
        var documents = new List<string?> { "Entirely unrelated prose about baking bread." };
        var scorer = new Bm25Scorer(CudaRuntimeQuery, documents);

        Assert.Equal(0, scorer.Score(0));
        Assert.Equal(0, scorer.DistinctTermsMatched(0));
    }

    [Fact]
    public void A_term_present_in_every_candidate_still_scores_above_zero()
    {
        // The classic BM25 IDF goes negative when a term appears in more than half the corpus,
        // which would make a chunk score WORSE for containing a query word than for omitting it.
        var documents = new List<string?> { "cuda one", "cuda two", "cuda three" };
        var scorer = new Bm25Scorer(new[] { "cuda" }, documents);

        for (int index = 0; index < documents.Count; index++) Assert.True(scorer.Score(index) > 0);
    }

    [Fact]
    public void Matching_is_case_insensitive_like_the_scorer_it_replaces()
    {
        var documents = new List<string?> { "The CUDA Runtime", "the cuda runtime" };
        var scorer = new Bm25Scorer(new[] { "cuda", "runtime" }, documents);

        Assert.Equal(2, scorer.DistinctTermsMatched(0));
        Assert.Equal(2, scorer.DistinctTermsMatched(1));
    }

    [Fact]
    public void Words_of_two_characters_or_fewer_are_ignored_as_before()
    {
        // The stores' Tokenize discards these on the query side, so the document side must agree
        // or the two halves would count different things.
        var documents = new List<string?> { "an ok go it" };
        var scorer = new Bm25Scorer(new[] { "ok", "go" }, documents);

        Assert.Equal(0, scorer.DistinctTermsMatched(0));
    }

    [Theory]
    [InlineData("cuda.runtime")]
    [InlineData("cuda,runtime")]
    [InlineData("cuda;runtime")]
    [InlineData("\"cuda\" 'runtime'")]
    [InlineData("cuda\nruntime")]
    public void The_delimiter_set_matches_the_stores_own_tokenizer(string content)
    {
        var scorer = new Bm25Scorer(new[] { "cuda", "runtime" }, new List<string?> { content });
        Assert.Equal(2, scorer.DistinctTermsMatched(0));
    }

    [Fact]
    public void An_empty_query_or_empty_candidate_set_is_handled_without_throwing()
    {
        Assert.Equal(0, new Bm25Scorer(Array.Empty<string>(), new List<string?> { "text" }).Score(0));
        Assert.Equal(0, new Bm25Scorer(new[] { "cuda" }, new List<string?>()).Score(0));
        Assert.Equal(0, new Bm25Scorer(new[] { "cuda" }, new List<string?> { null, "" }).Score(0));
    }

    [Fact]
    public void An_out_of_range_index_returns_zero_rather_than_throwing()
    {
        var scorer = new Bm25Scorer(new[] { "cuda" }, new List<string?> { "cuda" });
        Assert.Equal(0, scorer.Score(-1));
        Assert.Equal(0, scorer.Score(99));
        Assert.Equal(0, scorer.DistinctTermsMatched(99));
    }

    [Fact]
    public void Distinct_term_coverage_is_still_reported_separately_from_relevance()
    {
        // Coverage answers "how much of the question does this chunk touch", which is a different
        // question from "how relevant is it", and callers still surface it.
        var scorer = new Bm25Scorer(CudaRuntimeQuery, new List<string?>
        {
            "configure cuda runtime",
            "cuda cuda cuda"
        });

        Assert.Equal(3, scorer.DistinctTermsMatched(0));
        Assert.Equal(1, scorer.DistinctTermsMatched(1));
    }
}
