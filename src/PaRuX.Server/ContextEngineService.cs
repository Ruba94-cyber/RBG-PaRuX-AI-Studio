using System.Text;
using System.Text.Json.Nodes;
using PaRuX.Contracts;
using PaRuX.Server.ArtifactWorkspace;
using PaRuX.Server.ContextGraph;

namespace PaRuX.Server;

/// <summary>
/// Central, budgeted context construction over the existing Memory, RAG, Context Graph, persisted
/// conversation, verified-artifact, repository, Virtual Context V2 ledger and web sources. It
/// deliberately owns no persistence and does not replace any source's retrieval algorithm: it is
/// the one orchestration, policy, deduplication and budgeting boundary.
/// </summary>
/// <summary>Distinguishes "this exact conversation id has no persisted record" from any other
/// InvalidDataException so BuildForChatAsync can retry a chat turn without history instead of
/// failing it outright, while the direct /api/v1/context/build API (which still derives from
/// InvalidDataException, so existing generic 400 handling is unaffected) keeps rejecting an
/// explicitly wrong/unknown id as a caller error.</summary>
public sealed class ConversationNotFoundException : Exception
{
    public ConversationNotFoundException(string conversationId)
        : base("The requested conversation was not found.") { ConversationId = conversationId; }

    public string ConversationId { get; }
}

public sealed class ContextEngineService
{
    internal const int DefaultMaxCharacters = 12_000;
    internal const int MinimumMaxCharacters = 1_000;
    internal const int MaximumMaxCharacters = 30_000;
    internal const int MaximumQueryCharacters = ContextBuildRequest.MaximumQueryCharacters;
    internal const int MaximumTitleCharacters = 160;
    internal const int MaximumArtifactContentCharacters = 3_000;

    private readonly MemoryStore _memory;
    private readonly RagStore _rag;
    private readonly ContextGraphStore _graph;
    private readonly ArtifactStore _artifacts;
    private readonly ConversationStore _conversations;
    private readonly WebSearchService _web;
    private readonly InternetSettingsService _internet;
    private readonly McpClientRegistry _mcp;
    private readonly RepoIndexService _repoIndex;
    private readonly VirtualContextLedgerStore _virtualContextLedger;

    public ContextEngineService(
        MemoryStore memory, RagStore rag, ContextGraphStore graph, ArtifactStore artifacts,
        ConversationStore conversations, WebSearchService web, InternetSettingsService internet,
        McpClientRegistry mcp, RepoIndexService repoIndex)
        : this(memory, rag, graph, artifacts, conversations, web, internet, mcp, repoIndex,
            VirtualContextLedgerStore.Current)
    {
    }

    internal ContextEngineService(
        MemoryStore memory, RagStore rag, ContextGraphStore graph, ArtifactStore artifacts,
        ConversationStore conversations, WebSearchService web, InternetSettingsService internet,
        McpClientRegistry mcp, RepoIndexService repoIndex, VirtualContextLedgerStore virtualContextLedger)
    {
        _memory = memory;
        _rag = rag;
        _graph = graph;
        _artifacts = artifacts;
        _conversations = conversations;
        _web = web;
        _internet = internet;
        _mcp = mcp;
        _repoIndex = repoIndex;
        _virtualContextLedger = virtualContextLedger ?? throw new ArgumentNullException(nameof(virtualContextLedger));
    }

    public Task<ContextPackDto> BuildAsync(ContextBuildRequest request, CancellationToken cancellationToken)
    {
        ValidateQuery(request.Query, rejectOversized: true);
        return BuildCoreAsync(request, skill: null, webRequired: false, cancellationToken);
    }

    internal Task<ChatContextResult> BuildForChatAsync(
        string query, SkillManifest? skill, CancellationToken cancellationToken) =>
        BuildForChatAsync(query, skill, conversationId: null, includeConversationHistory: null, cancellationToken);

    internal Task<ChatContextResult> BuildForChatAsync(
        string query, SkillManifest? skill, string? conversationId, CancellationToken cancellationToken) =>
        BuildForChatAsync(query, skill, conversationId, includeConversationHistory: null, cancellationToken);

