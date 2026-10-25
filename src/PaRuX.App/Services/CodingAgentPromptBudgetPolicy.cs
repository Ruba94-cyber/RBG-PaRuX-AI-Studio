using System;

namespace PaRuX.Services
{
    /// <summary>Leaves room for the task, protocol, recent observations and model output before adding repository evidence.</summary>
    public static class CodingAgentPromptBudgetPolicy
    {
        /// <summary>Upper bound on replayed evidence, so one enormous window cannot turn the whole
        /// prompt into transcript and leave no room for repository context or the answer.</summary>
        public const int MaximumRecentEvidenceCharacters = 48_000;
        public const int SmallWindowContextTokens = 8_192;
        public const int SmallWindowFixedShareCharacterLimit = 7_000;
        public const int SmallWindowMinimumRecentEvidenceCharacters = 512;
        public const int SmallWindowMinimumRepositoryCharacters = 1_000;

        /// <summary>
        /// Until the coding role's model has been resolved, use at most the compact 8k profile.
        /// An unknown resident size gets the same conservative bound; a successful exact-token
        /// probe can rebuild the request with the selected model's actual context afterwards.
        /// </summary>
        public static int InitialPromptContextTokens(int residentContextTokens, bool reflectsCodingModel)
        {
            if (residentContextTokens <= 0) return SmallWindowContextTokens;
            if (reflectsCodingModel) return Math.Max(0, residentContextTokens);
            return Math.Min(residentContextTokens, SmallWindowContextTokens);
        }

        /// <summary>
        /// How much of the agent's own execution evidence - what it already read, ran and observed
        /// - is replayed into the next prompt. This is the agent's working memory, and it has to
        /// scale with the window: a fixed budget means a bigger context buys fresh repository
        /// context but no more recall, so the agent keeps re-reading what it has already seen.
        ///
        /// Measured on the physical PC (2026-09-21): the budget was a hard-coded 2,500 characters
        /// while a single observation was allowed 4,000 and one artifact read of a 154-line file
        /// returned 5,930 - so not even one full read survived into the following step. The coding
        /// agent re-read the same file until its repeat guard and exploration budget stopped it,
        /// and raising the physical window from 8,192 to 32,768 tokens changed nothing because
        /// this constant did not move. Floors at the previous value, so no configuration gets less
        /// recall than it had before.
        /// </summary>
        public static int RecentEvidenceCharacters(int contextTokens, int floorCharacters)
            => RecentEvidenceCharacters(contextTokens, floorCharacters, 0, 0, DefaultGenerationReserveTokens);

        public static int RecentEvidenceCharacters(
            int contextTokens,
            int floorCharacters,
            int fixedPromptCharacters,
            int taskCharacters,
            int generationReserveTokens)
        {
            floorCharacters = Math.Max(0, floorCharacters);
            if (contextTokens <= 0) return floorCharacters;

            // Account for the fixed prompt, task, minimum useful repository slice, framing margin,
            // and the reasoning plus visible-answer reserve before assigning replayed evidence.
            long promptCapacity = (long)Math.Max(0, contextTokens - Math.Max(0, generationReserveTokens)) * 3;
            int minimumRepository = MinimumRepositoryForContext(contextTokens);
            long remaining = promptCapacity
                - Math.Max(0, fixedPromptCharacters)
                - Math.Max(0, taskCharacters)
                - minimumRepository
                - 2_000;
            int available = (int)Math.Clamp(remaining, 0, MaximumRecentEvidenceCharacters);

            int effectiveFloor = contextTokens <= SmallWindowContextTokens
                ? Math.Min(floorCharacters, SmallWindowMinimumRecentEvidenceCharacters)
                : floorCharacters;
            long scaled = (long)Math.Max(0, contextTokens - DefaultGenerationReserveTokens) * 3 / 4;
            int target = (int)Math.Clamp(Math.Max(scaled, effectiveFloor), 0, MaximumRecentEvidenceCharacters);
            return Math.Min(target, available);
        }

        public static int RepositoryCharacters(int contextTokens, int suggestedCharacters,
            int taskCharacters, int systemCharacters, int recentEvidenceCharacters)
            => RepositoryCharacters(contextTokens, suggestedCharacters, taskCharacters,
                systemCharacters, recentEvidenceCharacters, DefaultGenerationReserveTokens);

        /// <summary>
        /// Tokens withheld for the model's own output when nothing better is known. A reasoning
        /// model needs its reasoning budget on top of the visible answer, which is why callers
        /// that know the configured budget pass it in - see <see cref="GenerationReserveTokens"/>.
        /// </summary>
        public const int DefaultGenerationReserveTokens = 2_048;

        /// <summary>The smallest repository-evidence slice worth compiling at all.</summary>
        public const int MinimumRepositoryCharacters = 3_000;

        /// <summary>
        /// What to withhold for generation given the runtime's configured reasoning budget. A
        /// coding launch runs at reasoning budget 4,096, so reserving only 2,048 overfills the
        /// window: the model spends the reserve on hidden reasoning and emits nothing.
        ///
        /// Measured on the physical PC (2026-09-21) at n_ctx 8,192: system prompt ~5,200 chars,
        /// action schema ~2,500, repository evidence 6,600 and working memory 4,600 left roughly
        /// 1,800 tokens for output against a 4,096-token reasoning budget. The agent returned
        /// "invalid action protocol - missing action (model returned an empty response)" twice in
        /// a row and the run died.
        /// </summary>
        public static int GenerationReserveTokens(int reasoningBudgetTokens)
        {
            // The visible answer is small - one JSON action - but it must not share the reserve
            // with reasoning, so the two are added rather than maxed.
            const int VisibleAnswerTokens = 768;
            int reasoning = reasoningBudgetTokens > 0 ? reasoningBudgetTokens : 0;
            return Math.Clamp(reasoning + VisibleAnswerTokens, DefaultGenerationReserveTokens, 16_384);
        }

