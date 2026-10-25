using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class BubblePlacementPolicyTests
{
    private static readonly BubblePlacementPolicy.Rect Work = new(0, 0, 1920, 1040);
    private static readonly BubblePlacementPolicy.Size Collapsed = new(132, 44);
    private static readonly BubblePlacementPolicy.Size Expanded = new(400, 480);

    [Fact]
    public void Default_lower_right_bubble_stays_clear_of_the_main_composer_band()
    {
        // The Send button also lives at the bottom-right of the composer (see ChatView.xaml's
        // SendButton), so it is the vertical composerClearance margin - not the horizontal side -
        // that has to keep the collapsed bubble clear of it. Calibrated against a real UI
        // Automation measurement: see DefaultBottomComposerClearanceLogical's comment.
        var main = new BubblePlacementPolicy.Rect(200, 80, 1500, 900);
        BubblePlacementPolicy.Point point = BubblePlacementPolicy.DefaultCollapsedOrigin(
            Work, main, Collapsed, inset: 18, composerClearance: 150);

        Assert.Equal(main.Right - Collapsed.Width - 18, point.X);
        Assert.Equal(main.Bottom - Collapsed.Height - 150, point.Y);
        Assert.True(point.Y + Collapsed.Height < main.Bottom);
    }

    [Fact]
    public void Default_lower_right_bubble_without_a_main_window_docks_to_the_screen_corner()
    {
        BubblePlacementPolicy.Point point = BubblePlacementPolicy.DefaultCollapsedOrigin(
            Work, mainWindow: null, Collapsed, inset: 18, composerClearance: 150);

        Assert.Equal(Work.Right - Collapsed.Width - 18, point.X);
        Assert.Equal(Work.Bottom - Collapsed.Height - 18, point.Y);
    }

    [Fact]
    public void Bottom_right_pivot_expands_up_and_left_without_moving_the_pivot_edge()
    {
        var pivot = new BubblePlacementPolicy.Point(1740, 950);
        BubblePlacementPolicy.Expansion expansion = BubblePlacementPolicy.ExpandedOrigin(
            Work, pivot, Collapsed, Expanded);

        Assert.True(expansion.GrowsLeft);
        Assert.True(expansion.GrowsUp);
        Assert.Equal(pivot.X + Collapsed.Width - Expanded.Width, expansion.Origin.X);
        Assert.Equal(pivot.Y + Collapsed.Height - Expanded.Height, expansion.Origin.Y);
        Assert.True(expansion.Origin.X + Expanded.Width <= Work.Right);
        Assert.True(expansion.Origin.Y + Expanded.Height <= Work.Bottom);
    }

    [Fact]
    public void Bubble_with_room_can_expand_down_and_right_in_place()
    {
        var pivot = new BubblePlacementPolicy.Point(300, 220);
        BubblePlacementPolicy.Expansion expansion = BubblePlacementPolicy.ExpandedOrigin(
            Work, pivot, Collapsed, Expanded);

        Assert.False(expansion.GrowsLeft);
        Assert.False(expansion.GrowsUp);
        Assert.Equal(pivot, expansion.Origin);
    }

    [Fact]
    public void Axes_are_independent_for_a_bubble_near_only_the_bottom_edge()
    {
        var pivot = new BubblePlacementPolicy.Point(300, 900);
        BubblePlacementPolicy.Expansion expansion = BubblePlacementPolicy.ExpandedOrigin(
            Work, pivot, Collapsed, Expanded);

        Assert.False(expansion.GrowsLeft);
        Assert.True(expansion.GrowsUp);
        Assert.Equal(pivot.X, expansion.Origin.X);
        Assert.Equal(pivot.Y + Collapsed.Height - Expanded.Height, expansion.Origin.Y);
    }

    [Fact]
    public void Oversized_or_partially_offscreen_geometry_is_clamped_to_work_area()
    {
        BubblePlacementPolicy.Point point = BubblePlacementPolicy.Clamp(
            new BubblePlacementPolicy.Rect(100, 50, 800, 600),
            new BubblePlacementPolicy.Point(2000, -200),
            new BubblePlacementPolicy.Size(400, 480));

        Assert.Equal(500, point.X);
        Assert.Equal(50, point.Y);
    }

    // Physical-PC regression, 2026-09-09: BubbleWindow.RestorePosition used to read AppWindow.Size
    // for this exact clamp call before SetCollapsedGeometry had resized the window from WinUI's
    // default size down to the real collapsed pill - so a legitimate saved position near the true
    // screen edge got clamped down to somewhere near screen-center on every launch, even though
    // nothing was actually off-screen. Fixed by reordering the two calls in the constructor; this
    // test pins the invariant Clamp itself must uphold given the REAL collapsed size (132x44,
    // matching Collapsed above): a saved bottom-right position that already fits must be returned
    // unchanged, not squeezed toward the center.
    [Fact]
    public void Clamp_leaves_a_saved_bottom_right_position_untouched_when_it_already_fits()
    {
        var work = new BubblePlacementPolicy.Rect(0, 0, 2560, 1600);
        var saved = new BubblePlacementPolicy.Point(2279, 1505);

        BubblePlacementPolicy.Point point = BubblePlacementPolicy.Clamp(work, saved, Collapsed);

        Assert.Equal(saved, point);
    }
}
