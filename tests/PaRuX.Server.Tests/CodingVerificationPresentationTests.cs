using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class CodingVerificationPresentationTests
{
    [Theory]
    [InlineData("pass")]
    [InlineData("PASS")]
    [InlineData("passed")]
    public void Only_a_pass_is_shown_as_verified(string status)
    {
        CodingVerificationPresentation shown = CodingVerificationPresentation.For(status);
        Assert.True(shown.IsVerified);
        Assert.Equal(CodingVerificationPresentation.VerifiedBadge, shown.Badge);
        Assert.Equal(string.Empty, shown.ApplyWarning);
    }

    [Theory]
    [InlineData("unverified")]
    [InlineData("fail")]
    [InlineData("failed")]
    [InlineData("inconclusive")]
    [InlineData("error")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("verified")]
    public void Everything_else_is_never_shown_as_verified_and_warns_before_apply(string? status)
    {
        CodingVerificationPresentation shown = CodingVerificationPresentation.For(status);
        Assert.False(shown.IsVerified);
        Assert.NotEqual(CodingVerificationPresentation.VerifiedBadge, shown.Badge);
        Assert.DoesNotContain("verified", shown.Badge.Replace("not verified", string.Empty, StringComparison.OrdinalIgnoreCase),
            StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("WARNING", shown.ApplyWarning, StringComparison.Ordinal);
    }

    [Fact]
    public void Unverified_and_failed_have_distinct_honest_badges()
    {
        Assert.Equal(CodingVerificationPresentation.UnverifiedBadge, CodingVerificationPresentation.For("unverified").Badge);
        Assert.Equal(CodingVerificationPresentation.FailedBadge, CodingVerificationPresentation.For("fail").Badge);
        Assert.Equal(CodingVerificationPresentation.UnconfirmedBadge, CodingVerificationPresentation.For("inconclusive").Badge);
    }
}
