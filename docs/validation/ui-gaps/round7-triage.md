# Round 7 UI test triage

Scope: the 16 FlaUI coverage classes that `merge/round7` adds on top of `main` (8c59ef5d). Each class was run alone against the round7 published layout, then all new classes were run together. Evidence files are git-ignored under `.ai/artifacts/round7-verify/` and `TestResults/UIArtifacts/`.

Rules applied: a test is changed only when the test itself is wrong (selector, control type, wait, or stale expectation) and the intent is kept. A reproducible product defect keeps its expectation and is marked `Skip = "PRODUCT GAP: ..."`. No test is deleted and no assertion is weakened.

## Product gaps (expectation kept, test skipped)

### OnboardingReturnAndReadinessCoverageTests: Model Hub handoff

- Tests: `Return_banner_appears_only_during_model_hub_handoff_and_is_absent_initially`, `Return_to_setup_button_navigates_back_to_onboarding_and_dismisses_banner`, `Handoff_return_banner_persists_across_workspace_switches_and_returns_to_onboarding`.
- Observed: on the onboarding page, activating the first-model install button ("Find <starter> in Model Hub") does not start the handoff. UI Automation Invoke throws `ArgumentException` ("Value does not fall within the expected range"). No status text appears, the app does not navigate to Model Hub, and no return banner appears. The UI-test storage starter has an empty `HuggingFaceRepoId`, and `FirstModelInstall_Click` passes it to `OnboardingStateMachine.HandoffToModelHub`, whose `SelectFirstModel` throws `ArgumentException("Repo ID cannot be empty.")`. The click handler does not validate the repo id, so the handoff is never reached.
- Expected: activating a recommended starter hands off to Model Hub, shows the return banner, and returns to onboarding when the download completes. A recommendation without a usable repo id should show a recoverable message instead of throwing.
- Evidence: `.ai/artifacts/round7-verify/diag/onboarding-invoke-v10.txt` (Invoke throws; sibling "Choose by task" Invoke succeeds on the same row); `.ai/artifacts/round7-verify/trx/OnboardingReturnAndReadinessCoverageTests-run1/`; `TestResults/UIArtifacts/Return_banner_appears_only_during_model_hub_handoff_and_is_absent_initially-*/` (failure.txt, uia-tree.txt).
- Note for the owner: the mapping from the throw to the UI Automation error is inferred from the Invoke result, not from a captured stack trace. A one-line fixture change (a `HuggingFaceRepoId` on the DEBUG storage fixture model in `OnboardingView.FirstModel.cs`) would make the handoff reachable, but it is a product-source change and was not made during verification.

## Test corrections (test was wrong, intent kept)

- `MainTourCoverageTests` (5ae4c7cb): the test expected a stale first title ("This is where you work") and six steps. The shipped tour (`src/PaRuX.App/MainWindow.Tour.cs`) has eight steps, starting with "Chat is your home workspace" and ending with "Help and guided tour replay". The step list now mirrors the shipped sequence. All four tests pass.
- `InstallProgressPresentationCoverageTests` (0e140239): the Settings refresh button was asserted enabled before discovery settled, while the product disables it during discovery. The assertion now follows the settle wait. The Runtime Manager rows check now polls (bounded) for an ownership label, because a single read raced row rendering. All seven tests pass.
- `LocalOnlyAndGamingHonestyCoverageTests` (7843acfa): the cloud-fallback block sits below the fold of the Settings Models page. A UI Automation toggle on the unrealized switch was a silent no-op, and the on-screen checks never passed. The test now steps the enclosing ScrollViewer (bounded) until the switch and then the status or guidance text are on screen. All eight tests pass.
- `OnboardingReturnAndReadinessCoverageTests` (this change): Get Started was clicked before component detection settled, and the click was dropped; the test now waits for the settled component summary. The readiness card is a `Border`, which has no UI Automation peer, so its AutomationId was never exposed; the test now uses the exposed `UnprovenReadinessHeader`. Invoke on below-the-fold onboarding controls now re-acquires the control by AutomationId after the panes are scrolled to the end. The three readiness tests pass.
- `ProjectsWorkspaceCoverageTests` (408bc211): `FindProjectButton` returned the first button under the goal text's parent. Project rows have no UI Automation peers, so every project resolved to the first row's button (or the repository chooser). It now returns the Active/Select button that follows the goal text, and the row refreshes are awaited. One class run hit a 25 s timeout that did not reproduce alone or on re-run; it is recorded as a load-related flake.
- `ResponsiveLayoutCoverageTests` (cdde5181): `NavigateToWorkspace` clicked the nav entry first. The Runtime entry sits below the fold of the nav pane, so the click landed off target. It now selects through the SelectionItem pattern first, as `WorkspaceNavigationPage` does.
- `ToolsAutomationCoverageTests` (715bfaf6): the Settings refresh-components button was asserted enabled before discovery settled. The assertion now follows the settle wait.

