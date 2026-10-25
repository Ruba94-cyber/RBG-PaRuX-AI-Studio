using System;
using FlaUI.Core.Definitions;
using PaRuX.UITests.Infrastructure;
using PaRuX.UITests.PageObjects;
using Xunit;

namespace PaRuX.UITests.Tests;

[Collection(UiTestCollection.Name)]
public sealed class WorkspaceNavigationCoverageTests
{
    private static void Run(string step, Action<UiTestApplication> action)
    {
        using var app = new UiTestApplication(step);
        try { action(app); }
        catch (Exception ex) { app.CaptureFailure(step, ex); throw; }
    }

    [Fact, Trait("Category", "Smoke"), Trait("Category", "Navigation")]
    public void Simple_mode_navigation_opens_every_workspace_with_landmark_and_selection() => Run(
        nameof(Simple_mode_navigation_opens_every_workspace_with_landmark_and_selection), app =>
        {
            var nav = new WorkspaceNavigationPage(app.MainWindow, app);

            // Initially the application starts in Chat home.
            Assert.True(
                nav.IsWorkspaceLandmarkVisible(WorkspaceNavigationPage.ChatDescriptor),
                "Chat landmark must be visible at startup.");

            WorkspaceDescriptor previous = WorkspaceNavigationPage.ChatDescriptor;
            foreach (WorkspaceDescriptor workspace in WorkspaceNavigationPage.SimpleWorkspaces)
            {
                nav.NavigateTo(workspace.Id);
                nav.WaitForWorkspace(workspace);

                Assert.True(
                    nav.IsWorkspaceLandmarkVisible(workspace),
                    workspace.Name + " landmark must be visible after navigation.");
                Assert.False(
                    nav.IsWorkspaceLandmarkVisible(previous),
                    previous.Name + " landmark must no longer be visible after opening " + workspace.Name + ".");

                Assert.True(
                    nav.IsItemSelected(workspace.Id),
                    workspace.Name + " navigation item must report selected state.");
                if (previous.Id != WorkspaceNavigationPage.ChatDescriptor.Id)
                {
                    Assert.False(
                        nav.IsItemSelected(previous.Id),
                        previous.Name + " navigation item must clear selected state after opening " + workspace.Name + ".");
                }

                Assert.Equal(workspace.Name, nav.GetItemName(workspace.Id));
                previous = workspace;
            }
        });

    [Fact, Trait("Category", "Smoke"), Trait("Category", "Navigation")]
    public void Advanced_mode_navigation_opens_every_advanced_workspace_with_landmark_and_selection() => Run(
        nameof(Advanced_mode_navigation_opens_every_advanced_workspace_with_landmark_and_selection), app =>
        {
            var nav = new WorkspaceNavigationPage(app.MainWindow, app);
            nav.EnableAdvanced();

            foreach (WorkspaceDescriptor workspace in WorkspaceNavigationPage.AdvancedWorkspaces)
            {
                UiWait.ById(app.MainWindow, workspace.Id, ControlType.ListItem);
            }

            WorkspaceDescriptor previous = WorkspaceNavigationPage.ChatDescriptor;
            foreach (WorkspaceDescriptor workspace in WorkspaceNavigationPage.AdvancedWorkspaces)
            {
                nav.NavigateTo(workspace.Id);
                nav.WaitForWorkspace(workspace);

                Assert.True(
                    nav.IsWorkspaceLandmarkVisible(workspace),
                    workspace.Name + " landmark must be visible after navigation.");
                Assert.False(
                    nav.IsWorkspaceLandmarkVisible(previous),
                    previous.Name + " landmark must no longer be visible after opening " + workspace.Name + ".");

                Assert.True(
                    nav.IsItemSelected(workspace.Id),
                    workspace.Name + " navigation item must report selected state.");
                if (previous.Id != WorkspaceNavigationPage.ChatDescriptor.Id)
                {
                    Assert.False(
                        nav.IsItemSelected(previous.Id),
                        previous.Name + " navigation item must clear selected state after opening " + workspace.Name + ".");
                }

                Assert.Equal(workspace.Name, nav.GetItemName(workspace.Id));
                previous = workspace;
            }
        });

