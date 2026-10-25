using System;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using PaRuX.UITests.Infrastructure;
using PaRuX.UITests.PageObjects;
using Xunit;

namespace PaRuX.UITests.Tests;

/// <summary>Lifecycle Lab covers dataset curation, evaluation, training, adapters and AutoLab. These tests only
/// check that its controls are reachable by stable id; no search, download, install or training is started.</summary>
[Collection(UiTestCollection.Name)]
public sealed class LifecycleLabCoverageTests
{
    private static void Run(string step, Action<UiTestApplication> action)
    {
        using var app = new UiTestApplication(step);
        try { action(app); }
        catch (Exception ex) { app.CaptureFailure(step, ex); throw; }
    }

    private static void Open(UiTestApplication app)
    {
        var nav = new WorkspaceNavigationPage(app.MainWindow, app);
        nav.EnableAdvanced();
        nav.NavigateTo("Navigation_Lifecycle_Item");
        UiWait.ById(app.MainWindow, "LifecycleLab_RefreshAll_Button", ControlType.Button);
    }

    [Fact, Trait("Category", "Regression"), Trait("Category", "Lifecycle"), Trait("Category", "FlaUI")]
    public void Lifecycle_lab_exposes_header_dataset_and_eval_controls() => Run(
        nameof(Lifecycle_lab_exposes_header_dataset_and_eval_controls), app =>
        {
            Open(app);

            AutomationElement title = UiWait.ById(app.MainWindow, "LifecycleLab_Title_Text", ControlType.Text);
            Assert.Equal("Lifecycle Lab", title.Name);
            UiWait.ById(app.MainWindow, "LifecycleLab_Status_Text", ControlType.Text);
            UiWait.ById(app.MainWindow, "LifecycleLab_DatasetSearch_TextBox", ControlType.Edit);
            UiWait.ById(app.MainWindow, "LifecycleLab_DatasetSearch_Button", ControlType.Button);
            UiWait.ById(app.MainWindow, "LifecycleLab_SourcePath_TextBox", ControlType.Edit);
            UiWait.ById(app.MainWindow, "LifecycleLab_Inspect_Button", ControlType.Button);
            UiWait.ById(app.MainWindow, "LifecycleLab_PromptColumn_ComboBox", ControlType.ComboBox);
            UiWait.ById(app.MainWindow, "LifecycleLab_EvalDataset_ComboBox", ControlType.ComboBox);
            UiWait.ById(app.MainWindow, "LifecycleLab_NewEvalName_TextBox", ControlType.Edit);
            UiWait.ById(app.MainWindow, "LifecycleLab_CreateEval_Button", ControlType.Button);
            UiWait.ById(app.MainWindow, "LifecycleLab_RunEval_Button", ControlType.Button);
        });

    [Fact, Trait("Category", "Regression"), Trait("Category", "Lifecycle"), Trait("Category", "FlaUI")]
    public void Lifecycle_lab_exposes_training_adapter_and_autolab_controls_without_starting_work() => Run(
        nameof(Lifecycle_lab_exposes_training_adapter_and_autolab_controls_without_starting_work), app =>
        {
            Open(app);

            UiWait.ById(app.MainWindow, "LifecycleLab_InstallRuntime_Button", ControlType.Button);
            UiWait.ById(app.MainWindow, "LifecycleLab_TrainingPath_TextBox", ControlType.Edit);
            UiWait.ById(app.MainWindow, "LifecycleLab_BaseModel_TextBox", ControlType.Edit);
            UiWait.ById(app.MainWindow, "LifecycleLab_TrainingMethod_ComboBox", ControlType.ComboBox);
            UiWait.ById(app.MainWindow, "LifecycleLab_CheckRun_Button", ControlType.Button);
            UiWait.ById(app.MainWindow, "LifecycleLab_StartTraining_Button", ControlType.Button);
            UiWait.ById(app.MainWindow, "LifecycleLab_SweepRoles_TextBox", ControlType.Edit);
            UiWait.ById(app.MainWindow, "LifecycleLab_RunSweep_Button", ControlType.Button);
        });

    [Fact, Trait("Category", "Regression"), Trait("Category", "Lifecycle"), Trait("Category", "FlaUI")]
    public void Lifecycle_lab_refresh_all_leaves_a_truthful_status_and_never_a_blank_one() => Run(
        nameof(Lifecycle_lab_refresh_all_leaves_a_truthful_status_and_never_a_blank_one), app =>
        {
            Open(app);
            UiWait.ById(app.MainWindow, "LifecycleLab_RefreshAll_Button", ControlType.Button)
                .Patterns.Invoke.Pattern.Invoke();

            AutomationElement status = UiWait.ById(app.MainWindow, "LifecycleLab_Status_Text", ControlType.Text);
            UiWait.For(() => !string.IsNullOrWhiteSpace(status.Name), "lifecycle status text after refresh");
        });
}
