using PaRuX.Contracts;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class AgentRepositoryAliasValidationCharacterizationTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void IsValid_rejects_null_or_empty(string? alias)
    {
        Assert.False(AgentRepositoryAliasValidation.IsValid(alias));
    }

    [Theory]
    [InlineData("a", true)]
    [InlineData("Z", true)]
    [InlineData("repo-1", true)]
    [InlineData("repo_1", true)]
    [InlineData("repo.1", true)]
    [InlineData("A1._-", true)]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", true)]
    public void IsValid_accepts_valid_aliases(string alias, bool expected)
    {
        Assert.Equal(expected, AgentRepositoryAliasValidation.IsValid(alias));
    }

    [Theory]
    [InlineData("1", "leading digit")]
    [InlineData(".", "leading dot")]
    [InlineData("_", "leading underscore")]
    [InlineData("-", "leading dash")]
    [InlineData("a b", "space")]
    [InlineData("a/b", "slash")]
    [InlineData("a\\b", "backslash")]
    [InlineData("a:b", "colon")]
    [InlineData("a?b", "question mark")]
    [InlineData("a*b", "star")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "too long")]
    public void IsValid_rejects_invalid_first_or_middle_chars(string alias, string _)
    {
        Assert.False(AgentRepositoryAliasValidation.IsValid(alias));
    }

    [Fact]
    public void IsValid_rejects_trailing_dot()
    {
        Assert.False(AgentRepositoryAliasValidation.IsValid("abc."));
    }

    [Fact]
    public void IsValid_rejects_windows_reserved_device_names()
    {
        Assert.False(AgentRepositoryAliasValidation.IsValid("CON"));
        Assert.False(AgentRepositoryAliasValidation.IsValid("NUL"));
        Assert.False(AgentRepositoryAliasValidation.IsValid("COM1"));
        Assert.False(AgentRepositoryAliasValidation.IsValid("LPT9"));
    }

    [Theory]
    [InlineData("CON", true)]
    [InlineData("con", true)]
    [InlineData("PRN", true)]
    [InlineData("AUX", true)]
    [InlineData("NUL", true)]
    [InlineData("CLOCK$", true)]
    [InlineData("COM1", true)]
    [InlineData("com9", true)]
    [InlineData("LPT1", true)]
    [InlineData("LPT9", true)]
    [InlineData("CON.txt", true)]
    [InlineData("COM0", false)]
    [InlineData("LPT0", false)]
    [InlineData("COMA", false)]
    [InlineData("CONSOLE", false)]
    [InlineData("COM10", false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    public void IsWindowsReservedDeviceName_classifies_reserved_names(string? name, bool expected)
    {
        Assert.Equal(expected, AgentRepositoryAliasValidation.IsWindowsReservedDeviceName(name));
    }

    [Fact]
    public void IsValid_allows_reserved_device_name_with_different_basename()
    {
        // "CONSOLE" starts with CON but basename is "CONSOLE" (7 chars), not "CON"
        Assert.True(AgentRepositoryAliasValidation.IsValid("CONSOLE"));
        // "CON1" is a valid alias: basename "CON1" (4 chars) but not COM/LPT prefix
        Assert.True(AgentRepositoryAliasValidation.IsValid("CON1"));
    }
}
