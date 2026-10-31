<#
.SYNOPSIS
Reproducible Windows validation of the current PaRuX checkout and its agent platform.
.DESCRIPTION
Runs real xUnit and optional Release/FlaUI/WinApp UIA checks, preserving each exit
code as independent PASS/FAIL/NOT_RUN. Never counts an unrun hardware test as success.
.EXAMPLE
powershell -ExecutionPolicy Bypass -File tools\test-agent-platform-v2.ps1
.EXAMPLE
powershell -ExecutionPolicy Bypass -File tools\test-agent-platform-v2.ps1 -RunReleaseGate -RunFlaUI -WinAppTarget PaRuX
.EXAMPLE
powershell -ExecutionPolicy Bypass -File tools\test-agent-platform-v2.ps1 -RunNativeUiRecovery
.EXAMPLE
powershell -ExecutionPolicy Bypass -File tools\test-agent-platform-v2.ps1 -RunNativeDialogE2E
.EXAMPLE
powershell -ExecutionPolicy Bypass -File tools\test-agent-platform-v2.ps1 -RunNativeModalE2E
.EXAMPLE
powershell -ExecutionPolicy Bypass -File tools\test-agent-platform-v2.ps1 -RunLiveComputerUseE2E -CliPath C:\tools\parux.exe
#>
[CmdletBinding()]
param(
    [switch]$RunReleaseGate,
    [switch]$RunFlaUI,
    [switch]$RunNativeUiE2E,
    [switch]$RunNativeUiRecovery,
    [switch]$RunNativeDialogE2E,
    [switch]$RunNativeModalE2E,
    [switch]$RunLiveComputerUseE2E,
    [string]$CliPath,
    [switch]$RunBrowserE2E,
    [string]$BrowserExecutable,
    [string]$WinAppCli = 'winapp',
    [switch]$CaptureWinAppScreenshot,
    [string]$WinAppTarget,
    [string]$AppPath,
    [string]$OutputRoot
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $repo
if (-not ([System.Environment]::OSVersion.Platform -eq [System.PlatformID]::Win32NT)) {
    throw 'Agent Platform V2 acceptance requires an interactive Windows host.'
}
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw '.NET SDK is not installed; no xUnit or release test can be accepted.'
}

if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $repo ('artifacts\validation\agent-platform-v2-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
}
New-Item -ItemType Directory -Path $OutputRoot -Force | Out-Null
$OutputRoot = (Resolve-Path -LiteralPath $OutputRoot).Path
# Record the actual checked-out source, never a historical branch label.
# A PASS from the wrong revision must not be mistaken for acceptance of main.
$currentBranch = 'UNKNOWN'
$currentCommit = 'UNKNOWN'
if (Get-Command git -ErrorAction SilentlyContinue) {
    try {
        $foundBranch = (& git -C $repo rev-parse --abbrev-ref HEAD 2>$null | Select-Object -First 1)
        if ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($foundBranch)) {
            $currentBranch = $foundBranch.Trim()
        }
        $foundCommit = (& git -C $repo rev-parse HEAD 2>$null | Select-Object -First 1)
        if ($LASTEXITCODE -eq 0 -and $foundCommit -match '^[a-f0-9]{40}$') {
            $currentCommit = $foundCommit
        }
    }
    catch {
        # A Git checkout is not required merely to run test binaries; unknown
        # source provenance must remain explicit rather than invented.
    }
}
$results = [ordered]@{
    branch = $currentBranch
    commit = $currentCommit
    executedUtc = [DateTime]::UtcNow.ToString('o')
    scriptSyntax = 'NOT_RUN'
    unit = 'NOT_RUN'
    browserDomJs = 'NOT_RUN'
    releaseGate = 'NOT_RUN'
    flaui = 'NOT_RUN'
    nativeUiFunctional = 'NOT_RUN'
    nativeUiRecovery = 'NOT_RUN'
    nativeUiDialog = 'NOT_RUN'
    nativeUiModal = 'NOT_RUN'
    liveComputerUseAgent = 'NOT_RUN'
    browserCdpFunctional = 'NOT_RUN'
    winappUia = 'NOT_RUN'
    physicalGpuAndLongContext = 'NOT_RUN'
    selfImprovementDogfood = 'NOT_RUN'
}

