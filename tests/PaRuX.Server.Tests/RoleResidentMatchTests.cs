using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Live benchmark review (2026-09-28): after the coding role was reassigned (or an exact candidate
/// was loaded for it), the role label stayed "coding" and the old resident model kept answering
/// while the route reported the newly assigned path.
/// </summary>
public sealed class RoleResidentMatchTests
{
    [Theory]
    [InlineData("coding", @"C:\m\small.gguf", @"C:\m\small.gguf", true, true)]
    [InlineData("coding", @"C:\m\small.gguf", @"C:\m\large.gguf", true, false)]
    [InlineData("coding", @"C:\M\SMALL.gguf", @"c:\m\small.gguf", true, true)]
    [InlineData("coding", null, @"C:\m\large.gguf", true, true)]
    [InlineData("standard", @"C:\m\small.gguf", @"C:\m\small.gguf", true, false)]
    [InlineData("coding", @"C:\m\small.gguf", @"C:\m\small.gguf", false, false)]
    public void Active_role_is_reused_only_when_the_resident_file_is_the_assigned_one(
        string activeRole, string? residentPath, string assignedPath, bool healthy, bool expected)
    {
        Assert.Equal(expected, RuntimeManagerService.ActiveRoleResidentMatchesAssignment(
            activeRole, residentPath, "coding", assignedPath, healthy));
    }
}
