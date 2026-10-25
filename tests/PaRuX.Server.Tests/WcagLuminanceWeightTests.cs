using System;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

// Pins the WCAG 2.1 relative-luminance channel weights with exact pure-channel values. The palette tests use neutral
// greys, black and white, where swapping the red and green weights is invisible, so only these cases catch a weight error.
public sealed class WcagLuminanceWeightTests
{
    [Theory]
    [InlineData("#FF0000", 0.2126)]
    [InlineData("#00FF00", 0.7152)]
    [InlineData("#0000FF", 0.0722)]
    public void Pure_channel_luminance_uses_the_WCAG_weight_for_that_channel(string hex, double expectedLuminance)
    {
        double actual = WcagContrastCalculator.CalculateLuminance(WcagColor.Parse(hex));

        Assert.True(Math.Abs(actual - expectedLuminance) < 1e-9, $"{hex}: expected {expectedLuminance}, got {actual}");
    }

    [Theory]
    [InlineData("#FF0000", 0.2126)]
    [InlineData("#00FF00", 0.7152)]
    [InlineData("#0000FF", 0.0722)]
    public void Pure_channel_contrast_against_white_matches_the_WCAG_formula(string hex, double expectedLuminance)
    {
        double expected = (1.0 + 0.05) / (expectedLuminance + 0.05);

        double actual = WcagContrastCalculator.CalculateContrastRatio(WcagColor.Parse(hex), WcagColor.White);

        Assert.True(Math.Abs(actual - expected) < 1e-9, $"{hex} on white: expected {expected}, got {actual}");
    }
}
