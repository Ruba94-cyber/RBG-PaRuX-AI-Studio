namespace PaRuX.Server;

/// <summary>
/// 2026-08-27: Model Hub discovery categories/taxonomy, designed around a real, confirmed upstream
/// limitation instead of leaving categorization permanently unavailable. Hugging Face's search
/// endpoint does not return `pipeline_tag` (confirmed live, 2026-08-27:
/// `GET https://huggingface.co/api/models?search=llama&filter=gguf&limit=2&full=true` - absent
/// from every result even with `full=true`; only the per-repo detail endpoint has it) - fetching it
/// for every search result would mean an N+1 per-repo call per search, an unbounded cost this
/// service deliberately does not pay per query.
///
/// Instead: a real, deterministic, zero-extra-cost local heuristic over data already fetched for
/// every search result - the repo id, every real GGUF file name, and the search endpoint's own
/// plain `tags` array (which IS present on search results, previously fetched nowhere). This is a
/// genuine architecture/family signal for the common cases (a coding-tuned model almost always has
/// "coder"/"code" in its name; a vision-language model almost always has "vl"/"vision"/"llava" in
/// its name or files) - not a fabricated category, and honestly coarse rather than a false claim of
/// precision. "general" is the deliberate default for anything that doesn't match a specific
/// pattern, not a silent misclassification.
///
/// Deliberately NOT wired to make an extra HTTP call, ever - this is called once per already-
/// fetched search result, nothing more, keeping ModelDownloadService.SearchAsync's existing single-
/// request-per-search cost model unchanged.
/// </summary>
public static class ModelCategoryClassifier
{
    public const string Coding = "coding";
    public const string Vision = "vision";
    public const string Embedding = "embedding";
    public const string Reranker = "reranker";
    public const string Reasoning = "reasoning";
    public const string UncensoredRoleplay = "uncensored-roleplay";
    /// <summary>Models whose refusal direction was removed after training. Kept apart from fine-tuned "uncensored" models: they behave differently and are searched for by name.</summary>
    public const string Abliterated = "abliterated";
    /// <summary>Speech/audio GGUF families (speech recognition, text-to-speech). The Model Hub lists them; the text runtime does not run them.</summary>
    public const string Audio = "audio";
    public const string General = "general";

    // Order matters: checked top to bottom, first match wins. More specific/rarer families are
    // checked first so a name that happens to contain multiple keywords lands on the more
    // identifying category (e.g. a reranker embedding model is a reranker, not a generic embedder).
    private static readonly (string Category, string[] Keywords)[] Rules =
    {
        (Reranker, new[] { "reranker", "rerank", "cross-encoder" }),
        (Embedding, new[] { "embed", "embedding", "bge-", "gte-", "e5-", "nomic-embed", "sentence-transformer" }),
        // Boundary-aware: a keyword carries the dash that starts its token, so "decoder", "decode" and "vllm"
        // do not match coding or vision by accident.
        (Coding, new[] { "-coder", "-code-", "-code.", "-codeqwen", "-codegemma", "codellama", "starcoder", "codegen", "codestral" }),
        (Vision, new[] { "-vision-", "-vision.", "-vl-", "-vl.", "llava", "multimodal", "moondream", "vlm", "pixtral", "minicpm-v" }),
        (Audio, new[] { "-whisper", "-tts-", "-tts.", "orpheus", "kokoro", "-audio-", "-audio.", "musicgen" }),
        (Abliterated, new[] { "abliterated", "-abliterate", "-obliterated" }),
        (UncensoredRoleplay, new[] { "uncensored", "nsfw", "roleplay" }),
        (Reasoning, new[] { "reasoning", "-r1", "-r1-", "thinking", "-qwq", "magistral", "o1-preview", "o1-mini" }),
    };

    public static string Classify(string repoId, IEnumerable<string> ggufFileNames, IEnumerable<string>? tags)
    {
        string haystack = BuildHaystack(repoId, ggufFileNames, tags);
        foreach ((string category, string[] keywords) in Rules)
            if (keywords.Any(k => haystack.Contains(k, StringComparison.Ordinal)))
                return category;
        return General;
    }

    /// <summary>The capabilities one name carries: coding, vision and reasoning, each checked on its own, so a name
    /// can carry more than one. It uses the keywords of <see cref="Classify"/>, so one name cannot be coding on
    /// one screen and general on another.</summary>
    public static IReadOnlyList<string> CapabilitiesOf(string? fileName, string? alias, string? repoId)
    {
        string haystack = BuildHaystack(repoId ?? string.Empty, new[] { fileName ?? string.Empty, alias ?? string.Empty }, null);
        var found = new List<string>(3);
        foreach (string capability in new[] { Coding, Vision, Reasoning })
        {
            bool present = Rules.Any(rule => rule.Category == capability
                && rule.Keywords.Any(keyword => haystack.Contains(keyword, StringComparison.Ordinal)));
            if (present) found.Add(capability);
        }
        return found;
    }

    private static string BuildHaystack(string repoId, IEnumerable<string> ggufFileNames, IEnumerable<string>? tags)
    {
        IEnumerable<string> parts = new[] { repoId }
            .Concat(ggufFileNames)
            .Concat(tags ?? Enumerable.Empty<string>());
        // Padded with dashes so a keyword like "-vl-" can match at the boundary between two
        // hyphen-joined segments regardless of where it falls in the joined string.
        return "-" + string.Join('-', parts).ToLowerInvariant().Replace(' ', '-').Replace('_', '-') + "-";
    }
}
