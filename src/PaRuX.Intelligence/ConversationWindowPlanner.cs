using System.Text;
using System.Text.RegularExpressions;

namespace PaRuX.Intelligence;

/// <summary>
/// Fits an arbitrarily long conversation into the real context window served by the active model.
///
/// The physical attention window and PaRuX's logical/virtual context are deliberately different
/// concepts. Recent turns always stay verbatim. Older history is converted into a bounded virtual
/// memory made of two layers: a durable state capsule (constraints, decisions, identifiers, paths,
/// failures, verification and unresolved work) and task-relevant historical pages selected against
/// the newest request. This lets an 8k/32k/128k model continue work over histories that are hundreds
/// of thousands or millions of tokens long without pretending the model has native attention over
/// all of them at once.
///
/// Compaction is deterministic and local-only. It never depends on another LLM call, so it remains
/// available precisely when inference is overloaded, restarting or unavailable.
/// </summary>
public static class ConversationWindowPlanner
{
    public enum TokenCountBasis { Estimated, PartlyEstimated, Tokenizer }

    /// <summary>
    /// Last-resort ratio for <see cref="EstimateTokens"/>, used only when no real tokenizer can be
    /// reached. It is an estimate and cannot be anything better: measured against the served Qwen
    /// tokenizer the true ratio is 5.66 for English prose, 4.24 for Italian, 4.43 for C#, 2.23 for
    /// JSON, 2.15 for logs and 1.16 for git SHAs and hex tokens. No constant is correct across that
    /// range, which is why the production chat path injects the runtime's own tokenizer and why
    /// every place that turns a token budget into characters now measures the text in front of it
    /// rather than trusting this number.
    /// </summary>
    public const double AssumedCharactersPerToken = 3.0;

    // A large physical context should be allowed to spend more of its budget recalling old state.
    // Small-context models still receive only what safely fits because Fit clamps this against the
    // actual remaining budget before BuildVirtualMemory is called.
    private const int MaximumVirtualMemoryTokens = 8_192;
    private const int MaximumDigestFragmentCharacters = 420;
    private const int VirtualPageTargetTokens = 2_048;
    private const int MaximumRecalledPages = 8;
    private const int MaximumFragmentsPerPage = 10;
    private const int MaximumSliceFitAttempts = 4;

    private static readonly Regex PathLike = new(
        @"(?:[A-Za-z]:\\[^\s]+|(?:^|\s)(?:\.?\.?/)?(?:[\w.-]+/)+[\w.-]+|\b[\w.-]+\.(?:cs|fs|vb|cpp|c|h|hpp|py|js|jsx|ts|tsx|java|kt|kts|rs|go|swift|dart|rb|php|json|ya?ml|toml|xml|md|sln|csproj|gradle|log)\b)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex IdentifierLike = new(
        @"\b(?:[0-9a-f]{7,40}|[A-Z][A-Z0-9_]{3,}|(?:issue|pr|pull request|task|ticket|commit|branch|build|test)\s*#?\s*[A-Za-z0-9._/-]+)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex QueryTerm = new(
        @"[A-Za-z0-9_][A-Za-z0-9_./:#-]{2,}", RegexOptions.Compiled);

    private static readonly string[] HighValueSignals =
    {
        "must", "must not", "do not", "don't", "never", "always", "require", "requirement",
        "constraint", "acceptance", "decision", "decided", "keep", "preserve", "important",
        "error", "failed", "failure", "exception", "crash", "regression", "blocked", "blocker",
        "todo", "remaining", "next step", "follow-up", "verify", "verified", "verification",
        "test", "tests", "build", "lint", "benchmark", "commit", "branch", "worktree", "diff",
        "security", "permission", "sandbox", "context", "token", "model", "runtime", "api",
        "endpoint", "version", "id", "sha", "result", "passed", "warning", "dependency"
    };

