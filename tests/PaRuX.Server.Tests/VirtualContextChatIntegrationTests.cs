using PaRuX.Contracts;
using PaRuX.Server;
using PaRuX.Server.ArtifactWorkspace;
using PaRuX.Server.ContextGraph;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class VirtualContextChatIntegrationTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "parux-virtual-chat-" + Guid.NewGuid().ToString("N"));
    private readonly List<McpClientRegistry> _registries = new();

    public void Dispose()
    {
        foreach (McpClientRegistry registry in _registries)
        {
            try { registry.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { }
        }
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }

    private ContextEngineService CreateEngine(VirtualContextLedgerStore ledger)
    {
        Directory.CreateDirectory(_root);
        var embeddings = new EmbeddingService(Path.Combine(_root, "embeddings-" + Guid.NewGuid().ToString("N")));
        var memory = new MemoryStore(embeddings, Path.Combine(_root, "memory-" + Guid.NewGuid().ToString("N")));
        var rag = new RagStore(embeddings, Path.Combine(_root, "rag-" + Guid.NewGuid().ToString("N")));
        var graph = new ContextGraphStore(Path.Combine(_root, "graph-" + Guid.NewGuid().ToString("N")));
        var artifacts = new ArtifactStore(Path.Combine(_root, "artifacts-" + Guid.NewGuid().ToString("N")));
        var conversations = new ConversationStore(Path.Combine(_root, "conversations-" + Guid.NewGuid().ToString("N")));
        var internet = new InternetSettingsService(Path.Combine(_root, "internet-" + Guid.NewGuid().ToString("N")));
        var web = new WebSearchService(internet);
        var mcp = new McpClientRegistry(Path.Combine(_root, "mcp-" + Guid.NewGuid().ToString("N")));
        _registries.Add(mcp);
        var repoIndex = new RepoIndexService(graph);
        return new ContextEngineService(
            memory, rag, graph, artifacts, conversations, web, internet, mcp, repoIndex, ledger);
    }

    [Fact]
    public async Task Explicit_durable_ledger_is_injected_even_when_raw_conversation_history_is_disabled()
    {
        string conversationId = "conversation-" + Guid.NewGuid().ToString("N");
        var ledger = new VirtualContextLedgerStore(Path.Combine(_root, "ledger"));
        ledger.Append(new AppendVirtualContextLedgerRequest
        {
            Scope = "conversation",
            ScopeId = conversationId,
            Kind = VirtualContextLedgerKind.Constraint,
            Text = "VIRTUAL_LEDGER_MARKER: never use the system Python runtime.",
            Source = "user-explicit"
        });
        ContextEngineService engine = CreateEngine(ledger);

        ChatContextResult result = await engine.BuildForChatAsync(
            "continue the implementation",
            skill: null,
            conversationId: conversationId,
            includeConversationHistory: false,
            CancellationToken.None);

        ContextItemDto item = Assert.Single(result.Pack.Items.Where(x => x.Kind == "virtual_context_v2"));
        Assert.Contains("VIRTUAL_LEDGER_MARKER", item.Content, StringComparison.Ordinal);
        Assert.Contains("Virtual Context V2", result.Pack.SourceLabels);
        Assert.DoesNotContain(result.Pack.Items, x => x.Kind == "conversation");
    }

    [Fact]
    public async Task Missing_raw_conversation_retry_preserves_existing_durable_ledger_scope()
    {
        string conversationId = "not-yet-persisted-" + Guid.NewGuid().ToString("N");
        var ledger = new VirtualContextLedgerStore(Path.Combine(_root, "ledger"));
        ledger.Append(new AppendVirtualContextLedgerRequest
        {
            Scope = "conversation",
            ScopeId = conversationId,
            Kind = VirtualContextLedgerKind.Decision,
            Text = "VIRTUAL_RETRY_MARKER: keep the existing authoritative Core path.",
            Source = "user-explicit"
        });
        ContextEngineService engine = CreateEngine(ledger);

        ChatContextResult result = await engine.BuildForChatAsync(
            "continue",
            skill: null,
            conversationId: conversationId,
            includeConversationHistory: true,
            CancellationToken.None);

        Assert.Contains("VIRTUAL_RETRY_MARKER", result.Pack.PromptText, StringComparison.Ordinal);
        Assert.Contains(result.Pack.Items, x => x.Kind == "virtual_context_v2");
        Assert.DoesNotContain(result.Pack.Items, x => x.Kind == "conversation");
    }

    [Fact]
    public async Task Chat_with_no_explicit_ledger_entries_does_not_invent_virtual_context()
    {
        string conversationId = "empty-ledger-" + Guid.NewGuid().ToString("N");
        var ledger = new VirtualContextLedgerStore(Path.Combine(_root, "ledger"));
        ContextEngineService engine = CreateEngine(ledger);

        ChatContextResult result = await engine.BuildForChatAsync(
            "ordinary question",
            skill: null,
            conversationId: conversationId,
            includeConversationHistory: false,
            CancellationToken.None);

        Assert.DoesNotContain(result.Pack.Items, x => x.Kind == "virtual_context_v2");
    }
}