    internal async Task<ChatContextResult> BuildForChatAsync(
        string query, SkillManifest? skill, string? conversationId, bool? includeConversationHistory,
        CancellationToken cancellationToken)
    {
        query = ValidateQuery(query, rejectOversized: false);
        bool includeMemory = !PolicyIsOff(skill?.MemoryPolicy);
        bool includeRag = !PolicyIsOff(skill?.RagPolicy);
        bool webRequired = PolicyIsRequired(skill?.InternetPolicy);
        bool includeWeb = ShouldIncludeWeb(query, skill?.InternetPolicy);
        bool hasConversationScope = !string.IsNullOrWhiteSpace(conversationId);
        bool includeConversation = (includeConversationHistory ?? true) && hasConversationScope;

        ContextBuildRequest buildRequest = new()
        {
            Query = query,
            MaxCharacters = DefaultMaxCharacters,
            IncludeMemory = includeMemory,
            IncludeRag = includeRag,
            IncludeGraph = true,
            IncludeArtifacts = true,
            IncludeConversationHistory = includeConversation,
            // Keep the scope id even when raw history is disabled: the Virtual Context V2 ledger
            // is explicit durable state and is intentionally independent from transcript loading.
            ConversationId = hasConversationScope ? conversationId : null,
            IncludeWeb = includeWeb
        };

        ContextPackDto pack;
        try
        {
            pack = await BuildCoreAsync(buildRequest, skill, webRequired, cancellationToken).ConfigureAwait(false);
        }
        catch (ConversationNotFoundException) when (includeConversation)
        {
            buildRequest.IncludeConversationHistory = false;
            // Do NOT clear ConversationId: a new/not-yet-persisted chat may already have explicit
            // durable ledger constraints. Retry without raw transcript but preserve that ledger.
            pack = await BuildCoreAsync(buildRequest, skill, webRequired, cancellationToken).ConfigureAwait(false);
        }

        IReadOnlyCollection<string>? allowedTools = skill == null ? null : skill.AllowedTools;
        JsonArray toolSchemas = await _mcp.GetToolSchemasAsync(cancellationToken, allowedTools).ConfigureAwait(false);
        McpToolSchemaRouter.Decision toolRouting = McpToolSchemaRouter.Route(query, toolSchemas);
        return new ChatContextResult(pack, toolRouting.Schemas);
    }

    private async Task<ContextPackDto> BuildCoreAsync(
        ContextBuildRequest request, SkillManifest? skill, bool webRequired, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.IncludeRepository
            && (string.IsNullOrWhiteSpace(request.RepositoryPath) || !Directory.Exists(request.RepositoryPath)))
            throw new InvalidDataException("repositoryPath must be an existing folder when repository context is enabled.");
        if (request.IncludeConversationHistory && string.IsNullOrWhiteSpace(request.ConversationId))
            throw new InvalidDataException("conversationId is required when conversation history is enabled.");

        int maxCharacters = Math.Clamp(request.MaxCharacters, MinimumMaxCharacters, MaximumMaxCharacters);

        // Every source reads independent state keyed only by the request. Starting them together
        // collapses wall-clock cost from a sum to a max while fixed group ordering keeps Assemble's
        // round-robin output deterministic. Virtual Context is a first-class group and is placed
        // first because explicit durable constraints/decisions outrank heuristic retrieval; it is
        // still bounded by exactly the same global character budget as every other source.
        Task<List<ContextCandidate>>? virtualContextTask = !string.IsNullOrWhiteSpace(request.ConversationId)
            ? Task.Run(() => BuildVirtualContextCandidates("conversation", request.ConversationId!), cancellationToken)
            : null;

        Task<List<ContextCandidate>>? memoryTask = request.IncludeMemory
            ? Task.Run(() => _memory.Retrieve(request.Query, 5)
                .Select(m => new ContextCandidate("memory", "memory:" + m.Id, "Local memory", m.Content, "Memory"))
                .ToList(), cancellationToken)
            : null;

        Task<List<RagChunkMatch>>? ragTask = request.IncludeRag
            ? _rag.RetrieveAsync(request.Query, 4, cancellationToken)
            : null;

        Task<List<ContextCandidate>>? graphTask = request.IncludeGraph
            ? Task.Run(() => BuildGraphCandidates(request.Query), cancellationToken)
            : null;

        Task<List<ContextCandidate>>? artifactTask = request.IncludeArtifacts
            ? Task.Run(() => BuildArtifactCandidates(request.Query), cancellationToken)
            : null;

        Task<List<ContextCandidate>>? conversationTask = request.IncludeConversationHistory
            ? Task.Run(() => BuildConversationCandidates(request.ConversationId!), cancellationToken)
            : null;

