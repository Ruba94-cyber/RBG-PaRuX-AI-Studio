using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Physical-PC regression, 2026-09-09: a saved manual Quick Chat bubble position near the true
/// screen edge (e.g. a user-dragged bottom-right spot) was silently relocated toward screen-center
/// on every relaunch, even though nothing was actually off-screen. Root cause, confirmed live: the
/// constructor called RestorePosition() before SetCollapsedGeometry(), so RestorePosition's
/// clamp-to-monitor math read AppWindow.Size while it still held WinUI's oversized default window
/// size instead of the real 132x44 collapsed pill - producing a much smaller maxX/maxY than the
/// screen actually allows and clamping a perfectly legitimate saved position down incorrectly.
/// BubblePlacementPolicyTests.Clamp_leaves_a_saved_bottom_right_position_untouched_when_it_already_fits
/// pins the pure clamp math's own correctness given the real collapsed size; PaRuX.App itself
/// (WinUI/net8.0-windows) can't be referenced from this net8.0 xunit project, so this test guards
/// the caller-side ordering fix the same way the rest of this file's siblings guard doc/contract
/// text - by asserting on the real source, not a compiled type.
/// </summary>
public sealed class BubbleWindowRestorePositionOrderTests
{
    [Fact]
    public void BubbleWindow_constructor_resizes_to_the_collapsed_geometry_before_restoring_position()
    {
        string root = FindRepositoryRoot();
        string path = Path.Combine(root, "src", "PaRuX.App", "Windows", "BubbleWindow.xaml.cs");
        Assert.True(File.Exists(path), "BubbleWindow.xaml.cs is missing: " + path);
        string text = File.ReadAllText(path);

        int setGeometryIndex = text.IndexOf("SetCollapsedGeometry();", StringComparison.Ordinal);
        int restorePositionIndex = text.IndexOf("RestorePosition();", StringComparison.Ordinal);

        Assert.True(setGeometryIndex >= 0, "BubbleWindow.xaml.cs no longer calls SetCollapsedGeometry().");
        Assert.True(restorePositionIndex >= 0, "BubbleWindow.xaml.cs no longer calls RestorePosition().");
        Assert.True(
            setGeometryIndex < restorePositionIndex,
            "SetCollapsedGeometry() must run before RestorePosition() - otherwise RestorePosition's " +
            "clamp-to-monitor math reads the window's stale, oversized default size instead of the " +
            "real collapsed pill size, and silently relocates a legitimate saved manual position.");
    }

    [Fact]
    public void RestorePosition_clamp_uses_the_shared_tested_BubblePlacementPolicy_Clamp_helper()
    {
        string root = FindRepositoryRoot();
        string path = Path.Combine(root, "src", "PaRuX.App", "Windows", "BubbleWindow.xaml.cs");
        string text = File.ReadAllText(path);

        int restorePositionStart = text.IndexOf("private void RestorePosition()", StringComparison.Ordinal);
        Assert.True(restorePositionStart >= 0, "Could not find RestorePosition() in BubbleWindow.xaml.cs.");

        int bodyStart = text.IndexOf('{', restorePositionStart);
        int depth = 0;
        int bodyEnd = -1;
        for (int i = bodyStart; i < text.Length; i++)
        {
            if (text[i] == '{') depth++;
            else if (text[i] == '}')
            {
                depth--;
                if (depth == 0) { bodyEnd = i; break; }
            }
        }
        Assert.True(bodyEnd > bodyStart, "Could not find the closing brace of RestorePosition().");

        string body = text[bodyStart..bodyEnd];
        Assert.Contains("BubblePlacementPolicy.Clamp(", body, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current != null)
        {
            if (File.Exists(Path.Combine(current.FullName, "PaRuX.sln")))
                return current.FullName;
            current = current.Parent;
        }

        current = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (current != null)
        {
            if (File.Exists(Path.Combine(current.FullName, "PaRuX.sln")))
                return current.FullName;
            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate PaRuX.sln from the test process.");
    }
}
