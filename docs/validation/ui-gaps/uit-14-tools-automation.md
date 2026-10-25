# UI Automation Gaps: uit-14-tools-automation

This document records controls, locator gaps, and behavioral UX observations identified during the authoring and verification of the Tools and automation workspace automated UI tests (`tests/PaRuX.UITests/Tests/ToolsAutomationCoverageTests.cs`).

## Controls Lacking AutomationId

`ToolsAutomationView` (`src/PaRuX.App/Views/ToolsAutomationView.cs`) is implemented in C# code-behind and currently sets no `AutomationProperties.AutomationId` or explicit `AutomationProperties.Name` on its visual elements. The automated tests locate controls through `ControlType` and verified textual content or element hierarchy. Related refresh controls in `SettingsView.xaml` are also listed.

| Control | Page / View | Source Location | Current Locator | Suggested `AutomationId` | Purpose / Impact |
|---|---|---|---|---|---|
| TextBlock (`title`) | ToolsAutomationView | `src/PaRuX.App/Views/ToolsAutomationView.cs:94` | `ControlType.Text` + Name `"Tools & Automations"` | `Tools_Title_Text` | Primary workspace header. |
| Button (`refresh`) | ToolsAutomationView | `src/PaRuX.App/Views/ToolsAutomationView.cs:96` | `ControlType.Button` + Name `"Refresh all"` | `Tools_RefreshAll_Button` | Triggers parallel refresh of skills, MCP, automations, and workflows. |
| TextBlock (`description`) | ToolsAutomationView | `src/PaRuX.App/Views/ToolsAutomationView.cs:102` | `ControlType.Text` + Name prefix `"Inspect skill permissions"` | `Tools_Description_Text` | Explains scope of permissions, MCP, automations and workflow DAGs. |
| ProgressBar (`_busy`) | ToolsAutomationView | `src/PaRuX.App/Views/ToolsAutomationView.cs:25` | `ControlType.ProgressBar` (indeterminate) | `Tools_Busy_ProgressBar` | Progress indicator displayed while refresh or actions are executing. |
| TextBlock (`_status`) | ToolsAutomationView | `src/PaRuX.App/Views/ToolsAutomationView.cs:21` | `ControlType.Text` + status prefix or validation text | `Tools_Status_Text` | Displays refresh state, operation results, and validation errors. |
| TextBlock (`SKILLS & PERMISSIONS`) | ToolsAutomationView | `src/PaRuX.App/Views/ToolsAutomationView.cs:108` | `ControlType.Text` + Name `"SKILLS & PERMISSIONS"` | `Tools_Section_Skills_Header` | Header for skill permissions matrix section. |
| TextBlock (`No skills registered.`) | ToolsAutomationView | `src/PaRuX.App/Views/ToolsAutomationView.cs:244` | `ControlType.Text` + Name `"No skills registered."` | `Tools_Skills_Empty_Text` | Empty state text when no skills are registered. |
| ToggleSwitch (`toggle`) | ToolsAutomationView | `src/PaRuX.App/Views/ToolsAutomationView.cs:224` | `ControlType.Button` / `ToggleSwitch` + Content `"Enabled"` / `"Disabled"` | `Tools_Skill_{skillId}_Toggle` | Toggles whether a skill is enabled in Core registry. |
| Button (`permissions`) | ToolsAutomationView | `src/PaRuX.App/Views/ToolsAutomationView.cs:239` | `ControlType.Button` + Name `"Permissions…"` | `Tools_Skill_{skillId}_Permissions_Button` | Opens modal dialog to inspect and edit skill permissions. |
| TextBlock (`MCP SERVERS`) | ToolsAutomationView | `src/PaRuX.App/Views/ToolsAutomationView.cs:109` | `ControlType.Text` + Name `"MCP SERVERS"` | `Tools_Section_Mcp_Header` | Header for outbound MCP servers section. |
| ComboBox (`_mcpPreset`) | ToolsAutomationView | `src/PaRuX.App/Views/ToolsAutomationView.cs:31` | `ControlType.ComboBox` + tree hierarchy in MCP section | `Tools_McpPreset_ComboBox` | Dropdown of preconfigured MCP server presets. |
| TextBox (`_mcpName`) | ToolsAutomationView | `src/PaRuX.App/Views/ToolsAutomationView.cs:32` | `ControlType.Edit` (sibling of Add MCP button) | `Tools_McpName_TextBox` | Input for new MCP server identifier. |
| TextBox (`_mcpCommand`) | ToolsAutomationView | `src/PaRuX.App/Views/ToolsAutomationView.cs:33` | `ControlType.Edit` (sibling of Add MCP button) | `Tools_McpCommand_TextBox` | Input for executable/runtime command (e.g. npx). |
| TextBox (`_mcpArgs`) | ToolsAutomationView | `src/PaRuX.App/Views/ToolsAutomationView.cs:34` | `ControlType.Edit` (sibling of Add MCP button) | `Tools_McpArgs_TextBox` | Input for command-line arguments and flags. |
| Button (`add`) | ToolsAutomationView | `src/PaRuX.App/Views/ToolsAutomationView.cs:139` | `ControlType.Button` + Name `"Add MCP server"` | `Tools_AddMcp_Button` | Validates and submits new outbound MCP server registration. |
| TextBlock (`No outbound MCP servers configured.`) | ToolsAutomationView | `src/PaRuX.App/Views/ToolsAutomationView.cs:352` | `ControlType.Text` + Name `"No outbound MCP servers configured."` | `Tools_Mcp_Empty_Text` | Empty state notice when no MCP servers are configured. |
| Button (`remove`) | ToolsAutomationView | `src/PaRuX.App/Views/ToolsAutomationView.cs:331` | `ControlType.Button` + Name `"Remove"` | `Tools_Mcp_{serverId}_Remove_Button` | Triggers confirmed removal of an MCP server. |
| TextBlock (`AUTOMATIONS`) | ToolsAutomationView | `src/PaRuX.App/Views/ToolsAutomationView.cs:110` | `ControlType.Text` + Name `"AUTOMATIONS"` | `Tools_Section_Automations_Header` | Header for automations section. |
| TextBox (`_automationName`) | ToolsAutomationView | `src/PaRuX.App/Views/ToolsAutomationView.cs:36` | `ControlType.Edit` in Automations section top row | `Tools_AutomationName_TextBox` | Input for automation name. |
| ComboBox (`_triggerType`) | ToolsAutomationView | `src/PaRuX.App/Views/ToolsAutomationView.cs:37` | `ControlType.ComboBox` in Automations section top row | `Tools_AutomationTriggerType_ComboBox` | Selector for trigger type (schedule, git_commit, etc.). |
| TextBox (`_triggerJson`) | ToolsAutomationView | `src/PaRuX.App/Views/ToolsAutomationView.cs:38` | `ControlType.Edit` with JSON text | `Tools_AutomationTriggerJson_TextBox` | Multiline JSON editor for trigger parameters. |
| TextBox (`_automationGoal`) | ToolsAutomationView | `src/PaRuX.App/Views/ToolsAutomationView.cs:39` | `ControlType.Edit` with goal placeholder | `Tools_AutomationGoal_TextBox` | Agent task goal template string. |
| TextBox (`_automationRole`) | ToolsAutomationView | `src/PaRuX.App/Views/ToolsAutomationView.cs:40` | `ControlType.Edit` with role placeholder | `Tools_AutomationRole_TextBox` | Optional role specification for agent task. |
| Button (`create`) | ToolsAutomationView | `src/PaRuX.App/Views/ToolsAutomationView.cs:157` | `ControlType.Button` + Name `"Create automation"` | `Tools_CreateAutomation_Button` | Validates inputs and creates automation in Core engine. |
| TextBlock (`No automations yet.`) | ToolsAutomationView | `src/PaRuX.App/Views/ToolsAutomationView.cs:420` | `ControlType.Text` + Name `"No automations yet."` | `Tools_Automations_Empty_Text` | Empty state notice when no automations are created. |
| Button (`delete`) | ToolsAutomationView | `src/PaRuX.App/Views/ToolsAutomationView.cs:400` | `ControlType.Button` + Name `"Delete"` | `Tools_Automation_{automationId}_Delete_Button` | Triggers confirmed deletion of an automation. |
| TextBlock (`WORKFLOW BUILDER`) | ToolsAutomationView | `src/PaRuX.App/Views/ToolsAutomationView.cs:111` | `ControlType.Text` + Name `"WORKFLOW BUILDER"` | `Tools_Section_Workflows_Header` | Header for workflow builder section. |
| TextBox (`_workflowName`) | ToolsAutomationView | `src/PaRuX.App/Views/ToolsAutomationView.cs:42` | `ControlType.Edit` with workflow name placeholder | `Tools_WorkflowName_TextBox` | Input for workflow template name. |
| TextBox (`_workflowDescription`) | ToolsAutomationView | `src/PaRuX.App/Views/ToolsAutomationView.cs:43` | `ControlType.Edit` with description placeholder | `Tools_WorkflowDescription_TextBox` | Optional description for workflow template. |
| TextBox (`_workflowStepsJson`) | ToolsAutomationView | `src/PaRuX.App/Views/ToolsAutomationView.cs:44` | `ControlType.Edit` containing initial steps DAG JSON | `Tools_WorkflowStepsJson_TextBox` | JSON array editor for workflow steps definition. |
| Button (`create`) | ToolsAutomationView | `src/PaRuX.App/Views/ToolsAutomationView.cs:181` | `ControlType.Button` + Name `"Create workflow template"` | `Tools_CreateWorkflow_Button` | Validates DAG and creates workflow template. |
| TextBlock (`No workflow templates yet.`) | ToolsAutomationView | `src/PaRuX.App/Views/ToolsAutomationView.cs:493` | `ControlType.Text` + Name `"No workflow templates yet."` | `Tools_Workflows_Empty_Text` | Empty state notice when no workflow templates exist. |
| Button (`run`) | ToolsAutomationView | `src/PaRuX.App/Views/ToolsAutomationView.cs:468` | `ControlType.Button` + Name `"Run…"` | `Tools_Workflow_{workflowId}_Run_Button` | Prompts for input and creates AgentTask DAG instance. |
| Button (`delete`) | ToolsAutomationView | `src/PaRuX.App/Views/ToolsAutomationView.cs:471` | `ControlType.Button` + Name `"Delete"` | `Tools_Workflow_{workflowId}_Delete_Button` | Deletes current leaf workflow template version. |
| TextBlock (`AgentTasksCountLabel`) | SettingsView | `src/PaRuX.App/Views/SettingsView.xaml:815` | `ControlType.Text` ending with `"task(s)"` | `Settings_AgentTasks_Count_Text` | Displays task count in Settings > Agents card. |
| Button (`RefreshAgentTasks_Click`) | SettingsView | `src/PaRuX.App/Views/SettingsView.xaml:816` | Sibling `ControlType.Button` with Name `"Refresh"` | `Settings_RefreshAgentTasks_Button` | Manually triggers refresh of Agent tasks list in Settings. |
| TextBlock (`AutomationsCountLabel`) | SettingsView | `src/PaRuX.App/Views/SettingsView.xaml:1094` | `ControlType.Text` ending with `"automation(s)"` | `Settings_Automations_Count_Text` | Displays automation count in Settings > Agents card. |
| Button (`RefreshAutomations_Click`) | SettingsView | `src/PaRuX.App/Views/SettingsView.xaml:1095` | Sibling `ControlType.Button` with Name `"Refresh"` | `Settings_RefreshAutomations_Button` | Manually triggers refresh of Automations list in Settings. |

