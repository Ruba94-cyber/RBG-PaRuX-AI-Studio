using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PaRuX.Contracts;
using PaRuX.Services;

namespace PaRuX
{
    /// <summary>
    /// First-class workspace navigation and recent conversations share the NavigationView's native
    /// MenuItems scroll surface, so neither section can paint over or make the other unreachable.
    /// </summary>
    public sealed partial class MainWindow
    {
        private bool _synchronizingStudioModeToggles;

        private bool _applyingNavigationMode;
        private bool _mediaEditSourceBridgeSubscribed;

        private enum StudioWorkspace
        {
            Chat,
            Create,
            Projects,
            ModelHub,
            ModelRouting,
            Lifecycle,
            Workflows,
            Tools,
            Observability,
            Updates,
            Runtime
        }

        private void NavView_Loaded(object sender, RoutedEventArgs e)
        {
            _applyingNavigationMode = true;
            try
            {
                bool studioModeEnabled = SettingsStore.Current.Data.StudioModeEnabled;
                StudioModeToggle.IsOn = studioModeEnabled;
                ContentStudioModeToggle.IsOn = studioModeEnabled;
            }
            finally
            {
                _applyingNavigationMode = false;
            }
            UpdateContentStudioModeControlsVisibility(NavView.IsPaneOpen);
            ApplyNavigationMode(navigateHomeIfHidden: false);

            if (!_mediaEditSourceBridgeSubscribed)
            {
                _mediaEditSourceBridgeSubscribed = true;
                MediaEditSourceBridge.Current.EditSourceRequested += (_, _) =>
                {
                    _dispatcher.TryEnqueue(() =>
                    {
                        MediaEditSourceBridge.TryApplyToCreateView(CreatePage);
                        ShowStudioWorkspace(StudioWorkspace.Create);
                    });
                };
            }
        }

        private void StudioModeToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_applyingNavigationMode || _synchronizingStudioModeToggles) return;

            bool studioModeEnabled = sender is ToggleSwitch source
                ? source.IsOn
                : StudioModeToggle.IsOn;
            _synchronizingStudioModeToggles = true;
            try
            {
                StudioModeToggle.IsOn = studioModeEnabled;
                ContentStudioModeToggle.IsOn = studioModeEnabled;
            }
            finally
            {
                _synchronizingStudioModeToggles = false;
            }