        Task<List<ContextCandidate>>? webTask = request.IncludeWeb
            ? BuildWebCandidatesAsync(request.Query, webRequired, cancellationToken)
            : null;

        var groups = new List<List<ContextCandidate>>();

        if (virtualContextTask != null) groups.Add(await virtualContextTask.ConfigureAwait(false));
        cancellationToken.ThrowIfCancellationRequested();

        if (memoryTask != null) groups.Add(await memoryTask.ConfigureAwait(false));
        cancellationToken.ThrowIfCancellationRequested();

        if (ragTask != null)
        {
            List<RagChunkMatch> ragMatches = await ragTask.ConfigureAwait(false);
            groups.Add(ragMatches
                .Select(m => new ContextCandidate(
                    "rag", "rag:" + m.FilePath, Path.GetFileName(m.FilePath), m.Content, Path.GetFileName(m.FilePath)))
                .ToList());
        }
        cancellationToken.ThrowIfCancellationRequested();

        if (graphTask != null) groups.Add(await graphTask.ConfigureAwait(false));
        cancellationToken.ThrowIfCancellationRequested();

        if (artifactTask != null) groups.Add(await artifactTask.ConfigureAwait(false));
        cancellationToken.ThrowIfCancellationRequested();

        if (conversationTask != null) groups.Add(await conversationTask.ConfigureAwait(false));
        cancellationToken.ThrowIfCancellationRequested();

        if (webTask != null) groups.Add(await webTask.ConfigureAwait(false));

        if (request.IncludeRepository)
            groups.AddRange(BuildRepositoryCandidateGroups(request.RepositoryPath!, request.Query));

