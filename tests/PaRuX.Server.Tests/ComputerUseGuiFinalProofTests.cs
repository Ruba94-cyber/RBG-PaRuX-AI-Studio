using System;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class ComputerUseGuiFinalProofTests
{
    private const string ExactTree = """
        {"windows":[{"hwnd":456,"elements":[
          {"selector":"txt-safe","name":"Result","value":"expected private value"}
        ]}]}
        """;

    private static WindowsUiTreeObservationService.Observation Evidence(
        string raw = ExactTree, int pid = 123, long hwnd = 456) =>
        new("observed", raw, pid, hwnd);

    [Fact]
    public void A_genuine_verified_assertion_can_be_rechecked_against_a_new_observation()
    {
        var proof = ComputerUseGuiFinalProof.FromVerifiedAssertion(
            123, 456, "txt-safe", "value", "expected private value",
            WindowsUiAssertionPolicy.Verdict.Verified);
        Assert.NotNull(proof);
        Assert.True(proof!.Recheck(Evidence()));
        // Record contents must not be emitted via string interpolation or logs.
        Assert.DoesNotContain("expected private value", proof.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("txt-safe", proof.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(124, 456)]
    [InlineData(123, 457)]
    [InlineData(0, 456)]
    [InlineData(123, 0)]
    public void Target_switches_cannot_reuse_a_prior_successful_check(int pid, long hwnd)
    {
        var proof = ComputerUseGuiFinalProof.FromVerifiedAssertion(
            123, 456, "txt-safe", "value", "expected private value",
            WindowsUiAssertionPolicy.Verdict.Verified);
        Assert.NotNull(proof);
        Assert.False(proof!.Recheck(Evidence(ExactTree, pid, hwnd)));
    }

    [Theory]
    [InlineData("""{"windows":[{"hwnd":456,"elements":[{"selector":"txt-safe","value":"modified"}]}]}""")]
    [InlineData("""{"windows":[{"hwnd":456,"elements":[]}]}""")]
    [InlineData("""{"windows":[{"hwnd":456,"elements":[{"selector":"txt-safe","name":"Result"}]}]}""")]
    [InlineData("""{"windows":[{"hwnd":456,"elements":[{"selector":"txt-safe","value":"expected private value"},{"selector":"txt-safe","value":"expected private value"}]}]}""")]
    [InlineData("""{"windows":[{"hwnd":789,"elements":[{"selector":"txt-safe","value":"expected private value"}]}]}""")]
    public void New_state_missing_or_ambiguous_controls_cannot_be_reported_verified(string changed)
    {
        var proof = ComputerUseGuiFinalProof.FromVerifiedAssertion(
            123, 456, "txt-safe", "value", "expected private value",
            WindowsUiAssertionPolicy.Verdict.Verified);
        Assert.NotNull(proof);
        Assert.False(proof!.Recheck(Evidence(changed)));
    }

    [Fact]
    public void Failed_or_unverified_ui_assert_never_creates_proof()
    {
        Assert.Null(ComputerUseGuiFinalProof.FromVerifiedAssertion(
            123, 456, "txt-safe", "value", "expected private value",
            WindowsUiAssertionPolicy.Verdict.DifferentValue));
        Assert.Null(ComputerUseGuiFinalProof.FromVerifiedAssertion(
            123, 456, "txt-safe", "value", null,
            WindowsUiAssertionPolicy.Verdict.Verified));
        Assert.Null(ComputerUseGuiFinalProof.FromVerifiedAssertion(
            0, 456, "txt-safe", "value", "ok",
            WindowsUiAssertionPolicy.Verdict.Verified));
    }

    [Fact]
    public void Missing_or_unavailable_readback_is_never_a_pass()
    {
        var proof = ComputerUseGuiFinalProof.FromVerifiedAssertion(
            123, 456, "txt-safe", "value", "expected private value",
            WindowsUiAssertionPolicy.Verdict.Verified);
        Assert.NotNull(proof);
        Assert.False(proof!.Recheck(null));
        Assert.False(proof.Recheck(new("timeout", string.Empty, 123, 456)));
    }
}
