# uit-26-main-tour UI gaps

The new FlaUI coverage starts the tour from Settings, checks all six ordered titles and their existing visible target controls, and covers Next, Skip, Finish, completion persistence, and restart behavior.

| Control | Page | Suggested AutomationId |
| --- | --- | --- |
| “Show me around” button | Settings, General | `Settings_GuidedTour_Show_Button` |
| Runtime-created TeachingTip | Main window | `MainWindow_GuidedTour_Tip` |
| TeachingTip title | Main window | `MainWindow_GuidedTour_Title_Text` |
| TeachingTip action button | Main window | `MainWindow_GuidedTour_Action_Button` |
| TeachingTip close button | Main window | `MainWindow_GuidedTour_Close_Button` |

The Settings button has no explicit `AutomationProperties.AutomationId`. The TeachingTip and its generated title/action/close controls also receive no stable IDs in app code. Tests locate those controls by ControlType and exact source-backed text. The target controls already have IDs; the tests verify each target remains visible while its step is shown. The tests do not verify the TeachingTip-to-target anchor relationship or visual placement. The popup's exact UIA scope and control types also remain unverified until the supervisor runs against the published layout.

Back navigation is absent: the tour only advances its index, and the app creates no Back action. Escape dismissal also has no explicit app handler; `IsLightDismissEnabled` is false. Neither behavior is asserted because the current source does not provide those interactions. Add a Back control (with `MainWindow_GuidedTour_Back_Button`) or explicit Escape handling before adding those acceptance tests.

The test fixture pre-seeds `OnboardingCompleted` as true. The tests therefore cover the Settings entry point, not the first-run completion handoff that invokes `MaybeStartGuidedTour`. The restart check confirms that the completion flag persists in the same data root and that no open tip appears after relaunch; it cannot exercise automatic first-run tour suppression because the fixture does not trigger that handoff.
