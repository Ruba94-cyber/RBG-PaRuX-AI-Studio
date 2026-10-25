using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using PaRuX.UITests.Infrastructure;
using PaRuX.UITests.PageObjects;
using Xunit;

namespace PaRuX.UITests.Tests;

[Collection(UiTestCollection.Name)]
public sealed class ProjectsWorkspaceCoverageTests
{
    private static void Run(string step, Action<UiTestApplication> action)
    {
        using var app = new UiTestApplication(step);
        try { action(app); }
        catch (Exception ex) { app.CaptureFailure(step, ex); throw; }
    }

    [Fact, Trait("Category", "Regression"), Trait("Category", "Projects"), Trait("Category", "FlaUI")]
    public void Projects_workspace_empty_state_is_explained_and_repository_actions_are_disabled() =>
        Run(nameof(Projects_workspace_empty_state_is_explained_and_repository_actions_are_disabled), app =>
        {
            var page = new ProjectsWorkspacePage(app);
            page.Open();

            Assert.Equal("Projects & Artifacts", page.TitleText.Name);
            Assert.Contains("Select a durable Project as the active workspace", page.DescriptionText.Name, StringComparison.Ordinal);
            Assert.Equal("Active project: none — new outputs remain unscoped until you explicitly select/link them.", page.ActiveProjectText.Name);
            Assert.Equal("Select a Project before assigning a repository.", page.RepositoryText.Name);
            Assert.False(page.ChooseRepositoryButton.Properties.IsEnabled.ValueOrDefault);
            Assert.False(page.ClearRepositoryButton.Properties.IsEnabled.ValueOrDefault);
            Assert.Equal("No durable Projects yet.", page.EmptyProjectsText.Name);
            Assert.Equal("Select a Project to see scoped artifacts.", page.ScopedArtifactsText.Name);
            Assert.Equal("Artifact Workspace is empty.", page.AllArtifactsText.Name);
        });

    [Fact, Trait("Category", "Regression"), Trait("Category", "Projects"), Trait("Category", "Accessibility")]
    public void Projects_workspace_primary_actions_are_present_with_accessible_names() =>
        Run(nameof(Projects_workspace_primary_actions_are_present_with_accessible_names), app =>
        {
            var page = new ProjectsWorkspacePage(app);
            page.Open();

            Assert.Equal("Create Project", page.CreateProjectButton.Name);
            Assert.True(page.CreateProjectButton.Properties.IsEnabled.ValueOrDefault);

            Assert.StartsWith("Choose repository for new Project", page.ChooseNewProjectRepositoryButton.Name, StringComparison.Ordinal);
            Assert.True(page.ChooseNewProjectRepositoryButton.Properties.IsEnabled.ValueOrDefault);

            Assert.Equal("Clear", page.ClearNewProjectRepositoryButton.Name);
            Assert.False(page.ClearNewProjectRepositoryButton.Properties.IsEnabled.ValueOrDefault);

            Assert.StartsWith("Choose Project repository", page.ChooseRepositoryButton.Name, StringComparison.Ordinal);
            Assert.False(page.ChooseRepositoryButton.Properties.IsEnabled.ValueOrDefault);

            Assert.Equal("Clear session repository", page.ClearRepositoryButton.Name);
            Assert.False(page.ClearRepositoryButton.Properties.IsEnabled.ValueOrDefault);

            Assert.NotNull(page.GoalTextBox);

            Assert.NotNull(page.FindSectionHeader("PROJECT REPOSITORY · DESKTOP ONLY"));
            Assert.NotNull(page.FindSectionHeader("PROJECTS"));
            Assert.NotNull(page.FindSectionHeader("ARTIFACTS IN ACTIVE PROJECT"));
            Assert.NotNull(page.FindSectionHeader("ALL ARTIFACTS · LINK EXPLICITLY"));
            Assert.NotNull(page.FindSectionHeader("SELECTED ARTIFACT VERSION HISTORY"));
        });

    [Fact, Trait("Category", "Regression"), Trait("Category", "Projects"), Trait("Category", "FlaUI")]
    public void Empty_goal_input_does_not_create_a_project_and_leaves_empty_state_intact() =>
        Run(nameof(Empty_goal_input_does_not_create_a_project_and_leaves_empty_state_intact), app =>
        {
            var page = new ProjectsWorkspacePage(app);
            page.Open();

            Assert.Equal("No durable Projects yet.", page.EmptyProjectsText.Name);
            Assert.Equal("Active project: none — new outputs remain unscoped until you explicitly select/link them.", page.ActiveProjectText.Name);

            page.SetGoal("   ");
            page.ClickCreateProject();

            Assert.Equal("No durable Projects yet.", page.EmptyProjectsText.Name);
            Assert.Equal("Active project: none — new outputs remain unscoped until you explicitly select/link them.", page.ActiveProjectText.Name);
            Assert.False(page.ChooseRepositoryButton.Properties.IsEnabled.ValueOrDefault);
        });

