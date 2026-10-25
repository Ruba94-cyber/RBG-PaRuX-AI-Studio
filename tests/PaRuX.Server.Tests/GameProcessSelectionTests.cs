using PaRuX.Server.Orchestration.Gaming;
using Xunit;
using Candidate = PaRuX.Server.Orchestration.Gaming.WorkloadOrchestrator.GameProcessCandidate;

namespace PaRuX.Server.Tests;

/// <summary>
/// Live functional-test regression: exiting a gaming session with no game running marked
/// C:\Windows\SystemApps\...\TextInputHost.exe as "the suspended game" (and would have frozen it with
/// the default Windows suspend provider), leaving PaRuX stuck in GameSuspended.
/// </summary>
public sealed class GameProcessSelectionTests
{
    private static readonly DateTime Session = new(2026, 9, 30, 6, 0, 0, DateTimeKind.Utc);
    private static readonly string Windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

    [Fact]
    public void Nothing_started_since_the_session_began_means_there_is_no_game()
    {
        var running = new[]
        {
            new Candidate(19184, "TextInputHost", Path.Combine(Windows, @"SystemApps\MicrosoftWindows.Client.CBS_cw5n1h2txyewy\TextInputHost.exe"), Session.AddHours(-3), true),
            new Candidate(2000, "chrome", @"C:\Program Files\Google\Chrome\Application\chrome.exe", Session.AddHours(-1), true),
            new Candidate(3000, "WINWORD", @"C:\Program Files\Microsoft Office\root\Office16\WINWORD.EXE", Session.AddMinutes(-5), true)
        };
        Assert.Null(WorkloadOrchestrator.SelectGameProcess(running, Session, foregroundProcessId: 19184));
    }

    [Fact]
    public void Windows_components_and_desktop_apps_are_never_the_game_even_when_started_later()
    {
        var running = new[]
        {
            new Candidate(10, "TextInputHost", Path.Combine(Windows, @"SystemApps\x\TextInputHost.exe"), Session.AddMinutes(1), true),
            new Candidate(11, "chrome", @"C:\Program Files\Google\Chrome\Application\chrome.exe", Session.AddMinutes(2), true),
            new Candidate(12, "Playnite.FullscreenApp", @"C:\Users\u\AppData\Local\Playnite\Playnite.FullscreenApp.exe", Session.AddMinutes(1), true)
        };
        Assert.Null(WorkloadOrchestrator.SelectGameProcess(running, Session, foregroundProcessId: 11));
    }

    [Fact]
    public void A_game_started_during_the_session_is_found_and_the_foreground_one_wins()
    {
        var running = new[]
        {
            new Candidate(20, "Cyberpunk2077", @"D:\SteamLibrary\steamapps\common\Cyberpunk 2077\bin\x64\Cyberpunk2077.exe", Session.AddMinutes(3), true),
            new Candidate(21, "eldenring", @"D:\SteamLibrary\steamapps\common\ELDEN RING\Game\eldenring.exe", Session.AddMinutes(5), true),
            new Candidate(22, "HiddenHelper", @"D:\Games\helper.exe", Session.AddMinutes(6), false)
        };
        Assert.Equal(20, WorkloadOrchestrator.SelectGameProcess(running, Session, foregroundProcessId: 20));
        Assert.Equal(21, WorkloadOrchestrator.SelectGameProcess(running, Session, foregroundProcessId: null));   // newest windowed
        Assert.Null(WorkloadOrchestrator.SelectGameProcess(running, sessionStartedUtc: null, foregroundProcessId: 20));
    }
}
