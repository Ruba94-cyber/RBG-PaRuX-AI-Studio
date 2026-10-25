using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using PaRuX.UITests.Infrastructure;
using PaRuX.UITests.PageObjects;
using Xunit;

namespace PaRuX.UITests.Tests;

[Collection(UiTestCollection.Name)]
public sealed class ToolsAutomationCoverageTests
{
    private static void Run(string step, Action<UiTestApplication> action)
    {
        using var app = new UiTestApplication(step);
        try { action(app); }
        catch (Exception ex) { app.CaptureFailure(step, ex); throw; }
    }

    [Fact, Trait("Category", "Regression"), Trait("Category", "Tools"), Trait("Category", "FlaUI")]
    public void Tools_automation_workspace_is_reachable_and_exposes_primary_sections() =>
        Run(nameof(Tools_automation_workspace_is_reachable_and_exposes_primary_sections), app =>
        {
            var page = new ToolsAutomationPage(app);
            page.Open();

            Assert.Equal("Tools & Automations", page.TitleText.Name);
            Assert.StartsWith("Inspect skill permissions, connect MCP tools", page.DescriptionText.Name, StringComparison.Ordinal);

            Assert.NotNull(page.FindSectionHeader("SKILLS & PERMISSIONS"));
            Assert.NotNull(page.FindSectionHeader("MCP SERVERS"));
            Assert.NotNull(page.FindSectionHeader("AUTOMATIONS"));
            Assert.NotNull(page.FindSectionHeader("WORKFLOW BUILDER"));

            Assert.True(page.RefreshAllButton.Properties.IsEnabled.ValueOrDefault);
        });

    [Fact, Trait("Category", "Regression"), Trait("Category", "Tools"), Trait("Category", "FlaUI")]
    public void Tools_automation_refresh_all_executes_and_returns_to_idle_without_stuck_busy_indicator() =>
        Run(nameof(Tools_automation_refresh_all_executes_and_returns_to_idle_without_stuck_busy_indicator), app =>
        {
            var page = new ToolsAutomationPage(app);
            page.Open();

            page.ClickRefreshAll();

            UiWait.For(() =>
            {
                var status = page.StatusText;
                if (status == null) return false;
                string text = status.Properties.Name.ValueOrDefault ?? string.Empty;
                return !text.StartsWith("Refreshing", StringComparison.Ordinal)
                    && (text.StartsWith("Tools and automation state", StringComparison.Ordinal)
                        || text.StartsWith("Refresh failed:", StringComparison.Ordinal));
            }, "Tools & Automations refresh completes to idle state", TimeSpan.FromSeconds(25));

            Assert.True(page.RefreshAllButton.Properties.IsEnabled.ValueOrDefault);
        });

    [Fact, Trait("Category", "Regression"), Trait("Category", "Tools"), Trait("Category", "FlaUI")]
    public void Tools_automation_lists_render_empty_state_sentences_or_truthful_items() =>
        Run(nameof(Tools_automation_lists_render_empty_state_sentences_or_truthful_items), app =>
        {
            var page = new ToolsAutomationPage(app);
            page.Open();

            var mcpEmpty = page.FindEmptyMcpText();
            Assert.NotNull(mcpEmpty);
            Assert.Equal("No outbound MCP servers configured.", mcpEmpty.Name);

            var automationsEmpty = page.FindEmptyAutomationsText();
            Assert.NotNull(automationsEmpty);
            Assert.Equal("No automations yet.", automationsEmpty.Name);

            var workflowsEmpty = page.FindEmptyWorkflowsText();
            Assert.NotNull(workflowsEmpty);
            Assert.Equal("No workflow templates yet.", workflowsEmpty.Name);

            var skillsEmpty = page.FindEmptySkillsText();
            var permissionButton = app.FindRawDescendant(app.MainWindow, el =>
                el.Properties.ControlType.ValueOrDefault == ControlType.Button
                && el.Properties.Name.ValueOrDefault == "Permissions…");
            Assert.True(skillsEmpty != null || permissionButton != null,
                "Skills section must display empty guidance or registered skills with permission actions.");
        });

    [Fact, Trait("Category", "Regression"), Trait("Category", "Tools"), Trait("Category", "FlaUI")]
    public void Tools_automation_mcp_section_validates_required_inputs_as_text_and_exposes_presets() =>
        Run(nameof(Tools_automation_mcp_section_validates_required_inputs_as_text_and_exposes_presets), app =>
        {
            var page = new ToolsAutomationPage(app);
            page.Open();

            page.ClickAddMcp();

            UiWait.For(() =>
            {
                var status = page.StatusText;
                return status != null && string.Equals(status.Name, "MCP name and command are required.", StringComparison.Ordinal);
            }, "MCP missing fields validation error", TimeSpan.FromSeconds(15));

            var mcpPresetCombo = page.FindMcpPresetComboBox();
            Assert.NotNull(mcpPresetCombo);
        });