    [Fact, Trait("Category", "Regression"), Trait("Category", "Projects"), Trait("Category", "FlaUI")]
    public void Creating_a_project_through_the_ui_updates_the_list_and_selection_state() =>
        Run(nameof(Creating_a_project_through_the_ui_updates_the_list_and_selection_state), app =>
        {
            var page = new ProjectsWorkspacePage(app);
            page.Open();

            const string testGoal = "Telemetry Dashboard";
            page.CreateProject(testGoal);

            page.WaitForActiveProject(testGoal);
            Assert.Contains(testGoal, page.ActiveProjectText.Name, StringComparison.Ordinal);

            UiWait.For(() => page.RepositoryText.Name.Contains("Session repository: none", StringComparison.Ordinal),
                "Repository guidance for active project");

            Assert.True(page.ChooseRepositoryButton.Properties.IsEnabled.ValueOrDefault);
            Assert.Null(page.FindEmptyProjectsText());

            AutomationElement projectButton = page.WaitForProjectButton(testGoal);
            Assert.Equal("Active", projectButton.Name);
            Assert.False(projectButton.Properties.IsEnabled.ValueOrDefault);

            Assert.Equal("No artifact is linked to this Project yet.", page.ScopedArtifactsText.Name);
        });

    [Fact, Trait("Category", "Regression"), Trait("Category", "Projects"), Trait("Category", "FlaUI")]
    public void Selecting_between_multiple_projects_updates_active_state_and_buttons() =>
        Run(nameof(Selecting_between_multiple_projects_updates_active_state_and_buttons), app =>
        {
            var page = new ProjectsWorkspacePage(app);
            page.Open();

            const string firstGoal = "Alpha Task";
            page.CreateProject(firstGoal);
            page.WaitForActiveProject(firstGoal);

            const string secondGoal = "Beta Task";
            page.CreateProject(secondGoal);
            page.WaitForActiveProject(secondGoal);

            AutomationElement betaButton = page.WaitForProjectButton(secondGoal);
            Assert.Equal("Active", betaButton.Name);
            Assert.False(betaButton.Properties.IsEnabled.ValueOrDefault);

            AutomationElement alphaButton = page.WaitForProjectButton(firstGoal);
            Assert.Equal("Select", alphaButton.Name);
            Assert.True(alphaButton.Properties.IsEnabled.ValueOrDefault);

            page.SelectProject(firstGoal);
            page.WaitForActiveProject(firstGoal);

            alphaButton = page.WaitForProjectButton(firstGoal);
            Assert.Equal("Active", alphaButton.Name);
            Assert.False(alphaButton.Properties.IsEnabled.ValueOrDefault);

            betaButton = page.WaitForProjectButton(secondGoal);
            Assert.Equal("Select", betaButton.Name);
            Assert.True(betaButton.Properties.IsEnabled.ValueOrDefault);
        });

    [Fact, Trait("Category", "Regression"), Trait("Category", "Projects"), Trait("Category", "Persistence")]
    public void Selected_project_persists_after_leaving_and_reentering_workspace() =>
        Run(nameof(Selected_project_persists_after_leaving_and_reentering_workspace), app =>
        {
            var page = new ProjectsWorkspacePage(app);
            page.Open();

            const string projectGoal = "Persistent Workspace";
            page.CreateProject(projectGoal);
            page.WaitForActiveProject(projectGoal);

            var main = new MainWindowPage(app.MainWindow);
            main.OpenSettings();
            UiWait.ById(app.MainWindow, "Settings_Theme_ComboBox", ControlType.ComboBox);

            page.Open();
            page.WaitForActiveProject(projectGoal);

            Assert.Contains(projectGoal, page.ActiveProjectText.Name, StringComparison.Ordinal);
            AutomationElement button = page.WaitForProjectButton(projectGoal);
            Assert.Equal("Active", button.Name);
            Assert.False(button.Properties.IsEnabled.ValueOrDefault);
            Assert.True(page.ChooseRepositoryButton.Properties.IsEnabled.ValueOrDefault);
        });
}
