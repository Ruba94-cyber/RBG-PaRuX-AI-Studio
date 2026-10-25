using System;
using System.Threading.Tasks;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using PaRuX.Services;
using Windows.System;
using Windows.UI.Core;

namespace PaRuX
{
    public sealed partial class MainWindow
    {
        private string? _workspaceNavigationFocusedAutomationId;
        private string? _workspaceNavigationSelectedAutomationIdAtFocus;

        private void InitializeAccessibilityKeyboardHandling()
        {
            if (RootGrid == null) return;
            RootGrid.KeyDown += RootGrid_AccessibilityKeyDown;
            // NavigationView items consume Up/Down for their own XY focus move (which can skip or overshoot
            // neighbouring workspaces), so the deterministic workspace order is applied on the tunnelling event.
            RootGrid.PreviewKeyDown += RootGrid_WorkspaceArrowPreviewKeyDown;

            // Observe every focus change (including programmatic and UI Automation focus) rather than per-item
            // GotFocus, so the remembered origin cannot lag behind the element that really holds focus.
            FocusManager.GotFocus += (_, focusArgs) =>
            {
                string? focusedId = FindFocusedWorkspaceAutomationId(focusArgs.NewFocusedElement as UIElement);
                // GotFocus is delivered after the focus move. A burst of arrow presses can therefore deliver the
                // event of an earlier item after focus already advanced; such a stale event must not overwrite the
                // origin, so only the element that still holds focus is remembered.
                if (focusedId != null
                    && string.Equals(
                        focusedId,
                        FindFocusedWorkspaceAutomationId(FocusManager.GetFocusedElement(RootGrid.XamlRoot) as UIElement),
                        StringComparison.Ordinal))
                {
                    RememberWorkspaceNavigationFocus(focusedId);
                }
            };
        }

        private void RootGrid_WorkspaceArrowPreviewKeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Handled || (e.Key != VirtualKey.Up && e.Key != VirtualKey.Down))
            {
                return;
            }

            // Only act when keyboard focus is inside a workspace navigation item; otherwise controls such as
            // ComboBox, ListView or menus must receive Up/Down themselves.
            if (RootGrid?.XamlRoot == null
                || FindFocusedWorkspaceAutomationId(FocusManager.GetFocusedElement(RootGrid.XamlRoot) as UIElement) == null)
            {
                return;
            }

