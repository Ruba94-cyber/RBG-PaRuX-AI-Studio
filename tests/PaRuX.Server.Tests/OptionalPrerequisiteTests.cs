using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Pins the policy behind optional-prerequisite reporting: a missing prerequisite is a Skipped test that names
/// its reason, or a failure when the release switch requires it, and never a Passed test.
/// </summary>
public sealed class OptionalPrerequisiteTests
{
    [Fact]
    public void A_present_prerequisite_never_skips()
    {
        Assert.Null(OptionalPrerequisite.SkipReasonWhenAbsent(available: true, "unused", required: false));
        Assert.Null(OptionalPrerequisite.SkipReasonWhenAbsent(available: true, "unused", required: true));
    }

    [Fact]
    public void A_missing_prerequisite_skips_with_its_reason_when_not_required()
    {
        string? reason = OptionalPrerequisite.SkipReasonWhenAbsent(
            available: false, "the local embedding engine", required: false);

        Assert.NotNull(reason);
        Assert.Contains("the local embedding engine", reason, StringComparison.Ordinal);
        Assert.Contains(OptionalPrerequisite.RequireSwitch, reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_missing_prerequisite_is_not_skipped_when_the_release_switch_requires_it()
    {
        Assert.Null(OptionalPrerequisite.SkipReasonWhenAbsent(
            available: false, "the local embedding engine", required: true));
    }

    [Fact]
    public void A_missing_required_prerequisite_fails_the_test_body()
    {
        Xunit.Sdk.XunitException failure = Assert.ThrowsAny<Xunit.Sdk.XunitException>(() =>
            OptionalPrerequisite.AssertAvailable(available: false, "the local embedding engine"));

        Assert.Contains("the local embedding engine", failure.Message, StringComparison.Ordinal);
        Assert.Contains(OptionalPrerequisite.RequireSwitch, failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_present_prerequisite_passes_the_body_guard()
    {
        OptionalPrerequisite.AssertAvailable(available: true, "the local embedding engine");
    }
}
