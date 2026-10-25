# PaRuX Windows UI tests

`PaRuX.UITests` is a project-local FlaUI UIA3 suite. It launches the real `PaRuX.exe`, drives its WinUI controls, and stores per-failure diagnostics under `TestResults/UIArtifacts/`. Each child process gets isolated `PARUX_DATA_ROOT` and `PARUX_LOCALAI_STATE_DIR` paths, so UI tests cannot mutate the user's conversations, runtime PID/profile markers or settings. It never uses another repository or shared test helper.

## Prerequisites

- Windows interactive desktop session; do not run while the desktop is locked.
- .NET 8 SDK and the WinUI prerequisites needed by PaRuX.
- No other PaRuX instance running. Each test creates a new temporary `PARUX_DATA_ROOT`, so user settings, chats and models are not modified.

## Commands

```powershell
# Fast UI smoke (real PaRuX process)
powershell -ExecutionPolicy Bypass -File tools\test-ui.ps1 -Suite Smoke

# All non-hardware UI regression tests
powershell -ExecutionPolicy Bypass -File tools\test-ui.ps1 -Suite Regression

# Reserved opt-in hardware lane. No HardwareE2E tests exist yet, so it exits non-zero with NOT_RUN
# rather than pretending that GPU, Computer Use, or external engines were executed.
powershell -ExecutionPolicy Bypass -File tools\test-ui.ps1 -Suite HardwareE2E

# Direct xUnit filtering
dotnet test tests\PaRuX.UITests\PaRuX.UITests.csproj --filter "Category=Smoke"
```

Set `PARUX_UI_APP_PATH` or pass `-AppPath` to test a published/MSI-layout executable. UI tests are serial by design because the Windows desktop, focus and input are shared resources.

## Adding coverage

Use AutomationId as the stable UI-test API (`Area_Feature_ControlType`), then centralize lookup and behavior in a Page Object. Do not use localized text, item indexes or coordinates when an AutomationId is available. Hardware/model/Computer Use workflows must use the `HardwareE2E` trait and remain opt-in.

## Diagnostics and troubleshooting

On failure the harness records UTC timestamp, test step, PID, active window, exception, a screenshot and a UIA control-tree summary. If startup fails, confirm that `PaRuX.exe` was built under the expected x64 Debug output or provide `-AppPath`. File pickers, UAC, network/model downloads and real hardware inference are deliberately outside Smoke.
