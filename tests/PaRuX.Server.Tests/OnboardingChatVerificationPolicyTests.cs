using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class OnboardingChatVerificationPolicyTests
{
    [Fact]
    public void Correct_local_challenge_reply_is_accepted()
    {
        Assert.Null(OnboardingChatVerificationPolicy.FailureReason(
            "PRX-A8F20C1D", "PRX-a8f20c1d", true, false));
    }

    [Theory]
    [InlineData(false, false, "PRX-12345678")]
    [InlineData(false, true, "PRX-12345678")]
    [InlineData(true, true, "PRX-12345678")]
    [InlineData(true, false, "Hello without the verification code")]
    [InlineData(true, false, "")]
    public void Missing_provenance_remote_fallback_and_wrong_answers_fail_closed(bool local, bool remote, string answer)
    {
        Assert.NotNull(OnboardingChatVerificationPolicy.FailureReason(
            "PRX-12345678", answer, local, remote));
    }

    [Fact]
    public void Empty_challenge_fails_closed() =>
        Assert.NotNull(OnboardingChatVerificationPolicy.FailureReason(" ", "anything", true, false));
}