## Architectural and UX Observations

1. **Programmatic C# Code-Behind Lacking Automation Properties**:
   `ToolsAutomationView` constructs its entire UI layout programmatically in C# without setting `AutomationProperties.AutomationId` or `AutomationProperties.Name` on interactive controls or containers. WinUI buttons expose their text `Content` as the UIA `Name`, but input boxes rely on `PlaceholderText` which is only visible to UIA when empty and not when populated with user input. Adding explicit AutomationIds will make automation testing and accessibility screen reader navigation considerably more resilient.

2. **Disambiguation of Sibling Refresh Buttons in SettingsView**:
   `SettingsView.xaml` contains identical `Content="Refresh"` buttons in both the Agent Tasks section (`src/PaRuX.App/Views/SettingsView.xaml:816`) and the Automations section (`src/PaRuX.App/Views/SettingsView.xaml:1095`). Because neither button sets an `AutomationProperties.AutomationId`, UI tests and assistive devices must discover the adjacent count label text and navigate via UIA tree walker parents. Explicit IDs such as `Settings_RefreshAgentTasks_Button` and `Settings_RefreshAutomations_Button` would disambiguate these actions cleanly.

3. **Validation Feedback and Screen Reader Politeness**:
   Validation failures in `ToolsAutomationView` (such as missing required fields for MCP, Automations, or Workflows) are rendered directly into the `_status` TextBlock synchronously. This avoids swallowed errors or unhandled exceptions, which is excellent. However, `_status` does not specify `AutomationProperties.LiveSetting = AutomationLiveSetting.Polite` or `Assertive`. Adding this attribute ensures screen readers immediately announce validation errors to users without requiring manual focus navigation.

4. **Workflow DAG Template Validation**:
   The workflow builder step editor pre-populates a valid 2-step JSON DAG (`Plan` and `Execute`). Submitting an invalid or circular dependency returns an error message originating from Core validation, which is safely presented in `_status.Text` without crashing the desktop client.

5. **RefreshComponents Label Stability Across Layouts**:
   The component detection refresh button `Settings_RefreshComponents_Button` in `src/PaRuX.App/Views/SettingsView.xaml:260` is labeled `"Refresh components"` in published main layouts and `"Detect"` in recent source revisions. Tests accept both labels to remain robust across published artifacts and active worktrees.
