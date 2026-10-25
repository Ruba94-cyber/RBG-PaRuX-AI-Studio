namespace PaRuX.Server;

/// <summary>
/// The single definition of "what counts as a word" for every lexical retrieval path
/// (<see cref="LexicalOverlapScorer"/>, <see cref="Bm25Scorer"/>, and the stores' own Tokenize).
///
/// It exists so a second scorer could be added without a second, subtly different tokenizer: two
/// lexical signals that disagree about word boundaries would rank the same text differently for
/// no reason a user could ever see or explain.
///
/// The enumerator is a struct and yields positions rather than substrings, so walking a candidate
/// allocates nothing. This runs over every candidate chunk on every chat turn, which is why the
/// original scorer was deliberately written allocation-free in the first place.
/// </summary>
internal static class LexicalTokenWalker
{
    /// <summary>The exact delimiter set the stores' own Tokenize splits on.</summary>
    internal static bool IsDelimiter(char c) =>
        c is ' ' or '\t' or '\n' or '\r' or '.' or ',' or '!' or '?' or ';' or ':' or '"' or '\'';

    internal readonly record struct Token(int Start, int Length);

    internal static Enumerator Walk(string content) => new(content);

    internal struct Enumerator
    {
        private readonly string _content;
        private int _index;

        internal Enumerator(string content)
        {
            _content = content ?? string.Empty;
            _index = 0;
            Current = default;
        }

        public Token Current { get; private set; }

        public Enumerator GetEnumerator() => this;

        public bool MoveNext()
        {
            while (_index < _content.Length && IsDelimiter(_content[_index])) _index++;
            if (_index >= _content.Length) return false;

            int start = _index;
            while (_index < _content.Length && !IsDelimiter(_content[_index])) _index++;
            Current = new Token(start, _index - start);
            return true;
        }
    }
}
