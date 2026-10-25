using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using PaRuX.Services;

namespace PaRuX
{
    /// <summary>
    /// A short walkthrough of the sidebar, shown once after first-run setup.
    ///
    /// PaRuX has a lot of surface, and someone who has never used a local AI tool has no way to guess
    /// that "Model Hub" is where models come from or that the interface switch hides most of the
    /// product. Documentation does not fix that, because nobody reads it before their first question.
    ///
    /// Deliberately built in code rather than XAML: each step points at a control that already
    /// exists, and a tour that lived in markup would be a second place where the sidebar is
    /// described - free to drift from the sidebar itself. One TeachingTip is created, reused and
    /// removed, so a skipped tour leaves nothing behind in the visual tree.
    /// </summary>
    public sealed partial class MainWindow
    {
        private sealed record TourStep(FrameworkElement? Target, string Title, string Body);

        private List<TourStep>? _tourSteps;
        private int _tourIndex;
        private TeachingTip? _tourTip;

        /// <summary>
        /// Runs the tour if this user has never seen it. Called after first-run setup finishes;
        /// safe to call at any time, because it does nothing once the flag is set.
        /// </summary>
        private void MaybeStartGuidedTour()
        {
            if (SettingsStore.Current.Data.GuidedTourCompleted) return;
            StartGuidedTour();
        }

        /// <summary>Starts the tour unconditionally - what the "Show me around" button or Help calls.</summary>
        public void StartGuidedTour()
        {
            if (_tourTip != null && _tourTip.IsOpen) return;

            if (NavView != null)
            {
                NavView.IsPaneOpen = true;
            }

            OverlayEscapeManager.Instance.Push(new OverlayDescriptor(
                "GuidedTour",
                OverlayKind.TeachingTip,
                isDismissibleOnEscape: true,
                triggeringControlAutomationId: "MainWindow_NewChat_Button",
                accessibleLabel: "Guided Tour Walkthrough"));
            _tourSteps = BuildTourSteps();
            _tourIndex = 0;
            ShowCurrentTourStep();
        }

        private List<TourStep> BuildTourSteps() => new()
        {
            new(ChatNavItem,
                "Chat is your home workspace",
                "Chat is always listed first and is one click away from any workspace. "
                + "Returning from another workspace keeps your open conversation and unsent draft intact."),

            new(NewChatButton,
                "Start a new conversation",
                "Click New chat whenever you want a fresh conversation with clean context. "
                + "Every conversation is saved in the list below and can be reopened."),

            new(ModelHubNavItem,
                "Model Hub is where models come from",
                "PaRuX needs at least one model file. Model Hub finds them, tells you which ones your "
                + "graphics card can actually run, and downloads them. You can have several and switch."),

            new(CreateNavItem,
                "Create makes images, video and speech",
                "The same idea as chat, for pictures, short video, voice and 3D - all generated on this "
                + "PC. It needs its own models, which PaRuX will offer to install the first time."),

            new(ProjectsNavItem,
                "Projects give the AI your material",
                "Point a Project at folders, documents or notes and PaRuX will answer from them, "
                + "citing the file. Good for a codebase, a contract, or a year of research."),

            new(StudioModeToggle,
                "Advanced shows the rest of PaRuX",
                "Simple keeps the sidebar short. Switch to Advanced and you get model routing, the "
                + "runtime, the training lab, workflows, tools and observability. It changes what you "
                + "can see, never how PaRuX answers - flip it any time."),

            new(SettingsNavItem,
                "Settings holds the connections",
                "Connect your notes, mail, calendar and documents so PaRuX can answer from them, and "
                + "set the optional cloud fallback for when local inference cannot run."),

            new(HelpNavItem,
                "Help and guided tour replay",
                "Access in-app guides, documentation topics, keyboard shortcuts, or replay this tour anytime from Help. "
                + "You are done - ask your first question whenever you like.")
        };

