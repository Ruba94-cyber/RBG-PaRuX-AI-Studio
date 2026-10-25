# UI Automation Gaps: uit-04-responsive-layouts

This document records controls and responsive layout observations identified during the authoring and verification of responsive layout coverage tests (`tests/PaRuX.UITests/Tests/ResponsiveLayoutCoverageTests.cs`).

## Controls Lacking AutomationId

| Control | Page / View | Source Location | Current Locator | Suggested `AutomationId` | Purpose / Impact |
|---|---|---|---|---|---|
| TextBlock ("Runtime Manager") | RuntimeManagerView | `src/PaRuX.App/Views/RuntimeManagerView.cs:59` | `ControlType.Text` + Name `"Runtime Manager"` | `Runtime_Header_Title_Text` | Primary workspace header text used to confirm RuntimeManagerView is displayed. |
| Button ("Refresh") | RuntimeManagerView | `src/PaRuX.App/Views/RuntimeManagerView.cs:67` | `ControlType.Button` + Name `"Refresh"` | `Runtime_Refresh_Button` | Primary action button to refresh detected local runtimes. |
| Button ("Cancel active install") | RuntimeManagerView | `src/PaRuX.App/Views/RuntimeManagerView.cs:70` | `ControlType.Button` + Name `"Cancel active install"` | `Runtime_CancelActiveInstall_Button` | Cancellation action for running runtime installation jobs. |
| TextBlock ("Inference Performance Engine") | RuntimeManagerView | `src/PaRuX.App/Views/RuntimeManagerView.cs:96` | `ControlType.Text` + Name `"Inference Performance Engine"` | `Runtime_PerformanceEngine_Heading` | Heading for the performance benchmark and optimization card. |
| TextBlock ("Runtime components") | RuntimeManagerView | `src/PaRuX.App/Views/RuntimeManagerView.cs:80` | `ControlType.Text` + Name `"Runtime components"` | `Runtime_Components_Heading` | Heading for the list of managed and detected runtime packages. |
| TextBox (`_imagePrompt`) | CreateWorkspaceView | `src/PaRuX.App/Views/CreateWorkspaceView.cs:38` | `ControlType.Edit` (PlaceholderText) | `Create_ImagePrompt_TextBox` | Primary text prompt for local media generation. |
| Button ("Generate image") | CreateWorkspaceView | `src/PaRuX.App/Views/CreateWorkspaceView.cs:126` | `ControlType.Button` + Name `"Generate image"` | `Create_GenerateImage_Button` | Action button to trigger local image generation pipeline. |

## Responsive Layout and Navigation Behaviors

1. **WinUI NavigationView Selection vs Invocation**:
   - WinUI 3 `NavigationView` only routes workspace changes on `ItemInvoked` (`NavView_ItemInvoked`), not `SelectionChanged`. Calling `ISelectionItemProvider.Select()` updates UIA selection state but does not fire `ItemInvoked`. Automated tests must invoke navigation items via real click or keyboard Space activation (`element.Focus()` + Space).
2. **Narrow Window Pane Accommodation**:
   - At 560x560, the expanded navigation pane (288px) overlays multi-column content in `SettingsView` (192px category rail) and `ModelHubView` (319px minimum auto columns). Toggling the navigation pane closed via `TogglePaneButton` provides sufficient width for content rows and allows primary controls to remain bounded within the window viewport.
