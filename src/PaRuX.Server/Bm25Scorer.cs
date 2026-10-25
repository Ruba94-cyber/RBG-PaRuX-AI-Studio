namespace PaRuX.Server;

/// <summary>
/// Okapi BM25 over a candidate set, replacing the "count how many distinct query words appear"
/// ranking the lexical half of retrieval used before.
///
/// Why counting was not good enough: it weighs every query term equally. For "how do I configure
/// the CUDA runtime", a chunk that happens to contain "configure" and "runtime" scored higher than
/// one containing only "CUDA" - even though "CUDA" is the term that actually decides which chunk
/// answers the question. BM25 fixes exactly that with three terms the count has none of:
///
///   * IDF - a term appearing in few candidates is worth more than one appearing in most of them,
///     so the discriminative word dominates instead of being outvoted by common ones;
///   * saturating term frequency - the fifth occurrence of a word adds far less than the first,
///     so a chunk cannot win by repeating one term;
///   * length normalisation - a long chunk does not out-rank a short precise one merely by
///     containing more words.
///
/// IDF is computed over the candidate set actually being ranked rather than the whole corpus. That
/// is the set the ranking has to discriminate within, it needs no separate persisted index that
/// could drift from the documents, and it stays correct when the ANN path has already narrowed the
/// candidates.
///
/// Tokenisation is deliberately <see cref="LexicalOverlapScorer"/>'s, so the two never disagree
/// about what a word is: same delimiters, same minimum length, and the caller's own stopword policy
/// applied to the query side.
/// </summary>
internal sealed class Bm25Scorer
{
    /// <summary>Term-frequency saturation. 1.2 is the standard Okapi value; higher makes repeated
    /// terms count for more, and retrieval here favours precision over repetition.</summary>
    private const double K1 = 1.2;

    /// <summary>Length-normalisation strength. 0.75 is the standard Okapi value: full
    /// normalisation (1.0) punishes long chunks harder than a chunked corpus warrants, since
    /// chunk length here is an artifact of the splitter rather than of the author.</summary>
    private const double B = 0.75;

    private readonly string[] _queryWords;
    private readonly int _documentCount;
    private readonly int[] _termFrequencies;   // flattened [document * queryWords.Length + term]
    private readonly int[] _documentLengths;
    private readonly double[] _inverseDocumentFrequency;
    private readonly double _averageDocumentLength;

    /// <param name="queryWords">Already tokenised and stopword-filtered by the caller, so each
    /// store keeps its own policy.</param>
    /// <param name="documents">The candidate set being ranked, in the caller's order. Indexes into
    /// this list are what <see cref="Score"/> takes.</param>
    internal Bm25Scorer(string[] queryWords, IReadOnlyList<string?> documents)
    {
        _queryWords = queryWords ?? Array.Empty<string>();
        _documentCount = documents?.Count ?? 0;
        int termCount = _queryWords.Length;

        _termFrequencies = new int[Math.Max(1, _documentCount * Math.Max(1, termCount))];
        _documentLengths = new int[Math.Max(1, _documentCount)];
        _inverseDocumentFrequency = new double[Math.Max(1, termCount)];
        if (_documentCount == 0 || termCount == 0) return;

        var documentFrequency = new int[termCount];
        long totalLength = 0;

        for (int document = 0; document < _documentCount; document++)
        {
            string? content = documents![document];
            if (string.IsNullOrEmpty(content)) continue;

            int offset = document * termCount;
            int length = 0;
            foreach (LexicalTokenWalker.Token token in LexicalTokenWalker.Walk(content))
            {
                length++;
                int term = MatchTerm(content, token);
                if (term >= 0) _termFrequencies[offset + term]++;
            }
            _documentLengths[document] = length;
            totalLength += length;
        }

        for (int document = 0; document < _documentCount; document++)
        {
            int offset = document * termCount;
            for (int term = 0; term < termCount; term++)
                if (_termFrequencies[offset + term] > 0) documentFrequency[term]++;
        }

        _averageDocumentLength = totalLength == 0 ? 1 : (double)totalLength / _documentCount;

        for (int term = 0; term < termCount; term++)
        {
            // The "plus one" (BM25+) form, which stays positive for a term present in every
            // candidate. The classic form goes negative there, which would let a chunk score
            // WORSE for containing a query word than for omitting it.
            double df = documentFrequency[term];
            _inverseDocumentFrequency[term] =
                Math.Log(1.0 + ((_documentCount - df + 0.5) / (df + 0.5)));
        }
    }

    /// <summary>BM25 relevance of one candidate, by its index in the list passed to the
    /// constructor. Zero when the candidate contains none of the query terms - the same
    /// "is this relevant at all" signal the old overlap count gave, so callers gating on
    /// &gt; 0 keep their exact behaviour.</summary>
    internal double Score(int documentIndex)
    {
        if (documentIndex < 0 || documentIndex >= _documentCount || _queryWords.Length == 0) return 0;

        int termCount = _queryWords.Length;
        int offset = documentIndex * termCount;
        double lengthRatio = _averageDocumentLength <= 0
            ? 1
            : _documentLengths[documentIndex] / _averageDocumentLength;
        double normalisation = K1 * (1 - B + (B * lengthRatio));

        double score = 0;
        for (int term = 0; term < termCount; term++)
        {
            int frequency = _termFrequencies[offset + term];
            if (frequency == 0) continue;
            score += _inverseDocumentFrequency[term] * (frequency * (K1 + 1)) / (frequency + normalisation);
        }
        return score;
    }

    /// <summary>How many DISTINCT query terms this candidate contains - the value the old overlap
    /// count produced. Retained because it is a genuinely different signal from relevance
    /// ("coverage of the question") and is still reported to callers.</summary>
    internal int DistinctTermsMatched(int documentIndex)
    {
        if (documentIndex < 0 || documentIndex >= _documentCount) return 0;
        int termCount = _queryWords.Length;
        int offset = documentIndex * termCount;
        int matched = 0;
        for (int term = 0; term < termCount; term++)
            if (_termFrequencies[offset + term] > 0) matched++;
        return matched;
    }

    private int MatchTerm(string content, LexicalTokenWalker.Token token)
    {
        if (token.Length <= 2) return -1;
        for (int term = 0; term < _queryWords.Length; term++)
        {
            string word = _queryWords[term];
            if (word.Length != token.Length) continue;
            if (content.AsSpan(token.Start, token.Length)
                .Equals(word.AsSpan(), StringComparison.OrdinalIgnoreCase))
                return term;
        }
        return -1;
    }
}
