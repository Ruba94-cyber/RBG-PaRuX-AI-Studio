using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// The training runtime pins a Python SERIES, not a patch uv may not publish. The series must be the one the
/// hash-locked wheel closure was generated for (CPython 3.11): a hashed cp311 lock cannot be installed into any other
/// interpreter, so the identity pin and the lock move together.
/// </summary>
public sealed class TrainingPythonPinTests
{
    [Fact]
    public void The_pin_is_the_series_the_hashed_lock_was_generated_for()
    {
        Assert.Equal("3.11", TrainingRuntimeCatalog.PythonVersion);
        Assert.True(TrainingRuntimeCatalog.MatchesPinnedPythonSeries(ManagedPythonRuntime.DefaultPythonVersion));
    }

    [Theory]
    [InlineData("3.11.14")]
    [InlineData("3.11.9")]
    [InlineData("3.11.0")]
    [InlineData("3.11")]
    public void Any_patch_of_the_pinned_series_satisfies_it(string resolved) =>
        Assert.True(TrainingRuntimeCatalog.MatchesPinnedPythonSeries(resolved),
            resolved + " belongs to the pinned series but was rejected.");

    [Theory]
    [InlineData("3.13.8")]
    [InlineData("3.12.8")]
    [InlineData("3.1.3")]
    [InlineData("")]
    [InlineData(null)]
    public void A_different_series_does_not(string? resolved) =>
        Assert.False(TrainingRuntimeCatalog.MatchesPinnedPythonSeries(resolved),
            (resolved ?? "<null>") + " is not the pinned series but was accepted.");

    [Fact]
    public void A_neighbouring_series_is_not_matched_by_prefix_alone()
    {
        // "3.111.0" starts with "3.11" as text and is a different series entirely.
        Assert.False(TrainingRuntimeCatalog.MatchesPinnedPythonSeries("3.111.0"));
    }
}
