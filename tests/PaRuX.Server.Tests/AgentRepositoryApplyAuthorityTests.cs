using PaRuX.Contracts;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class AgentRepositoryApplyAuthorityTests
{
    [Fact]
    public void Single_primary_from_task_repository_set_is_authoritative()
    {
        var set = new AgentRepositorySetDto
        {
            TaskId = "task-1",
            Repositories = new List<AgentRepositoryAuthorityDto>
            {
                new() { Alias = "primary", WorkspaceRoot = @"C:\repo", IsPrimary = true }
            }
        };

        Assert.Equal(@"C:\repo", AgentRepositoryApplyAuthority.RequireSinglePrimaryRoot(set, "task-1"));
    }

    [Fact]
    public void Wrong_task_or_missing_primary_cannot_authorize_apply()
    {
        var set = new AgentRepositorySetDto
        {
            TaskId = "task-1",
            Repositories = new List<AgentRepositoryAuthorityDto>
            {
                new() { Alias = "primary", WorkspaceRoot = @"C:\repo", IsPrimary = false }
            }
        };

        Assert.Throws<System.IO.InvalidDataException>(() => AgentRepositoryApplyAuthority.RequireSinglePrimaryRoot(set, "task-1"));
        set.Repositories[0].IsPrimary = true;
        Assert.Throws<System.IO.InvalidDataException>(() => AgentRepositoryApplyAuthority.RequireSinglePrimaryRoot(set, "different-task"));
        set.Repositories.Add(new AgentRepositoryAuthorityDto { Alias = "other", WorkspaceRoot = @"C:\other" });
        Assert.Throws<System.IO.InvalidDataException>(() => AgentRepositoryApplyAuthority.RequireSinglePrimaryRoot(set, "task-1"));
    }
}
