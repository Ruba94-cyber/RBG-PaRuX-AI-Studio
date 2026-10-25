using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Differential proof that the allocation-free retrieval scorer is behaviourally identical to the
/// original Overlap(queryWords, Tokenize(content)) it replaced. The optimization's whole claim is
/// "same ranking, less work", so that claim is tested directly rather than assumed - including the
/// cases where the two implementations could plausibly diverge: casing, punctuation-adjacent
/// tokens, repeated words, stopwords, sub-3-character tokens, unusually long tokens and non-ASCII.
/// </summary>
public sealed class RagOverlapEquivalenceTests
{
    private static readonly string[] Queries =
    {
        "how does the router forward packets",
        "DNS RESOLVES a Domain Name",
        "compost drainage clay soil",
        "a an the of it is",                       // stopwords/short words only
        "Kepler's laws describe orbital speed",
        "unusuallylongsingletokenthatshouldstillmatchcorrectly",
        "café naïve über",                          // non-ASCII
        "",                                          // empty query
        "x yz abc",                                  // boundary around the length>2 filter
    };

    private static readonly string?[] Contents =
    {
        null,
        "",
        "   ",
        "A router forwards packets between networks by consulting its routing table.",
        "ROUTER, ROUTER; router... router! (router) \"router\" 'router'",
        "DNS resolves a human-readable domain name into an IP address.",
        "compost improves drainage in clay soil and water retention in sandy soil",
        "Kepler's laws describe how orbital speed changes.",
        "unusuallylongsingletokenthatshouldstillmatchcorrectly appears here once",
        "Café naïve Über CAFÉ NAÏVE ÜBER",
        "x yz abc abcd",
        "the of and it is a an",
        "punctuation.separated,tokens;here:and\"more\"'quoted'",
        "tabs\tand\nnewlines\rmixed   with     spaces",
        "repeated repeated repeated repeated distinct",
    };

    [Fact]
    public void OverlapWithQuery_matches_the_original_tokenize_and_overlap_exactly()
    {
        var compared = 0;
        foreach (string query in Queries)
        {
            string[] queryWords = RagStore.Tokenize(query);
            var scratch = new LexicalOverlapScorer.Scratch(queryWords);

            foreach (string? content in Contents)
            {
                int expected = queryWords.Length == 0 || string.IsNullOrEmpty(content)
                    ? 0
                    : RagStore.Overlap(queryWords, RagStore.Tokenize(content));
                int actual = LexicalOverlapScorer.Count(scratch, content);

                Assert.True(
                    expected == actual,
                    $"query=\"{query}\" content=\"{content}\" expected={expected} actual={actual}");
                compared++;
            }
        }

        Assert.Equal(Queries.Length * Contents.Length, compared);
    }

    /// <summary>The scratch buffer is reused across chunks, so a match found for one chunk must
    /// never leak into the score of the next one.</summary>
    [Fact]
    public void Reused_scratch_state_does_not_leak_matches_between_chunks()
    {
        string[] queryWords = RagStore.Tokenize("router packets networks");
        var scratch = new LexicalOverlapScorer.Scratch(queryWords);

        Assert.Equal(3, LexicalOverlapScorer.Count(scratch, "router packets networks"));
        Assert.Equal(0, LexicalOverlapScorer.Count(scratch, "nothing relevant whatsoever"));
        Assert.Equal(1, LexicalOverlapScorer.Count(scratch, "only router appears"));
        Assert.Equal(3, LexicalOverlapScorer.Count(scratch, "networks packets router"));
    }
}