function Invoke-Gate {
    param([string]$Name, [scriptblock]$Action, [string]$LogName)
    $log = Join-Path $OutputRoot $LogName
    try {
        $global:LASTEXITCODE = 0
        & $Action *>&1 | Out-File -FilePath $log -Encoding UTF8
        if ($LASTEXITCODE -ne 0) {
            $results[$Name] = 'FAIL'
            return
        }
        $results[$Name] = 'PASS'
    }
    catch {
        $results[$Name] = 'FAIL'
        $_ | Out-String | Out-File -FilePath $log -Append -Encoding UTF8
    }
}

try {
    Invoke-Gate -Name scriptSyntax -LogName 'agent-script-syntax.log' -Action {
        & (Join-Path $repo 'tools\test-agent-validation-syntax.ps1')
    }
    Invoke-Gate -Name unit -LogName 'agent-v2-xunit.log' -Action {
        dotnet test tests\PaRuX.Server.Tests\PaRuX.Server.Tests.csproj -c Debug --filter 'FullyQualifiedName~CodingAgentPromptBudgetPolicyTests|FullyQualifiedName~CodingAgentCheckpointStoreTests|FullyQualifiedName~CodingWorkspaceVerificationFingerprintTests|FullyQualifiedName~CodingWorkspaceGitLayoutPolicyTests|FullyQualifiedName~CodingRepositoryAliasPolicyTests|FullyQualifiedName~CodingBrowserPostconditionPolicyTests|FullyQualifiedName~CodingVerificationCommandPolicyTests|FullyQualifiedName~CodingVerificationManifestPolicyTests|FullyQualifiedName~CodingVerificationDiscoveryTests|FullyQualifiedName~CodingTaskApplyAdmissionPolicyTests|FullyQualifiedName~CodingAgentObservationPolicyTests|FullyQualifiedName~OpenCodeLocalProbeTests|FullyQualifiedName~WindowsUiActionServiceTests|FullyQualifiedName~WindowsUiAssertionPolicyTests|FullyQualifiedName~ComputerUseAppSelectionPolicyTests|FullyQualifiedName~ComputerUseAppLaunchPolicyTests|FullyQualifiedName~ComputerUseRunningAppDiscoveryTests|FullyQualifiedName~ComputerUseGuiTestEvidenceGateTests|FullyQualifiedName~ComputerUseGuiFinalProofTests|FullyQualifiedName~ComputerUseGuiProofLedgerTests|FullyQualifiedName~ComputerUseGuiRepairBudgetTests|FullyQualifiedName~ComputerUseGuiRecoveryContractTests|FullyQualifiedName~ComputerUseGuiRunTraceTests|FullyQualifiedName~ComputerUseGuiReceiptPresenterTests|FullyQualifiedName~ComputerUseResultVerifierGateTests|FullyQualifiedName~ComputerUseWindowSelectionPolicyTests|FullyQualifiedName~ComputerUseWindowFocusPolicyTests|FullyQualifiedName~ComputerUseTaskAppLeaseTests|FullyQualifiedName~ComputerUseSensitiveActionPolicyTests|FullyQualifiedName~WindowsUiTreeObservationServiceTests|FullyQualifiedName~UiCaptureProvenancePolicyTests|FullyQualifiedName~UiVerificationEvidenceGateTests|FullyQualifiedName~CodingResultVerifierGateTests|FullyQualifiedName~VerifierServiceTests|FullyQualifiedName~ComputerUseActionParserTests|FullyQualifiedName~ComputerUseCompletionGateTests|FullyQualifiedName~LocalSearxngSearchProviderTests|FullyQualifiedName~CodingWebSearchPolicyTests|FullyQualifiedName~CodingWebSearchEvidenceFormatterTests|FullyQualifiedName~CodingWebSearchEndpointTests|FullyQualifiedName~OpenCodeSessionObserverTests|FullyQualifiedName~OpenCodeSessionEndpointTests|FullyQualifiedName~CodingAgentBackendSelectionPolicyTests|FullyQualifiedName~BrowserDevToolsEvaluationPolicyTests|FullyQualifiedName~WindowsUiActionResultPolicyTests|FullyQualifiedName~CodingRenderedPromptCountContractTests|FullyQualifiedName~CodingPromptTokenCountEndpointTests|FullyQualifiedName~RuntimeTokenCounterTests|FullyQualifiedName~BrowserRedirectObservationPolicyTests|FullyQualifiedName~BrowserRequestAdmissionPolicyTests|FullyQualifiedName~BrowserLiteralAddressPolicyTests|FullyQualifiedName~BrowserAttachedTargetPolicyTests|FullyQualifiedName~BrowserDevToolsNavigationResultPolicyTests|FullyQualifiedName~BrowserNetworkEvidencePolicyTests|FullyQualifiedName~CodingCompletionClaimPolicyTests|FullyQualifiedName~CodingAgentProactiveContextPolicyTests|FullyQualifiedName~CodingProjectInstructionResolverTests|FullyQualifiedName~CodingStepStageReporterTests|FullyQualifiedName~BrowserDomActionScriptBuilderTests|FullyQualifiedName~BrowserDomActionResultPolicyTests|FullyQualifiedName~BrowserDomAssertionPolicyTests|FullyQualifiedName~BrowserDomAssertionResultPolicyTests|FullyQualifiedName~BrowserUiMutationPolicyTests|FullyQualifiedName~BrowserTaskOwnerLeaseTests|FullyQualifiedName~CodingTaskRunLeaseTests|FullyQualifiedName~CodeWorktreeApplyServiceTests|FullyQualifiedName~MultiRepositoryApplyServiceTests|FullyQualifiedName~ShadowWorkspaceServiceTests|FullyQualifiedName~ShadowApplyTransactionTests|FullyQualifiedName~ShadowApplyRecoveryInspectorTests|FullyQualifiedName~ShadowApplyRecoveryServiceTests|FullyQualifiedName~ShadowRecoveryDialogModelTests|FullyQualifiedName~ShadowApplyPathPolicyTests'
    }
    # browserDomJs is a required gate, not an opt-in one. Without Node its two suites never execute, so the
    # gate must read FAIL (the exit logic below then fails the run) rather than stay NOT_RUN and exit 0.
    Invoke-Gate -Name browserDomJs -LogName 'browser-dom-js.log' -Action {
        if (-not (Get-Command node -ErrorAction SilentlyContinue)) {
            throw 'Node.js is not on PATH, so tools/test-browser-dom-assertion.mjs and tools/test-browser-agent-fixture.mjs cannot run.'
        }
        node --test tools/test-browser-dom-assertion.mjs tools/test-browser-agent-fixture.mjs
    }
    if ($RunReleaseGate) {
        Invoke-Gate -Name releaseGate -LogName 'agent-v2-release-gate.log' -Action {
            & (Join-Path $repo 'tools\release-gate.ps1') -Restore
        }
    }
    if ($RunFlaUI) {
        Invoke-Gate -Name flaui -LogName 'agent-v2-flaui.log' -Action {
            if ([string]::IsNullOrWhiteSpace($AppPath)) {
                & (Join-Path $repo 'tools\test-ui.ps1') -Suite Regression
            } else {
                & (Join-Path $repo 'tools\test-ui.ps1') -Suite Regression -AppPath $AppPath
            }
        }
    }
    if ($RunBrowserE2E) {
        Invoke-Gate -Name browserCdpFunctional -LogName 'browser-cdp-functional.log' -Action {
            if ([string]::IsNullOrWhiteSpace($BrowserExecutable)) {
                throw 'RunBrowserE2E requires -BrowserExecutable with an installed Edge/Chrome executable.'
            }
            node tools/test-browser-cdp-e2e.mjs $BrowserExecutable
        }
    }
    if ($RunNativeUiE2E) {
        Invoke-Gate -Name nativeUiFunctional -LogName 'native-ui-functional.log' -Action {
            & (Join-Path $repo 'tools\test-agent-native-ui-e2e.ps1') -ConfirmFunctionalTest `
                -WinAppCli $WinAppCli -OutputRoot (Join-Path $OutputRoot 'native-ui')
        }
    }
    if ($RunNativeUiRecovery) {
        Invoke-Gate -Name nativeUiRecovery -LogName 'native-ui-recovery.log' -Action {
            & (Join-Path $repo 'tools\test-agent-native-ui-e2e.ps1') -ConfirmFunctionalTest `
                -RunRecoveryScenario -WinAppCli $WinAppCli `
                -OutputRoot (Join-Path $OutputRoot 'native-ui-recovery')
        }
    }
    if ($RunNativeDialogE2E) {
        Invoke-Gate -Name nativeUiDialog -LogName 'native-ui-dialog.log' -Action {
            & (Join-Path $repo 'tools\test-agent-native-ui-e2e.ps1') -ConfirmFunctionalTest `
                -RunDialogScenario -WinAppCli $WinAppCli `
                -OutputRoot (Join-Path $OutputRoot 'native-ui-dialog')
        }
    }
    if ($RunNativeModalE2E) {
        Invoke-Gate -Name nativeUiModal -LogName 'native-ui-modal.log' -Action {
            & (Join-Path $repo 'tools\test-agent-native-modal-e2e.ps1') -ConfirmModalTest `
                -WinAppCli $WinAppCli `
                -OutputRoot (Join-Path $OutputRoot 'native-ui-modal')
        }
    }
    if ($RunLiveComputerUseE2E) {
        Invoke-Gate -Name liveComputerUseAgent -LogName 'live-computer-use-agent.log' -Action {
            if ($results.scriptSyntax -ne 'PASS') {
                throw 'Real agent E2E is refused because the required PowerShell syntax gate did not pass.'
            }
            if ([string]::IsNullOrWhiteSpace($CliPath)) {
                throw '-RunLiveComputerUseE2E requires -CliPath pointing to parux.exe.'
            }
            & (Join-Path $repo 'tools\test-agent-live-computer-use.ps1') `
                -ConfirmLiveAgentTest -CliPath $CliPath -WinAppCli $WinAppCli `
                -OutputRoot (Join-Path $OutputRoot 'live-agent-gui')
        }
    }
    if (-not [string]::IsNullOrWhiteSpace($WinAppTarget)) {
        Invoke-Gate -Name winappUia -LogName 'agent-v2-winapp-ui.log' -Action {
            $parameters = @{
                AppName = $WinAppTarget
                OutputRoot = (Join-Path $OutputRoot 'winapp-evidence')
            }
            if ($CaptureWinAppScreenshot) { $parameters.CaptureScreenshot = $true }
            & (Join-Path $repo 'tools\test-winapp-ui.ps1') @parameters
        }
    }
}
finally {
    $results.executedUtc = [DateTime]::UtcNow.ToString('o')
    $path = Join-Path $OutputRoot 'validation-status.json'
    $results | ConvertTo-Json -Depth 5 | Out-File -FilePath $path -Encoding UTF8
    Write-Output ('Agent Platform V2 validation report: ' + $path)
    foreach ($key in @('scriptSyntax', 'unit', 'browserDomJs', 'releaseGate', 'flaui', 'nativeUiFunctional', 'nativeUiRecovery', 'nativeUiDialog', 'nativeUiModal', 'liveComputerUseAgent', 'browserCdpFunctional', 'winappUia', 'physicalGpuAndLongContext', 'selfImprovementDogfood')) {
        Write-Output ($key + ': ' + $results[$key])
    }
}

if ($results.scriptSyntax -ne 'PASS' -or
    $results.unit -ne 'PASS' -or
    $results.browserDomJs -eq 'FAIL' -or
    ($RunReleaseGate -and $results.releaseGate -ne 'PASS') -or
    ($RunFlaUI -and $results.flaui -ne 'PASS') -or
    ($RunNativeUiE2E -and $results.nativeUiFunctional -ne 'PASS') -or
    ($RunNativeUiRecovery -and $results.nativeUiRecovery -ne 'PASS') -or
    ($RunNativeDialogE2E -and $results.nativeUiDialog -ne 'PASS') -or
    ($RunNativeModalE2E -and $results.nativeUiModal -ne 'PASS') -or
    ($RunLiveComputerUseE2E -and $results.liveComputerUseAgent -ne 'PASS') -or
    ($RunBrowserE2E -and $results.browserCdpFunctional -ne 'PASS') -or
    (-not [string]::IsNullOrWhiteSpace($WinAppTarget) -and $results.winappUia -ne 'PASS')) {
    exit 1
}
exit 0