## Vacuity flags (reported, not changed)

- `CoreUnavailableRecoveryCoverageTests.AssertNoModalDialogs` (used by `Core_exit_does_not_open_a_modal_dialog` and the cancellation test) reads `Window.ModalWindows`. WinUI ContentDialogs render inside the main window, so this check cannot observe them and passes regardless. It needs a dialog oracle before it can fail.
- `OnboardingReturnAndReadinessCoverageTests` absence checks on the return banner (lines 41-42, 78-79, 118-119) have no positive control in the non-skipped tests; the positive assertions live in the skipped handoff tests.
- `ClientIntegrationsCoverageTests`: the "Not installed" connect and launch absence checks run only when a client reports that state; the gateway-state check accepts any of four truthful states.
- `ToolsAutomationCoverageTests` (around line 82): `skillsEmpty != null || permissionButton != null` passes if either control exists.
- `MainTourCoverageTests`: `Assert.Equal(TourSteps[0].Title, firstTitle.Name)` is tautological because the element was located by that name; the target-visibility assertion next to it is meaningful.
- Harness: `UiTestApplication.FindRawDescendant` swallows predicate exceptions and stops after 5000 nodes, so a "not found" result is not proof of absence. `KeyboardTraversalPage.TabUntil` and `ShiftTabUntil` ignore their `maxTabs` parameter and are bounded by time (15 s) instead.

## Regression set (main-owned tests, not changed by round 7)

The round-7 layout keeps the `main` App sources (`git diff main...HEAD -- src/PaRuX.App` is empty), and no `SmokeTests.cs` or `NavigationLayoutReachabilityTests.cs` change is in the round.

- `SmokeTests`: 58 of 59 passed. `Chat_header_adapts_and_recent_history_is_bounded_when_the_window_is_resized` fails with "Create receives keyboard focus by Up-arrow navigation from Projects". The same test fails on the `main` baseline (8c59ef5d, detached worktree `ci-round7-baseline`) under the same host state, and it passed there in an earlier run when host memory was lower. Classified as an environment-sensitive focus test, not a round-7 regression. No change was kept: an attempted precondition fix (focus Projects first) did not make the test pass, so it was reverted.
- `NavigationLayoutReachabilityTests` (narrow 560x560): fails with "Navigation_Create_Item selected by mouse click (advanced/narrow)". It fails identically on the `main` baseline under the same host state. Classified as pre-existing on `main` under current host load, not a round-7 regression.
- `ExplicitDiscoveryTests`: 7 of 7 passed.

Host state during these runs: system RAM was 90 to 99 percent at times, because another local model server was loaded for a separate workload. The product's startup admission rejected warm-up work at that level (see the application log lines "resource admission rejected the workload").

## Isolation finding

The UI harness isolates `PARUX_DATA_ROOT` and `PARUX_LOCALAI_STATE_DIR`, but the product still auto-detects the user's real LocalAI folder under the Documents tree. During an onboarding test window, log files in that folder's `logs` directory were written (`gpu-detect.log`, `llama-server.stdout.log`, `llama-server.stderr.log`). Those timestamps suggest a local model server was launched from that folder during the test; the launching process was not captured, so this is an inference. The harness does not isolate that root. Recommended fix before further automated runs on this machine: an isolated LocalAI root override for UI tests (product or harness change, not made by the verifier).
