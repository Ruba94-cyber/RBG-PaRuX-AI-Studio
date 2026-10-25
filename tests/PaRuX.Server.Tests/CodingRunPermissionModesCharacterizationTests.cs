using System;
using PaRuX.Contracts;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class CodingRunPermissionModesCharacterizationTests
{
    [Theory]
    [InlineData("bypass")]
    [InlineData("BYPASS")]
    [InlineData("Bypass")]
    [InlineData("ask")]
    [InlineData("ASK")]
    [InlineData("plan")]
    [InlineData("PLAN")]
    public void IsKnown_accepts_every_spelling_of_each_mode(string value)
    {
        Assert.True(CodingRunPermissionModes.IsKnown(value));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("yolo")]
    [InlineData("approve")]
    [InlineData("bypass ")]
    public void IsKnown_rejects_unknown_or_blank_input(string? value)
    {
        Assert.False(CodingRunPermissionModes.IsKnown(value));
    }

    [Theory]
    [InlineData("  bypass  ", "bypass")]
    [InlineData("Ask", "ask")]
    [InlineData("pLaN", "plan")]
    public void Normalize_returns_lowercase_trimmed_known_mode(string input, string expected)
    {
        Assert.Equal(expected, CodingRunPermissionModes.Normalize(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("anything")]
    public void Normalize_falls_back_to_bypass_for_unknown_input(string? value)
    {
        Assert.Equal("bypass", CodingRunPermissionModes.Normalize(value));
    }

    [Fact]
    public void Constants_are_distinct_lowercase_tokens()
    {
        Assert.Equal("bypass", CodingRunPermissionModes.Bypass);
        Assert.Equal("ask", CodingRunPermissionModes.Ask);
        Assert.Equal("plan", CodingRunPermissionModes.Plan);
        Assert.True(CodingRunPermissionModes.Bypass != CodingRunPermissionModes.Ask);
        Assert.True(CodingRunPermissionModes.Ask != CodingRunPermissionModes.Plan);
        Assert.True(CodingRunPermissionModes.Bypass != CodingRunPermissionModes.Plan);
    }

    [Theory]
    [InlineData("bypass")]
    [InlineData("ask")]
    [InlineData("plan")]
    public void Normalize_is_idempotent_on_canonical_values(string canonical)
    {
        Assert.Equal(canonical, CodingRunPermissionModes.Normalize(CodingRunPermissionModes.Normalize(canonical)));
    }

    [Fact]
    public void Normalize_is_fixed_point_only_for_known_values()
    {
        Assert.False(string.Equals(CodingRunPermissionModes.Normalize("bogus"), "bogus", StringComparison.Ordinal));
        Assert.Equal(CodingRunPermissionModes.Normalize(CodingRunPermissionModes.Normalize("bogus")), CodingRunPermissionModes.Bypass);
    }
}