        return Assemble(groups, maxCharacters);
    }

    private List<ContextCandidate> BuildVirtualContextCandidates(string scope, string scopeId)
    {
        VirtualContextLedgerStateResponse state = _virtualContextLedger.GetState(scope, scopeId, maxEntries: 1_000);
        if (string.IsNullOrWhiteSpace(state.PromptText)) return new List<ContextCandidate>();
        return new List<ContextCandidate>
        {
            new(
                "virtual_context_v2",
                "virtual-context-v2:" + scope + ":" + scopeId,
                "Durable " + scope + " ledger",
                state.PromptText,
                "Virtual Context V2")
        };
    }

    private List<ContextCandidate> BuildConversationCandidates(string conversationId)
    {
        ConversationDocument? conversation = _conversations.Get(conversationId);
        if (conversation == null)
            throw new ConversationNotFoundException(conversationId);

        string content = ConversationContextExtractor.Extract(conversation.DocumentJson);
        if (string.IsNullOrWhiteSpace(content)) return new List<ContextCandidate>();
        return new List<ContextCandidate>
        {
            new("conversation", "conversation:" + conversation.Id, conversation.Title, content,
                "Conversation: " + conversation.Title)
        };
    }

    private List<List<ContextCandidate>> BuildRepositoryCandidateGroups(string repositoryPath, string query)
    {
        RepoContextPack pack = _repoIndex.SearchContext(repositoryPath, query, maxFiles: 8, maxSymbols: 15);
        List<ContextCandidate> files = pack.RelevantFiles.Select(path => new ContextCandidate(
            "repository_file", "repository:file:" + path, path,
            "Potentially relevant indexed repository file: " + path + ". Verify its current contents before editing.", path)).ToList();
        List<ContextCandidate> symbols = pack.RelevantSymbols.Select(symbol => new ContextCandidate(
            "repository_symbol", "repository:symbol:" + symbol, symbol,
            "Potentially relevant indexed repository symbol: " + symbol + ". Verify the signature before relying on it.", symbol)).ToList();
        return new List<List<ContextCandidate>> { files, symbols };
    }

    private List<ContextCandidate> BuildGraphCandidates(string query)
    {
        var candidates = new List<ContextCandidate>();
        foreach (ContextEntityDto entity in _graph.FindEntitiesMentionedIn(query, 3))
        {
            ContextNeighborhoodResponse? neighborhood = _graph.GetNeighborhood(entity.Id);
            if (neighborhood == null) continue;

            var facts = new List<string>();
            foreach (ContextRelationDto relation in neighborhood.OutgoingRelations)
            {
                ContextEntityDto? target = neighborhood.NeighborEntities.FirstOrDefault(e => e.Id == relation.ToEntityId);
                if (target != null) facts.Add($"{entity.Name} {relation.RelationType} {target.Name}");
            }
            foreach (ContextRelationDto relation in neighborhood.IncomingRelations)
            {
                ContextEntityDto? source = neighborhood.NeighborEntities.FirstOrDefault(e => e.Id == relation.FromEntityId);
                if (source != null) facts.Add($"{source.Name} {relation.RelationType} {entity.Name}");
            }

            if (facts.Count > 0)
            {
                candidates.Add(new ContextCandidate(
                    "graph", "graph:" + entity.Id, $"{entity.Name} ({entity.EntityType})",
                    string.Join("\n", facts.Distinct(StringComparer.OrdinalIgnoreCase)), entity.Name));
            }
        }
        return candidates;
    }

    private List<ContextCandidate> BuildArtifactCandidates(string query)
    {
        return _artifacts.FindRelevant(query, 3).Select(relevant =>
        {
            string content = relevant.Version.ContentRef;
            if (content.Length > MaximumArtifactContentCharacters)
                content = content[..MaximumArtifactContentCharacters].TrimEnd() + "…";
            return new ContextCandidate(
                "artifact",
                $"artifact:{relevant.Artifact.Id}:v{relevant.Version.VersionNumber}",
                relevant.Artifact.Title,
                content,
                relevant.Artifact.Title);
        }).ToList();
    }

    private async Task<List<ContextCandidate>> BuildWebCandidatesAsync(
        string query, bool required, CancellationToken cancellationToken)
    {
        if (!_internet.GetEnabled())
        {
            return required
                ? new List<ContextCandidate> { new("notice", "internet:disabled", "Internet unavailable",
                    "The selected skill requires Internet, but Internet access is disabled in Settings. Say so plainly and do not invent results.",
                    "Internet disabled") }
                : new List<ContextCandidate>();
        }

        List<WebSearchResult> results;
        try
        {
            results = await _web.SearchAsync(query, 5, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new List<ContextCandidate> { new("notice", "internet:error", "Web search failed",
                "A web search was requested but failed (" + ex.Message + "). Say so plainly and do not invent results.",
                "Web search failed") };
        }

        // A search that returns nothing has to be as loud as one that throws. Confirmed live on the
        // physical PC: DuckDuckGo's HTML endpoint now answers an anti-bot challenge page with HTTP
        // 200, so EnsureSuccessStatusCode passes, the result regex matches nothing, and this method
        // used to hand back an empty list - indistinguishable from "no web lookup was attempted".
        // The model then answered "I cannot search the web" from its own knowledge while the user,
        // who had just enabled web search and asked for it explicitly, reasonably believed a search
        // had happened. The disabled and failed branches above already say so plainly; an empty
        // result must too.
        if (results.Count == 0 && required)
        {
            return new List<ContextCandidate> { new("notice", "internet:empty", "Web search returned nothing",
                "A web search ran for this request but returned no usable results, so no web content is available here. Say so plainly and do not invent results or cite sources you have not been given.",
                "Web search empty") };
        }

        string? firstPage = null;
        if (results.Count > 0)
        {
            try { firstPage = await _web.FetchPageTextAsync(results[0].Url, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch { }
        }

        return results.Select((result, index) => new ContextCandidate(
            "web", result.Url, result.Title,
            WebSearchService.UntrustedContentHeader + "\n"
                + (index == 0 && !string.IsNullOrWhiteSpace(firstPage) ? firstPage! : result.Snippet),
            result.Url)).ToList();
    }

    internal static ContextPackDto Assemble(IReadOnlyList<List<ContextCandidate>> groups, int maxCharacters)
    {
        maxCharacters = Math.Clamp(maxCharacters, MinimumMaxCharacters, MaximumMaxCharacters);
        var selected = new List<ContextItemDto>();
        var labels = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var output = new StringBuilder();
        int duplicates = 0;
        bool truncated = false;
        int activeGroupCount = Math.Max(1, groups.Count(g => g.Count > 0));
        int candidateBudget = Math.Max(256, maxCharacters / (activeGroupCount + 1));
        int depth = groups.Count == 0 ? 0 : groups.Max(g => g.Count);

        for (int rank = 0; rank < depth; rank++)
        {
            foreach (List<ContextCandidate> group in groups)
            {
                if (rank >= group.Count) continue;
                ContextCandidate candidate = group[rank];
                string key = Canonicalize(candidate.Content);
                if (!seen.Add(key)) { duplicates++; continue; }

                string title = candidate.Title.Trim();
                if (title.Length > MaximumTitleCharacters)
                    title = title[..(MaximumTitleCharacters - 1)].TrimEnd() + "…";
                string prefix = "\n\n[" + Header(candidate.Kind) + "] " + title + "\n";
                int remaining = Math.Min(maxCharacters - output.Length, candidateBudget);
                if (remaining <= prefix.Length) { truncated = true; continue; }

                string content = candidate.Content.Trim();
                bool itemTruncated = content.Length > remaining - prefix.Length;
                if (itemTruncated)
                {
                    int take = Math.Max(0, remaining - prefix.Length - 1);
                    content = content[..Math.Min(take, content.Length)].TrimEnd() + "…";
                    truncated = true;
                }

                output.Append(prefix).Append(content);
                selected.Add(new ContextItemDto
                {
                    Kind = candidate.Kind,
                    Source = candidate.Source,
                    Title = title,
                    Content = content,
                    UsedCharacters = prefix.Length + content.Length,
                    Truncated = itemTruncated
                });
                if (!labels.Contains(candidate.Label, StringComparer.OrdinalIgnoreCase)) labels.Add(candidate.Label);
            }
        }

        return new ContextPackDto
        {
            PromptText = output.ToString().TrimStart(),
            Items = selected,
            SourceLabels = labels,
            UsedCharacters = output.Length == 0 ? 0 : output.ToString().TrimStart().Length,
            MaxCharacters = maxCharacters,
            Truncated = truncated,
            DuplicateItemsDropped = duplicates
        };
    }

    internal static bool ToolNameAllowed(string toolName, IReadOnlyCollection<string>? allowedTools)
    {
        if (allowedTools == null) return true;
        foreach (string pattern in allowedTools)
        {
            if (pattern == "*") return true;
            if (pattern.EndsWith('*') && toolName.StartsWith(pattern[..^1], StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(pattern, toolName, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    internal static bool ShouldIncludeWeb(string query, string? internetPolicy) =>
        ShouldIncludeWeb(query, internetPolicy, DateOnly.FromDateTime(DateTime.Now));

    /// <summary>
    /// The Internet switch still decides whether PaRuX may reach the network at all; this decides
    /// whether a turn that is allowed to should. It now recognises a perishable question rather than
    /// only a literal "search the web" - see <see cref="WebIntentDetector"/> for why answering
    /// "what is the latest release?" from training data was the wrong default.
    /// </summary>
    internal static bool ShouldIncludeWeb(string query, string? internetPolicy, DateOnly today) =>
        !PolicyIsOff(internetPolicy)
        && (PolicyIsRequired(internetPolicy) || WebIntentDetector.WantsWebContext(query, today));

    private static string ValidateQuery(string? query, bool rejectOversized)
    {
        if (string.IsNullOrWhiteSpace(query))
            throw new InvalidDataException("A context query is required.");
        if (query.Length <= MaximumQueryCharacters) return query;
        if (rejectOversized)
            throw new InvalidDataException($"A context query cannot exceed {MaximumQueryCharacters} characters.");
        return query[..MaximumQueryCharacters];
    }

    private static bool PolicyIsOff(string? value) =>
        string.Equals(value, "off", StringComparison.OrdinalIgnoreCase);

    private static bool PolicyIsRequired(string? value) =>
        string.Equals(value, "required", StringComparison.OrdinalIgnoreCase);

    private static string Header(string kind) => kind switch
    {
        "virtual_context_v2" => "VIRTUAL CONTEXT V2 - explicit durable constraints/decisions; contradictions remain unresolved until superseded",
        "memory" => "LOCAL MEMORY - use only if relevant",
        "rag" => "INDEXED LOCAL DOCUMENT - cite the source when used",
        "graph" => "LOCAL CONTEXT GRAPH",
        "artifact" => "VERIFIED ARTIFACT - a prior agent-produced, independently verified result",
        "conversation" => "PERSISTED CONVERSATION HISTORY - scoped to this chat only",
        "web" => "WEB SEARCH RESULT",
        "repository_file" => "REPOSITORY INDEX FILE",
        "repository_symbol" => "REPOSITORY INDEX SYMBOL",
        _ => "CONTEXT NOTICE"
    };

    private static string Canonicalize(string value) =>
        string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();

    internal sealed record ContextCandidate(string Kind, string Source, string Title, string Content, string Label);
}

internal sealed record ChatContextResult(ContextPackDto Pack, JsonArray ToolSchemas);