        public static int RepositoryCharacters(int contextTokens, int suggestedCharacters,
            int taskCharacters, int systemCharacters, int recentEvidenceCharacters,
            int generationReserveTokens)
        {
            if (contextTokens <= 0)
                return Math.Clamp(suggestedCharacters, MinimumRepositoryCharacters, CodingContextCompiler.MaximumMaxChars);

            // Character counts are only an estimate of tokens; three characters per token is
            // intentionally conservative for mixed code/text.
            long available = (long)Math.Max(0, contextTokens - Math.Max(0, generationReserveTokens)) * 3
                - Math.Max(0, taskCharacters)
                - Math.Max(0, systemCharacters)
                - Math.Max(0, recentEvidenceCharacters)
                - 2_000; // schema transport, prompt framing and tokenizer variance
            return (int)Math.Clamp(Math.Min(available, suggestedCharacters),
                (long)MinimumRepositoryForContext(contextTokens), CodingContextCompiler.MaximumMaxChars);
        }

        /// <summary>
        /// True when the window cannot hold the fixed cost (system prompt, schema, framing) plus
        /// the generation reserve and still leave the minimum repository slice. The budgets clamp
        /// to their floors in that case, which silently overfills the context - so the agent
        /// reports it instead of letting the model return empty answers nobody can explain.
        /// </summary>
        /// <summary>
        /// Last-resort admission check on the ACTUAL constructed prompt. The earlier repository
        /// budget is a prediction, while restore hints, a persisted plan and memory retrieval
        /// can all increase the final request. The exact runtime tokenizer may not be available
        /// to the desktop agent; three characters per token is a conservative planning estimate,
        /// not a claim of exact token counting. Keep a 2k-character framing margin in addition
        /// to the model's generation reserve. Unknown windows retain legacy behaviour.
        /// </summary>
        public static bool FitsFinalPrompt(
            int contextTokens, int generationReserveTokens,
            int systemCharacters, int schemaCharacters, int userCharacters)
        {
            if (contextTokens <= 0) return true;
            long capacity = (long)Math.Max(0, contextTokens - Math.Max(0, generationReserveTokens)) * 3 - 2_000;
            long required = (long)Math.Max(0, systemCharacters)
                + Math.Max(0, schemaCharacters) + Math.Max(0, userCharacters);
            return capacity >= 0 && required <= capacity;
        }

        /// <summary>
        /// Deterministic retry envelope. A failed prompt-fit check first reduces replay
        /// (which remains fully preserved in the checkpoint), then reduces repository evidence.
        /// Neither task instructions nor system/tool protocol nor persisted state are discarded.
        /// After the final pass, the caller must pause instead of sending an oversized prompt.
        /// </summary>
        /// <summary>
        /// Recognize only explicit provider context-window overflow responses. Do not treat
        /// unrelated HTTP 400, transient network faults, authentication, rate limits, or
        /// ordinary output-length termination as a context overflow.
        /// </summary>
        public static bool IsProviderContextOverflow(string? error)
        {
            if (string.IsNullOrWhiteSpace(error)) return false;
            string[] signals =
            {
                "context_length_exceeded",
                "context window exceeded",
                "maximum context length",
                "exceeds the context window",
                "exceeded the context window",
                "prompt exceeds",
                "prompt too long",
                "too many tokens",
                "n_ctx exceeded",
                "context size exceeded",
                "context shift is disabled"
            };
            foreach (string signal in signals)
                if (error.IndexOf(signal, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            return false;
        }

        public static (int RecentCharacters, int RepositoryCharacters) RecoveryBudgets(
            int pass, int initialRecentCharacters, int initialRepositoryCharacters)
        {
            int recent = Math.Max(0, initialRecentCharacters);
            int repository = Math.Max(MinimumRepositoryCharacters, initialRepositoryCharacters);
            return pass switch
            {
                0 => (Math.Min(recent, 2_500), repository),
                1 => (0, repository),
                2 => (0, Math.Min(repository, 6_000)),
                3 => (0, MinimumRepositoryCharacters),
                _ => throw new ArgumentOutOfRangeException(nameof(pass))
            };
        }

        public static bool IsContextTooSmall(
            int contextTokens, int taskCharacters, int systemCharacters,
            int recentEvidenceCharacters, int generationReserveTokens)
        {
            if (contextTokens <= 0) return false; // Unknown context is handled conservatively elsewhere.
            long available = (long)Math.Max(0, contextTokens - Math.Max(0, generationReserveTokens)) * 3
                - Math.Max(0, taskCharacters)
                - Math.Max(0, systemCharacters)
                - Math.Max(0, recentEvidenceCharacters)
                - 2_000;
            return available < MinimumRepositoryForContext(contextTokens);
        }

        private static int MinimumRepositoryForContext(int contextTokens) =>
            contextTokens > 0 && contextTokens <= SmallWindowContextTokens
                ? SmallWindowMinimumRepositoryCharacters
                : MinimumRepositoryCharacters;
    }
}
