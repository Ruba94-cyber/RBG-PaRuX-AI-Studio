# UI Automation Gaps: uit-31-onboarding-return-readiness

This document records controls, locator gaps, and behavioral UX observations identified during the authoring and verification of the Onboarding return banner and unproven-readiness card automated UI tests (`tests/PaRuX.UITests/Tests/OnboardingReturnAndReadinessCoverageTests.cs`).

## Controls Lacking AutomationId

While `MainWindow_OnboardingReturn_Banner`, `MainWindow_ReturnToOnboarding_Button`, `FirstModelInstallButton`, `UnprovenReadinessCard`, `UnprovenReadinessHeader`, and `UnprovenReadinessText` provide stable `AutomationProperties.AutomationId` attributes, several controls involved in the onboarding completion and unproven readiness dialog flow lack explicit automation identifiers:

| Control | Page / View | Source Location | Current Locator | Suggested `AutomationId` | Purpose / Impact |
|---|---|---|---|---|---|
| Button (`GetStartedButton`) | OnboardingView | `src/PaRuX.App/Views/OnboardingView.xaml:146` | `ControlType.Button` + Name `"Get Started"` | `Onboarding_GetStarted_Button` | Primary action button to complete onboarding or trigger unproven readiness confirmation. |
| TextBlock (`SetupSummaryText`) | OnboardingView | `src/PaRuX.App/Views/OnboardingView.xaml:117` | `ControlType.Text` + Content prefix | `Onboarding_SetupSummary_Text` | Displays real-time summary of runtime readiness and unproven status. |
| ContentDialog (`dialog`) | OnboardingView | `src/PaRuX.App/Views/OnboardingView.xaml.cs:600` | ContentDialog + Title `"Unproven Chat Readiness"` | `Onboarding_UnprovenReadiness_Dialog` | Modal confirmation dialog explaining unverified requirements before proceeding. |
| Button (`PrimaryButton`) | OnboardingView | `src/PaRuX.App/Views/OnboardingView.xaml.cs:608` | `ControlType.Button` + Name `"Proceed anyway"` | `Onboarding_ProceedAnyway_Button` | Explicit user override action to proceed into Chat despite unverified readiness. |
| Button (`CloseButton`) | OnboardingView | `src/PaRuX.App/Views/OnboardingView.xaml.cs:609` | `ControlType.Button` + Name `"Go back"` | `Onboarding_GoBack_Button` | Action to cancel completion and remain on the onboarding setup page. |

## Architectural and UX Observations

1. **Programmatic ContentDialog Automation Properties**:
   The `Unproven Chat Readiness` confirmation dialog is instantiated programmatically in `src/PaRuX.App/Views/OnboardingView.xaml.cs:600`. WinUI 3 `ContentDialog` exposes primary and close buttons whose default UIA Name matches their button text (`"Proceed anyway"` and `"Go back"`), but neither the dialog frame nor its buttons set `AutomationProperties.AutomationId`. Setting explicit IDs would simplify automated UI testing and provide reliable navigation anchors for assistive technologies.

2. **Handoff Model Download Follower in Automated Testing**:
   `MainWindow.Workspaces.cs` starts an onboarding download follower timer (`StartOnboardingDownloadFollower()`) upon handoff to Model Hub. When a model download completes, `OnboardingReturnAction.ReturnToSetup` is emitted and the user is automatically transitioned back to Onboarding. In automated test environments where network downloads are disabled, testing the automatic return upon download completion requires a dedicated download follower test fixture. The manual return mechanism via `MainWindow_ReturnToOnboarding_Button` operates reliably without downloads.

3. **Honesty and Explicit User Override**:
   `OnboardingView` strictly enforces verified readiness: when local text runtime, model, or probe verification are missing, clicking `GetStartedButton` never completes silently. Instead, `UnprovenReadinessCard` is made visible with a clear bulleted breakdown of unverified prerequisites, and the user must explicitly choose `"Proceed anyway"` in a dedicated confirmation dialog before `OnboardingCompleted` is persisted.
