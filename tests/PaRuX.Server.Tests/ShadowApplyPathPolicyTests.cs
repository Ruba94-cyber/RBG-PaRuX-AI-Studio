using System;
using System.IO;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class ShadowApplyPathPolicyTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(),
        "parux-shadow-safe-path-" + Guid.NewGuid().ToString("N"));
    private string Source => Path.Combine(_base, "source");
    private string Destination => Path.Combine(_base, "destination");

    public ShadowApplyPathPolicyTests()
    {
        Directory.CreateDirectory(Source);
        Directory.CreateDirectory(Destination);
    }

    [Theory]
    [InlineData("src/Program.cs", true)]
    [InlineData("README.md", true)]
    [InlineData("images/icon.png", true)]
    [InlineData("../outside.txt", false)]
    [InlineData("folder/../../outside", false)]
    [InlineData("folder/./child", false)]
    [InlineData("folder//child", false)]
    [InlineData("CON", false)]
    [InlineData("nul.txt", false)]
    [InlineData("data:extra", false)]
    [InlineData("", false)]
    public void Only_relative_non_device_non_traversal_paths_remain_inside_both_roots(
        string relative, bool expected)
    {
        Assert.Equal(expected,
            ShadowApplyPathPolicy.IsSafe(Source, Destination, relative));
    }

    [Fact]
    public void Existing_destination_junction_or_symlink_must_not_redirect_write_outside_root()
    {
        string outside = Directory.CreateDirectory(Path.Combine(_base, "outside")).FullName;
        string link = Path.Combine(Destination, "linked");
        try { Directory.CreateSymbolicLink(link, outside); }
        catch (Exception ex) when (ex is UnauthorizedAccessException
            or IOException or PlatformNotSupportedException)
        {
            // Windows hosts without developer mode cannot create symlinks,
            // so this case needs the dedicated physical desktop suite.
            return;
        }
        Assert.False(ShadowApplyPathPolicy.IsSafe(
            Source, Destination, "linked/secret.txt"));
    }

    [Fact]
    public void Existing_source_link_cannot_be_read_as_a_project_owned_file()
    {
        string outside = Directory.CreateDirectory(Path.Combine(_base, "outside")).FullName;
        string link = Path.Combine(Source, "linked");
        try { Directory.CreateSymbolicLink(link, outside); }
        catch (Exception ex) when (ex is UnauthorizedAccessException
            or IOException or PlatformNotSupportedException)
        {
            return;
        }
        Assert.False(ShadowApplyPathPolicy.IsSafe(
            Source, Destination, "linked/secret.txt"));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_base)) Directory.Delete(_base, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