    [Fact, Trait("Category", "Regression"), Trait("Category", "Navigation"), Trait("Category", "Chat")]
    public void Round_trip_navigation_between_chat_home_and_workspaces_preserves_state() => Run(
        nameof(Round_trip_navigation_between_chat_home_and_workspaces_preserves_state), app =>
        {
            var nav = new WorkspaceNavigationPage(app.MainWindow, app);
            var chat = new ChatPage(app.MainWindow);

            const string promptText = "Preserved roundtrip draft message";
            chat.EnterPrompt(promptText);
            Assert.Equal(promptText, chat.Prompt.Text);

            WorkspaceDescriptor[] roundTripTargets =
            {
                WorkspaceNavigationPage.CreateDescriptor,
                WorkspaceNavigationPage.SettingsDescriptor,
                WorkspaceNavigationPage.ModelHubDescriptor
            };

            foreach (WorkspaceDescriptor target in roundTripTargets)
            {
                nav.NavigateTo(target.Id);
                nav.WaitForWorkspace(target);

                Assert.True(
                    nav.IsWorkspaceLandmarkVisible(target),
                    target.Name + " landmark must be visible during round trip.");
                Assert.False(
                    nav.IsWorkspaceLandmarkVisible(WorkspaceNavigationPage.ChatDescriptor),
                    "Chat landmark must be collapsed while on " + target.Name + ".");

                nav.NavigateToChat();
                nav.WaitForWorkspace(WorkspaceNavigationPage.ChatDescriptor);

                Assert.True(
                    nav.IsWorkspaceLandmarkVisible(WorkspaceNavigationPage.ChatDescriptor),
                    "Chat landmark must be restored after returning from " + target.Name + ".");
                Assert.False(
                    nav.IsWorkspaceLandmarkVisible(target),
                    target.Name + " landmark must be collapsed after returning to Chat.");

                Assert.Equal(promptText, chat.Prompt.Text);
            }
        });

    [Fact, Trait("Category", "Regression"), Trait("Category", "Navigation")]
    public void Rapid_switching_between_three_workspaces_leaves_exactly_one_page_visible() => Run(
        nameof(Rapid_switching_between_three_workspaces_leaves_exactly_one_page_visible), app =>
        {
            var nav = new WorkspaceNavigationPage(app.MainWindow, app);

            // Rapidly activate Create -> Model Hub -> Settings in quick succession
            nav.NavigateTo(WorkspaceNavigationPage.CreateDescriptor.Id);
            nav.NavigateTo(WorkspaceNavigationPage.ModelHubDescriptor.Id);
            nav.NavigateTo(WorkspaceNavigationPage.SettingsDescriptor.Id);

            nav.WaitForWorkspace(WorkspaceNavigationPage.SettingsDescriptor);

            Assert.True(
                nav.IsWorkspaceLandmarkVisible(WorkspaceNavigationPage.SettingsDescriptor),
                "Settings landmark must be visible after rapid navigation.");
            Assert.False(
                nav.IsWorkspaceLandmarkVisible(WorkspaceNavigationPage.CreateDescriptor),
                "Create landmark must not be visible after rapid switching to Settings.");
            Assert.False(
                nav.IsWorkspaceLandmarkVisible(WorkspaceNavigationPage.ModelHubDescriptor),
                "Model Hub landmark must not be visible after rapid switching to Settings.");
            Assert.False(
                nav.IsWorkspaceLandmarkVisible(WorkspaceNavigationPage.ChatDescriptor),
                "Chat landmark must not be visible after rapid switching to Settings.");

            Assert.True(
                nav.IsItemSelected(WorkspaceNavigationPage.SettingsDescriptor.Id),
                "Settings nav item must be selected.");
            Assert.False(
                nav.IsItemSelected(WorkspaceNavigationPage.CreateDescriptor.Id),
                "Create nav item must not be selected.");
            Assert.False(
                nav.IsItemSelected(WorkspaceNavigationPage.ModelHubDescriptor.Id),
                "Model Hub nav item must not be selected.");
        });

