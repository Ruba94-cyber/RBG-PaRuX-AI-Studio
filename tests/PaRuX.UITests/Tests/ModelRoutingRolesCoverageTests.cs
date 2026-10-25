using System;
using System.Linq;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using PaRuX.UITests.Infrastructure;
using PaRuX.UITests.PageObjects;
using Xunit;

namespace PaRuX.UITests.Tests;

[Collection(UiTestCollection.Name)]
public sealed class ModelRoutingRolesCoverageTests
{
    [Fact, Trait("Category", "Regression"), Trait("Category", "Models"), Trait("Category", "FlaUI")]
    public void Every_role_explains_its_empty_candidate_and_model_picker_state()
    {
        const string testName = nameof(Every_role_explains_its_empty_candidate_and_model_picker_state);
        using var app = new UiTestApplication(testName);
        try
        {
            OpenModelRouting(app);
            AutomationElement rolePicker = UiWait.ById(app.MainWindow,
                "ModelRouting_Role_ComboBox", ControlType.ComboBox);
            string[] roles = ReadRoleNames(rolePicker);
            Assert.Equal(8, roles.Length);
            Assert.Equal(roles.Length, roles.Distinct(StringComparer.Ordinal).Count());

            AutomationElement roleExplanation = UiWait.ById(app.MainWindow,
                "ModelRouting_Status_Text", ControlType.Text);
            AutomationElement emptyPickerMessage = UiWait.ById(app.MainWindow,
                "ModelRouting_InstalledModelStatus_Text", ControlType.Text);
            AutomationElement candidates = UiWait.ById(app.MainWindow,
                "ModelRouting_Candidates_List", ControlType.List);
            AutomationElement installedPicker = UiWait.ById(app.MainWindow,
                "ModelRouting_InstalledModel_ComboBox", ControlType.ComboBox);
            AutomationElement addModel = UiWait.ById(app.MainWindow,
                "ModelRouting_AddInstalledModel_Button", ControlType.Button);

            foreach (string role in roles)
            {
                SelectRole(rolePicker, role);
                WaitForRoleRefresh(roleExplanation, role);
                UiWait.For(() => RoleSummaryMatchesCurrentRole(roleExplanation, candidates, role),
                    "candidate explanation for " + role);
                UiWait.For(() => ReadText(emptyPickerMessage).Contains("No unassigned managed GGUF models are available for this role.", StringComparison.Ordinal)
                    && ReadText(emptyPickerMessage).Contains("Import or install a model in Model Hub, or choose another role.", StringComparison.Ordinal),
                    "actionable empty model-picker message for " + role);
                Assert.False(installedPicker.Properties.IsEnabled.ValueOrDefault);
                Assert.False(addModel.Properties.IsEnabled.ValueOrDefault);
            }
        }
        catch (Exception error)
        {
            app.CaptureFailure(testName, error);
            throw;
        }
    }

    [Fact, Trait("Category", "Regression"), Trait("Category", "Models"), Trait("Category", "FlaUI")]
    public void Empty_routing_state_discloses_no_coding_model_assignment()
    {
        const string testName = nameof(Empty_routing_state_discloses_no_coding_model_assignment);
        using var app = new UiTestApplication(testName, modelRoutingFixture: "empty");
        try
        {
            OpenModelRouting(app);
            AutomationElement codingSelection = UiWait.ById(app.MainWindow,
                "ModelRouting_CodingSelection_Text", ControlType.Text);
            UiWait.For(() => ReadText(codingSelection).Contains("(no coding model assigned)", StringComparison.Ordinal),
                "explicit empty coding-model summary");

            AutomationElement installedPicker = UiWait.ById(app.MainWindow,
                "ModelRouting_InstalledModel_ComboBox", ControlType.ComboBox);
            AutomationElement addModel = UiWait.ById(app.MainWindow,
                "ModelRouting_AddInstalledModel_Button", ControlType.Button);
            AutomationElement emptyMessage = UiWait.ById(app.MainWindow,
                "ModelRouting_InstalledModelStatus_Text", ControlType.Text);
            UiWait.For(() => ReadText(emptyMessage).Contains("No unassigned managed GGUF models are available for this role.", StringComparison.Ordinal),
                "empty installed-model state");
            Assert.False(installedPicker.Properties.IsEnabled.ValueOrDefault);
            Assert.False(addModel.Properties.IsEnabled.ValueOrDefault);
        }
        catch (Exception error)
        {
            app.CaptureFailure(testName, error);
            throw;
        }
    }

    [Fact, Trait("Category", "Regression"), Trait("Category", "Models"), Trait("Category", "FlaUI")]
    public void Role_picker_keyboard_selection_updates_the_role_explanation()
    {
        const string testName = nameof(Role_picker_keyboard_selection_updates_the_role_explanation);
        using var app = new UiTestApplication(testName);
        try
        {
            OpenModelRouting(app);
            AutomationElement rolePicker = UiWait.ById(app.MainWindow,
                "ModelRouting_Role_ComboBox", ControlType.ComboBox);
            string[] roles = ReadRoleNames(rolePicker);
            Assert.Equal(8, roles.Length);
            string nextRole = roles[1];
            AutomationElement roleExplanation = UiWait.ById(app.MainWindow,
                "ModelRouting_Status_Text", ControlType.Text);
            AutomationElement candidates = UiWait.ById(app.MainWindow,
                "ModelRouting_Candidates_List", ControlType.List);
            WaitForRoleRefresh(roleExplanation, roles[0]);
            UiWait.For(() => RoleSummaryMatchesCurrentRole(roleExplanation, candidates, roles[0]),
                "initial role explanation");

            rolePicker.Focus();
            Keyboard.Press(VirtualKeyShort.F4);
            UiWait.For(() => rolePicker.Patterns.ExpandCollapse.Pattern.ExpandCollapseState == ExpandCollapseState.Expanded,
                "role picker opened by keyboard");
            Keyboard.Press(VirtualKeyShort.DOWN);
            Keyboard.Press(VirtualKeyShort.RETURN);

            UiWait.For(() => string.Equals(ReadText(rolePicker.AsComboBox().SelectedItem), nextRole, StringComparison.Ordinal),
                "keyboard-selected role");
            WaitForRoleRefresh(roleExplanation, nextRole);
            UiWait.For(() => RoleSummaryMatchesCurrentRole(roleExplanation, candidates, nextRole),
                "keyboard-selected role explanation");
        }
        catch (Exception error)
        {
            app.CaptureFailure(testName, error);
            throw;
        }
    }

