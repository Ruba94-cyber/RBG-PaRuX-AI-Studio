# UI Automation Gaps: uit-10-settings-integrations

This document records controls, locator gaps, and integration behaviors identified during the authoring and verification of client integrations and desktop integrations coverage tests (`tests/PaRuX.UITests/Tests/ClientIntegrationsCoverageTests.cs`).

## Controls Lacking AutomationId

| Control | Page / View | Source Location | Current Locator | Suggested `AutomationId` | Purpose / Impact |
|---|---|---|---|---|---|
| Button (`ChooseIntegrationFolder`) | SettingsView | `src/PaRuX.App/Views/SettingsView.xaml:931` | `ControlType.Button` + Name `"Choose folder"` | `Settings_Integrations_ChooseFolder_Button` | Folder picker button for integration launch working directory. |
| Expander (`Advanced gateway settings`) | SettingsView | `src/PaRuX.App/Views/SettingsView.xaml:898` | Name `"Advanced gateway settings"` | `Settings_Gateway_Advanced_Expander` | Container expander for loopback port and vendor model defaults. |
| ToggleSwitch (`GatewayEnabledToggle`) | SettingsView | `src/PaRuX.App/Views/SettingsView.xaml:900` | Name `"Gateway port"` | `Settings_Gateway_Enabled_Toggle` | Enables or disables loopback gateway listener. |
| NumberBox (`GatewayPortBox`) | SettingsView | `src/PaRuX.App/Views/SettingsView.xaml:901` | Name `"Port"` | `Settings_Gateway_Port_NumberBox` | Sets port for local gateway listener (1024-65535). |
| ComboBox (`GatewayVendorModelCombo`) | SettingsView | `src/PaRuX.App/Views/SettingsView.xaml:902` | Name `"When a client asks for a Claude or GPT model name, use"` | `Settings_Gateway_VendorModel_ComboBox` | Maps external vendor model names to internal model routes. |
| Button (`SaveGatewaySettings`) | SettingsView | `src/PaRuX.App/Views/SettingsView.xaml:911` | `ControlType.Button` + Name `"Save gateway settings"` | `Settings_Gateway_Save_Button` | Commits port, model default and listener state updates. |
| TextBlock (`GatewayDetailText`) | SettingsView | `src/PaRuX.App/Views/SettingsView.xaml:893` | Inferred from gateway state panel | `Settings_Gateway_Detail_Text` | Explanatory detail of models, routing and restart notices. |
| Button (`RefreshDesktopIntegrationsButton`) | SettingsView | `src/PaRuX.App/Views/SettingsView.xaml:298` | `ControlType.Button` + Name `"Scan this PC"` | `Settings_DesktopIntegrations_Scan_Button` | Triggers local scan for installed desktop sources (notes, mail, calendar, bookmarks). |
| TextBlock (`DesktopIntegrationsStatusText`) | SettingsView | `src/PaRuX.App/Views/SettingsView.xaml:299` | Inferred via `ControlType.Text` in scan row | `Settings_DesktopIntegrations_Status_Text` | Summarizes connected vs detected desktop app sources. |
| Button (`ChooseDocumentOutputButton`) | SettingsView | `src/PaRuX.App/Views/SettingsView.xaml:313` | `ControlType.Button` + Name `"Choose folder"` | `Settings_DocumentOutput_ChooseFolder_Button` | Pick destination directory for generated Word and Excel files. |
| Button (`ResetDocumentOutputButton`) | SettingsView | `src/PaRuX.App/Views/SettingsView.xaml:314` | `ControlType.Button` + Name `"Use PaRuX's folder"` | `Settings_DocumentOutput_Reset_Button` | Resets document generation target to default internal folder. |
| TextBlock (`DocumentOutputText`) | SettingsView | `src/PaRuX.App/Views/SettingsView.xaml:315` | Inferred via `ControlType.Text` in output row | `Settings_DocumentOutput_Status_Text` | Displays current active folder destination for generated files. |
| Button (`CopyGenericOpenAiUrl`) | SettingsView | `src/PaRuX.App/Views/SettingsView.xaml:949` | `ControlType.Button` + Name `"Copy"` | `Settings_Generic_OpenAiUrl_Copy_Button` | Copies OpenAI-compatible base URL to clipboard. |
| Button (`CopyGenericAnthropicUrl`) | SettingsView | `src/PaRuX.App/Views/SettingsView.xaml:952` | `ControlType.Button` + Name `"Copy"` | `Settings_Generic_AnthropicUrl_Copy_Button` | Copies Anthropic-compatible base URL to clipboard. |
| Button (`CreateGenericGatewayKey`) | SettingsView | `src/PaRuX.App/Views/SettingsView.xaml:955` | `ControlType.Button` + Name `"Create a key for another app"` | `Settings_Generic_CreateKey_Button` | Generates a new API bearer token for external apps. |
| TextBlock (`StartupHint`) | SettingsView | `src/PaRuX.App/Views/SettingsView.xaml:78` | `ControlType.Text` + Name prefix `"Adds PaRuX to the Windows startup list"` | `Settings_Startup_Hint_Text` | Explains background Windows startup registration and runtime impact. |

## Architectural and UX Observations

1. **Client Action Button Omission When Not Installed**:
   - `SettingsView.ClientIntegrations.cs:147-168` conditionally renders action buttons (`Connect`, `Launch`, `Test`, `Disconnect`, `Restore original`) only when `detection.Installed == true`. Uninstalled clients present a clean informative row containing DisplayName, Protocol and a truthful status badge ("Not installed") without clickable action buttons that would inevitably fail.
2. **Explicit User Save for Gateway Listener and Port Settings**:
   - In `SettingsView.xaml:898-915` and `SettingsView.ClientIntegrations.cs:270-285`, changing `GatewayEnabledToggle` or `GatewayPortBox` does not immediately alter runtime state. Saving requires an explicit click on "Save gateway settings" and changes take effect on the subsequent application restart.
3. **Desktop Integrations Read-Only Disclosure and Scoping**:
   - `SettingsView.xaml:288-326` prominently declares that reading external apps (notes, mail, calendar, bookmarks) is strictly opt-in, read-only, scoped to the user-selected folder or file, and never transmitted across the network. Generated documents are restricted to a single nominated folder and never overwrite existing files.