            CoreVirtualKeyStates ctrlState = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control);
            if ((ctrlState & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down)
            {
                return;
            }

            if (HandleWorkspaceArrowNavigation(e.Key))
            {
                e.Handled = true;
            }
        }

        private async void RootGrid_AccessibilityKeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == VirtualKey.Escape)
            {
                var result = OverlayEscapeManager.Instance.HandleEscape();
                if (result.Handled)
                {
                    e.Handled = true;
                    if (result.Dismissed
                        && string.Equals(result.DismissedOverlayId, "GuidedTour", StringComparison.OrdinalIgnoreCase))
                    {
                        EndGuidedTour();
                    }
                }
                else if (NavView?.IsPaneOpen == true)
                {
                    NavView.IsPaneOpen = false;
                    _userExplicitlyToggledPane = true;
                    e.Handled = true;
                }
                return;
            }

            if (e.Key == VirtualKey.F1)
            {
                e.Handled = true;
                await ShowKeyboardShortcutsDialogAsync();
                return;
            }

            // Check if Ctrl is held down
            CoreVirtualKeyStates ctrlState = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control);
            bool ctrlDown = (ctrlState & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down;

            if (ctrlDown && e.Key == VirtualKey.M)
            {
                e.Handled = true;
                if (StudioModeToggle != null)
                {
                    StudioModeToggle.IsOn = !StudioModeToggle.IsOn;
                }
                return;
            }

            if (ctrlDown && e.Key == VirtualKey.N)
            {
                e.Handled = true;
                NewChat_Click(NewChatButton, new RoutedEventArgs());
                return;
            }

            if (!ctrlDown && (e.Key == VirtualKey.Up || e.Key == VirtualKey.Down))
            {
                if (HandleWorkspaceArrowNavigation(e.Key))
                {
                    e.Handled = true;
                    return;
                }
            }
        }

        private bool HandleWorkspaceArrowNavigation(VirtualKey key)
        {
            CoreVirtualKeyStates altState = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu);
            if ((altState & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down)
            {
                return false;
            }

            CoreVirtualKeyStates shiftState = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift);
            if ((shiftState & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down)
            {
                return false;
            }

            if (RootGrid?.XamlRoot == null)
            {
                return false;
            }

            object? focusedObj = FocusManager.GetFocusedElement(RootGrid.XamlRoot);
            UIElement? focused = focusedObj as UIElement;

            // Never intercept arrow keys while the user is typing in a text field
            if (focused is TextBox || focused is RichEditBox)
            {
                return false;
            }

            bool isAdvanced = StudioModeToggle != null && StudioModeToggle.IsOn;
            string? focusedAutomationId = FindFocusedWorkspaceAutomationId(focused);
            string? selectedAutomationId = GetSelectedWorkspaceAutomationId();
            // Focus is current while the selected workspace is still the one that was selected when focus landed.
            // A late SelectionChanged that re-selects the same item (e.g. the Chat home restore) is not a newer choice.
            bool keyboardFocusIsCurrent =
                string.Equals(focusedAutomationId, _workspaceNavigationFocusedAutomationId, StringComparison.Ordinal)
                && string.Equals(selectedAutomationId, _workspaceNavigationSelectedAutomationIdAtFocus, StringComparison.Ordinal);
            string? currentAutomationId = WorkspaceNavigationOrder.ResolveNavigationOrigin(
                focusedAutomationId,
                selectedAutomationId,
                keyboardFocusIsCurrent,
                isAdvanced);
            if (currentAutomationId == null)
            {
                return false;
            }

            string? targetAutomationId = key == VirtualKey.Up
                ? WorkspaceNavigationOrder.GetPrevious(currentAutomationId, isAdvanced)
                : WorkspaceNavigationOrder.GetNext(currentAutomationId, isAdvanced);

            if (string.IsNullOrEmpty(targetAutomationId))
            {
                // Defined end of navigation list (e.g. Chat at top on Up, or Help/last item at bottom on Down).
                // Returning true stops focus from jumping unpredictably to other parts of the window.
                return true;
            }

            NavigationViewItem? targetItem = FindWorkspaceNavigationItem(targetAutomationId);
            if (targetItem != null)
            {
                targetItem.StartBringIntoView();
                targetItem.Focus(FocusState.Keyboard);
                RememberWorkspaceNavigationFocus(targetAutomationId);
                return true;
            }

            return false;
        }

        private string? FindFocusedWorkspaceAutomationId(UIElement? element)
        {
            DependencyObject? current = element;
            while (current != null)
            {
                if (current is UIElement uiElement)
                {
                    string id = AutomationProperties.GetAutomationId(uiElement);
                    if (WorkspaceNavigationOrder.IsWorkspaceAutomationId(id))
                    {
                        return id;
                    }
                }

                if (current is NavigationViewItem nvi)
                {
                    string id = AutomationProperties.GetAutomationId(nvi);
                    if (WorkspaceNavigationOrder.IsWorkspaceAutomationId(id))
                    {
                        return id;
                    }
                }

                current = VisualTreeHelper.GetParent(current);
            }

            return null;
        }

        private string? GetSelectedWorkspaceAutomationId()
        {
            if (NavView?.SelectedItem is not NavigationViewItem selectedItem)
            {
                return null;
            }

            string id = AutomationProperties.GetAutomationId(selectedItem);
            return WorkspaceNavigationOrder.IsWorkspaceAutomationId(id) ? id : null;
        }

        private void RememberWorkspaceNavigationFocus(string automationId)
        {
            if (!WorkspaceNavigationOrder.IsWorkspaceAutomationId(automationId)) return;

            _workspaceNavigationFocusedAutomationId = automationId;
            _workspaceNavigationSelectedAutomationIdAtFocus = GetSelectedWorkspaceAutomationId();
        }

        private NavigationViewItem? FindWorkspaceNavigationItem(string automationId) => automationId switch
        {
            WorkspaceNavigationOrder.ChatAutomationId => ChatNavItem,
            WorkspaceNavigationOrder.CreateAutomationId => CreateNavItem,
            WorkspaceNavigationOrder.ProjectsAutomationId => ProjectsNavItem,
            WorkspaceNavigationOrder.ModelHubAutomationId => ModelHubNavItem,
            WorkspaceNavigationOrder.ModelRoutingAutomationId => ModelRoutingNavItem,
            WorkspaceNavigationOrder.LifecycleAutomationId => LifecycleNavItem,
            WorkspaceNavigationOrder.WorkflowAutomationId => WorkflowNavItem,
            WorkspaceNavigationOrder.ToolsAutomationId => ToolsNavItem,
            WorkspaceNavigationOrder.ObservabilityAutomationId => ObservabilityNavItem,
            WorkspaceNavigationOrder.UpdatesAutomationId => UpdatesNavItem,
            WorkspaceNavigationOrder.RuntimeAutomationId => RuntimeNavItem,
            WorkspaceNavigationOrder.SettingsAutomationId => SettingsNavItem,
            WorkspaceNavigationOrder.HelpAutomationId => HelpNavItem,
            _ => null
        };

        public async Task ShowKeyboardShortcutsDialogAsync()
        {
            if (Content == null || Content.XamlRoot == null) return;

            const string overlayId = "KeyboardShortcutsDialog";
            OverlayEscapeManager.Instance.Push(new OverlayDescriptor(
                overlayId,
                OverlayKind.Dialog,
                isDismissibleOnEscape: true,
                triggeringControlAutomationId: "MainWindow_Shortcuts_Button",
                accessibleLabel: "Keyboard Shortcuts"));

            try
            {
                string markdown = AccessibleKeyboardRegistry.Default.GenerateCheatSheetMarkdown();
                var dialog = new ContentDialog
                {
                    Title = "Keyboard Shortcuts",
                    Content = new ScrollViewer
                    {
                        MaxHeight = 450,
                        Content = new TextBlock
                        {
                            Text = markdown,
                            TextWrapping = TextWrapping.Wrap,
                            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas, Segoe UI Variable, Segoe UI")
                        }
                    },
                    CloseButtonText = "Close",
                    DefaultButton = ContentDialogButton.Close,
                    XamlRoot = Content.XamlRoot
                };

                await dialog.ShowAsync();
            }
            finally
            {
                OverlayEscapeManager.Instance.Pop(overlayId);
            }
        }
    }
}