    private static readonly HashSet<string> QueryStopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "this", "that", "with", "from", "into", "then", "than", "when", "where", "what",
        "have", "will", "should", "could", "would", "there", "their", "these", "those", "after",
        "before", "make", "made", "using", "used", "user", "task", "code", "file", "files",
        "implement", "implementation", "change", "changes", "update", "updated", "please",
        "questo", "questa", "quello", "quella", "della", "delle", "degli", "sono", "come",
        "fare", "fatto", "anche", "tutto", "tutti", "dopo", "prima", "senza", "ancora",
        "continue", "continua", "answer", "reply", "conversation", "message"
    };

    public sealed record Turn(string Role, string Content)
    {
        public bool IsSystem => string.Equals(Role, "system", StringComparison.OrdinalIgnoreCase);
    }

    public sealed record WindowPlan(
        IReadOnlyList<Turn> Messages,
        int KeptTurns,
        int CompactedTurns,
        int EstimatedTokens,
        int AvailableTokens,
        bool Compacted,
        string Explanation)
    {
        /// <summary>The basis used for the token counts in this plan.</summary>
        public string CountBasis { get; init; } = nameof(TokenCountBasis.Estimated);
    }

    private sealed record DigestFragment(
        int TurnIndex,
        string Role,
        string Text,
        int Score,
        bool IsAnchor,
        int QueryScore);

    private sealed record VirtualPage(
        int Index,
        int FirstTurnIndex,
        int LastTurnIndex,
        IReadOnlyList<DigestFragment> Fragments,
        int QueryScore,
        int StateScore,
        bool IsNewestPage);

    public static int EstimateTokens(string text) =>
        (int)Math.Ceiling((text ?? string.Empty).Length / AssumedCharactersPerToken) + 4;

    public static WindowPlan Fit(
        IReadOnlyList<Turn> turns,
        int contextTokens,
        int reservedForReplyTokens = 1024,
        Func<string, int>? countTokens = null,
        TokenCountBasis countBasis = TokenCountBasis.Estimated,
        Func<TokenCountBasis>? countBasisProvider = null)
    {
        Func<string, int> count = countTokens ?? EstimateTokens;
        // Without a counter the planner measures with EstimateTokens, so the plan is always labeled Estimated.
        TokenCountBasis CurrentCountBasis() => countTokens == null
            ? TokenCountBasis.Estimated
            : countBasisProvider?.Invoke() ?? countBasis;

        if (turns == null || turns.Count == 0)
        {
            return new WindowPlan(Array.Empty<Turn>(), 0, 0, 0, Math.Max(0, contextTokens - reservedForReplyTokens),
                false, "There is nothing to send.") { CountBasis = CurrentCountBasis().ToString() };
        }

        int available = contextTokens - reservedForReplyTokens;
        if (contextTokens <= 0 || available <= 0)
        {
            return new WindowPlan(turns, turns.Count, 0, Total(turns, count), Math.Max(0, available), false,
                "The model's usable context is not known, so the conversation is sent unchanged.")
                { CountBasis = CurrentCountBasis().ToString() };
        }

        int total = Total(turns, count);
        if (total <= available)
        {
            return new WindowPlan(turns, turns.Count, 0, total, available, false,
                $"The whole conversation fits: about {total:N0} of {available:N0} usable tokens.")
                { CountBasis = CurrentCountBasis().ToString() };
        }

        var system = turns.Where(turn => turn.IsSystem).ToList();
        var body = turns.Where(turn => !turn.IsSystem).ToList();
        int systemTokens = Total(system, count);

        int budget = available - systemTokens;
        if (budget <= 0)
        {
            return new WindowPlan(system, system.Count, body.Count, systemTokens, available, true,
                "The system prompt alone fills this model's context, so no conversation history could "
                + "be included. A longer physical context or a shorter system prompt is needed.")
                { CountBasis = CurrentCountBasis().ToString() };
        }

        // Virtual memory gets a meaningful but bounded slice. At tiny windows we still preserve
        // recent dialogue first; at 32k/128k windows the historical recall budget grows instead of
        // being artificially frozen at the old 900-token digest ceiling.
        int virtualReserve = Math.Clamp(budget / 4, 96, MaximumVirtualMemoryTokens);
        virtualReserve = Math.Min(virtualReserve, Math.Max(0, budget - 32));

        var kept = new List<Turn>();
        int used = 0;
        int index = body.Count - 1;
        for (; index >= 0; index--)
        {
            int cost = count(body[index].Content);
            if (used + cost > budget - virtualReserve) break;
            used += cost;
            kept.Insert(0, body[index]);
        }

        var dropped = body.Take(index + 1).ToList();

        // The current request is never intentionally removed. If it is itself larger than the
        // window, the runtime remains responsible for token-level handling of that one turn - but
        // that has to be *reported*, not assumed to work. Measured on the physical PC: a single
        // 74,000-character message was handed straight to llama.cpp, which answered with zero
        // content tokens, and because nothing had been dropped the plan was not marked compacted,
        // so no context_compacted event was emitted and the user saw only "The model returned an
        // empty response" with no reason at all. What is sent is unchanged; the silence is not.
        if (kept.Count == 0 && body.Count > 0)
        {
            kept.Add(body[^1]);
            dropped = body.Take(body.Count - 1).ToList();
            used = count(body[^1].Content);

            if (used > budget)
            {
                return FitOversizedTurn(system, body[^1], systemTokens, budget, available, dropped.Count, count)
                    with { CountBasis = CurrentCountBasis().ToString() };
            }
        }

        int recalledPages = 0;
        string historyMemory = string.Empty;
        if (dropped.Count > 0)
        {
            int memoryBudget = Math.Max(0, budget - used);
            string newestRequest = body.LastOrDefault(turn =>
                string.Equals(turn.Role, "user", StringComparison.OrdinalIgnoreCase))?.Content
                ?? body.LastOrDefault()?.Content
                ?? string.Empty;
            historyMemory = BuildVirtualMemory(
                dropped, newestRequest, memoryBudget, count, out recalledPages);
        }

        var result = MergeSystemWithVirtualMemory(system, historyMemory);
        result.AddRange(kept);

        int estimated = Total(result, count);
        return new WindowPlan(result, kept.Count, dropped.Count, estimated, available, dropped.Count > 0,
            $"This conversation is longer than the model's physical context. The {kept.Count:N0} most recent "
            + $"messages are sent in full and the {dropped.Count:N0} older ones are represented by PaRuX Virtual Context: "
            + $"a loss-aware durable-state digest plus {recalledPages:N0} task-relevant historical page(s), using about "
            + $"{estimated:N0} of {available:N0} usable tokens. The logical history may be far larger than the native model window.")
            { CountBasis = CurrentCountBasis().ToString() };
    }

    private static int Total(IEnumerable<Turn> turns, Func<string, int> count) =>
        turns.Sum(turn => count(turn.Content));

    /// <summary>
    /// Hierarchical virtual memory for arbitrarily long history. It does not claim that old tokens
    /// remain simultaneously inside the model's attention window. Instead it keeps a global state
    /// capsule and retrieves the old pages most related to the current request, under one hard token
    /// budget. Complexity is linear in the archived history and the emitted prompt stays bounded.
    /// </summary>
    /// <summary>
    /// One message that does not fit the physical window on its own - a pasted document, a log, a
    /// whole file. PaRuX's promise is that logical context can exceed the native window, and that
    /// promise previously covered only *older conversation turns*: a single oversized turn was
    /// handed to the runtime whole, which answered with nothing at all. It now goes through the
    /// same Virtual Context machinery. The opening and closing of the message are kept verbatim,
    /// because that is where the instruction and the question almost always live, and the middle is
    /// paged and recalled against them, so what reaches the model is bounded and relevant instead of
    /// truncated or refused. This is lossy and says so: the returned plan is always marked
    /// compacted, and its explanation names the real figures.
    /// </summary>
    private static WindowPlan FitOversizedTurn(
        List<Turn> system,
        Turn oversized,
        int systemTokens,
        int budget,
        int available,
        int alreadyDroppedTurns,
        Func<string, int> count)
    {
        int originalTokens = count(oversized.Content);

        // Roughly two fifths of the budget is spent on verbatim edges, split between the opening
        // (the instruction) and the ending (usually the actual question). The rest funds recall.
        int verbatimBudget = Math.Max(64, budget * 2 / 5);
        int headTokens = Math.Max(32, verbatimBudget / 2);
        int tailTokens = Math.Max(32, verbatimBudget - headTokens);

        string head = TakeTokensFromStart(oversized.Content, headTokens, count);
        string tail = TakeTokensFromEnd(oversized.Content, tailTokens, count);

        int middleStart = head.Length;
        int middleEnd = Math.Max(middleStart, oversized.Content.Length - tail.Length);
        string middle = oversized.Content[middleStart..middleEnd];

        // Pages are sized from the middle's own ratio for the same reason the edges are: a page of
        // 2,048 tokens' worth of prose and a page of 2,048 tokens' worth of commit hashes are not
        // the same number of characters, and a page that is really four times its nominal size
        // pushes the recall budget over the window it was computed against.
        var pages = new List<Turn>();
        int pageCharacters = Math.Max(256, (int)(VirtualPageTargetTokens * LocalCharactersPerToken(middle, count)));
        for (int offset = 0; offset < middle.Length; offset += pageCharacters)
            pages.Add(new Turn("user", middle.Substring(offset, Math.Min(pageCharacters, middle.Length - offset))));

        string rebuilt = pages.Count == 0
            ? head + tail
            : head + "\n\n[… middle of this message represented by PaRuX Virtual Context below …]\n\n" + tail;

        int recalledPages = 0;
        string virtualMemory = string.Empty;
        int memoryBudget = Math.Max(0, budget - count(rebuilt));
        if (pages.Count > 0 && memoryBudget > 24)
            virtualMemory = BuildVirtualMemory(pages, head + "\n" + tail, memoryBudget, count, out recalledPages);

        List<Turn> result = MergeSystemWithVirtualMemory(system, virtualMemory);
        result.Add(new Turn(oversized.Role, rebuilt));

        int estimated = Total(result, count);
        return new WindowPlan(result, 1, alreadyDroppedTurns + pages.Count, estimated, available, true,
            $"This single message is about {originalTokens:N0} tokens, larger than the {budget:N0} left for it "
            + $"after the system prompt, so it is not sent verbatim. Its opening and ending are kept in full and "
            + $"the middle is represented by PaRuX Virtual Context: {pages.Count:N0} page(s) archived, "
            + $"{recalledPages:N0} recalled as most relevant, using about {estimated:N0} of {available:N0} usable "
            + "tokens. The model is answering from a digest of the middle, not the exact wording.");
    }

    /// <summary>
    /// Characters per token measured on <paramref name="text"/> itself instead of assumed globally.
    ///
    /// Measured on the physical PC against the served Qwen tokenizer, the true ratio spans 1.16
    /// (git SHAs, hex tokens, API keys) to 5.66 (English prose) - a 4.9x range no single constant
    /// covers. <see cref="AssumedCharactersPerToken"/> therefore does not merely over-count prose:
    /// on identifier-dense text it under-counts by more than 2x, so a slice sized from it is over
    /// the very budget it was cut to respect. Sizing from the text's own ratio is self-correcting,
    /// and costs one extra count of a string the caller has already counted once.
    /// </summary>
    private static double LocalCharactersPerToken(string text, Func<string, int> count)
    {
        if (string.IsNullOrEmpty(text)) return AssumedCharactersPerToken;
        int tokens = count(text);
        return tokens <= 0 ? AssumedCharactersPerToken : Math.Max(0.25, (double)text.Length / tokens);
    }

    /// <summary>
    /// Shrinks <paramref name="characters"/> until <paramref name="slice"/> really fits the budget.
    ///
    /// The local ratio is an average, so a slice can still land over budget when the text is not
    /// uniform - a prose document that ends in a stack trace, say. Each attempt rescales by the
    /// measured overshoot, which converges in one or two passes. The final fallback is one
    /// character per token: no tokenizer emits more tokens than the string has characters, so that
    /// bound always holds and the loop can never leave an over-budget slice behind.
    /// </summary>
    private static string FitSliceToBudget(
        string text, int budgetTokens, int characters, Func<string, int> count, bool fromEnd)
    {
        string slice = fromEnd ? CutFromEnd(text, characters) : CutFromStart(text, characters);
        for (int attempt = 0; attempt < MaximumSliceFitAttempts; attempt++)
        {
            int tokens = count(slice);
            if (tokens <= budgetTokens || slice.Length <= 1) return slice;
            int next = Math.Max(1, (int)(characters * (double)budgetTokens / tokens) - 1);
            if (next >= characters) next = characters - 1;
            characters = next;
            slice = fromEnd ? CutFromEnd(text, characters) : CutFromStart(text, characters);
        }

        if (count(slice) <= budgetTokens) return slice;

        // Still over after rescaling - the text is too uneven for an average to converge on. Start
        // from the one-character-per-token bound and halve until the count itself agrees, which
        // terminates in at most log2 of the length. A counter that charges a fixed per-message
        // overhead can make even a single character exceed a tiny budget; that budget cannot be
        // met by any slice, and the shortest one is the honest answer rather than a silent overrun.
        characters = Math.Max(1, Math.Min(characters, budgetTokens));
        slice = fromEnd ? CutFromEnd(text, characters) : CutFromStart(text, characters);
        while (characters > 1 && count(slice) > budgetTokens)
        {
            characters /= 2;
            slice = fromEnd ? CutFromEnd(text, characters) : CutFromStart(text, characters);
        }

        return slice;
    }

    private static string CutFromStart(string text, int characters)
    {
        characters = Math.Clamp(characters, 1, text.Length);
        int boundary = text.LastIndexOf(' ', Math.Min(characters, text.Length - 1));
        if (boundary > characters / 2) characters = boundary;
        return text[..characters];
    }

    private static string CutFromEnd(string text, int characters)
    {
        characters = Math.Clamp(characters, 1, text.Length);
        int start = text.Length - characters;
        int boundary = text.IndexOf(' ', start);
        if (boundary >= 0 && boundary < text.Length - characters / 2) start = boundary + 1;
        return text[start..];
    }

    /// <summary>Longest prefix that fits the budget, cut on a word boundary where one is near.</summary>
    private static string TakeTokensFromStart(string text, int budgetTokens, Func<string, int> count)
    {
        if (string.IsNullOrEmpty(text) || count(text) <= budgetTokens) return text ?? string.Empty;
        int characters = Math.Min(text.Length, Math.Max(1, (int)(budgetTokens * LocalCharactersPerToken(text, count))));
        return FitSliceToBudget(text, budgetTokens, characters, count, fromEnd: false);
    }

    /// <summary>Longest suffix that fits the budget, cut on a word boundary where one is near.</summary>
    private static string TakeTokensFromEnd(string text, int budgetTokens, Func<string, int> count)
    {
        if (string.IsNullOrEmpty(text) || count(text) <= budgetTokens) return text ?? string.Empty;
        int characters = Math.Min(text.Length, Math.Max(1, (int)(budgetTokens * LocalCharactersPerToken(text, count))));
        return FitSliceToBudget(text, budgetTokens, characters, count, fromEnd: true);
    }

    /// <summary>
    /// Folds the Virtual Context digest into the single leading system message instead of adding a
    /// second one.
    ///
    /// Measured on the physical PC: emitting the digest as its own system turn made llama.cpp
    /// answer HTTP 500 with "Jinja Exception: System message must be at the beginning." - Qwen's
    /// chat template raises on any system message that is not first. The proxy surfaced that as an
    /// empty reply, so every compacted turn - the whole Virtual Context feature, for both long
    /// conversations and oversized single messages - silently produced nothing, while short turns
    /// worked. Keeping exactly one system message at the front is what makes the feature usable.
    /// </summary>
    private static List<Turn> MergeSystemWithVirtualMemory(IReadOnlyList<Turn> system, string virtualMemory)
    {
        if (string.IsNullOrEmpty(virtualMemory)) return system.ToList();
        if (system.Count == 0) return new List<Turn> { new("system", virtualMemory) };

        var merged = new List<Turn>
        {
            new(system[0].Role, system[0].Content + "\n\n" + virtualMemory)
        };
        // Any further system turns are already unusual; keep their text inside the same leading
        // message rather than re-introducing the shape the template rejects.
        for (int i = 1; i < system.Count; i++)
            merged[0] = new Turn(merged[0].Role, merged[0].Content + "\n\n" + system[i].Content);
        return merged;
    }

    private static string BuildVirtualMemory(
        IReadOnlyList<Turn> dropped,
        string currentRequest,
        int budgetTokens,
        Func<string, int> count,
        out int recalledPages)
    {
        recalledPages = 0;
        if (dropped.Count == 0 || budgetTokens <= 24) return string.Empty;

        const string header = "[Earlier conversation compacted because it no longer fits the model context. "
            + "PaRuX Virtual Context is recalling durable state and relevant historical pages. "
            + "This is a loss-aware background digest, not verbatim wording. Preserve explicit constraints, "
            + "decisions, identifiers, failures, verification evidence and unresolved work below.]\n";

        int headerCost = count(header);
        if (headerCost >= budgetTokens) return string.Empty;

        HashSet<string> queryTerms = ExtractQueryTerms(currentRequest);
        List<DigestFragment> candidates = BuildDigestCandidates(dropped, queryTerms);
        if (candidates.Count == 0) return string.Empty;

        List<VirtualPage> pages = BuildVirtualPages(dropped, candidates, count);
        var builder = new StringBuilder(header);
        int used = headerCost;

        // Layer 1: global durable state. This deliberately ignores page boundaries: an ancient
        // constraint, commit id or failed verification can outrank much newer filler.
        const string stateHeading = "\nDurable state remembered from the full history:\n";
        int headingCost = count(stateHeading);
        if (used + headingCost < budgetTokens)
        {
            builder.Append(stateHeading);
            used += headingCost;

            int stateBudget = Math.Max(0, Math.Min(
                budgetTokens - used,
                Math.Max(64, (budgetTokens - headerCost) * 45 / 100)));
            int stateUsed = 0;
            var selectedState = new List<DigestFragment>();

            // Keep a few newest dropped-turn anchors for continuity, then fill by durable/query value.
            foreach (DigestFragment fragment in candidates
                .Where(fragment => fragment.IsAnchor)
                .OrderByDescending(fragment => fragment.TurnIndex))
            {
                string rendered = RenderFragment(fragment);
                int cost = count(rendered);
                if (stateUsed + cost > stateBudget) continue;
                selectedState.Add(fragment);
                stateUsed += cost;
                if (selectedState.Count >= Math.Min(4, dropped.Count)) break;
            }

            foreach (DigestFragment fragment in candidates
                .Where(fragment => !selectedState.Contains(fragment))
                .OrderByDescending(fragment => fragment.QueryScore)
                .ThenByDescending(fragment => fragment.Score)
                .ThenByDescending(fragment => fragment.TurnIndex))
            {
                string rendered = RenderFragment(fragment);
                int cost = count(rendered);
                if (stateUsed + cost > stateBudget) continue;
                selectedState.Add(fragment);
                stateUsed += cost;
            }

            foreach (DigestFragment fragment in selectedState
                .OrderBy(fragment => fragment.TurnIndex)
                .ThenByDescending(fragment => fragment.QueryScore)
                .ThenByDescending(fragment => fragment.Score))
            {
                string rendered = RenderFragment(fragment);
                if (used + count(rendered) > budgetTokens) break;
                builder.Append(rendered);
                used += count(rendered);
            }
        }

        // Layer 2: page retrieval. A page is a bounded portion of old conversation. Pages related
        // to the newest user request rank first, while the newest archived page is always eligible
        // so conversational continuity does not disappear on a vague "continue" request.
        const string pageHeading = "\nRelevant historical pages recalled for the current request:\n";
        int pageHeadingCost = count(pageHeading);
        if (used + pageHeadingCost < budgetTokens)
        {
            builder.Append(pageHeading);
            used += pageHeadingCost;

            List<VirtualPage> selectedPages = pages
                .OrderByDescending(page => page.QueryScore)
                .ThenByDescending(page => page.StateScore)
                .ThenByDescending(page => page.IsNewestPage)
                .ThenByDescending(page => page.LastTurnIndex)
                .Take(MaximumRecalledPages)
                .ToList();

            // Guarantee newest archived page participates even when lexical retrieval strongly
            // favors old pages. Remove the weakest non-newest page if the list is already full.
            VirtualPage? newestPage = pages.LastOrDefault();
            if (newestPage != null && !selectedPages.Contains(newestPage))
            {
                if (selectedPages.Count >= MaximumRecalledPages)
                    selectedPages.RemoveAt(selectedPages.Count - 1);
                selectedPages.Add(newestPage);
            }

            foreach (VirtualPage page in selectedPages
                .OrderByDescending(page => page.QueryScore)
                .ThenByDescending(page => page.StateScore)
                .ThenByDescending(page => page.LastTurnIndex))
            {
                string label = $"\n[history page {page.Index + 1}: archived turns {page.FirstTurnIndex + 1}-{page.LastTurnIndex + 1}]\n";
                int labelCost = count(label);
                if (used + labelCost >= budgetTokens) break;

                var pageFragments = page.Fragments
                    .OrderByDescending(fragment => fragment.QueryScore)
                    .ThenByDescending(fragment => fragment.Score)
                    .ThenByDescending(fragment => fragment.TurnIndex)
                    .Take(MaximumFragmentsPerPage)
                    .ToList();

                var pageText = new StringBuilder(label);
                int pageCost = labelCost;
                int added = 0;
                foreach (DigestFragment fragment in pageFragments)
                {
                    string rendered = RenderFragment(fragment);
                    int cost = count(rendered);
                    if (used + pageCost + cost > budgetTokens) continue;
                    pageText.Append(rendered);
                    pageCost += cost;
                    added++;
                }

                if (added == 0) continue;
                builder.Append(pageText);
                used += pageCost;
                recalledPages++;
            }
        }

        // In tiny budgets the headings may have consumed the useful remainder. Falling back to the
        // strongest fragment is better than emitting a header-only virtual memory block.
        if (builder.Length == header.Length)
        {
            DigestFragment best = candidates
                .OrderByDescending(fragment => fragment.QueryScore)
                .ThenByDescending(fragment => fragment.Score)
                .ThenByDescending(fragment => fragment.TurnIndex)
                .First();
            string rendered = RenderFragment(best);
            if (headerCost + count(rendered) <= budgetTokens) builder.Append(rendered);
        }

        return builder.ToString();
    }

    private static List<VirtualPage> BuildVirtualPages(
        IReadOnlyList<Turn> dropped,
        IReadOnlyList<DigestFragment> fragments,
        Func<string, int> count)
    {
        if (dropped.Count == 0) return new List<VirtualPage>();

        var pageRanges = new List<(int First, int Last)>();
        int first = 0;
        int tokens = 0;
        for (int index = 0; index < dropped.Count; index++)
        {
            int turnCost = Math.Max(1, count(dropped[index].Content));
            if (index > first && tokens + turnCost > VirtualPageTargetTokens)
            {
                pageRanges.Add((first, index - 1));
                first = index;
                tokens = 0;
            }
            tokens += turnCost;
        }
        pageRanges.Add((first, dropped.Count - 1));

        var pages = new List<VirtualPage>(pageRanges.Count);
        for (int pageIndex = 0; pageIndex < pageRanges.Count; pageIndex++)
        {
            (int rangeFirst, int rangeLast) = pageRanges[pageIndex];
            List<DigestFragment> pageFragments = fragments
                .Where(fragment => fragment.TurnIndex >= rangeFirst && fragment.TurnIndex <= rangeLast)
                .ToList();
            int queryScore = pageFragments.Sum(fragment => Math.Min(40, fragment.QueryScore));
            int stateScore = pageFragments.Sum(fragment => Math.Min(24, fragment.Score));
            pages.Add(new VirtualPage(
                pageIndex,
                rangeFirst,
                rangeLast,
                pageFragments,
                queryScore,
                stateScore,
                pageIndex == pageRanges.Count - 1));
        }
        return pages;
    }

    private static List<DigestFragment> BuildDigestCandidates(
        IReadOnlyList<Turn> dropped,
        IReadOnlySet<string> queryTerms)
    {
        var result = new List<DigestFragment>();
        for (int turnIndex = 0; turnIndex < dropped.Count; turnIndex++)
        {
            Turn turn = dropped[turnIndex];
            List<string> fragments = SplitIntoFragments(turn.Content);
            if (fragments.Count == 0) continue;

            string anchor = BuildAnchor(turn.Content);
            if (!string.IsNullOrWhiteSpace(anchor))
            {
                result.Add(new DigestFragment(
                    turnIndex,
                    turn.Role,
                    anchor,
                    ScoreFragment(anchor, turn.Role, turnIndex, dropped.Count) + 8,
                    IsAnchor: true,
                    QueryScore: ScoreQueryMatch(anchor, queryTerms)));
            }

            foreach (string fragment in fragments)
            {
                int score = ScoreFragment(fragment, turn.Role, turnIndex, dropped.Count);
                int queryScore = ScoreQueryMatch(fragment, queryTerms);
                if (score <= 0 && queryScore <= 0) continue;
                result.Add(new DigestFragment(
                    turnIndex, turn.Role, fragment, score, IsAnchor: false, QueryScore: queryScore));
            }
        }

        // Exact duplicates are common in retries and logs. Keep the most useful/newest copy so
        // repeated evidence never eats the finite virtual-memory budget.
        return result
            .GroupBy(fragment => NormalizeForDedup(fragment.Text), StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderByDescending(fragment => fragment.QueryScore)
                .ThenByDescending(fragment => fragment.Score)
                .ThenByDescending(fragment => fragment.TurnIndex)
                .First())
            .ToList();
    }

    private static HashSet<string> ExtractQueryTerms(string request)
    {
        var terms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in QueryTerm.Matches(request ?? string.Empty))
        {
            string value = match.Value.Trim('.', '/', ':', '#', '-');
            if (value.Length < 3 || QueryStopWords.Contains(value)) continue;
            terms.Add(value);
            if (terms.Count >= 32) break;
        }
        return terms;
    }

    private static int ScoreQueryMatch(string text, IReadOnlySet<string> queryTerms)
    {
        if (queryTerms.Count == 0 || string.IsNullOrWhiteSpace(text)) return 0;
        int score = 0;
        foreach (string term in queryTerms)
        {
            if (!text.Contains(term, StringComparison.OrdinalIgnoreCase)) continue;
            // Structured identifiers and paths are rarer and more discriminative than prose words.
            score += term.IndexOfAny(new[] { '.', '/', '\\', '_', '-', ':', '#' }) >= 0 ? 16 : 9;
        }
        return score;
    }

    private static List<string> SplitIntoFragments(string content)
    {
        string normalized = (content ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n');
        var result = new List<string>();
        foreach (string line in normalized.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string trimmed = CollapseWhitespace(line);
            if (trimmed.Length == 0) continue;

            if (LooksStructured(trimmed))
            {
                result.Add(TrimToCharacters(trimmed, MaximumDigestFragmentCharacters));
                continue;
            }

            foreach (string sentence in Regex.Split(trimmed, @"(?<=[.!?])\s+"))
            {
                string value = CollapseWhitespace(sentence);
                if (value.Length > 0)
                    result.Add(TrimToCharacters(value, MaximumDigestFragmentCharacters));
            }
        }
        return result;
    }

    private static int ScoreFragment(string text, string role, int turnIndex, int turnCount)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        int score = 0;
        string lower = text.ToLowerInvariant();

        foreach (string signal in HighValueSignals)
            if (lower.Contains(signal, StringComparison.Ordinal)) score += signal.Length >= 8 ? 7 : 4;

        if (PathLike.IsMatch(text)) score += 12;
        if (IdentifierLike.IsMatch(text)) score += 10;
        if (text.Contains('`') || text.Contains("=>", StringComparison.Ordinal) || text.Contains("exit ", StringComparison.OrdinalIgnoreCase)) score += 6;
        if (text.Contains(": ", StringComparison.Ordinal) || text.Contains(" = ", StringComparison.Ordinal)) score += 3;
        if (string.Equals(role, "user", StringComparison.OrdinalIgnoreCase)) score += 4;

        // Recency matters, but not enough to outrank an explicit old constraint or failure.
        if (turnCount > 1) score += (int)Math.Round(6d * turnIndex / (turnCount - 1));
        return score;
    }

    private static bool LooksStructured(string text) =>
        text.StartsWith("- ", StringComparison.Ordinal)
        || text.StartsWith("* ", StringComparison.Ordinal)
        || text.StartsWith("> ", StringComparison.Ordinal)
        || text.StartsWith("$ ", StringComparison.Ordinal)
        || text.StartsWith("error", StringComparison.OrdinalIgnoreCase)
        || text.StartsWith("exception", StringComparison.OrdinalIgnoreCase)
        || PathLike.IsMatch(text)
        || text.Contains("=>", StringComparison.Ordinal)
        || text.Contains(" = ", StringComparison.Ordinal);

    private static string BuildAnchor(string content)
    {
        string text = CollapseWhitespace(content);
        if (text.Length <= 260) return text;

        const int head = 150;
        const int tail = 100;
        return text[..head].TrimEnd() + " … " + text[^tail..].TrimStart();
    }

    private static string RenderFragment(DigestFragment fragment) =>
        "- [" + fragment.Role + "] " + fragment.Text.Trim() + "\n";

    private static string NormalizeForDedup(string text) =>
        CollapseWhitespace(text).Trim().TrimEnd('.', '!', '?');

    private static string CollapseWhitespace(string text) =>
        Regex.Replace(text ?? string.Empty, @"\s+", " ").Trim();

    private static string TrimToCharacters(string value, int maximumCharacters)
    {
        if (value.Length <= maximumCharacters) return value;
        string cut = value[..maximumCharacters];
        int lastSpace = cut.LastIndexOf(' ');
        return (lastSpace > maximumCharacters / 2 ? cut[..lastSpace] : cut).TrimEnd() + "…";
    }
}