            SettingsStore.Current.Data.StudioModeEnabled = studioModeEnabled;
            SettingsStore.Current.Save();
            ApplyNavigationMode(navigateHomeIfHidden: true);
        }

        private const string AdvancedWorkspaceList =
            "Model routing, Runtime, Lifecycle Lab, Workflows, Tools, Observability and Updates";

        private void ApplyNavigationMode(bool navigateHomeIfHidden)
        {
            bool studio = StudioModeToggle.IsOn;
            Visibility advanced = studio ? Visibility.Visible : Visibility.Collapsed;

            ModelRoutingNavItem.Visibility = advanced;
            LifecycleNavItem.Visibility = advanced;
            WorkflowNavItem.Visibility = advanced;
            ToolsNavItem.Visibility = advanced;
            ObservabilityNavItem.Visibility = advanced;
            UpdatesNavItem.Visibility = advanced;
            RuntimeNavItem.Visibility = advanced;
            ChatPage.ApplyInterfaceMode(studio);
            ModelHubPage.ApplyInterfaceMode(studio);

            if (!studio && navigateHomeIfHidden)
            {
                ShowChat();
                RefreshHistory();
            }
        }

        private bool IsAdvancedWorkspaceVisible() =>
            ModelRoutingPage.Visibility == Visibility.Visible
            || LifecycleLabPage.Visibility == Visibility.Visible
            || WorkflowPage.Visibility == Visibility.Visible
            || ToolsAutomationPage.Visibility == Visibility.Visible
            || ObservabilityPage.Visibility == Visibility.Visible
            || UpdateManagerPage.Visibility == Visibility.Visible
            || RuntimeManagerPage.Visibility == Visibility.Visible;

        private void HideAllWorkspacePages()
        {
            CreatePage.Visibility = Visibility.Collapsed;
            ProjectsPage.Visibility = Visibility.Collapsed;
            ModelHubPage.Visibility = Visibility.Collapsed;
            ModelRoutingPage.Visibility = Visibility.Collapsed;
            LifecycleLabPage.Visibility = Visibility.Collapsed;
            WorkflowPage.Visibility = Visibility.Collapsed;
            ToolsAutomationPage.Visibility = Visibility.Collapsed;
            ObservabilityPage.Visibility = Visibility.Collapsed;
            UpdateManagerPage.Visibility = Visibility.Collapsed;
            RuntimeManagerPage.Visibility = Visibility.Collapsed;
        }

        private bool _onboardingHandoffActive;

        private void OnFirstModelRequested(string repoId)
        {
            // The recommended first-model path hands off to Model Hub and MUST return to onboarding;
            // OnboardingCompleted is NOT marked true until the user finishes setup with working chat evidence.
            _onboardingHandoffActive = true;
            if (OnboardingReturnBanner != null) OnboardingReturnBanner.IsOpen = true;
            NavView.IsBackButtonVisible = NavigationViewBackButtonVisible.Visible;

            ShowStudioWorkspace(StudioWorkspace.ModelHub);
            ModelHubPage.OnShownSearching(repoId, OnboardingPage.SuggestedQuantHintForHandoff);
            GuidedTourDecision tourDecision = GuidedTourTriggerPolicy.EvaluateHandoff(
                SettingsStore.Current.Data.GuidedTourCompleted,
                "ModelHub");
            if (tourDecision.ShouldStart)
            {
                _dispatcher.TryEnqueue(MaybeStartGuidedTour);
            }
        }

        private void ReturnToOnboardingButton_Click(object sender, RoutedEventArgs e)
        {
            ShowOnboarding();
        }

        private void NavView_BackRequested(NavigationView sender, NavigationViewBackRequestedEventArgs args)
        {
            if (_onboardingHandoffActive)
            {
                ShowOnboarding();
            }
        }

        private bool TryNavigateWorkspace(string tag)
        {
            switch (tag)
            {
                case "__chat__":
                    ShowChat();
                    return true;
                case "__create__":
                    ShowStudioWorkspace(StudioWorkspace.Create);
                    CreatePage.OnShown();
                    return true;
                case "__projects__":
                    ShowStudioWorkspace(StudioWorkspace.Projects);
                    ProjectsPage.OnShown();
                    return true;
                case "__modelhub__":
                    ShowStudioWorkspace(StudioWorkspace.ModelHub);
                    ModelHubPage.OnShown();
                    return true;
                case "__modelrouting__":
                    ShowStudioWorkspace(StudioWorkspace.ModelRouting);
                    ModelRoutingPage.OnShown();
                    return true;
                case "__lifecycle__":
                    ShowStudioWorkspace(StudioWorkspace.Lifecycle);
                    LifecycleLabPage.OnShown();
                    return true;
                case "__workflows__":
                    ShowStudioWorkspace(StudioWorkspace.Workflows);
                    WorkflowPage.OnShown();
                    return true;
                case "__tools__":
                    ShowStudioWorkspace(StudioWorkspace.Tools);
                    ToolsAutomationPage.OnShown();
                    return true;
                case "__observability__":
                    ShowStudioWorkspace(StudioWorkspace.Observability);
                    ObservabilityPage.OnShown();
                    return true;
                case "__updates__":
                    ShowStudioWorkspace(StudioWorkspace.Updates);
                    UpdateManagerPage.OnShown();
                    return true;
                case "__runtime__":
                    ShowStudioWorkspace(StudioWorkspace.Runtime);
                    RuntimeManagerPage.OnShown();
                    return true;
                case "__help__":
                    _ = ShowInAppHelpDialogAsync();
                    return true;
                case "__settings__":
                    ShowSettings();
                    return true;
                default:
                    return false;
            }
        }

        private void ShowStudioWorkspace(StudioWorkspace workspace)
        {
            if (workspace == StudioWorkspace.Chat)
            {
                ShowChat();
                return;
            }

            NavView.IsPaneVisible = true;
            ChatPage.Visibility = Visibility.Collapsed;
            SettingsPage.Visibility = Visibility.Collapsed;
            OnboardingPage.Visibility = Visibility.Collapsed;
            HideAllWorkspacePages();
            SelectWorkspaceNavigationItem(NavigationItemFor(workspace));

            switch (workspace)
            {
                case StudioWorkspace.Create: CreatePage.Visibility = Visibility.Visible; break;
                case StudioWorkspace.Projects: ProjectsPage.Visibility = Visibility.Visible; break;
                case StudioWorkspace.ModelHub: ModelHubPage.Visibility = Visibility.Visible; break;
                case StudioWorkspace.ModelRouting: ModelRoutingPage.Visibility = Visibility.Visible; break;
                case StudioWorkspace.Lifecycle: LifecycleLabPage.Visibility = Visibility.Visible; break;
                case StudioWorkspace.Workflows: WorkflowPage.Visibility = Visibility.Visible; break;
                case StudioWorkspace.Tools: ToolsAutomationPage.Visibility = Visibility.Visible; break;
                case StudioWorkspace.Observability: ObservabilityPage.Visibility = Visibility.Visible; break;
                case StudioWorkspace.Updates: UpdateManagerPage.Visibility = Visibility.Visible; break;
                case StudioWorkspace.Runtime: RuntimeManagerPage.Visibility = Visibility.Visible; break;
            }
        }

        private void SelectWorkspaceNavigationItem(NavigationViewItem? selected)
        {
            NavView.SelectedItem = selected;
            selected?.Focus(FocusState.Programmatic);
        }

        private NavigationViewItem[] WorkspaceNavigationItems() => new[]
        {
            ChatNavItem, CreateNavItem, ProjectsNavItem, ModelHubNavItem, ModelRoutingNavItem,
            LifecycleNavItem, WorkflowNavItem, ToolsNavItem, ObservabilityNavItem,
            UpdatesNavItem, RuntimeNavItem, SettingsNavItem, HelpNavItem
        };

        private NavigationViewItem NavigationItemFor(StudioWorkspace workspace) => workspace switch
        {
            StudioWorkspace.Chat => ChatNavItem,
            StudioWorkspace.Create => CreateNavItem,
            StudioWorkspace.Projects => ProjectsNavItem,
            StudioWorkspace.ModelHub => ModelHubNavItem,
            StudioWorkspace.ModelRouting => ModelRoutingNavItem,
            StudioWorkspace.Lifecycle => LifecycleNavItem,
            StudioWorkspace.Workflows => WorkflowNavItem,
            StudioWorkspace.Tools => ToolsNavItem,
            StudioWorkspace.Observability => ObservabilityNavItem,
            StudioWorkspace.Updates => UpdatesNavItem,
            StudioWorkspace.Runtime => RuntimeNavItem,
            _ => CreateNavItem
        };
    }
}
