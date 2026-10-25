# UI Automation Gaps: uit-21-chat-simple-advanced

This document records controls and functional aspects identified during the authoring of `ChatSimpleAdvancedCoverageTests` that lack stable `AutomationProperties.AutomationId` values or have architectural limitations regarding UI automation.

## Missing AutomationIds

| Control | ControlType | Page / View | Current Selector | Suggested AutomationId |
|---|---|---|---|---|
| Profile Selector Container Panel | `Group` / `Pane` (`Border`) | `ChatView.xaml:222` | Parent `Chat_ProfileSelector_ScrollViewer` (`ChatView.xaml:213`) or child toggle `Chat_Profile_Auto_Toggle` (`ChatView.xaml:226`) | `Chat_ProfileSelector_Panel` |
| Simple Mode Answering Model Panel | `Group` / `Pane` (`StackPanel`) | `ChatView.xaml:594` | Child label `Chat_SimpleMode_AnsweringModel_Label` (`ChatView.xaml:611`) or button `Chat_SimpleMode_RouteReason_Button` (`ChatView.xaml:621`) | `Chat_SimpleMode_AnsweringModel_Panel` |
| Simple Mode Runtime Health Panel | `Group` / `Pane` (`StackPanel`) | `ChatView.xaml:634` | Child button `Chat_SimpleMode_RuntimeHealth_Button` (`ChatView.xaml:641`) or label `Chat_SimpleMode_RuntimeHealth_Label` (`ChatView.xaml:659`) | `Chat_SimpleMode_RuntimeHealth_Panel` |

## Functional and Architectural Notes

- **Sampling Options in ChatView**:
  - The task specification mentions inspecting sampling options in Advanced mode ("shows/hides exactly the advanced controls (model selector, routing explanation, sampling options, whatever exists)").
  - In `ChatView.xaml` / `ChatView.SimpleMode.cs`, sampling parameters (e.g. temperature, top_p, top_k, context length, repetition penalty) are not exposed as inline controls in either Simple or Advanced mode. Sampling parameters are managed via global engine configuration and Settings rather than inline in ChatView.
- **Responsive Pane Collapse & Interface Toggle**:
  - In narrow layouts (width <= 640 px), `WindowResponsivenessPolicy` automatically collapses the navigation pane to an icon rail (`NavigationViewPaneDisplayMode.LeftCompact`).
  - Because `MainWindow_StudioMode_Toggle` is situated in the `NavigationView.PaneHeader`, it is not realized when the pane is collapsed. A secondary `ContentStudioModeToggle` (`MainWindow_StudioMode_Content_Toggle`) is rendered at the top of the content area when the pane is closed to ensure accessibility across breakpoints.
