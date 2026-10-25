# UI Automation Gaps: uit-05-settings-categories

## Overview
This document records UI automation gaps identified during task `uit-05-settings-categories` for the Settings categories rail and section navigation in `src/PaRuX.App/Views/SettingsView.xaml`.
Controls lacking stable `AutomationProperties.AutomationId` values were located in tests using their `ControlType` combined with their visible text / name (`Content` or `Text`). Adding explicit AutomationIds will improve test robustness and accessibility.

## Missing AutomationIds

| Control Type | Location / Context | Existing Identifiers | Suggested AutomationId |
|---|---|---|---|
| `ScrollViewer` | SettingsView line 31 (main content scroll surface) | None | `Settings_Content_ScrollViewer` |
| `ScrollViewer` | SettingsView line 27 (left category rail scroll surface) | None | `Settings_Rail_ScrollViewer` |
| `Button` | SettingsView line 326 (Finding your way around) | `x:Name="ShowGuidedTourButton"`, `Content="Show me around"` | `Settings_ShowGuidedTour_Button` |
| `Button` | SettingsView line 357 (Apps on this PC / Documents) | `x:Name="ChooseDocumentOutputButton"`, `Content="Choose folder"` | `Settings_DocumentOutput_Choose_Button` |
| `Button` | SettingsView line 358 (Apps on this PC / Documents) | `x:Name="ResetDocumentOutputButton"`, `Content="Use PaRuX's folder"` | `Settings_DocumentOutput_Reset_Button` |
| `Button` | SettingsView line 711 (Privacy & Network / Browser access) | `x:Name="SignOutWebSessionsButton"`, `Content="Sign out all browsers"` | `Settings_BrowserAccess_SignOut_Button` |
| `Button` | SettingsView line 738 (Privacy & Network / API keys) | `x:Name="CopyApiKeyButton"`, `Content="Copy to clipboard"` | `Settings_ApiKeys_Copy_Button` |
| `Button` | SettingsView line 999 (Integrations / Other apps) | `Content="Create a key for another app"` | `Settings_GenericKey_Create_Button` |
| `Button` | SettingsView line 1006 (Integrations / Other apps) | `Content="Copy key"` | `Settings_GenericKey_Copy_Button` |
| `Button` | SettingsView line 1093 (Gaming & AI Sharing) | `x:Name="GamingSaveProfileBtn"`, `Content="Save Profile Settings"` | `Settings_Gaming_SaveProfile_Button` |
| `Button` | SettingsView line 1180 (Agents / Context Graph) | `Content="Add relation"` | `Settings_ContextGraph_AddRelation_Button` |
| `Button` | SettingsView line 1260 (PaRuX Platform / Universal Diff) | `x:Name="UniversalDiffCompareButton"`, `Content="Compare"` | `Settings_UniversalDiff_Compare_Button` |
| `Button` | SettingsView line 1261 (PaRuX Platform / Universal Diff) | `Content="Clear"` | `Settings_UniversalDiff_Clear_Button` |
| `ToggleSwitch` | SettingsView line 1318 (Privacy & Network / Internet) | `x:Name="InternetSwitch"`, `Header="Web search"` | `Settings_Internet_Toggle` |
| `Button` | SettingsView line 1447 (Data / Import chat history) | `Content="Choose export file…"` | `Settings_ChatHistory_Import_Button` |
| `Button` | SettingsView line 1461 (Chat Behavior / Skills) | `Content="What are Skills? (guide)"` | `Settings_Skills_Guide_Button` |
| `Button` | SettingsView line 1466 (Chat Behavior / Skills) | `Content="New skill…"` | `Settings_Skills_NewSkill_Button` |
| `Button` | SettingsView line 1695 (Models / Group Chat) | `x:Name="GroupChatRunButton"`, `Content="Start group chat"` | `Settings_GroupChat_Start_Button` |
| `Button` | SettingsView line 1817 (Media Generation / 3D Studio) | `x:Name="ThreedGenerateButton"`, `Content="Generate 3D model"` | `Settings_Media_ThreedGenerate_Button` |
| `Button` | SettingsView line 1860 (Automation / Training Lab) | `x:Name="TrainingStartButton"`, `Content="Start training"` | `Settings_Training_Start_Button` |
| `Button` | SettingsView line 1884 (Advanced / Diagnostics) | `Content="Refresh"` | `Settings_Diagnostics_Refresh_Button` |
| `TextBlock` | Section headings across SettingsView (lines 44, 160, 218, etc.) | `Style="{StaticResource PaRuXSectionHeaderTextStyle}"` | `Settings_Section_<Category>_<Section>_Header` |

## Published Layout Differences
- **Models Candidate Discovery Policy**: Controls for candidate evaluation and promotion policy (`Settings_DiscoveryPolicy_AutoEvaluate_Toggle`, `Settings_DiscoveryPolicy_Promotion_ComboBox`) are present in source at `src/PaRuX.App/Views/SettingsView.xaml` line 1705, but are absent from the published main layout against which FlaUI tests execute. The last control under Models in the published build is `GroupChatRunButton` ("Start group chat") at line 1695.
- **General Notifications**: The `NOTIFICATIONS` section (`Settings_NotificationsGeneration_Toggle`, etc.) at line 95 is not present in the published layout. General category headings present in the published build are `GENERAL`, `QUICK CHAT BUBBLE`, and `FINDING YOUR WAY AROUND`.
