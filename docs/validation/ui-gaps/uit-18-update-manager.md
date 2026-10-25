# Update Manager UI automation gaps

## Missing stable AutomationIds

`UpdateManagerView` builds these controls in C# without assigning `AutomationId` or `AutomationProperties.AutomationId`. FlaUI currently locates them by control type and source-verified accessible name/text.

| Page/control | Suggested AutomationId |
| --- | --- |
| Update Manager heading | `Updates_Heading_Text` |
| Check again button | `Updates_CheckAgain_Button` |
| Busy progress bar | `Updates_Busy_ProgressBar` |
| Busy/result status text | `Updates_Status_Text` |
| Installed version text | `Updates_InstalledVersion_Text` |
| Release discovery result text | `Updates_ReleaseStatus_Text` |
| Secure installer availability explanation | `Updates_SecureInstallerStatus_Text` |
| Verified MSI install action, when rendered | `Updates_InstallVerifiedMsi_Button` |
| Component card title/ownership | `Updates_Component_{componentId}_Title_Text` |
| User-managed ownership explanation | `Updates_Component_{componentId}_Ownership_Text` |
| Managed component check/update action | `Updates_Component_{componentId}_Update_Button` |
| Managed component rollback action | `Updates_Component_{componentId}_Rollback_Button` |

The app install action is omitted unless release discovery and secure installer verification both succeed. In the tested unknown/offline state the test confirms the action is absent or disabled and checks the explanation, but the current UI does not expose a disabled control with a reason.

The user-managed test verifies the component title and the explicit statement that Update Manager will not modify the location. It does not assert that update/rollback buttons are absent within that component: in the supervisor UIA run, the raw-view ancestor selected around the ownership title also contained a `Check / update…` button, so UIA did not provide a reliable component boundary for that assertion. Per-component AutomationIds would make that relationship testable.

## Uncovered states

- The tests route release requests through a loopback HTTP proxy that rejects the app's HTTPS CONNECT tunnel with HTTP 502. The app consequently reports its transport/unverifiable result; this does not simulate a physically disconnected network, a direct HTTP feed response, or a successful release response.
- A verified update, downloaded MSI, elevation prompt, and installation handoff are not exercised. They require a signed release fixture or a real release/download and are outside this isolated offline UI check.
- The installed app version is asserted non-empty. No latest published version is expected while availability is unknown.
- Runtime build installation and rollback are not exercised. User-managed ownership is covered with a disposable ComfyUI location under the isolated UI test data root.
