# UI gaps: uit-28-core-down-recovery

The previous supervisor gate run did not demonstrate Core recovery. After the test terminated its isolated Core process and submitted a chat prompt, the app log reported `PaRuX Server startup timed out`. The captured UIA tree showed the chat still working and no startup-status text; main-window UIA calls continued to answer. The repaired tests cover only the observable behaviors they can verify and make no successful-recovery claim.

## Gaps

- `ChatView`'s `StatusText` `TextBlock` has no stable `AutomationId`. The source sets its startup text to `Starting PaRuX Server…`, but the supervisor's failure snapshot did not expose the status element. Suggested id: `Chat_Status_Text` (page: Chat).
- The current UI does not expose a persistent Core-unavailable banner or a clear failure status in the observed failed-start state. The tests cannot verify that Core-dependent actions explain the failure or that the same controls work after a successful recovery.
- The current test setup does not inject a deterministic failed Core startup. Repeated failed-start handling and stacked dialogs therefore remain unverified; the tests only cover the single exit and cancellation paths that are observable without claiming a recovery.
