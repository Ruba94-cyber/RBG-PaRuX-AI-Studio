using PaRuX.Contracts;
using PaRuX.Server.Orchestration;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class PlannerMemoryContextProviderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parux-planner-memory-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void Root_planner_receives_relevant_local_memory_under_a_hard_bound()
    {
        PlannerMemoryContextProvider provider = CreateProvider(out MemoryStore memory);
        memory.Add(new MemoryAddRequest
        {
            Scope = "User",
            Content = "Manual model locks are authoritative and PaRuX runtime inference must remain local-only without proprietary cloud AI."
        });
        memory.Add(new MemoryAddRequest
        {
            Scope = "User",
            Content = "Unrelated note about a garden irrigation timer."
        });

        var task = new AgentTaskDto
        {
            Id = "root-planner",
            Goal = "Plan work for manual model locks and local-only inference",
            Role = "planner",
            Status = AgentTaskStatus.Running,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        };

        string context = provider.Build(task);

        Assert.Contains("Relevant local Memory for planning", context, StringComparison.Ordinal);
        Assert.Contains("Manual model locks", context, StringComparison.Ordinal);
        Assert.True(context.Length <= PlannerMemoryContextProvider.MaxContextChars);
    }

    [Fact]
    public void Delegated_planner_cannot_implicitly_escape_context_isolation_through_memory()
    {
        PlannerMemoryContextProvider provider = CreateProvider(out MemoryStore memory);
        memory.Add(new MemoryAddRequest { Scope = "User", Content = "secret planner memory marker" });
        var task = new AgentTaskDto
        {
            Id = "child-planner",
            ParentId = "parent",
            Goal = "Plan around secret planner memory marker",
            Role = "planner",
            Status = AgentTaskStatus.Running,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        };

        Assert.Equal(string.Empty, provider.Build(task));
    }

    [Fact]
    public void Delegated_planner_receives_memory_only_after_explicit_capability_grant()
    {
        PlannerMemoryContextProvider provider = CreateProvider(out MemoryStore memory);
        memory.Add(new MemoryAddRequest { Scope = "Agent", Content = "explicit memory grant marker" });
        var task = new AgentTaskDto
        {
            Id = "child-planner",
            ParentId = "parent",
            Goal = "Plan using explicit memory grant marker",
            Role = "planner",
            Status = AgentTaskStatus.Running,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow,
            GrantedCapabilities = new List<string> { "memory" }
        };

        string context = provider.Build(task);

        Assert.Contains("explicit memory grant marker", context, StringComparison.Ordinal);
    }

    [Fact]
    public void Non_planner_roles_never_receive_planner_memory_context()
    {
        PlannerMemoryContextProvider provider = CreateProvider(out MemoryStore memory);
        memory.Add(new MemoryAddRequest { Scope = "User", Content = "research memory marker" });
        var task = new AgentTaskDto
        {
            Id = "research",
            Goal = "Research research memory marker",
            Role = "research",
            Status = AgentTaskStatus.Running,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        };

        Assert.Equal(string.Empty, provider.Build(task));
    }

    private PlannerMemoryContextProvider CreateProvider(out MemoryStore memory)
    {
        string embeddings = Path.Combine(_root, "embeddings");
        string database = Path.Combine(_root, "db");
        Directory.CreateDirectory(_root);
        var embeddingService = new EmbeddingService(embeddings);
        memory = new MemoryStore(embeddingService, database);
        return new PlannerMemoryContextProvider(memory);
    }
}
