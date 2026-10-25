using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class UiCaptureProvenancePolicyTests
{
    [Theory]
    [InlineData(54, 101, 54, 101, true)]
    [InlineData(54, 101, 55, 101, false)]
    [InlineData(54, 101, 54, 102, false)]
    [InlineData(54, 101, 0, 0, false)]
    [InlineData(0, 101, 0, 101, false)]
    [InlineData(54, 0, 54, 0, false)]
    [InlineData(54, 101, 54, 0, false)]
    [InlineData(54, 101, 0, 101, false)]
    public void A_stable_foreground_screen_capture_requires_positive_matching_native_window_identity(
        int beforePid, long beforeHwnd, int afterPid, long afterHwnd,
        bool expected)
    {
        Assert.Equal(expected, UiCaptureProvenancePolicy.IsStable(
            beforePid, beforeHwnd, afterPid, afterHwnd));
    }

    [Theory]
    [InlineData(1024, 987654, 1024, 987654, true)]
    [InlineData(1024, 987654, 1024, 987655, false)]
    [InlineData(1024, 987654, 2048, 987654, false)]
    [InlineData(1024, 987654, 0, 0, false)]
    public void Ui_tree_from_another_window_is_not_correlated_with_a_captured_screenshot(
        int capturedPid, long capturedHwnd, int observedPid,
        long observedHwnd, bool expected)
    {
        Assert.Equal(expected, UiCaptureProvenancePolicy.IsMatchingUiTree(
            capturedPid, capturedHwnd, observedPid, observedHwnd));
    }
}
