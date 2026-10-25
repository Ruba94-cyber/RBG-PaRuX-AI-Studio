using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Developer-browser leftovers from an earlier PaRuX process: orphaned browser trees are selected for termination
/// only when they sit in PaRuX's own session profiles and nothing alive still owns them, and screenshots are kept
/// under PaRuX's data root with a sweep for crash leftovers. The process snapshot is a fake, so no browser runs.
/// </summary>
public sealed class BrowserOrphanAndScreenshotTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parux-orphan-" + Guid.NewGuid().ToString("N"));

    private string ProfileRoot => Path.Combine(_root, "browser-dev-profile");

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }

    private string SessionDirectory(string name) => Path.Combine(ProfileRoot, "session-" + name);

    private static BrowserProcessRecord Row(int pid, int parent, string? profile) =>
        new(pid, parent, "msedge.exe",
            profile == null ? null : "\"C:\\Edge\\msedge.exe\" --remote-debugging-port=1234 --user-data-dir=\"" + profile + "\" about:blank");

    [Fact]
    public void An_orphaned_session_tree_is_selected_by_its_root_only()
    {
        string profile = SessionDirectory("orphan");
        var rows = new[]
        {
            Row(100, parent: 50, profile),   // the root: its starter (50) no longer runs
            Row(101, parent: 100, profile),  // children are reached through the root
            Row(102, parent: 100, profile)
        };

        IReadOnlyList<int> selected = BrowserOrphanPolicy.SelectOrphanRoots(rows, ProfileRoot, pid => pid == 999);

        Assert.Equal(new[] { 100 }, selected);
    }

    [Fact]
    public void A_session_tree_that_a_live_process_still_owns_is_kept()
    {
        string profile = SessionDirectory("live");
        var rows = new[]
        {
            Row(200, parent: 60, profile),   // its starter (60) is still running
            Row(201, parent: 200, profile)
        };

        IReadOnlyList<int> selected = BrowserOrphanPolicy.SelectOrphanRoots(rows, ProfileRoot, pid => pid == 60);

        Assert.Empty(selected);
    }

    [Fact]
    public void Browsers_outside_the_session_directories_under_paRuX_root_are_never_selected()
    {
        var rows = new[]
        {
            Row(300, parent: 70, Path.Combine(_root, "someone-else", "profile")),            // another product's profile
            Row(301, parent: 71, Path.Combine(ProfileRoot, "screenshots")),                  // not a session directory
            Row(302, parent: 72, Path.Combine(_root, "browser-dev-profile-sibling", "session-x")), // similar prefix only
            Row(303, parent: 73, null)                                                       // no profile argument
        };

        IReadOnlyList<int> selected = BrowserOrphanPolicy.SelectOrphanRoots(rows, ProfileRoot, pid => false);

        Assert.Empty(selected);
    }

    [Fact]
    public void Screenshots_are_written_under_the_paRuX_root_with_the_expected_name()
    {
        string directory = BrowserScreenshotStore.DirectoryUnder(_root);
        string path = BrowserScreenshotStore.NewPath(directory);

        string fullPath = Path.GetFullPath(path);
        Assert.StartsWith(Path.GetFullPath(_root) + Path.DirectorySeparatorChar, fullPath, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("parux-browser-", Path.GetFileName(fullPath), StringComparison.Ordinal);
        Assert.EndsWith(".png", fullPath, StringComparison.Ordinal);
    }

    [Fact]
    public void A_stale_screenshot_left_by_a_crash_is_swept_while_fresh_and_unrelated_files_are_kept()
    {
        string directory = BrowserScreenshotStore.DirectoryUnder(_root);
        Directory.CreateDirectory(directory);
        string stale = BrowserScreenshotStore.NewPath(directory);
        string fresh = BrowserScreenshotStore.NewPath(directory);
        string unrelated = Path.Combine(directory, "notes.txt");
        File.WriteAllBytes(stale, new byte[] { 1 });
        File.WriteAllBytes(fresh, new byte[] { 2 });
        File.WriteAllText(unrelated, "keep");

        DateTime now = DateTime.UtcNow;
        File.SetLastWriteTimeUtc(stale, now.AddHours(-3));
        File.SetLastWriteTimeUtc(unrelated, now.AddHours(-3));
        File.SetLastWriteTimeUtc(fresh, now);

        int removed = BrowserScreenshotStore.SweepStale(directory, now, TimeSpan.FromHours(1));

        Assert.Equal(1, removed);
        Assert.False(File.Exists(stale));
        Assert.True(File.Exists(fresh));
        Assert.True(File.Exists(unrelated));
    }

    /// <summary>The quoting .NET uses when the whole --user-data-dir argument contains a space.</summary>
    private static BrowserProcessRecord WholeArgumentRow(int pid, int parent, string profile) =>
        new(pid, parent, "msedge.exe", "\"C:\\Edge\\msedge.exe\" \"--user-data-dir=" + profile + "\" about:blank");

    [Fact]
    public void An_orphaned_tree_whose_profile_path_contains_spaces_is_selected()
    {
        string spacedRoot = Path.Combine(_root, "Pat Smith", "browser-dev-profile");
        string profile = Path.Combine(spacedRoot, "session-spaced");
        var rows = new[]
        {
            WholeArgumentRow(400, parent: 80, profile),
            WholeArgumentRow(401, parent: 400, profile)
        };

        IReadOnlyList<int> selected = BrowserOrphanPolicy.SelectOrphanRoots(rows, spacedRoot, pid => false);

        Assert.Equal(new[] { 400 }, selected);
    }

    private static readonly DateTime Started = new(2026, 10, 18, 9, 0, 0, DateTimeKind.Utc);
    private const string SessionCommandLine =
        "\"C:\\Edge\\msedge.exe\" \"--user-data-dir=C:\\PaRuX\\browser-dev-profile\\session-a\" about:blank";

    [Fact]
    public void A_fresh_row_for_the_selected_process_is_the_same_process()
    {
        var selected = new BrowserProcessRecord(100, 50, "msedge.exe", SessionCommandLine, Started);

        Assert.True(BrowserOrphanPolicy.IsSameProcess(selected,
            new BrowserProcessRecord(100, 50, "msedge.exe", SessionCommandLine, Started)));
    }

    [Fact]
    public void A_reused_id_with_another_image_is_refused()
    {
        var selected = new BrowserProcessRecord(100, 50, "msedge.exe", SessionCommandLine, Started);

        Assert.False(BrowserOrphanPolicy.IsSameProcess(selected,
            new BrowserProcessRecord(100, 50, "chrome.exe", SessionCommandLine, Started)));
    }

    [Fact]
    public void A_reused_id_with_another_command_line_is_refused()
    {
        var selected = new BrowserProcessRecord(100, 50, "msedge.exe", SessionCommandLine, Started);

        Assert.False(BrowserOrphanPolicy.IsSameProcess(selected,
            new BrowserProcessRecord(100, 50, "msedge.exe", "msedge.exe --user-data-dir=C:\\Users\\me\\Edge", Started)));
    }

    [Fact]
    public void A_reused_id_with_another_start_time_is_refused()
    {
        var selected = new BrowserProcessRecord(100, 50, "msedge.exe", SessionCommandLine, Started);

        Assert.False(BrowserOrphanPolicy.IsSameProcess(selected,
            new BrowserProcessRecord(100, 50, "msedge.exe", SessionCommandLine, Started.AddMinutes(5))));
    }

    [Fact]
    public void A_process_that_has_gone_or_has_no_known_start_time_is_refused()
    {
        var selected = new BrowserProcessRecord(100, 50, "msedge.exe", SessionCommandLine, Started);
        var unknownStart = new BrowserProcessRecord(100, 50, "msedge.exe", SessionCommandLine, null);

        Assert.False(BrowserOrphanPolicy.IsSameProcess(selected, null));
        Assert.False(BrowserOrphanPolicy.IsSameProcess(unknownStart, unknownStart));
    }

    [Fact]
    public void Preparing_a_screenshot_path_sweeps_crash_leftovers_first_and_creates_the_folder()
    {
        string directory = BrowserScreenshotStore.DirectoryUnder(_root);
        Directory.CreateDirectory(directory);
        string stale = BrowserScreenshotStore.NewPath(directory);
        File.WriteAllBytes(stale, new byte[] { 1 });
        DateTime now = DateTime.UtcNow;
        File.SetLastWriteTimeUtc(stale, now.AddHours(-3));

        string path = BrowserScreenshotStore.PrepareNewPath(_root, now, TimeSpan.FromHours(1));

        Assert.False(File.Exists(stale));
        Assert.Equal(Path.GetFullPath(directory), Path.GetFullPath(Path.GetDirectoryName(path)!));
        string firstRunRoot = Path.Combine(_root, "first-run");
        BrowserScreenshotStore.PrepareNewPath(firstRunRoot, now, TimeSpan.FromHours(1));
        Assert.True(Directory.Exists(BrowserScreenshotStore.DirectoryUnder(firstRunRoot)));
    }
}
