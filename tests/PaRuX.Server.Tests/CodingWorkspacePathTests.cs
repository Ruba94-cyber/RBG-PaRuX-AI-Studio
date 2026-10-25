using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class CodingWorkspacePathTests
{
    [Theory]
    // Live test: the code-folder picker stored the git metadata folder as the coding workspace.
    [InlineData(@"C:\work\cart-repo\.git\hooks", @"C:\work\cart-repo")]
    [InlineData(@"C:\work\cart-repo\.git", @"C:\work\cart-repo")]
    [InlineData(@"C:\work\cart-repo\.GIT\refs\heads", @"C:\work\cart-repo")]
    [InlineData(@"D:\.git\objects", @"D:\")]
    [InlineData(@"C:\work\cart-repo", @"C:\work\cart-repo")]
    [InlineData(@"C:\work\cart-repo\src\.github\workflows", @"C:\work\cart-repo\src\.github\workflows")] // .github is not .git
    [InlineData("  ", "")]
    public void Git_metadata_folders_resolve_to_their_working_tree(string input, string expected) =>
        Assert.Equal(expected, CodingWorkspacePath.Normalize(input));
}
