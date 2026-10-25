# UI Automation Gaps: uit-03-keyboard-traversal

This document records accessibility and keyboard navigation gaps identified during the authoring of `KeyboardTraversalCoverageTests` for task `uit-03-keyboard-traversal-r2`.

## Keyboard Traversal and Focus Gaps

| Control / Feature | ControlType | Page / View | Location | Issue Description | Suggested Improvement |
|---|---|---|---|---|---|
| In-App Help Trigger Return Focus | `Button` | `ChatView` / `MainWindow` | `src/PaRuX.App/MainWindow.Tour.cs:193` | `ShowInAppHelpDialogAsync` hardcodes `triggeringControlAutomationId: "Navigation_Help_Item"`. When invoked via `Chat_Header_Help_Button` (`ChatView.xaml:192`), `OverlayEscapeManager` associates return focus with `Navigation_Help_Item` rather than the invoking button in the Chat header. | Accept `triggeringControlAutomationId` parameter in `ShowInAppHelpDialogAsync` so focus returns to the invoking control regardless of whether Help was opened from the sidebar or header. |
| Settings Category Rail Buttons | `Button` | `SettingsView` | `src/PaRuX.App/Views/SettingsView.xaml.cs:100-106` | Category rail buttons are dynamically instantiated in code-behind with concatenated names (`Settings_Category_<Name>_Button`) without access keys (e.g. `AccessKey="G"` for General, `AccessKey="M"` for Models). | Add explicit `AccessKey` properties to category buttons to allow Alt-key direct jumps. |
| Chat Profile Selector Tab Navigation | `ToggleButton` group | `ChatView` | `src/PaRuX.App/Views/ChatView.xaml:224-255` | The profile selector toggles (Auto, Standard, Uncensored) are individual sequential tab stops rather than a radio group. Keyboard-only users must tab through all three toggles to reach the composer prompt. | Set `KeyboardNavigation.TabNavigation="Once"` on the profile container so arrow keys navigate between options and a single Tab advances past the group. |
| Chat Navigation Item Peer Realization | `ListItem` / Custom | `MainWindow` | `src/PaRuX.App/MainWindow.xaml:54` | In WinUI 3, the `NavigationViewItem` for the Chat home workspace exposes custom name "Chat, home workspace" and depending on runtime state can require lookup by AutomationId alone rather than strict `ControlType.ListItem`. | Ensure consistent AutomationPeer exposure as `ListItem` across all window lifecycle states. |
| Section Level Keyboard Jumps (F6) | Container / Pane | `MainWindow` | `src/PaRuX.App/MainWindow.xaml:39-235` | Navigation between major functional regions (Sidebar Navigation Pane, Header / Search, Workspace Content Area) relies solely on sequential Tab / Shift+Tab rather than F6 / Shift+F6 landmark switching. | Implement F6 cycle between navigation pane, pane custom footer, and active workspace content. |

## Tested Behaviors and Verification

The test class `tests/PaRuX.UITests/Tests/KeyboardTraversalCoverageTests.cs` verifies:
- Deterministic Up/Down arrow key traversal across navigation items with focus verification and on-screen bounding rectangle checks (`Arrow_keys_navigate_between_workspace_navigation_items_with_focus_and_on_screen_bounds`).
- Activation of focused navigation items and settings category buttons via Space key (`Space_key_activates_focused_navigation_item_and_focused_settings_category_button`).
- Activation of focused category buttons and New Chat button via Enter key (`Enter_key_activates_focused_settings_category_button_restoring_general_category`).
- Forward Tab navigation from navigation into Chat content and Shift+Tab return (`Tab_from_navigation_into_chat_reaches_first_interactive_control_and_shift_tab_returns`).
- Forward Tab navigation from navigation into Settings content and Shift+Tab return (`Tab_from_navigation_into_settings_reaches_first_interactive_control_and_shift_tab_returns`).
- Forward Tab navigation from navigation into Model Hub content and Shift+Tab return (`Tab_from_navigation_into_model_hub_reaches_first_interactive_control_and_shift_tab_returns`).
- Escape key dismissal of modal In-App Help dialog with focus restoration to the invoking navigation item (`Escape_key_closes_open_in_app_help_dialog_and_restores_focus_to_invoking_control`).
- 60-step Tab cycling verifying absence of keyboard traps, visiting at least 6 distinct interactive controls, cycling out of the page area, and confirming every focused control remains on-screen (`Cycling_through_sixty_tabs_never_traps_visits_distinct_controls_and_keeps_focus_on_screen`).
