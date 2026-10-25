# UI Automation Gaps: uit-07-gaming-cloud-honesty

This document records controls, status badges, and section elements identified during the authoring of `LocalOnlyAndGamingHonestyCoverageTests` that lack explicit `AutomationProperties.AutomationId` values matching the PaRuX naming standard (e.g. `Settings_<Section>_<Control>_<Type>`) and are located by `AutomationId` (derived from `x:Name`) or `ControlType` plus `Name` / `Text`.

## Missing AutomationIds

| Control | ControlType | Page / View | Current Selector | Suggested AutomationId |
|---|---|---|---|---|
| `GamingStatusText` (Hard-off status) | `Text` | `SettingsView.xaml:632` | `ControlType.Text` containing "Ready" / "ACTIVE" | `Settings_Gaming_HardOff_Status_Text` |
| `EnterGamingButton` (Hard-off Enter) | `Button` | `SettingsView.xaml:634` | `cf.ByName("Enter Gaming Mode").And(cf.ByControlType(ControlType.Button))` | `Settings_Gaming_EnterHardOff_Button` |
| `ExitGamingButton` (Hard-off Exit) | `Button` | `SettingsView.xaml:635` | `cf.ByName("Exit Gaming Mode").And(cf.ByControlType(ControlType.Button))` | `Settings_Gaming_ExitHardOff_Button` |
| `GamingStatusBadge` (Orchestrator badge) | `Text` | `SettingsView.xaml:1031` | `cf.ByAutomationId("GamingStatusBadge")` / state word (`AIREADY`, `CHECKING`, etc.) | `Settings_Gaming_Orchestrator_Status_Badge` |
| `CloudFallbackToggle` (ToggleSwitch) | `Button` | `SettingsView.xaml:383` | `cf.ByAutomationId("CloudFallbackToggle")` / `ControlType.Button` containing "Allow a cloud fallback" | `Settings_CloudFallback_Toggle` |
| `CloudFallbackStatusText` | `Text` | `SettingsView.xaml:418` | `cf.ByAutomationId("CloudFallbackStatusText")` / `ControlType.Text` with status wording | `Settings_CloudFallback_Status_Text` |
| `SaveCloudFallbackButton` | `Button` | `SettingsView.xaml:413` | `cf.ByAutomationId("SaveCloudFallbackButton")` / `cf.ByName("Save")` | `Settings_CloudFallback_Save_Button` |
| `TestCloudFallbackButton` | `Button` | `SettingsView.xaml:414` | `cf.ByAutomationId("TestCloudFallbackButton")` / `cf.ByName("Test connection")` | `Settings_CloudFallback_Test_Button` |
| `CloudFallbackBaseUrlBox` | `Edit` | `SettingsView.xaml:397` | `cf.ByAutomationId("CloudFallbackBaseUrlBox")` / `cf.ByName("API base URL")` | `Settings_CloudFallback_BaseUrl_TextBox` |
| `CloudFallbackModelBox` | `Edit` | `SettingsView.xaml:401` | `cf.ByAutomationId("CloudFallbackModelBox")` / `cf.ByName("Model id")` | `Settings_CloudFallback_Model_TextBox` |
| `CloudFallbackApiKeyBox` | `Edit` | `SettingsView.xaml:405` | `cf.ByAutomationId("CloudFallbackApiKeyBox")` / `cf.ByName("API key")` | `Settings_CloudFallback_ApiKey_PasswordBox` |
| Section Heading "CLOUD FALLBACK (OPTIONAL)" | `Text` | `SettingsView.xaml:374` | `cf.ByName("CLOUD FALLBACK (OPTIONAL)").And(cf.ByControlType(ControlType.Text))` | `Settings_CloudFallback_Heading_Text` |
| Section Heading "GAMING" | `Text` | `SettingsView.xaml:626` | `cf.ByName("GAMING").And(cf.ByControlType(ControlType.Text))` | `Settings_Privacy_Gaming_Heading_Text` |
