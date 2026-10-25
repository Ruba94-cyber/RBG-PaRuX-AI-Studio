using System;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using PaRuX.UITests.Infrastructure;
using PaRuX.UITests.PageObjects;
using Xunit;

namespace PaRuX.UITests.Tests;

/// <summary>The Create workspace exposes the workflow import button and the LoRA search box. Reachability only:
/// no file is picked, nothing is searched and nothing is downloaded.</summary>
[Collection(UiTestCollection.Name)]
public sealed class WorkflowLoraEntryTests
{
    [Fact, Trait("Category", "Regression"), Trait("Category", "Create"), Trait("Category", "FlaUI")]
    public void Create_workspace_exposes_workflow_import_and_lora_search()
    {
        using var app = new UiTestApplication(nameof(Create_workspace_exposes_workflow_import_and_lora_search));
        try
        {
            new MainWindowPage(app.MainWindow).Click("Navigation_Create_Item", ControlType.ListItem);
            AutomationElement import = UiWait.ById(app.MainWindow, "Create_Workflow_Import_Button", ControlType.Button);
            Assert.True(import.Properties.IsEnabled.ValueOrDefault);
            AutomationElement search = UiWait.ById(app.MainWindow, "Create_Lora_Search", ControlType.Edit);
            Assert.True(search.Properties.IsEnabled.ValueOrDefault);
        }
        catch (Exception ex) { app.CaptureFailure(nameof(Create_workspace_exposes_workflow_import_and_lora_search), ex); throw; }
    }
}
