# UI Automation Gaps: uit-12-projects-workspace

This document records controls, locator gaps, and behavioral UX observations identified during the authoring and verification of the Projects workspace automated UI tests (`tests/PaRuX.UITests/Tests/ProjectsWorkspaceCoverageTests.cs`).

## Controls Lacking AutomationId

`ProjectsWorkspaceView` (`src/PaRuX.App/Views/ProjectsWorkspaceView.cs`) is implemented purely in C# code-behind and currently sets no `AutomationProperties.AutomationId` or explicit `AutomationProperties.Name` on its visual elements. The automated tests locate controls through `ControlType` and verified textual content or element hierarchy.

| Control | Page / View | Source Location | Current Locator | Suggested `AutomationId` | Purpose / Impact |
|---|---|---|---|---|---|
| TextBlock (`ProjectsTitle`) | ProjectsWorkspaceView | `src/PaRuX.App/Views/ProjectsWorkspaceView.cs:62` | `ControlType.Text` + Name `"Projects & Artifacts"` | `Projects_Title_Text` | Main workspace title header. |
| TextBlock (`ProjectsDescription`) | ProjectsWorkspaceView | `src/PaRuX.App/Views/ProjectsWorkspaceView.cs:65` | `ControlType.Text` + Name prefix `"Select a durable Project"` | `Projects_Description_Text` | Workspace purpose explanation and scoping declaration. |
| TextBlock (`_active`) | ProjectsWorkspaceView | `src/PaRuX.App/Views/ProjectsWorkspaceView.cs:25` | `ControlType.Text` + Name prefix `"Active project:"` | `Projects_ActiveProject_Text` | Displays currently selected project and ID, or unselected guidance. |
| TextBlock (`_repository`) | ProjectsWorkspaceView | `src/PaRuX.App/Views/ProjectsWorkspaceView.cs:26` | `ControlType.Text` + Name prefix `"Select a Project"` or `"Session repository:"` | `Projects_Repository_Status_Text` | Displays session repository path or selection prerequisite guidance. |
| Button (`_chooseRepository`) | ProjectsWorkspaceView | `src/PaRuX.App/Views/ProjectsWorkspaceView.cs:27` | `ControlType.Button` + Name `"Choose Project repository…"` | `Projects_ChooseRepository_Button` | Opens folder picker to assign desktop Git repository authority. |
| Button (`_clearRepository`) | ProjectsWorkspaceView | `src/PaRuX.App/Views/ProjectsWorkspaceView.cs:28` | `ControlType.Button` + Name `"Clear session repository"` | `Projects_ClearRepository_Button` | Clears current session repository from active workspace. |
| TextBlock (`_status`) | ProjectsWorkspaceView | `src/PaRuX.App/Views/ProjectsWorkspaceView.cs:24` | `ControlType.Text` + Name prefix `"Project workspace"` or `"Created and selected"` | `Projects_Status_Text` | Operational feedback for project creation, refresh and errors. |
| TextBox (`_goal`) | ProjectsWorkspaceView | `src/PaRuX.App/Views/ProjectsWorkspaceView.cs:29` | `ControlType.Edit` (non-chat, visible) | `Projects_Goal_TextBox` | Goal entry box for creating a new durable Project. |
| Button (`createButton`) | ProjectsWorkspaceView | `src/PaRuX.App/Views/ProjectsWorkspaceView.cs:85` | `ControlType.Button` + Name `"Create Project"` | `Projects_CreateProject_Button` | Submits goal to create and select a new durable Project. |
| Button (`_chooseNewProjectRepository`) | ProjectsWorkspaceView | `src/PaRuX.App/Views/ProjectsWorkspaceView.cs:31` | `ControlType.Button` + Name `"Choose repository for new Project…"` | `Projects_ChooseNewProjectRepository_Button` | Folder picker for repository before project creation. |
| Button (`_clearNewProjectRepository`) | ProjectsWorkspaceView | `src/PaRuX.App/Views/ProjectsWorkspaceView.cs:32` | `ControlType.Button` + Name `"Clear"` | `Projects_ClearNewProjectRepository_Button` | Resets chosen new-project repository before creation. |
| TextBlock (`_newProjectRepository`) | ProjectsWorkspaceView | `src/PaRuX.App/Views/ProjectsWorkspaceView.cs:30` | `ControlType.Text` + Name prefix `"New Project repository:"` | `Projects_NewProjectRepository_Text` | Reports repository path staged for upcoming project creation. |
| StackPanel (`_projects`) | ProjectsWorkspaceView | `src/PaRuX.App/Views/ProjectsWorkspaceView.cs:34` | Tree position under `"PROJECTS"` section | `Projects_List_StackPanel` | Container hosting project rows or empty state. |
| TextBlock (`EmptyProjects`) | ProjectsWorkspaceView | `src/PaRuX.App/Views/ProjectsWorkspaceView.cs:149` | `ControlType.Text` + Name `"No durable Projects yet."` | `Projects_EmptyProjects_Text` | Empty state notice when no durable projects exist. |
| Button (`select`) | ProjectsWorkspaceView | `src/PaRuX.App/Views/ProjectsWorkspaceView.cs:143` | Sibling `ControlType.Button` with Name `"Active"` or `"Select"` | `Projects_Select_{projectId}_Button` | Activates project in workspace session or indicates active state. |
| TextBlock (`ScopedArtifacts`) | ProjectsWorkspaceView | `src/PaRuX.App/Views/ProjectsWorkspaceView.cs:299` | `ControlType.Text` + Name `"Select a Project to see scoped artifacts."` | `Projects_ScopedArtifacts_Empty_Text` | Empty guidance when active project has no linked artifacts or none selected. |
| TextBlock (`AllArtifacts`) | ProjectsWorkspaceView | `src/PaRuX.App/Views/ProjectsWorkspaceView.cs:304` | `ControlType.Text` + Name `"Artifact Workspace is empty."` | `Projects_AllArtifacts_Empty_Text` | Empty state notice when artifact workspace contains no items. |

## Architectural and UX Observations

1. **Silent Early Return on Empty Goal Input**:
   - In `ProjectsWorkspaceView.cs:154-155`:
     ```csharp
     string goal = (_goal.Text ?? string.Empty).Trim();
     if (goal.Length == 0) return;
     ```
     When a user clicks "Create Project" with an empty or whitespace-only goal, the method silently returns without updating `_status.Text` or giving inline validation feedback (e.g., "Please enter a project goal"). This leaves the user without visible explanation for why nothing happened.
2. **WinRT FolderPicker Dependency Without Fallback Path Input**:
   - In `ProjectsWorkspaceView.cs:176-180` and `211-215`, choosing a project repository directly launches the native WinRT `FolderPicker`. There is no text entry fallback to paste or type a valid local directory path. For accessibility tools, automated pipelines, or headless/remote desktop sessions, adding an editable path field alongside the picker button would significantly improve testability and keyboard operation.
3. **TextBox Accessible Name and Screen Reader Support**:
   - The goal `TextBox` (`_goal`) defines only `PlaceholderText = "Goal for a new durable Project"` without setting `AutomationProperties.Name` or an explicit `<TextBox Header="..." />`. Assistive technologies may present the input as an unnamed edit control once user text replaces the placeholder.
4. **Project Selection In-Memory Persistence and Boundary**:
   - `ProjectWorkspaceSession.Current` manages active project selection in memory. Navigation away from Projects to Settings or Chat and back preserves the active selection within the session. Switching projects explicitly clears the session repository root to avoid cross-project contamination of filesystem authority.
