namespace PaRuX.Server;

/// <summary>
/// Allocation-free lexical overlap scoring shared by the retrieval paths that run on every chat
/// turn (RagStore chunks, MemoryStore entries).
///
/// It is behaviourally identical to the <c>Overlap(queryWords, Tokenize(content))</c> form both
/// stores previously used: their Tokenize already applies the length-&gt;2 and stopword filters to
/// the query side, so any content token equal to a query word necessarily passed those filters,
/// and Tokenize's Distinct() cannot change set membership. What differs is cost - the old form
/// allocated, for every candidate on every query, a fully lowercased copy of that candidate's
/// text, a split array of all its tokens, a LINQ pipeline and a HashSet. Over a few thousand
/// candidates that dominated retrieval latency.
///
/// RagOverlapEquivalenceTests proves the equivalence directly rather than trusting this comment.
/// </summary>
internal static class LexicalOverlapScorer
{
    /// <summary>Delegated to <see cref="LexicalTokenWalker"/> so this and BM25 can never disagree
    /// about where a word ends. The optimized inner loop below is unchanged.</summary>
    private static bool IsTokenDelimiter(char c) => LexicalTokenWalker.IsDelimiter(c);

    /// <summary>Per-query scratch state, created once per retrieval call rather than once per
    /// candidate. Callers must pass the query words produced by their own Tokenize, so each
    /// store keeps its own stopword policy.</summary>
    internal readonly struct Scratch
    {
        public Scratch(string[] queryWords)
        {
            QueryWords = queryWords ?? Array.Empty<string>();
            Matched = new bool[QueryWords.Length];
            int longest = 0;
            foreach (string word in QueryWords) longest = Math.Max(longest, word.Length);
            Lowered = new char[Math.Max(1, longest)];
            // A content token can only equal a query word of exactly the same length, so tokens
            // of any other length are rejected before doing any case conversion at all.
            LengthWanted = new bool[longest + 1];
            foreach (string word in QueryWords) LengthWanted[word.Length] = true;
        }

        public string[] QueryWords { get; }
        public bool[] Matched { get; }
        public char[] Lowered { get; }
        public bool[] LengthWanted { get; }
    }

    /// <summary>Counts how many distinct query words occur as tokens in <paramref name="content"/>.</summary>
    internal static int Count(in Scratch scratch, string? content)
    {
        string[] queryWords = scratch.QueryWords;
        if (queryWords.Length == 0 || string.IsNullOrEmpty(content)) return 0;

        bool[] matched = scratch.Matched;
        Array.Clear(matched, 0, matched.Length);
        char[] loweredBuffer = scratch.Lowered;
        int maxLength = loweredBuffer.Length;
        int found = 0;
        int index = 0;

        while (index < content.Length && found < queryWords.Length)
        {
            while (index < content.Length && IsTokenDelimiter(content[index])) index++;
            int start = index;
            while (index < content.Length && !IsTokenDelimiter(content[index])) index++;
            int length = index - start;

            // Tokenize discards anything of length <= 2, and a token can only match a query word
            // of identical length, so everything else is skipped without touching the characters.
            if (length <= 2 || length > maxLength || !scratch.LengthWanted[length]) continue;

            Span<char> lowered = loweredBuffer.AsSpan(0, length);
            if (content.AsSpan(start, length).ToLowerInvariant(lowered) != length) continue;

            for (int q = 0; q < queryWords.Length; q++)
            {
                if (matched[q] || queryWords[q].Length != length) continue;
                if (!lowered.SequenceEqual(queryWords[q].AsSpan())) continue;
                matched[q] = true;
                found++;
                break;
            }
        }
        return found;
    }
}