    [Fact, Trait("Category", "Regression"), Trait("Category", "Tools"), Trait("Category", "FlaUI")]
    public void Tools_automation_create_automation_validates_required_fields_as_text() =>
        Run(nameof(Tools_automation_create_automation_validates_required_fields_as_text), app =>
        {
            var page = new ToolsAutomationPage(app);
            page.Open();

            page.ClickCreateAutomation();

            UiWait.For(() =>
            {
                var status = page.StatusText;
                return status != null && string.Equals(status.Name, "Automation name, trigger config and goal are required.", StringComparison.Ordinal);
            }, "Automation validation error text", TimeSpan.FromSeconds(15));

            var triggerCombo = page.FindAutomationTriggerComboBox();
            Assert.NotNull(triggerCombo);
        });

    [Fact, Trait("Category", "Regression"), Trait("Category", "Tools"), Trait("Category", "FlaUI")]
    public void Tools_automation_workflow_builder_initializes_step_dag_and_validates_name() =>
        Run(nameof(Tools_automation_workflow_builder_initializes_step_dag_and_validates_name), app =>
        {
            var page = new ToolsAutomationPage(app);
            page.Open();

            var stepsBox = page.FindWorkflowStepsTextBox();
            Assert.NotNull(stepsBox);

            page.ClickCreateWorkflow();

            UiWait.For(() =>
            {
                var status = page.StatusText;
                return status != null && string.Equals(status.Name, "Workflow name is required.", StringComparison.Ordinal);
            }, "Workflow name validation error text", TimeSpan.FromSeconds(15));
        });

    [Fact, Trait("Category", "Regression"), Trait("Category", "Settings"), Trait("Category", "Tools")]
    public void Settings_automation_and_agent_tasks_refresh_controls_cycle_and_report_state() =>
        Run(nameof(Settings_automation_and_agent_tasks_refresh_controls_cycle_and_report_state), app =>
        {
            var main = new MainWindowPage(app.MainWindow);
            main.OpenSettings();
            main.OpenSettingsCategory("Settings_Category_Agents_Button");

            AutomationElement? agentTasksCount = null;
            UiWait.For(() =>
            {
                agentTasksCount = app.FindRawDescendant(app.MainWindow, el =>
                    el.Properties.ControlType.ValueOrDefault == ControlType.Text
                    && el.Properties.Name.ValueOrDefault != null
                    && el.Properties.Name.ValueOrDefault.EndsWith("task(s)", StringComparison.Ordinal));
                return agentTasksCount != null;
            }, "Agent tasks count label", TimeSpan.FromSeconds(20));

            Assert.NotNull(agentTasksCount);
            Assert.False(string.IsNullOrWhiteSpace(agentTasksCount.Name));

            var walker = app.Automation.TreeWalkerFactory.GetRawViewWalker();
            var tasksGrid = walker.GetParent(agentTasksCount);
            Assert.NotNull(tasksGrid);
            var refreshTasksBtn = tasksGrid.FindFirstDescendant(cf =>
                cf.ByControlType(ControlType.Button).And(cf.ByName("Refresh")));
            Assert.NotNull(refreshTasksBtn);
            ToolsAutomationPage.ClickElement(refreshTasksBtn);

            AutomationElement? automationsCount = null;
            UiWait.For(() =>
            {
                automationsCount = app.FindRawDescendant(app.MainWindow, el =>
                    el.Properties.ControlType.ValueOrDefault == ControlType.Text
                    && el.Properties.Name.ValueOrDefault != null
                    && el.Properties.Name.ValueOrDefault.EndsWith("automation(s)", StringComparison.Ordinal));
                return automationsCount != null;
            }, "Automations count label", TimeSpan.FromSeconds(20));

            Assert.NotNull(automationsCount);
            Assert.False(string.IsNullOrWhiteSpace(automationsCount.Name));

            var automationsGrid = walker.GetParent(automationsCount);
            Assert.NotNull(automationsGrid);
            var refreshAutomationsBtn = automationsGrid.FindFirstDescendant(cf =>
                cf.ByControlType(ControlType.Button).And(cf.ByName("Refresh")));
            Assert.NotNull(refreshAutomationsBtn);
            ToolsAutomationPage.ClickElement(refreshAutomationsBtn);

            main.OpenSettingsCategory("Settings_Category_Models_Button");
            AutomationElement detectBtn = UiWait.ById(app.MainWindow, "Settings_RefreshComponents_Button", ControlType.Button);
            // The button is intentionally disabled while component detection runs; it must become enabled again.
            UiWait.For(() => detectBtn.Properties.IsEnabled.ValueOrDefault, "Refresh components re-enabled after detection", TimeSpan.FromSeconds(60));
            Assert.True(detectBtn.Name is "Refresh components" or "Detect",
                $"Expected 'Refresh components' or 'Detect', but got '{detectBtn.Name}'.");
        });
}
