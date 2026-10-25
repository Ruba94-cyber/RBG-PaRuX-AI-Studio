using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class BrowserProcessTreePolicyTests
{
    private const string Root = @"C:\Users\user\AppData\Local\PaRuX\browser-dev-profile";
    private const string Profile = @"C:\Users\user\AppData\Local\PaRuX\browser-dev-profile\session-a1";
    private const string UserBrowser = @"C:\Users\user\AppData\Local\Microsoft\Edge\User Data";

    [Fact]
    public void Owned_tree_is_the_profile_root_and_its_children_even_without_the_argument()
    {
        var rows = new[]
        {
            new BrowserProcessRecord(100, 1, "msedge.exe",
                $"\"C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe\" --headless=new --user-data-dir=\"{Profile}\""),
            new BrowserProcessRecord(101, 100, "msedge.exe", "msedge.exe --type=renderer"),
            new BrowserProcessRecord(102, 101, "msedge.exe", "msedge.exe --type=utility"),
        };

        Assert.Equal(new[] { 100, 101, 102 },
            BrowserProcessTreePolicy.SelectOwnedProcessIds(rows, Profile, excludedProcessId: 999));
    }

    [Fact]
    public void The_users_own_browser_and_unrelated_processes_are_never_selected()
    {
        var rows = new[]
        {
            new BrowserProcessRecord(200, 1, "msedge.exe", $"msedge.exe --user-data-dir=\"{UserBrowser}\""),
            new BrowserProcessRecord(201, 200, "msedge.exe", "msedge.exe --type=renderer"),
            new BrowserProcessRecord(300, 1, "powershell.exe", $"powershell -NoProfile -Command \"Get-Item '{Profile}'\""),
            new BrowserProcessRecord(301, 300, "msedge.exe", "msedge.exe --type=gpu-process"),
        };

        Assert.Empty(BrowserProcessTreePolicy.SelectOwnedProcessIds(rows, Profile, excludedProcessId: 999));
    }

    [Fact]
    public void A_profile_that_only_shares_a_prefix_is_not_matched()
    {
        var rows = new[]
        {
            new BrowserProcessRecord(400, 1, "msedge.exe", $"msedge.exe --user-data-dir=\"{Profile}2\""),
            new BrowserProcessRecord(401, 1, "msedge.exe", $"msedge.exe --user-data-dir={Profile}-old"),
        };

        Assert.Empty(BrowserProcessTreePolicy.SelectOwnedProcessIds(rows, Profile, excludedProcessId: 999));
    }

    [Fact]
    public void Children_of_a_root_that_already_exited_are_not_claimed_without_the_profile_argument()
    {
        // The session root is gone and its id may now belong to an unrelated Edge process.
        var rows = new[]
        {
            new BrowserProcessRecord(500, 1, "msedge.exe", $"msedge.exe --user-data-dir=\"{UserBrowser}\""),
            new BrowserProcessRecord(501, 500, "msedge.exe", "msedge.exe --type=renderer"),
            new BrowserProcessRecord(600, 42, "msedge.exe", "msedge.exe --type=crashpad-handler"),
        };

        Assert.Empty(BrowserProcessTreePolicy.SelectOwnedProcessIds(rows, Profile, excludedProcessId: 999));
    }

    [Fact]
    public void The_current_process_is_excluded_and_a_blank_profile_selects_nothing()
    {
        var rows = new[] { new BrowserProcessRecord(7, 1, "msedge.exe", $"msedge.exe --user-data-dir={Profile}") };

        Assert.Empty(BrowserProcessTreePolicy.SelectOwnedProcessIds(rows, Profile, excludedProcessId: 7));
        Assert.Empty(BrowserProcessTreePolicy.SelectOwnedProcessIds(rows, "   ", excludedProcessId: 999));
    }

    [Theory]
    [InlineData("msedge.exe --user-data-dir=\"C:\\a b\\session-x\" --headless", "C:\\a b\\session-x")]
    // .NET quotes a whole argument that contains a space, so the quote comes before the flag.
    [InlineData("msedge.exe \"--user-data-dir=C:\\a b\\session-x\" --headless", "C:\\a b\\session-x")]
    [InlineData("\"C:\\Edge\\msedge.exe\" \"--user-data-dir=C:\\Pat Smith\\session-x\"", "C:\\Pat Smith\\session-x")]
    [InlineData("msedge.exe \"--user-data-dir=\" --headless", null)]
    [InlineData("msedge.exe --user-data-dir=C:\\a\\session-x --no-first-run", "C:\\a\\session-x")]
    [InlineData("msedge.exe --user-data-dir=C:\\a\\session-x", "C:\\a\\session-x")]
    [InlineData("msedge.exe --headless", null)]
    [InlineData("msedge.exe --user-data-dir=\"\"", null)]
    [InlineData(null, null)]
    public void Profile_directory_is_read_whether_quoted_or_bare(string? commandLine, string? expected)
    {
        Assert.Equal(expected, BrowserProcessTreePolicy.ProfileDirectoryOf(commandLine));
    }

    [Fact]
    public void Only_old_unowned_session_directories_directly_under_the_root_are_selected()
    {
        DateTime now = new(2026, 10, 18, 12, 0, 0, DateTimeKind.Utc);
        string oldIdle = Path.Combine(Root, "session-old-idle");
        string oldLive = Path.Combine(Root, "session-old-live");
        var entries = new[]
        {
            new ProfileDirectoryEntry(oldIdle, now.AddHours(-2)),
            new ProfileDirectoryEntry(oldLive, now.AddHours(-3)),
            new ProfileDirectoryEntry(Path.Combine(Root, "session-recent"), now.AddMinutes(-20)),
            new ProfileDirectoryEntry(Path.Combine(Root, "..", "other", "session-old"), now.AddDays(-9)),
            new ProfileDirectoryEntry(Path.Combine(Root, "cache-old"), now.AddDays(-9)),
            new ProfileDirectoryEntry(Path.Combine(Root, "session-parent", "session-nested"), now.AddDays(-9)),
        };

        IReadOnlyList<string> stale = BrowserProcessTreePolicy.SelectStaleProfileDirectories(
            Root, entries, now, TimeSpan.FromHours(1), new[] { oldLive });

        Assert.Equal(new[] { Path.GetFullPath(oldIdle) }, stale);
    }

    [Fact]
    public void Live_profiles_are_matched_regardless_of_path_case()
    {
        DateTime now = new(2026, 10, 18, 12, 0, 0, DateTimeKind.Utc);
        string idle = Path.Combine(Root, "session-idle");
        var entries = new[] { new ProfileDirectoryEntry(idle, now.AddDays(-1)) };

        Assert.Empty(BrowserProcessTreePolicy.SelectStaleProfileDirectories(
            Root, entries, now, TimeSpan.FromHours(1), new[] { Path.GetFullPath(idle).ToUpperInvariant() }));
    }
}
