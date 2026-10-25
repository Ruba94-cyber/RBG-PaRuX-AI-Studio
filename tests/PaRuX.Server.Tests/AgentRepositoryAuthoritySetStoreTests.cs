using PaRuX.Contracts;
using PaRuX.Server.Orchestration;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class AgentRepositoryAuthoritySetStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parux-repo-set-v2-" + Guid.NewGuid().ToString("N"));
    private readonly string _primary;
    private readonly string _backend;
    private readonly string _frontend;

    public AgentRepositoryAuthoritySetStoreTests()
    {
        Directory.CreateDirectory(_root);
        _primary = Directory.CreateDirectory(Path.Combine(_root, "primary")).FullName;
        _backend = Directory.CreateDirectory(Path.Combine(_root, "backend")).FullName;
        _frontend = Directory.CreateDirectory(Path.Combine(_root, "frontend")).FullName;
    }

    private static AgentTaskDto Task(string id = "task-1", string status = AgentTaskStatus.Queued, int progress = 0, string? worktree = null) => new()
    {
        Id = id,
        Goal = "Implement across repositories",
        Role = "coding",
        Status = status,
        ProgressPercent = progress,
        WorktreePath = worktree,
        CreatedUtc = DateTime.UtcNow,
        UpdatedUtc = DateTime.UtcNow
    };

    [Fact]
    public void Replace_persists_one_complete_set_and_survives_store_restart()
    {
        var store = new AgentRepositoryAuthoritySetStore(_root);
        AgentRepositorySetDto saved = store.Replace("task-1", new ReplaceAgentRepositorySetRequest
        {
            PrimaryWorkspaceRoot = _primary,
            AdditionalRepositories = new List<AddAgentRepositoryAuthorityRequest>
            {
                new() { Alias = "backend", WorkspaceRoot = _backend },
                new() { Alias = "frontend", WorkspaceRoot = _frontend }
            }
        }, Task());

        Assert.Equal(3, saved.Repositories.Count);
        Assert.Equal("primary", saved.Repositories[0].Alias);
        Assert.True(saved.Repositories[0].IsPrimary);
        Assert.Equal(new[] { "primary", "backend", "frontend" }, saved.Repositories.Select(item => item.Alias));

        var restarted = new AgentRepositoryAuthoritySetStore(_root);
        AgentRepositorySetDto restored = restarted.Get("task-1")!;
        Assert.Equal(saved.Repositories.Select(item => item.WorkspaceRoot), restored.Repositories.Select(item => item.WorkspaceRoot));
    }

    [Fact]
    public void Second_replace_is_complete_not_additive()
    {
        var store = new AgentRepositoryAuthoritySetStore(_root);
        store.Replace("task-1", new ReplaceAgentRepositorySetRequest
        {
            PrimaryWorkspaceRoot = _primary,
            AdditionalRepositories = new List<AddAgentRepositoryAuthorityRequest>
            {
                new() { Alias = "backend", WorkspaceRoot = _backend }
            }
        }, Task());

        AgentRepositorySetDto replaced = store.Replace("task-1", new ReplaceAgentRepositorySetRequest
        {
            PrimaryWorkspaceRoot = _primary,
            AdditionalRepositories = new List<AddAgentRepositoryAuthorityRequest>
            {
                new() { Alias = "frontend", WorkspaceRoot = _frontend }
            }
        }, Task());

        Assert.Equal(new[] { "primary", "frontend" }, replaced.Repositories.Select(item => item.Alias));
        Assert.DoesNotContain(replaced.Repositories, item => item.Alias == "backend");
        Assert.Equal(new[] { "primary", "frontend" }, store.Get("task-1")!.Repositories.Select(item => item.Alias));
    }

    [Fact]
    public void Invalid_alias_or_duplicate_path_fails_without_replacing_previous_complete_set()
    {
        var store = new AgentRepositoryAuthoritySetStore(_root);
        store.Replace("task-1", new ReplaceAgentRepositorySetRequest
        {
            PrimaryWorkspaceRoot = _primary,
            AdditionalRepositories = new List<AddAgentRepositoryAuthorityRequest>
            {
                new() { Alias = "backend", WorkspaceRoot = _backend }
            }
        }, Task());

        Assert.Throws<InvalidDataException>(() => store.Replace("task-1", new ReplaceAgentRepositorySetRequest
        {
            PrimaryWorkspaceRoot = _primary,
            AdditionalRepositories = new List<AddAgentRepositoryAuthorityRequest>
            {
                new() { Alias = "bad alias", WorkspaceRoot = _frontend }
            }
        }, Task()));
        Assert.Equal(new[] { "primary", "backend" }, store.Get("task-1")!.Repositories.Select(item => item.Alias));

        Assert.Throws<InvalidDataException>(() => store.Replace("task-1", new ReplaceAgentRepositorySetRequest
        {
            PrimaryWorkspaceRoot = _primary,
            AdditionalRepositories = new List<AddAgentRepositoryAuthorityRequest>
            {
                new() { Alias = "duplicate", WorkspaceRoot = _primary }
            }
        }, Task()));
        Assert.Equal(new[] { "primary", "backend" }, store.Get("task-1")!.Repositories.Select(item => item.Alias));
    }

    [Theory]
    [InlineData("CON")]
    [InlineData("nul.txt")]
    [InlineData("COM1")]
    [InlineData("trailing.")]
    [InlineData("../escape")]
    [InlineData("bad & alias")]
    public void Invalid_Windows_aliases_cannot_replace_or_corrupt_the_complete_authority_set(
        string alias)
    {
        var store = new AgentRepositoryAuthoritySetStore(
            Path.Combine(_root, "aliases-" + Guid.NewGuid().ToString("N")));
        Assert.Throws<InvalidDataException>(() => store.Replace(
            "task-1", new ReplaceAgentRepositorySetRequest
            {
                PrimaryWorkspaceRoot = _primary,
                AdditionalRepositories = new List<AddAgentRepositoryAuthorityRequest>
                {
                    new() { Alias = alias, WorkspaceRoot = _frontend }
                }
            }, Task()));
        Assert.Null(store.Get("task-1"));
    }

    [Theory]
    [InlineData(AgentTaskStatus.Running, 1, null)]
    [InlineData(AgentTaskStatus.Verifying, 0, null)]
    [InlineData(AgentTaskStatus.Completed, 0, null)]
    public void Authority_is_immutable_after_execution_begins(string status, int progress, string? worktree)
    {
        var store = new AgentRepositoryAuthoritySetStore(_root);
        InvalidDataException error = Assert.Throws<InvalidDataException>(() => store.Replace("task-1",
            new ReplaceAgentRepositorySetRequest { PrimaryWorkspaceRoot = _primary },
            Task(status: status, progress: progress, worktree: worktree)));

        Assert.Contains("already started", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(store.Get("task-1"));
    }

    [Fact]
    public void Running_zero_progress_without_worktree_is_still_configurable_for_scheduler_race_safety()
    {
        var store = new AgentRepositoryAuthoritySetStore(_root);
        AgentRepositorySetDto set = store.Replace("task-1",
            new ReplaceAgentRepositorySetRequest
            {
                PrimaryWorkspaceRoot = _primary,
                AdditionalRepositories = new List<AddAgentRepositoryAuthorityRequest>
                {
                    new() { Alias = "backend", WorkspaceRoot = _backend }
                }
            },
            Task(status: AgentTaskStatus.Running, progress: 0));

        Assert.Equal(2, set.Repositories.Count);
    }

    [Fact]
    public void More_than_eight_repositories_is_rejected_before_persistence()
    {
        var store = new AgentRepositoryAuthoritySetStore(_root);
        var additional = new List<AddAgentRepositoryAuthorityRequest>();
        for (int i = 0; i < 8; i++)
        {
            string directory = Directory.CreateDirectory(Path.Combine(_root, "repo-" + i)).FullName;
            additional.Add(new AddAgentRepositoryAuthorityRequest { Alias = "repo" + i, WorkspaceRoot = directory });
        }

        Assert.Throws<InvalidDataException>(() => store.Replace("task-1", new ReplaceAgentRepositorySetRequest
        {
            PrimaryWorkspaceRoot = _primary,
            AdditionalRepositories = additional
        }, Task()));
        Assert.Null(store.Get("task-1"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
