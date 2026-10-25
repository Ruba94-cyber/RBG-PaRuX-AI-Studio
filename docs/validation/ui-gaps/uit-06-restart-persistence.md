# UI Automation Gaps: uit-06-restart-persistence

This document records controls, locator gaps, and state persistence behaviors identified during the authoring and verification of restart persistence coverage tests (`tests/PaRuX.UITests/Tests/StatePersistenceCoverageTests.cs`).

## Controls Lacking AutomationId

| Control | Page / View | Source Location | Current Locator | Suggested `AutomationId` | Purpose / Impact |
|---|---|---|---|---|---|
| ComboBoxItem ("Dark") | SettingsView | `src/PaRuX.App/Views/SettingsView.xaml:65` | `ControlType.ListItem` + Name `"Dark"` | `Settings_Theme_Dark_Item` | Item in Theme combo; tests must resolve by raw tree walker and text name. |
| ComboBoxItem ("Light") | SettingsView | `src/PaRuX.App/Views/SettingsView.xaml:64` | `ControlType.ListItem` + Name `"Light"` | `Settings_Theme_Light_Item` | Item in Theme combo; tests must resolve by raw tree walker and text name. |
| ComboBoxItem ("System") | SettingsView | `src/PaRuX.App/Views/SettingsView.xaml:63` | `ControlType.ListItem` + Name `"System"` | `Settings_Theme_System_Item` | Item in Theme combo; tests must resolve by raw tree walker and text name. |
| ToggleSwitch (`ComputerUseAutomationSwitch`) | SettingsView | `src/PaRuX.App/Views/SettingsView.xaml:715` | `ControlType.Button` + Name `"Full Computer Use (mouse, keyboard, screen)"` | `Settings_ComputerUseAutomation_Toggle` | Automation toggle that persists to `settings.json` (`ComputerUseFullAutomationEnabled`). |
| Border (`StatusPanel`) | ChatView | `src/PaRuX.App/Views/ChatView.xaml:387` | Inferred via `Chat_Stop_Button` / child tree | `Chat_Status_Panel` | Transient busy status pill container for inference model loads. |
| ProgressRing (`StatusRing`) | ChatView | `src/PaRuX.App/Views/ChatView.xaml:398` | Inferred via raw tree walker | `Chat_Status_Ring` | Transient activity ring shown during long operations. |
| TextBlock (`StatusText`) | ChatView | `src/PaRuX.App/Views/ChatView.xaml:405` | Inferred via raw tree walker | `Chat_Status_Text` | Transient status explanation text paired with the progress ring. |

## Restart Persistence Architectural Observations

1. **Workspace Navigation Resets to Chat Home**:
   - `MainWindow.xaml.cs:144` executes `ShowChat()` on startup whenever onboarding is completed. The active workspace is deliberately not saved across sessions; after an abrupt restart or clean launch, PaRuX always presents the primary Chat surface.
2. **Window Sizing and State**:
   - `MainWindow.xaml.cs:83-92, 181-185` (`ResizeForCurrentDisplay()` and `MaximizeOnLaunch()`) deliberately starts MainWindow in the Maximized state on every launch. User window dimension changes are not restored for MainWindow (unlike `BubbleWindow` which persists its position).
3. **Chat Composer Draft State**:
   - `ChatView.xaml:455-473` (`InputBox`) maintains in-memory draft text only. Uncommitted prompts are not persisted to `SettingsStore` or `HistoryStore` and start clean (`""`) after app restart.
4. **Settings > General Startup Toggle vs Isolated Test Data Root**:
   - `Settings_Startup_Toggle` (`StartupSwitch`, `SettingsView.xaml.cs:1903-1910`) prioritizes the machine-level registry (`HKCU\Software\Microsoft\Windows\CurrentVersion\Run`) over `settings.json`. In isolated test execution environments without registry modifications, `ThemeCombo` (`Settings_Theme_ComboBox`) represents the primary safe setting persisted directly in `settings.json`.
