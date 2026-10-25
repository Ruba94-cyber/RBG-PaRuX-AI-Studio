using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class CodingRepositoryAliasPolicyTests
{
    [Theory]
    [InlineData("primary", true)]
    [InlineData("backend-v2", true)]
    [InlineData("api.server", true)]
    [InlineData("UI_test", true)]
    [InlineData(".", false)]
    [InlineData("..", false)]
    [InlineData(".git", false)]
    [InlineData("bad & injected", false)]
    [InlineData("bad%PATH%", false)]
    [InlineData("0-primary", false)]
    [InlineData("trailing.", false)]
    [InlineData("CON", false)]
    [InlineData("nul.txt", false)]
    [InlineData("COM1", false)]
    [InlineData("LPT9", false)]
    [InlineData("naïve", false)]
    [InlineData("", false)]
    public void Repository_alias_is_unambiguous_safe_Windows_path_and_cmd_segment(
        string alias, bool allowed)
    {
        Assert.Equal(allowed, CodingRepositoryAliasPolicy.IsValid(alias));
    }

    [Theory]
    [InlineData("task-a", true)]
    [InlineData("0123456789abcdef", true)]
    [InlineData(".", false)]
    [InlineData("..", false)]
    [InlineData(".hidden", false)]
    [InlineData("CON", false)]
    [InlineData("nul.txt", false)]
    [InlineData("COM1", false)]
    [InlineData("bad/child", false)]
    [InlineData("bad\\child", false)]
    [InlineData("%BAD%", false)]
    [InlineData("", false)]
    public void Task_id_cannot_escape_its_owned_multi_worktree_directory(
        string taskId, bool allowed)
    {
        Assert.Equal(allowed, CodingRepositoryAliasPolicy.IsSafeTaskId(taskId));
    }
}
