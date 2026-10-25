using System;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using PaRuX.UITests.Infrastructure;
using PaRuX.UITests.PageObjects;
using Xunit;

namespace PaRuX.UITests.Tests;

[Collection(UiTestCollection.Name)]
public sealed class ModelArenaCoverageTests
{
    [Fact, Trait("Category", "Regression"), Trait("Category", "Models"), Trait("Category", "FlaUI")]
    public void Compare_models_section_exposes_pickers_start_and_status()
    {
        const string testName = nameof(Compare_models_section_exposes_pickers_start_and_status);
        using var app = new UiTestApplication(testName, modelRoutingFixture: "empty");
        try
        {
            var main = new MainWindowPage(app.MainWindow);
            main.EnableAdvanced();
            main.Click("Navigation_ModelRouting_Item", ControlType.ListItem);

            UiWait.ById(app.MainWindow, "Arena_Model_0", ControlType.ComboBox);
            UiWait.ById(app.MainWindow, "Arena_Model_1", ControlType.ComboBox);
            AutomationElement start = UiWait.ById(app.MainWindow, "Arena_Start_Button", ControlType.Button);
            UiWait.ById(app.MainWindow, "Arena_Cancel_Button", ControlType.Button);
            Assert.NotNull(start);
        }
        catch (Exception error)
        {
            app.CaptureFailure(testName, error);
            throw;
        }
    }
}
