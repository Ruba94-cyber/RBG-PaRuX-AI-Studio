using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class CodingVerificationCommandPolicyTests
{
    [Theory]
    [InlineData(0, false)]
    [InlineData(2, false)]
    [InlineData(3, true)]
    [InlineData(22, true)]
    public void Repeated_unverified_completion_attempts_pause_the_attempt(int rejections, bool expected)
    {
        Assert.Equal(expected, CodingVerificationCommandPolicy.ShouldPauseAfterUnverifiedCompletions(rejections));
    }

    [Theory]
    [InlineData(false, "Retry with the granted file.", true)]
    // Already consumed on an earlier resume: must not waive verification of later changes again.
    [InlineData(true, "Retry with the granted file.", false)]
    // Unanswered: not recorded, so the later answer is still delivered and consumed.
    [InlineData(false, null, false)]
    [InlineData(false, "  ", false)]
    public void User_answer_is_consumed_once_and_only_when_answered(bool alreadyConsumed, string? answer, bool expected)
    {
        Assert.Equal(expected, CodingVerificationCommandPolicy.IsNewUserAnswer(alreadyConsumed, answer));
    }

    [Theory]
    [InlineData(true, false, false, true, CodingWorkerHandoffDecision.WaitForUser)]
    [InlineData(true, false, true, false, CodingWorkerHandoffDecision.WaitForUser)]
    [InlineData(false, true, false, true, CodingWorkerHandoffDecision.WaitForDelegatedChild)]
    [InlineData(false, true, true, true, CodingWorkerHandoffDecision.WaitForDelegatedChild)]
    [InlineData(false, false, false, true, CodingWorkerHandoffDecision.RejectPendingVerification)]
    [InlineData(false, false, true, true, CodingWorkerHandoffDecision.RejectPendingVerification)]
    [InlineData(false, false, false, false, CodingWorkerHandoffDecision.RejectPendingVerification)]
    [InlineData(false, false, true, false, CodingWorkerHandoffDecision.SendToVerifier)]
    public void Nonterminal_pauses_never_get_confused_with_verifier_handoff_or_failed_tests(
        bool waitingForUser, bool waitingForChild, bool goalReported,
        bool verificationPending, CodingWorkerHandoffDecision expected)
    {
        Assert.Equal(expected, CodingVerificationCommandPolicy.DecideHandoff(
            waitingForUser, waitingForChild, goalReported, verificationPending));
    }

    [Theory]
    // Live BIC-09 run c93334bb: paused after invalid payloads with the fix never verified; the
    // model-based verifier then passed the diff and the task was recorded as Verified.
    [InlineData(false, true, false)]
    [InlineData(true, true, false)] // Even a model completion claim cannot waive pending checks.
    [InlineData(false, false, false)]
    [InlineData(true, false, true)]
    public void Paused_attempt_with_outstanding_verification_is_not_handed_to_the_verifier(
        bool goalReported, bool verificationPending, bool expected)
    {
        Assert.Equal(expected, CodingVerificationCommandPolicy.MayHandPausedAttemptToVerifier(goalReported, verificationPending));
    }

    [Theory]
    [InlineData("dotnet test", "dotnet test")]
    [InlineData("dotnet   test --no-restore", " dotnet test   --no-restore ")]
    [InlineData("pushd \"api\" && dotnet test --no-restore && popd", "pushd \"api\"  &&  dotnet test --no-restore && popd")]
    public void Exact_discovered_command_matches_after_whitespace_normalization(string discovered, string executed)
    {
        Assert.True(CodingVerificationCommandPolicy.IsExactMatch(discovered, executed));
    }

    [Theory]
    // Live benchmark r2: `dotnet test` failed with MSB1025 (named pipe denied in the AppContainer)
    // and the model's `dotnet test -m:1` passed but never counted as verification.
    [InlineData("dotnet test --no-restore", "dotnet test --no-restore -m:1", true)]
    [InlineData("dotnet build --no-restore", "dotnet build --no-restore -m:1 --disable-build-servers", true)]
    [InlineData("dotnet test", "dotnet test -maxcpucount:1 -nodeReuse:false", true)]
    [InlineData("dotnet test --no-restore", "dotnet test --no-restore -m:1 --filter Smoke", false)]
    [InlineData("dotnet test --no-restore", "dotnet test --no-restore -m:1 & echo ok", false)]
    // Restores, then runs the same tests serially: at least as strong as the discovered command.
    [InlineData("dotnet test --no-restore", "dotnet test -m:1", true)]
    [InlineData("dotnet test --no-restore", "dotnet test -m:1 --filter X", false)]
    [InlineData("npm test", "npm test -m:1", false)]
    public void Dotnet_verification_may_add_only_serial_build_switches(string discovered, string executed, bool expected)
    {
        Assert.Equal(expected, CodingVerificationCommandPolicy.IsExactMatch(discovered, executed));
    }

    [Theory]
    // Live benchmark r3: the model ran `dotnet test` (restores, then tests) 42 times; the discovered
    // `dotnet test --no-restore` never matched, so verification was never satisfied.
    [InlineData("dotnet test --no-restore", "dotnet test", true)]
    [InlineData("dotnet build --no-restore", "dotnet build -m:1", true)]
    [InlineData("dotnet test --no-restore", "dotnet test --filter X", false)]
    [InlineData("npm run build --no-restore", "npm run build", false)]
    public void Dropping_no_restore_from_a_dotnet_command_still_verifies(string discovered, string executed, bool expected)
    {
        Assert.Equal(expected, CodingVerificationCommandPolicy.IsExactMatch(discovered, executed));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(2, false)]
    [InlineData(3, true)]
    public void Repeating_an_unchanged_successful_command_pauses_the_attempt(int repeats, bool expected)
    {
        Assert.Equal(expected, CodingVerificationCommandPolicy.ShouldPauseAfterRepeatedSuccess(repeats));
    }

    [Theory]
    [InlineData("dotnet test", "dotnet test --no-restore")]
    [InlineData("dotnet test --no-restore", "dotnet test --no-restore & echo ok")]
    [InlineData("dotnet test --no-restore", "dotnet test --no-restore && exit /b 0")]
    [InlineData("npm test", "npm test || echo ignored")]
    [InlineData("cargo test --offline", "cargo test --offline -- --nocapture")]
    public void Appended_flags_or_shell_chaining_never_satisfy_completion_verification(string discovered, string executed)
    {
        Assert.False(CodingVerificationCommandPolicy.IsExactMatch(discovered, executed));
    }

    [Theory]
    [InlineData(null, "dotnet test")]
    [InlineData("dotnet test", null)]
    [InlineData("", "dotnet test")]
    [InlineData("dotnet test", "   ")]
    public void Missing_commands_never_match(string? discovered, string? executed)
    {
        Assert.False(CodingVerificationCommandPolicy.IsExactMatch(discovered, executed));
    }
}
