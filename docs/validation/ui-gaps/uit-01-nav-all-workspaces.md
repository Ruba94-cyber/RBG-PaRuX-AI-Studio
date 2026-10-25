# UI Automation Gaps: uit-01-nav-all-workspaces

This document records controls identified during the authoring of `WorkspaceNavigationCoverageTests` that lack stable `AutomationProperties.AutomationId` values and are currently located by `ControlType` plus `Name` / `Text`.

## Missing AutomationIds

| Control | ControlType | Page / View | Current Selector | Suggested AutomationId |
|---|---|---|---|---|
| Page Heading "Projects & Artifacts" | `Text` | `ProjectsWorkspaceView.cs:63` | `cf.ByName("Projects & Artifacts").And(cf.ByControlType(ControlType.Text))` | `Projects_Header_Title_Text` |
| "Create Project" Button | `Button` | `ProjectsWorkspaceView.cs:86` | `cf.ByName("Create Project").And(cf.ByControlType(ControlType.Button))` | `Projects_CreateProject_Button` |
| Page Heading "Lifecycle Lab" | `Text` | `LifecycleLabView.cs:102` | `cf.ByName("Lifecycle Lab").And(cf.ByControlType(ControlType.Text))` | `LifecycleLab_Header_Title_Text` |
| "Refresh all" Button | `Button` | `LifecycleLabView.cs:104` | `cf.ByName("Refresh all").And(cf.ByControlType(ControlType.Button))` | `LifecycleLab_RefreshAll_Button` |
| Page Heading "Workflows" | `Text` | `WorkflowWorkspaceView.cs:56` | `cf.ByName("Workflows").And(cf.ByControlType(ControlType.Text))` | `Workflows_Header_Title_Text` |
| "New workflow…" Button | `Button` | `WorkflowWorkspaceView.cs:67` | `cf.ByName("New workflow…").And(cf.ByControlType(ControlType.Button))` | `Workflows_NewWorkflow_Button` |
| Page Heading "Tools & Automations" | `Text` | `ToolsAutomationView.cs:94` | `cf.ByName("Tools & Automations").And(cf.ByControlType(ControlType.Text))` | `Tools_Header_Title_Text` |
| Section Header "SKILLS & PERMISSIONS" | `Text` | `ToolsAutomationView.cs:108` | `cf.ByName("SKILLS & PERMISSIONS").And(cf.ByControlType(ControlType.Text))` | `Tools_SkillsPermissions_Heading_Text` |
| Page Heading "Observability & Benchmarks" | `Text` | `ObservabilityView.cs:56` | `cf.ByName("Observability & Benchmarks").And(cf.ByControlType(ControlType.Text))` | `Observability_Header_Title_Text` |
| Section Header "CURRENT CORE SNAPSHOT" | `Text` | `ObservabilityView.cs:78` | `cf.ByName("CURRENT CORE SNAPSHOT").And(cf.ByControlType(ControlType.Text))` | `Observability_CurrentSnapshot_Heading_Text` |
| Page Heading "Update Manager" | `Text` | `UpdateManagerView.cs:63` | `cf.ByName("Update Manager").And(cf.ByControlType(ControlType.Text))` | `UpdateManager_Header_Title_Text` |
| "Check again" Button | `Button` | `UpdateManagerView.cs:64` | `cf.ByName("Check again").And(cf.ByControlType(ControlType.Button))` | `UpdateManager_CheckAgain_Button` |
| Page Heading "Runtime Manager" | `Text` | `RuntimeManagerView.cs:59` | `cf.ByName("Runtime Manager").And(cf.ByControlType(ControlType.Text))` | `RuntimeManager_Header_Title_Text` |
| "Cancel active install" Button | `Button` | `RuntimeManagerView.cs:70` | `cf.ByName("Cancel active install").And(cf.ByControlType(ControlType.Button))` | `RuntimeManager_CancelActiveInstall_Button` |

## Navigation Item Realization Notes

- `Navigation_Chat_Item`:
  - Definition: `src/PaRuX.App/MainWindow.xaml:54`
  - Tag: `__chat__`
  - In earlier published layouts, `Navigation_Chat_Item` was not included in `WorkspaceNavigationItems()` before commit `4b9f634f`. Tests check for `Navigation_Chat_Item` when realized and use `MainWindow_NewChat_Button` (`MainWindow.xaml:139`) as a fallback to reliably return to Chat home across layout revisions.