        private void ShowCurrentTourStep()
        {
            if (_tourSteps == null || _tourIndex >= _tourSteps.Count)
            {
                EndGuidedTour();
                return;
            }

            TourStep step = _tourSteps[_tourIndex];

            // A step whose control is not on screen - the pane collapsed, an item hidden by Simple
            // mode - is skipped rather than shown pointing at nothing.
            if (step.Target == null || step.Target.Visibility != Visibility.Visible)
            {
                _tourIndex++;
                ShowCurrentTourStep();
                return;
            }

            EnsureTourTip();
            if (_tourTip == null) { EndGuidedTour(); return; }

            bool last = _tourIndex == _tourSteps.Count - 1;
            _tourTip.Target = step.Target;
            _tourTip.Title = step.Title;
            _tourTip.Subtitle = step.Body;
            _tourTip.ActionButtonContent = last ? "Finish" : "Next";
            _tourTip.CloseButtonContent = last ? "Close" : "Skip";
            _tourTip.IsOpen = true;
        }

        private void EnsureTourTip()
        {
            if (_tourTip != null) return;
            if (RootGrid == null) return;

            _tourTip = new TeachingTip
            {
                IsLightDismissEnabled = false,
                PreferredPlacement = TeachingTipPlacementMode.RightTop
            };

            _tourTip.ActionButtonClick += (_, _) =>
            {
                _tourTip!.IsOpen = false;
                _tourIndex++;
                ShowCurrentTourStep();
            };
            // Skipping is a decision, not a postponement: someone who dismissed the tour should not
            // be shown it again on the next launch.
            _tourTip.CloseButtonClick += (_, _) => EndGuidedTour();

            RootGrid.Children.Add(_tourTip);
        }

        private void EndGuidedTour()
        {
            OverlayEscapeManager.Instance.Pop("GuidedTour");
            if (_tourTip != null)
            {
                _tourTip.IsOpen = false;
                RootGrid?.Children.Remove(_tourTip);
                _tourTip = null;
            }
            _tourSteps = null;
            _tourIndex = 0;

            if (GuidedTourTriggerPolicy.ShouldRecordCompletionOnDismiss(SettingsStore.Current.Data.GuidedTourCompleted)
                && !SettingsStore.Current.Data.GuidedTourCompleted)
            {
                SettingsStore.Current.Data.GuidedTourCompleted = true;
                SettingsStore.Current.Save();
            }
        }

        /// <summary>
        /// Displays the in-app help and documentation dialog with topics from InAppHelpCatalog.
        /// Offers immediate replay of the guided tour and keyboard shortcuts sheet.
        /// </summary>
        public async Task ShowInAppHelpDialogAsync()
        {
            if (Content == null || Content.XamlRoot == null) return;
            string overlayId = "InAppHelpDialog_" + Guid.NewGuid().ToString("N");
            OverlayEscapeManager.Instance.Push(new OverlayDescriptor(
                overlayId,
                OverlayKind.Dialog,
                isDismissibleOnEscape: true,
                triggeringControlAutomationId: "Navigation_Help_Item",
                accessibleLabel: "PaRuX In-App Help & Guided Tour"));

            try
            {
                string markdown = InAppHelpCatalog.Default.GenerateHelpSummaryMarkdown();
                var dialog = new ContentDialog
                {
                    Title = "PaRuX Help & Guided Tour",
                    Content = new ScrollViewer
                    {
                        MaxHeight = 480,
                        Content = new TextBlock
                        {
                            Text = markdown,
                            TextWrapping = TextWrapping.Wrap,
                            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Segoe UI Variable, Segoe UI")
                        }
                    },
                    PrimaryButtonText = "Replay Guided Tour",
                    SecondaryButtonText = "Keyboard Shortcuts",
                    CloseButtonText = "Close",
                    DefaultButton = ContentDialogButton.Primary,
                    XamlRoot = Content.XamlRoot
                };
                AutomationProperties.SetAutomationId(dialog, "MainWindow_InAppHelp_Dialog");

                ContentDialogResult result = await dialog.ShowAsync();
                if (result == ContentDialogResult.Primary)
                {
                    // Replay tour unconditionally
                    StartGuidedTour();
                }
                else if (result == ContentDialogResult.Secondary)
                {
                    _ = ShowKeyboardShortcutsDialogAsync();
                }
            }
            finally
            {
                OverlayEscapeManager.Instance.Pop(overlayId);
            }
        }
    }
}
