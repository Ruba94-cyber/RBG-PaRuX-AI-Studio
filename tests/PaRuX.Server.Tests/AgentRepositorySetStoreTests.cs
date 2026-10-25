using PaRuX.Contracts;
using PaRuX.Server.Orchestration;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class AgentRepositorySetStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parux-repo-set-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _state;
    private readonly string _repoA;
    private readonly string _repoB;
    private readonly string _repoC;

    public AgentRepositorySetStoreTests()
    {
        Directory.CreateDirectory(_root);
        _state = Path.Combine(_root, "state");
        _repoA = Path.Combine(_root, "a");
        _repoB = Path.Combine(_root, "b");
        _repoC = Path.Combine(_root, "c");
        Directory.CreateDirectory(_repoA);
        Directory.CreateDirectory(_repoB);
        Directory.CreateDirectory(_repoC);
    }

    [Fact]
    public void Primary_and_additional_repositories_are_composed_without_rewriting_legacy_primary_store()
    {
        var primary = new AgentWorkspaceAssignmentStore(Path.Combine(_state, "primary"));
        var store = new AgentRepositorySetStore(Path.Combine(_state, "extra"), primary);
        AgentTaskDto task = Task("coding");
        primary.Set(task.Id, _repoA, task);

        AgentRepositorySetDto set = store.Add(task.Id, new AddAgentRepositoryAuthorityRequest
        {
            Alias = "backend",
            WorkspaceRoot = _repoB
        }, task);

        Assert.Equal(2, set.Repositories.Count);
        Assert.Equal("primary", set.Repositories[0].Alias);
        Assert.True(set.Repositories[0].IsPrimary);
        Assert.Equal(Path.GetFullPath(_repoA), set.Repositories[0].WorkspaceRoot);
        Assert.Contains(set.Repositories, item => item.Alias == "backend" && !item.IsPrimary && item.WorkspaceRoot == Path.GetFullPath(_repoB));
        Assert.Equal(Path.GetFullPath(_repoA), primary.Get(task.Id)!.WorkspaceRoot);
    }

    [Fact]
    public void Alias_and_path_duplicates_are_rejected()
    {
        var primary = new AgentWorkspaceAssignmentStore(Path.Combine(_state, "primary-dupe"));
        var store = new AgentRepositorySetStore(Path.Combine(_state, "extra-dupe"), primary);
        AgentTaskDto task = Task("review");
        primary.Set(task.Id, _repoA, task);
        store.Add(task.Id, new AddAgentRepositoryAuthorityRequest { Alias = "api", WorkspaceRoot = _repoB }, task);

        Assert.Throws<InvalidDataException>(() => store.Add(task.Id,
            new AddAgentRepositoryAuthorityRequest { Alias = "API", WorkspaceRoot = _repoC }, task));
        Assert.Throws<InvalidDataException>(() => store.Add(task.Id,
            new AddAgentRepositoryAuthorityRequest { Alias = "other", WorkspaceRoot = _repoB }, task));
        Assert.Throws<InvalidDataException>(() => store.Add(task.Id,
            new AddAgentRepositoryAuthorityRequest { Alias = "primary", WorkspaceRoot = _repoC }, task));
    }

    [Theory]
    [InlineData("")]
    [InlineData("9bad")]
    [InlineData("../escape")]
    [InlineData("has space")]
    [InlineData("CON")]
    [InlineData("nul.txt")]
    [InlineData("COM1")]
    [InlineData("LPT9")]
    [InlineData("trailing.")]
    [InlineData(".git")]
    public void Unsafe_aliases_are_rejected(string alias)
    {
        var primary = new AgentWorkspaceAssignmentStore(Path.Combine(_state, "primary-alias-" + Guid.NewGuid().ToString("N")));
        var store = new AgentRepositorySetStore(Path.Combine(_state, "extra-alias-" + Guid.NewGuid().ToString("N")), primary);
        AgentTaskDto task = Task("testing");

        Assert.Throws<InvalidDataException>(() => store.Add(task.Id,
            new AddAgentRepositoryAuthorityRequest { Alias = alias, WorkspaceRoot = _repoB }, task));
    }

    [Fact]
    public void Repository_set_is_immutable_after_execution_materially_starts()
    {
        var primary = new AgentWorkspaceAssignmentStore(Path.Combine(_state, "primary-running"));
        var store = new AgentRepositorySetStore(Path.Combine(_state, "extra-running"), primary);
        AgentTaskDto task = Task("coding");
        primary.Set(task.Id, _repoA, task);
        store.Add(task.Id, new AddAgentRepositoryAuthorityRequest { Alias = "api", WorkspaceRoot = _repoB }, task);
        task.Status = AgentTaskStatus.Running;
        task.Stage = "Editing files";
        task.ProgressPercent = 10;

        InvalidDataException add = Assert.Throws<InvalidDataException>(() => store.Add(task.Id,
            new AddAgentRepositoryAuthorityRequest { Alias = "web", WorkspaceRoot = _repoC }, task));
        InvalidDataException remove = Assert.Throws<InvalidDataException>(() => store.Remove(task.Id, "api", task));

        Assert.Contains("immutable", add.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("immutable", remove.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, store.Get(task.Id, task).Repositories.Count);
    }

    [Fact]
    public void Untouched_scheduler_dispatch_can_still_receive_its_authorities()
    {
        var primary = new AgentWorkspaceAssignmentStore(Path.Combine(_state, "primary-dispatch"));
        var store = new AgentRepositorySetStore(Path.Combine(_state, "extra-dispatch"), primary);
        AgentTaskDto task = Task("coding");
        task.Status = AgentTaskStatus.Running;
        task.Stage = "Dispatched to coding role";
        task.ProgressPercent = 0;

        AgentRepositorySetDto set = store.Add(task.Id,
            new AddAgentRepositoryAuthorityRequest { Alias = "web", WorkspaceRoot = _repoB }, task);

        Assert.Single(set.Repositories);
        Assert.Equal("web", set.Repositories[0].Alias);
    }

    [Fact]
    public void Child_inherits_only_additional_repositories_and_primary_remains_separately_scoped()
    {
        var primary = new AgentWorkspaceAssignmentStore(Path.Combine(_state, "primary-inherit"));
        var store = new AgentRepositorySetStore(Path.Combine(_state, "extra-inherit"), primary);
        AgentTaskDto parent = Task("review");
        AgentTaskDto child = Task("security");
        primary.Set(parent.Id, _repoA, parent);
        primary.Set(child.Id, _repoA, child);
        store.Add(parent.Id, new AddAgentRepositoryAuthorityRequest { Alias = "backend", WorkspaceRoot = _repoB }, parent);
        store.Add(parent.Id, new AddAgentRepositoryAuthorityRequest { Alias = "frontend", WorkspaceRoot = _repoC }, parent);

        store.Inherit(parent.Id, parent, child.Id, child);
        AgentRepositorySetDto inherited = store.Get(child.Id, child);

        Assert.Equal(3, inherited.Repositories.Count);
        Assert.Equal("primary", inherited.Repositories[0].Alias);
        Assert.Contains(inherited.Repositories, item => item.Alias == "backend");
        Assert.Contains(inherited.Repositories, item => item.Alias == "frontend");
    }

    private static AgentTaskDto Task(string role) => new()
    {
        Id = "task-" + Guid.NewGuid().ToString("N"),
        Goal = "test",
        Role = role,
        Status = AgentTaskStatus.Queued,
        CreatedUtc = DateTime.UtcNow,
        UpdatedUtc = DateTime.UtcNow
    };

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
