using System.Linq;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>Windowing of the long discovered-model list: bounded rendering, pinned installed entries, expand/collapse.</summary>
public sealed class MediaHubDiscoveredWindowTests
{
    private static string[] Items(int n) => Enumerable.Range(0, n).Select(i => "m" + i).ToArray();

    [Fact]
    public void DefaultShowsSixAndCountsHidden()
    {
        var r = DiscoveredListWindow.Compute(Items(700), _ => false, DiscoveredListWindow.DefaultLimit);
        Assert.Equal(6, r.Visible.Count);
        Assert.Equal(694, r.HiddenCount);
        Assert.True(r.CanShowMore);
        Assert.False(r.CanShowFewer);
        Assert.Equal("Show more (694)", r.ShowMoreLabel);
    }

    [Fact]
    public void OrderIsKeptSoBestProposedStaysFirst()
    {
        var r = DiscoveredListWindow.Compute(Items(20), _ => false, 6);
        Assert.Equal(new[] { "m0", "m1", "m2", "m3", "m4", "m5" }, r.Visible);
    }

    [Fact]
    public void ExpandAddsTwelveThenCollapseResets()
    {
        int limit = DiscoveredListWindow.Expand(DiscoveredListWindow.DefaultLimit);
        Assert.Equal(18, limit);
        var r = DiscoveredListWindow.Compute(Items(700), _ => false, limit);
        Assert.Equal(18, r.Visible.Count);
        Assert.Equal(682, r.HiddenCount);
        Assert.True(r.CanShowFewer);
        Assert.Equal(12, r.NextRevealCount);
        Assert.Equal(6, DiscoveredListWindow.Compute(Items(700), _ => false, DiscoveredListWindow.Collapse(limit)).Visible.Count);
    }

    [Fact]
    public void InstalledEntriesAreAlwaysVisibleAndDoNotUseTheLimit()
    {
        var r = DiscoveredListWindow.Compute(Items(50), x => x == "m40" || x == "m49", 6);
        Assert.Equal(8, r.Visible.Count);
        Assert.Contains("m40", r.Visible);
        Assert.Contains("m49", r.Visible);
        Assert.Equal(42, r.HiddenCount);
    }

    [Fact]
    public void ShortListHasNoControls()
    {
        var r = DiscoveredListWindow.Compute(Items(4), _ => false, 6);
        Assert.Equal(4, r.Visible.Count);
        Assert.Equal(0, r.HiddenCount);
        Assert.False(r.CanShowMore);
        Assert.False(r.CanShowFewer);
    }

    [Fact]
    public void LastRevealShowsRemainderAndOffersShowFewer()
    {
        var r = DiscoveredListWindow.Compute(Items(20), _ => false, 18);
        Assert.Equal(2, r.HiddenCount);
        Assert.Equal(2, r.NextRevealCount);
        Assert.True(r.CanShowFewer);
    }
}
