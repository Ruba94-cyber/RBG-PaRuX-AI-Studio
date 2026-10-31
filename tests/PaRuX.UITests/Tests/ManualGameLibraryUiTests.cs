using System.Net.NetworkInformation;
using System.Diagnostics;
using System.Text.Json;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using PaRuX.UITests.Infrastructure;
using PaRuX.UITests.PageObjects;
using Xunit;

namespace PaRuX.UITests.Tests;

[Collection(UiTestCollection.Name)]
public sealed class ManualGameLibraryUiTests
{
    [Fact, Trait("Category", "Regression"), Trait("Category", "FlaUI")]
    public void Manual_library_adds_and_removes_entry_without_removing_game_file()
    {
        Assert.DoesNotContain(IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners(), endpoint => endpoint.Port == 51123);
        using var app = new UiTestApplication(nameof(Manual_library_adds_and_removes_entry_without_removing_game_file));
        try
        {
            string marker = Path.Combine(app.DataRoot, "fixture-game.exe");
            File.WriteAllText(marker, "fixture, not executable");
            var item = UiWait.ById(app.MainWindow, "Navigation_GameLibrary_Item", ControlType.ListItem);
            new InstallProgressPage(app).Invoke(item);
            var name = UiWait.ById(app.MainWindow, "GameLibrary_Name_TextBox", ControlType.Edit).AsTextBox();
            var path = UiWait.ById(app.MainWindow, "GameLibrary_Path_TextBox", ControlType.Edit).AsTextBox();
            UiWait.For(() => File.Exists(Path.Combine(app.DataRoot, "server", "bootstrap.json")), "isolated core bootstrap");
            var bootstrap = JsonDocument.Parse(File.ReadAllText(Path.Combine(app.DataRoot, "server", "bootstrap.json")));
            Assert.Equal("http://127.0.0.1:51123", bootstrap.RootElement.GetProperty("endpoint").GetString()?.TrimEnd('/'));
            int serverPid = bootstrap.RootElement.GetProperty("processId").GetInt32();
            using var server = Process.GetProcessById(serverPid);
            string expectedServer = Path.Combine(Path.GetDirectoryName(Repository.AppExecutable)!, "PaRuX.Server", "PaRuX.Server.exe");
            Assert.Equal(Path.GetFullPath(expectedServer), Path.GetFullPath(server.MainModule!.FileName), ignoreCase: true);
            name.Text = "FlaUI fixture game"; path.Text = marker;
            var add = UiWait.ById(app.MainWindow, "GameLibrary_Add_Button", ControlType.Button);
            UiWait.For(() => add.Properties.IsEnabled.ValueOrDefault, "library finished loading");
            add.Patterns.Invoke.Pattern.Invoke();
            string library = Path.Combine(app.DataRoot, "server", "manual-games.json");
            UiWait.For(() => File.Exists(library) && File.ReadAllText(library).Contains("FlaUI fixture game", StringComparison.Ordinal), "fixture entry persisted");
            using var libraryDocument = JsonDocument.Parse(File.ReadAllText(library));
            string gameId = libraryDocument.RootElement.GetProperty("Games")[0].GetProperty("Id").GetString()!;
            var remove = UiWait.ById(app.MainWindow, "GameLibrary_Remove_" + gameId, ControlType.Button);
            remove.Patterns.Invoke.Pattern.Invoke();
            var confirm = Find(() => app.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("PrimaryButton").And(cf.ByControlType(ControlType.Button))), "remove confirmation");
            confirm.Patterns.Invoke.Pattern.Invoke();
            UiWait.For(() => !File.ReadAllText(library).Contains("FlaUI fixture game", StringComparison.Ordinal), "fixture entry removed");
            Assert.Equal("fixture, not executable", File.ReadAllText(marker));
        }
        catch (Exception ex) { app.CaptureFailure(nameof(Manual_library_adds_and_removes_entry_without_removing_game_file), ex); throw; }
    }
    private static AutomationElement Find(Func<AutomationElement?> find, string description)
    {
        AutomationElement? element = null;
        UiWait.For(() => (element = find()) != null, description);
        return element!;
    }

}