    [Fact, Trait("Category", "Regression"), Trait("Category", "Navigation"), Trait("Category", "Accessibility")]
    public void All_twelve_workspace_navigation_items_expose_accessible_names_and_single_selection() => Run(
        nameof(All_twelve_workspace_navigation_items_expose_accessible_names_and_single_selection), app =>
        {
            var nav = new WorkspaceNavigationPage(app.MainWindow, app);
            nav.EnableAdvanced();

            foreach (WorkspaceDescriptor ws in WorkspaceNavigationPage.AllNavigationWorkspaces)
            {
                UiWait.ById(app.MainWindow, ws.Id, ControlType.ListItem);
            }

            foreach (WorkspaceDescriptor ws in WorkspaceNavigationPage.AllNavigationWorkspaces)
            {
                Assert.Equal(ws.Name, nav.GetItemName(ws.Id));
            }

            var chatItem = nav.FindChatNavItem();
            if (chatItem != null)
            {
                Assert.StartsWith("Chat", chatItem.Name, StringComparison.Ordinal);
            }

            WorkspaceDescriptor[] sampleWorkspaces =
            {
                WorkspaceNavigationPage.ModelRoutingDescriptor,
                WorkspaceNavigationPage.LifecycleDescriptor,
                WorkspaceNavigationPage.RuntimeDescriptor
            };

            foreach (WorkspaceDescriptor sample in sampleWorkspaces)
            {
                nav.NavigateTo(sample.Id);
                nav.WaitForWorkspace(sample);

                Assert.True(
                    nav.IsItemSelected(sample.Id),
                    sample.Name + " navigation item must report selected state.");

                int selectedCount = 0;
                foreach (WorkspaceDescriptor item in WorkspaceNavigationPage.AllNavigationWorkspaces)
                {
                    if (nav.IsItemSelected(item.Id))
                    {
                        selectedCount++;
                    }
                }
                Assert.Equal(1, selectedCount);
            }
        });

    [Fact, Trait("Category", "Regression"), Trait("Category", "Navigation")]
    public void Switching_to_simple_mode_while_advanced_workspace_open_navigates_home() => Run(
        nameof(Switching_to_simple_mode_while_advanced_workspace_open_navigates_home), app =>
        {
            var nav = new WorkspaceNavigationPage(app.MainWindow, app);
            nav.EnableAdvanced();

            nav.NavigateTo(WorkspaceNavigationPage.RuntimeDescriptor.Id);
            nav.WaitForWorkspace(WorkspaceNavigationPage.RuntimeDescriptor);

            Assert.True(
                nav.IsWorkspaceLandmarkVisible(WorkspaceNavigationPage.RuntimeDescriptor),
                "Runtime landmark must be visible in Advanced mode.");
            Assert.True(
                nav.IsItemSelected(WorkspaceNavigationPage.RuntimeDescriptor.Id),
                "Runtime navigation item must report selected state.");

            nav.EnableSimple();
            nav.WaitForWorkspace(WorkspaceNavigationPage.ChatDescriptor);

            Assert.True(
                nav.IsWorkspaceLandmarkVisible(WorkspaceNavigationPage.ChatDescriptor),
                "Chat landmark must be restored after switching to Simple mode.");
            Assert.False(
                nav.IsWorkspaceLandmarkVisible(WorkspaceNavigationPage.RuntimeDescriptor),
                "Runtime landmark must be collapsed after switching to Simple mode.");

            nav.EnableAdvanced();
            nav.NavigateTo(WorkspaceNavigationPage.ProjectsDescriptor.Id);
            nav.WaitForWorkspace(WorkspaceNavigationPage.ProjectsDescriptor);

            Assert.True(
                nav.IsWorkspaceLandmarkVisible(WorkspaceNavigationPage.ProjectsDescriptor),
                "Projects landmark must be visible after re-enabling Advanced mode.");
            Assert.False(
                nav.IsWorkspaceLandmarkVisible(WorkspaceNavigationPage.ChatDescriptor),
                "Chat landmark must be collapsed when Projects is open.");
        });
}
