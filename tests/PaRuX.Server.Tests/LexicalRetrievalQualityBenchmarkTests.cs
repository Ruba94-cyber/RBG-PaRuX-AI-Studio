using System.Text;
using PaRuX.Server;
using Xunit;
using Xunit.Abstractions;

namespace PaRuX.Server.Tests;

/// <summary>
/// Before/after measurement for the lexical half of retrieval, kept as a test so the improvement is
/// re-measured on every run rather than asserted once in a report.
///
/// The corpus is built to expose the exact weakness of counting matched words: every query mixes
/// one discriminative term with two or three that recur throughout the corpus. A counter ranks by
/// how many words overlap, so a chunk carrying only the common words beats the one chunk that
/// carries the term the question is actually about. That is not a contrived edge case - it is what
/// ordinary technical questions look like ("how do I configure the CUDA runtime", "where do I set
/// the reranker model").
///
/// Metrics are the standard retrieval pair:
///   * Recall@1  - how often the single correct chunk is ranked first
///   * MRR       - mean reciprocal rank, which also rewards being close when not first
///
/// The old scorer is measured, not remembered: LexicalOverlapScorer is still present and is run
/// over the same corpus and queries, so the comparison is against real behaviour.
/// </summary>
public sealed class LexicalRetrievalQualityBenchmarkTests
{
    private readonly ITestOutputHelper _output;

    public LexicalRetrievalQualityBenchmarkTests(ITestOutputHelper output) => _output = output;

    // The shape that exposes the defect, and the shape real documentation actually has: the chunk
    // that answers the question is short and specific, while boilerplate-heavy pages repeat every
    // common word in the query. A counter ranks by how many query words overlap, so the
    // boilerplate wins with three ubiquitous terms against the one term that identifies the
    // answer. An earlier version of this corpus gave the target chunks the boilerplate too, which
    // let the counter score 100% as well - the guard assertion at the end of the test caught that
    // and is kept for exactly that reason.

    private const string Boilerplate =
        "Configure the runtime settings and the model settings for the application.";

    private sealed record Case(string Query, string ExpectedId);

    private static readonly (string Id, string Text)[] Corpus =
    {
        ("cuda",      "CUDA acceleration notes. Select the NVIDIA device."),
        ("reranker",  "Reranker notes. Pick the cross-encoder used for scoring."),
        ("vulkan",    "Vulkan backend notes. The portable GPU path."),
        ("embedding", "Embedding provider notes. The sentence vectoriser."),
        ("noise1",    Boilerplate),
        ("noise2",    Boilerplate + " Runtime settings and model settings are described here."),
        ("noise3",    Boilerplate + " How to configure runtime settings and model settings."),
        ("noise4",    Boilerplate + " Configure the settings; configure the model."),
        ("noise5",    Boilerplate + " Runtime and model settings you can configure."),
        ("noise6",    Boilerplate + " Settings for the runtime, settings for the model.")
    };

    private static readonly Case[] Cases =
    {
        new("configure the cuda runtime settings", "cuda"),
        new("configure the reranker model settings", "reranker"),
        new("configure the vulkan runtime settings", "vulkan"),
        new("configure the embedding model settings", "embedding")
    };

    /// <summary>The same tokenisation the stores apply to the query side.</summary>
    private static string[] Tokenize(string text) => text
        .Split(new[] { ' ', '\t', '\n', '\r', '.', ',', '!', '?', ';', ':', '"', '\'' },
            StringSplitOptions.RemoveEmptyEntries)
        .Select(word => word.ToLowerInvariant())
        .Where(word => word.Length > 2)
        .Distinct(StringComparer.Ordinal)
        .ToArray();

    private static (double RecallAt1, double Mrr) Measure(Func<string[], IReadOnlyList<string?>, Func<int, double>> rank)
    {
        string[] documents = Corpus.Select(entry => entry.Text).ToArray();
        int hits = 0;
        double reciprocalRankTotal = 0;

        foreach (Case testCase in Cases)
        {
            string[] queryWords = Tokenize(testCase.Query);
            Func<int, double> score = rank(queryWords, documents!);

            List<string> ordered = Enumerable.Range(0, Corpus.Length)
                .OrderByDescending(score)
                // Ties broken by corpus order, identically for both scorers, so neither is
                // flattered by the tie-break rather than by its ranking.
                .ThenBy(index => index)
                .Select(index => Corpus[index].Id)
                .ToList();

            int rankOfExpected = ordered.IndexOf(testCase.ExpectedId) + 1;
            if (rankOfExpected == 1) hits++;
            reciprocalRankTotal += 1.0 / rankOfExpected;
        }

        return ((double)hits / Cases.Length, reciprocalRankTotal / Cases.Length);
    }

    [Fact]
    public void Bm25_measurably_beats_the_word_count_it_replaced()
    {
        (double oldRecall, double oldMrr) = Measure((queryWords, documents) =>
        {
            var scratch = new LexicalOverlapScorer.Scratch(queryWords);
            return index => LexicalOverlapScorer.Count(scratch, documents[index]);
        });

        (double newRecall, double newMrr) = Measure((queryWords, documents) =>
        {
            var scorer = new Bm25Scorer(queryWords, documents);
            return scorer.Score;
        });

        var report = new StringBuilder();
        report.AppendLine("Lexical retrieval quality, " + Corpus.Length + " chunks, " + Cases.Length + " queries");
        report.AppendLine($"  overlap count : Recall@1 {oldRecall:P0}   MRR {oldMrr:0.000}");
        report.AppendLine($"  BM25          : Recall@1 {newRecall:P0}   MRR {newMrr:0.000}");
        _output.WriteLine(report.ToString());

        // BM25 must never be worse than what it replaced, and on this corpus - where the
        // discriminative term is the whole point - it must be strictly better.
        Assert.True(newRecall >= oldRecall, report.ToString());
        Assert.True(newMrr > oldMrr, report.ToString());

        // The old scorer's specific failure, pinned so a regression back to it is visible: it
        // cannot put the right chunk first when common words outnumber the discriminative one.
        Assert.True(oldRecall < 1.0, "The corpus no longer exposes the defect this measures.");
        Assert.Equal(1.0, newRecall);
    }
}
