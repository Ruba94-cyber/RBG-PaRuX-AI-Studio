# UI Automation Gaps: uit-33-install-progress-stages

This document records controls, locator gaps, and behavioral UX observations identified during the authoring and verification of the automated UI tests for install progress stages and presentation (`tests/PaRuX.UITests/Tests/InstallProgressPresentationCoverageTests.cs`).

## Controls Lacking AutomationId

While `FirstModelDownloadProgress_Header`, `FirstModelDownloadProgress_Status`, `Settings_RefreshComponents_Button`, `Settings_Components_Progress`, `Settings_Components_StatusText`, and `Settings_Components_List` provide stable `AutomationProperties.AutomationId` attributes, several controls in `RuntimeManagerView`, `OnboardingView`, and `SettingsView` lack explicit automation identifiers and had to be located by control type and text content:

| Control | Page / View | Source Location | Current Locator | Suggested `AutomationId` | Purpose / Impact |
|---|---|---|---|---|---|
| TextBlock (`title`) | RuntimeManagerView | `src/PaRuX.App/Views/RuntimeManagerView.cs:59` | `ControlType.Text` + Name `"Runtime Manager"` | `RuntimeManager_Title_TextBlock` | Workspace view title header. |
| Button (`refresh`) | RuntimeManagerView | `src/PaRuX.App/Views/RuntimeManagerView.cs:67` | `ControlType.Button` + Name `"Refresh"` | `RuntimeManager_Refresh_Button` | Refreshes runtime components and performance status. |
| Button (`cancel`) | RuntimeManagerView | `src/PaRuX.App/Views/RuntimeManagerView.cs:70` | `ControlType.Button` + Name `"Cancel active install"` | `RuntimeManager_CancelInstall_Button` | Cancels in-progress runtime installation or reports idle status. |
| ProgressBar (`_progress`) | RuntimeManagerView | `src/PaRuX.App/Views/RuntimeManagerView.cs:19` | `ControlType.ProgressBar` under view body | `RuntimeManager_Install_ProgressBar` | Visual feedback for determinate/indeterminate install progress. |
| TextBlock (`_status`) | RuntimeManagerView | `src/PaRuX.App/Views/RuntimeManagerView.cs:18` | `ControlType.Text` with status content | `RuntimeManager_Status_TextBlock` | Textual description of install/discovery stage or cancel remediation. |
| TextBlock (`Runtime components`) | RuntimeManagerView | `src/PaRuX.App/Views/RuntimeManagerView.cs:80` | `ControlType.Text` + Name `"Runtime components"` | `RuntimeManager_ComponentsSection_Header` | Section header for installed and available component cards. |
| StackPanel (`_components`) | RuntimeManagerView | `src/PaRuX.App/Views/RuntimeManagerView.cs:20` | Container child of scroll body | `RuntimeManager_Components_List` | Container holding individual runtime component cards. |
| TextBlock (`ComponentsStatusText`) | OnboardingView | `src/PaRuX.App/Views/OnboardingView.xaml:60` | `ControlType.Text` + Content prefix | `Onboarding_Components_StatusText` | Displays component discovery status and non-destructive detection guarantee. |
| ProgressBar (`ComponentsProgress`) | OnboardingView | `src/PaRuX.App/Views/OnboardingView.xaml:62` | `ControlType.ProgressBar` in card | `Onboarding_Components_ProgressBar` | Indeterminate progress indicator during component scanning. |
| StackPanel (`ComponentsList`) | OnboardingView | `src/PaRuX.App/Views/OnboardingView.xaml:63` | Container in components card | `Onboarding_Components_List` | List of detected and configurable runtime components. |
| StackPanel (`FirstModelDownloadRows`) | OnboardingView | `src/PaRuX.App/Views/OnboardingView.xaml:108` | Child stack panel under download progress | `Onboarding_FirstModel_DownloadRows` | Container for active download job rows or the empty state label. |
| Button (`categoryButton`) | SettingsView | `src/PaRuX.App/Views/SettingsView.xaml.cs:91` | `ControlType.Button` + Name `"Models"` | `Settings_Category_Models_Button` | Settings category rail selector for Models and runtime components. |

## Architectural and UX Observations

1. **Truthful Idle and Remediation Messaging**:
   Both `OnboardingView` and `RuntimeManagerView` handle the idle/uninstalled state cleanly without presenting false progress indicators, `0%`, `NaN`, or raw internal enum tokens (`PARUX_MANAGED`, `USER_MANAGED`, `SYSTEM_DETECTED`). When no model download is active in onboarding, `FirstModelDownloadProgress_Status` clearly explains that downloads must be initiated from Model Hub, and `FirstModelDownloadRows` displays `"No downloads yet."` rather than an empty or uninitialized progress bar. In `RuntimeManagerView`, clicking `"Cancel active install"` while idle immediately provides a clear remediation explanation (`"No Runtime Manager install is active in this view."`) without crashing or entering a faulted state.

2. **Plain Language Presentation Invariants**:
   `InstallProgressPresenter` and `ComponentLifecyclePresenter` consistently translate internal machine and enum codes to sentence-cased human terms (`"managed by PaRuX"`, `"managed by you"`, `"detected on this PC"`, `"not configured"`, `"skipped for now"`). These invariants are maintained across Onboarding, Runtime Manager, and Settings views.

3. **Active Download / Install Progression Testing Without Live Network**:
   In clean test environments where external downloads and real model fetches are disabled, live multi-stage progress (e.g. downloading bytes, speed calculations, ETA estimations, extraction, activation) cannot be observed end-to-end without a mock installation server or simulated progress injection fixture. Adding an environment-driven mock install fixture (similar to `PARUX_UI_TEST_CODING_TASK_CARD`) would enable FlaUI verification of determinate percentage progression, ETA formatting, and in-flight cancellation transitions.