    [Fact, Trait("Category", "Regression"), Trait("Category", "Models"), Trait("Category", "FlaUI")]
    public void Installed_model_picker_opens_and_cancels_with_the_keyboard()
    {
        const string testName = nameof(Installed_model_picker_opens_and_cancels_with_the_keyboard);
        using var app = new UiTestApplication(testName, modelRoutingFixture: "models");
        try
        {
            OpenModelRouting(app);
            AutomationElement installedPicker = UiWait.ById(app.MainWindow,
                "ModelRouting_InstalledModel_ComboBox", ControlType.ComboBox);
            AutomationElement selectionStatus = UiWait.ById(app.MainWindow,
                "ModelRouting_InstalledModelStatus_Text", ControlType.Text);
            UiWait.For(() => selectionStatus.Name.Contains("Selected: managed-fixture.gguf", StringComparison.Ordinal),
                "fixture model available in the picker");

            installedPicker.Focus();
            Keyboard.Press(VirtualKeyShort.F4);
            UiWait.For(() => installedPicker.Patterns.ExpandCollapse.Pattern.ExpandCollapseState == ExpandCollapseState.Expanded,
                "installed-model picker opened by keyboard");
            Keyboard.Press(VirtualKeyShort.ESCAPE);
            UiWait.For(() => installedPicker.Patterns.ExpandCollapse.Pattern.ExpandCollapseState == ExpandCollapseState.Collapsed,
                "installed-model picker cancelled by keyboard");

            Assert.Contains("Selected: managed-fixture.gguf", selectionStatus.Name, StringComparison.Ordinal);
        }
        catch (Exception error)
        {
            app.CaptureFailure(testName, error);
            throw;
        }
    }

    private static void OpenModelRouting(UiTestApplication app)
    {
        var main = new MainWindowPage(app.MainWindow);
        main.EnableAdvanced();
        main.Click("Navigation_ModelRouting_Item", ControlType.ListItem);
    }

    private static string[] ReadRoleNames(AutomationElement rolePicker)
    {
        rolePicker.Patterns.ExpandCollapse.Pattern.Expand();
        try
        {
            AutomationElement[] options = Array.Empty<AutomationElement>();
            UiWait.For(() =>
            {
                options = rolePicker.AsComboBox().Items
                    .Where(option => option.Properties.ControlType.ValueOrDefault == ControlType.ListItem)
                    .ToArray();
                return options.Length > 0;
            }, "role picker options");
            return options.Select(ReadText)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .ToArray();
        }
        finally
        {
            rolePicker.Patterns.ExpandCollapse.Pattern.Collapse();
        }
    }

    private static void SelectRole(AutomationElement rolePicker, string role)
    {
        rolePicker.Patterns.ExpandCollapse.Pattern.Expand();
        AutomationElement? option = null;
        UiWait.For(() =>
        {
            option = rolePicker.AsComboBox().Items.FirstOrDefault(item =>
                item.Properties.ControlType.ValueOrDefault == ControlType.ListItem
                && string.Equals(ReadText(item), role, StringComparison.Ordinal));
            return option != null;
        }, "role option " + role);
        option!.Patterns.SelectionItem.Pattern.Select();
    }

    private static void WaitForRoleRefresh(AutomationElement roleExplanation, string role) =>
        UiWait.For(() => !string.Equals(ReadText(roleExplanation), "Loading real candidate evidence…", StringComparison.Ordinal),
            "candidate evidence refresh for " + role);

    private static bool RoleSummaryMatchesCurrentRole(
        AutomationElement roleExplanation,
        AutomationElement candidates,
        string role)
    {
        string text = ReadText(roleExplanation);
        int visibleCandidates = candidates.FindAllChildren(cf => cf.ByControlType(ControlType.ListItem)).Length;
        if (string.Equals(text,
                "No registered candidates for " + role + ". Choose an installed GGUF above and add it to this role.",
                StringComparison.Ordinal))
            return visibleCandidates == 0;

        const string candidateSummarySuffix = " candidate(s). Capability and throughput are shown independently.";
        return text.EndsWith(candidateSummarySuffix, StringComparison.Ordinal)
            && int.TryParse(text.AsSpan(0, text.Length - candidateSummarySuffix.Length), out int count)
            && count > 0
            && visibleCandidates > 0;
    }

    private static string ReadText(AutomationElement? element)
    {
        if (element == null) return string.Empty;
        try { return element.Properties.Name.ValueOrDefault ?? string.Empty; }
        catch (FlaUI.Core.Exceptions.PropertyNotSupportedException) { return string.Empty; }
    }
}
