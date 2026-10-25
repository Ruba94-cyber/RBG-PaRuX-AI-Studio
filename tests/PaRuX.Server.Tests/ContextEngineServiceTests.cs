using PaRuX.Contracts;
using PaRuX.Server;
using PaRuX.Server.ArtifactWorkspace;
using PaRuX.Server.ContextGraph;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class ContextEngineServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parux-context-engine-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }

    private ContextEngineService CreateEngine(out MemoryStore memory, out ContextGraphStore graph, out ArtifactStore artifacts)
    {
        Directory.CreateDirectory(_root);
        var embeddings = new EmbeddingService(Path.Combine(_root, "embeddings"));
        memory = new MemoryStore(embeddings, Path.Combine(_root, "memory"));
        var rag = new RagStore(embeddings, Path.Combine(_root, "rag"));
        graph = new ContextGraphStore(Path.Combine(_root, "graph"));
        artifacts = new ArtifactStore(Path.Combine(_root, "artifacts"));
        var conversations = new ConversationStore(Path.Combine(_root, "conversations"));
        var internet = new InternetSettingsService(Path.Combine(_root, "internet"));
        var web = new WebSearchService(internet);
        var mcp = new McpClientRegistry(Path.Combine(_root, "mcp"));
        var repoIndex = new RepoIndexService(graph);
        return new ContextEngineService(memory, rag, graph, artifacts, conversations, web, internet, mcp, repoIndex);
    }

    /// <summary>Guards CompileAsync's/BuildCoreAsync's independent-source concurrency: Memory, Graph
    /// and Artifacts are backed by separate SQLite databases with no dependency on one another's
    /// result, so running them concurrently must still combine every source's real evidence into
    /// one pack - exactly like the sequential version already did.</summary>
    [Fact]
    public async Task BuildForChatAsync_combines_memory_graph_and_artifact_evidence_from_independent_stores()
    {
        ContextEngineService engine = CreateEngine(out MemoryStore memory, out ContextGraphStore graph, out ArtifactStore artifacts);

        memory.Add(new MemoryAddRequest { Scope = "User", Content = "MEMORY_MARKER: prefer the release build for benchmarks." });

        ContextEntityDto entity = graph.CreateEntity(new CreateContextEntityRequest { EntityType = "project", Name = "GRAPH_MARKER_Project" });
        ContextEntityDto related = graph.CreateEntity(new CreateContextEntityRequest { EntityType = "task", Name = "GRAPH_MARKER_Task" });
        graph.CreateRelation(new CreateContextRelationRequest { FromEntityId = entity.Id, RelationType = "tracks", ToEntityId = related.Id });

        ArtifactDto artifact = artifacts.Create(new CreateArtifactRequest
        {
            ArtifactType = "note", Title = "Artifact title", ContentRef = "ARTIFACT_MARKER content body"
        });
        artifacts.SetStatus(artifact.Id, ArtifactStore.StatusFinal);

        string query = "GRAPH_MARKER_Project MEMORY_MARKER ARTIFACT_MARKER benchmarks";
        ChatContextResult result = await engine.BuildForChatAsync(query, skill: null, CancellationToken.None);

        Assert.Contains("MEMORY_MARKER", result.Pack.PromptText, StringComparison.Ordinal);
        Assert.Contains("GRAPH_MARKER_Project", result.Pack.PromptText, StringComparison.Ordinal);
        Assert.Contains("ARTIFACT_MARKER", result.Pack.PromptText, StringComparison.Ordinal);
    }

    /// <summary>The two-phase catch/retry in BuildForChatAsync (drop conversation history and retry
    /// once ConversationNotFoundException surfaces) must still work once independent retrieval steps
    /// run concurrently - an unknown conversation id must never fail the whole turn.</summary>
    [Fact]
    public async Task BuildForChatAsync_recovers_from_an_unknown_conversation_id_instead_of_failing_the_turn()
    {
        ContextEngineService engine = CreateEngine(out MemoryStore memory, out _, out _);
        memory.Add(new MemoryAddRequest { Scope = "User", Content = "MEMORY_MARKER survives a missing conversation id." });

        ChatContextResult result = await engine.BuildForChatAsync(
            "MEMORY_MARKER", skill: null, conversationId: "no-such-conversation-" + Guid.NewGuid().ToString("N"),
            includeConversationHistory: true, CancellationToken.None);

        Assert.Contains("MEMORY_MARKER", result.Pack.PromptText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BuildForChatAsync_honors_a_precancelled_token()
    {
        ContextEngineService engine = CreateEngine(out _, out _, out _);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => engine.BuildForChatAsync("anything", skill: null, cts.Token));
    }
    [Fact]
    public void Assemble_enforces_the_global_budget_and_reports_truncation()
    {
        ContextPackDto pack = ContextEngineService.Assemble(new List<List<ContextEngineService.ContextCandidate>>
        {
            new() { Candidate("memory", "m1", new string('m', 4_000)) },
            new() { Candidate("rag", "r1", new string('r', 4_000)) }
        }, 1_000);

        Assert.InRange(pack.UsedCharacters, 1, 1_000);
        Assert.Equal(pack.PromptText.Length, pack.UsedCharacters);
        Assert.True(pack.Truncated);
        Assert.All(pack.Items, item => Assert.True(item.Truncated));
    }

    [Fact]
    public void Assemble_reserves_space_for_every_nonempty_source()
    {
        ContextPackDto pack = ContextEngineService.Assemble(new List<List<ContextEngineService.ContextCandidate>>
        {
            new() { Candidate("memory", "m1", new string('m', 5_000)) },
            new() { Candidate("rag", "r1", new string('r', 5_000)) },
            new() { Candidate("graph", "g1", new string('g', 5_000)) },
            new() { Candidate("web", "w1", new string('w', 5_000)) }
        }, 2_000);

        Assert.Equal(new[] { "memory", "rag", "graph", "web" }, pack.Items.Select(item => item.Kind));
        Assert.InRange(pack.UsedCharacters, 1, 2_000);
    }

    [Fact]
    public void Assemble_preserves_all_sources_at_the_minimum_budget_with_oversized_titles()
    {
        string title = new('T', 2_000);
        ContextEngineService.ContextCandidate Item(string kind) =>
            new(kind, kind, title, new string(kind[0], 2_000), kind);
        ContextPackDto pack = ContextEngineService.Assemble(new List<List<ContextEngineService.ContextCandidate>>
        {
            new() { Item("memory") }, new() { Item("rag") },
            new() { Item("graph") }, new() { Item("web") }
        }, 1_000);

        Assert.Equal(new[] { "memory", "rag", "graph", "web" }, pack.Items.Select(item => item.Kind));
        Assert.All(pack.Items, item => Assert.InRange(item.Title.Length, 1, ContextEngineService.MaximumTitleCharacters));
        Assert.InRange(pack.UsedCharacters, 1, 1_000);
    }

    [Fact]
    public void Assemble_is_deterministic_and_round_robins_by_rank()
    {
        var groups = new List<List<ContextEngineService.ContextCandidate>>
        {
            new() { Candidate("memory", "m1", "one"), Candidate("memory", "m2", "three") },
            new() { Candidate("rag", "r1", "two"), Candidate("rag", "r2", "four") }
        };

        ContextPackDto first = ContextEngineService.Assemble(groups, 2_000);
        ContextPackDto second = ContextEngineService.Assemble(groups, 2_000);

        Assert.Equal(first.PromptText, second.PromptText);
        Assert.Equal(new[] { "m1", "r1", "m2", "r2" }, first.Items.Select(item => item.Source));
    }

    [Fact]
    public void Chat_footer_lists_retrievable_sources_without_internal_context_labels()
    {
        ContextPackDto pack = ContextEngineService.Assemble(new List<List<ContextEngineService.ContextCandidate>>
        {
            new() { new("memory", "memory:1", "Memory", "Remember the user preference", "Memory") },
            new() { new("graph", "graph:1", "at", "An incidental graph relation", "at") },
            new() { new("conversation", "conversation:1", "Prior chat", "Earlier chat text", "Conversation: Prior chat") },
            new() { new("artifact", "artifact:1", "Old note", "An artifact hint", "Old note") },
            new() { new("rag", "rag:manual.pdf", "manual.pdf", "Documentation excerpt", "manual.pdf") },
            new() { new("web", "https://example.org/source", "Web result", "Web search excerpt", "https://example.org/source") }
        }, 4_000);

        Assert.Contains("at", pack.SourceLabels);
        Assert.Equal(new[] { "manual.pdf", "https://example.org/source" }, ChatProxyService.DisplaySources(pack));
    }

    [Fact]
    public void Assemble_deduplicates_equivalent_content_across_sources()
    {
        ContextPackDto pack = ContextEngineService.Assemble(new List<List<ContextEngineService.ContextCandidate>>
        {
            new() { Candidate("memory", "m1", "Use the release build command") },
            new() { Candidate("rag", "r1", "  use   THE release build command  ") }
        }, 2_000);

        Assert.Single(pack.Items);
        Assert.Equal(1, pack.DuplicateItemsDropped);
    }

    [Theory]
    [InlineData("read_file", null, true)]
    [InlineData("read_file", "*", true)]
    [InlineData("browser_open", "browser_*", true)]
    [InlineData("write_file", "read_file", false)]
    public void Tool_allowlist_supports_unrestricted_exact_and_prefix_rules(
        string toolName, string? pattern, bool expected)
    {
        IReadOnlyCollection<string>? allowed = pattern == null ? null : new[] { pattern };
        Assert.Equal(expected, ContextEngineService.ToolNameAllowed(toolName, allowed));
    }

    [Theory]
    [InlineData("search the web for release news", "optional", true)]
    [InlineData("write a release note", "optional", false)]
    [InlineData("search the web for release news", "off", false)]
    [InlineData("write a release note", "required", true)]
    public void Skill_internet_policy_preserves_explicit_general_search_behavior(
        string query, string policy, bool expected) =>
        Assert.Equal(expected, ContextEngineService.ShouldIncludeWeb(query, policy));

    [Fact]
    public async Task Mcp_dispatch_denies_an_off_list_tool_before_server_discovery()
    {
        await using var registry = new McpClientRegistry();

        UnauthorizedAccessException error = await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            registry.CallToolAsync("dangerous_tool", "{}", CancellationToken.None, new[] { "safe_tool" }));

        Assert.Contains("does not allow", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Mcp_discovery_and_dispatch_propagate_preexisting_cancellation()
    {
        await using var registry = new McpClientRegistry();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            registry.GetToolSchemasAsync(cancellation.Token, new[] { "*" }));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            registry.CallToolAsync("safe_tool", "{}", cancellation.Token, new[] { "safe_tool" }));
    }

    private static ContextEngineService.ContextCandidate Candidate(string kind, string source, string content) =>
        new(kind, source, source, content, source);
}
