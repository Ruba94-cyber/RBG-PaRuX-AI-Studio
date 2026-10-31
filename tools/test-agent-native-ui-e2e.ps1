<#
.SYNOPSIS
Opt-in deterministic native UIA functional acceptance on a disposable WinForms target.
.DESCRIPTION
Builds an isolated fixture under artifacts, starts it as a tracked child process,
binds a native HWND to that PID, discovers exact selectors from that HWND's real
WinApp inspect tree, then delegates one actual button invocation and a separate
post-action value assertion to test-winapp-functional.ps1. A failed/unobserved
assertion never becomes PASS. Does not edit a user's repository or use GitHub Actions.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][switch]$ConfirmFunctionalTest,
    [switch]$RunRecoveryScenario,
    [string]$WinAppCli = 'winapp',
    [string]$OutputRoot
)
$ErrorActionPreference = 'Stop'
if (-not ([Environment]::OSVersion.Platform -eq [PlatformID]::Win32NT)) {
    throw 'Real Windows desktop and .NET SDK are required.'
}
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw '.NET 8 SDK is required for the disposable fixture.'
}
if (-not $ConfirmFunctionalTest) {
    throw 'An explicit opt-in is required for a UI mutation test.'
}
$cli = Get-Command -Name $WinAppCli -ErrorAction Stop
if ($cli.CommandType -ne 'Application' -or
    -not $cli.Source.EndsWith('.exe', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Specify the already-installed WinApp CLI .exe.'
}
$exe = $cli.Source
$repo = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $repo ('artifacts\validation\native-ui-e2e-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
}
New-Item -ItemType Directory -Path $OutputRoot -Force | Out-Null
$OutputRoot = (Resolve-Path -LiteralPath $OutputRoot).Path
$fixtureOutput = Join-Path $OutputRoot 'fixture'
$nonce = [guid]::NewGuid().ToString('N')
$target = Join-Path $repo 'tools\PaRuX.UiAcceptanceFixture\PaRuX.UiAcceptanceFixture.csproj'
$process = $null
$recovery = [ordered]@{
    scenario = 'disposable-native-uia-recovery'
    executedUtc = [DateTime]::UtcNow.ToString('o')
    initialNegative = 'NOT_RUN'
    firstPositive = 'NOT_RUN'
    resetPositive = 'NOT_RUN'
    postResetNegative = 'NOT_RUN'
    recoveryPositive = 'NOT_RUN'
    finalReadback = 'NOT_RUN'
    realAgentOrchestration = 'NOT_RUN'
}

# A negative scenario is acceptable only when WinApp explicitly reports a
# NOT-FOUND timeout for the known control. A malformed response or CLI error
# is never interpreted as proof of expected failure.
function Assert-ExpectedNotPresent {
    param([string]$Selector,[string]$WrongValue,[string]$Evidence)
    $outputFile = Join-Path $OutputRoot $Evidence
    $global:LASTEXITCODE = 0
    & $exe ui wait-for $Selector -w $window --value $WrongValue --timeout 800 --json *>&1 |
        Out-File -FilePath $outputFile -Encoding UTF8
    $rawProbe = [IO.File]::ReadAllText($outputFile)
    if ([string]::IsNullOrWhiteSpace($rawProbe) -or $rawProbe.Length -gt 131072) {
        throw 'Negative UIA probe returned missing or oversized evidence.'
    }
    try { $probe = ConvertFrom-Json -InputObject $rawProbe -ErrorAction Stop }
    catch { throw 'Negative UIA probe did not return structured JSON.' }
    if ($null -eq $probe -or $probe -is [array] -or $null -ne $probe.error
        -or $probe.found -cne $false -or $probe.timedOut -cne $true) {
        throw 'The negative UIA probe was not a genuine observed absence.'
    }
}

function Find-FixtureSelector {
    param($Elements, [string]$AutomationId)
    foreach ($element in @($Elements)) {
        if ($null -eq $element) { continue }
        $keys = @('automationId','automation_id','automationID','id','name')
        foreach ($key in $keys) {
            $property = $element.PSObject.Properties[$key]
            if ($null -ne $property -and [string]$property.Value -ceq $AutomationId) {
                $selector = [string]$element.selector
                if ($selector -match '^[A-Za-z0-9][A-Za-z0-9_-]{0,127}$') {
                    return $selector
                }
            }
        }
        $nested = Find-FixtureSelector -Elements $element.children -AutomationId $AutomationId
        if ($null -ne $nested) { return $nested }
    }
    return $null
}

try {
    $global:LASTEXITCODE = 0
    & dotnet publish $target -c Release -o $fixtureOutput --nologo *>&1 |
        Out-File -FilePath (Join-Path $OutputRoot 'fixture-build.log') -Encoding UTF8
    if ($LASTEXITCODE -ne 0) { throw 'Disposable native UI fixture failed to compile.' }
    $targetExe = Join-Path $fixtureOutput 'PaRuX.UiAcceptanceFixture.exe'
    if (-not (Test-Path -LiteralPath $targetExe)) { throw 'Fixture executable is missing.' }

    $process = Start-Process -FilePath $targetExe -ArgumentList @($nonce) -PassThru
    if ($null -eq $process -or $process.Id -le 0) { throw 'Fixture could not start.' }
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    $handle = [long]0
    while ([DateTime]::UtcNow -lt $deadline) {
        $process.Refresh()
        if ($process.HasExited) { throw 'Fixture exited before a native HWND was ready.' }
        $handle = $process.MainWindowHandle.ToInt64()
        if ($handle -gt 0 -and $process.MainWindowTitle -eq ('PaRuX Disposable UI Acceptance ' + $nonce)) {
            break
        }
        $handle = 0
        [Threading.Thread]::Sleep(150)
    }
    if ($handle -le 0) { throw 'Fixture did not create the expected task-specific window.' }
    $window = $handle.ToString([Globalization.CultureInfo]::InvariantCulture)

    $inspectPath = Join-Path $OutputRoot 'discover.json'
    $global:LASTEXITCODE = 0
    & $exe ui inspect -w $window --json *>&1 |
        Out-File -FilePath $inspectPath -Encoding UTF8
    if ($LASTEXITCODE -ne 0) { throw 'WinApp inspect failed for the fixture HWND.' }
    $raw = [IO.File]::ReadAllText($inspectPath)
    if ($raw.Length -gt 131072 -or [string]::IsNullOrWhiteSpace($raw)) {
        throw 'WinApp inspect output was empty or oversized.'
    }
    try { $tree = ConvertFrom-Json -InputObject $raw -ErrorAction Stop }
    catch { throw 'WinApp inspect response was not valid JSON.' }

    $action = $null
    $result = $null
    $reset = $null
    $ownerFound = $false
    foreach ($node in @($tree.windows)) {
        if ([string]$node.hwnd -cne $window) { continue }
        $ownerFound = $true
        $action = Find-FixtureSelector -Elements $node.elements -AutomationId 'parux-fixture-run'
        $result = Find-FixtureSelector -Elements $node.elements -AutomationId 'parux-fixture-result'
        if ($RunRecoveryScenario) {
            $reset = Find-FixtureSelector -Elements $node.elements -AutomationId 'parux-fixture-reset'
        }
        break
    }
    if (-not $ownerFound -or [string]::IsNullOrWhiteSpace($action) -or
        [string]::IsNullOrWhiteSpace($result)) {
        throw 'Fixture selectors were not independently observed under the exact HWND.'
    }
    if ($action -ceq $result) { throw 'Action and result controls cannot share a selector.' }
    if ($RunRecoveryScenario -and ([string]::IsNullOrWhiteSpace($reset)
        -or $reset -ceq $action -or $reset -ceq $result)) {
        throw 'The reset control must be independently observed and distinct.'
    }

    $expected = 'ACCEPTED-' + $nonce
    if ($RunRecoveryScenario) {
        Assert-ExpectedNotPresent -Selector $result -WrongValue $expected -Evidence 'initial-expected-absent.json'
        $recovery.initialNegative = 'PASS'
    }
    $functional = Join-Path $repo 'tools\test-winapp-functional.ps1'
    $global:LASTEXITCODE = 0
    & $functional -ConfirmFunctionalTest -WindowHwnd $handle -Action invoke `
        -Selector $action -ExpectedSelector $result -ExpectedValue $expected `
        -WinAppCli $exe -OutputRoot (Join-Path $OutputRoot 'functional') *>&1 |
        Out-File -FilePath (Join-Path $OutputRoot 'functional-run.log') -Encoding UTF8
    if ($LASTEXITCODE -ne 0) {
        throw 'The real WinApp invoke/wait-for/native UIA assertion failed.'
    }
    if ($RunRecoveryScenario) {
        $recovery.firstPositive = 'PASS'

        # Deliberately regress the observed control from ACCEPTED to PENDING.
        # The mutation is made via a second independently discovered control.
        $global:LASTEXITCODE = 0
        & $functional -ConfirmFunctionalTest -WindowHwnd $handle -Action invoke `
            -Selector $reset -ExpectedSelector $result -ExpectedValue 'PENDING' `
            -WinAppCli $exe -OutputRoot (Join-Path $OutputRoot 'reset') *>&1 |
            Out-File -FilePath (Join-Path $OutputRoot 'reset-run.log') -Encoding UTF8
        if ($LASTEXITCODE -ne 0) { throw 'The real UIA reset did not verify PENDING.' }
        $recovery.resetPositive = 'PASS'

        Assert-ExpectedNotPresent -Selector $result -WrongValue $expected -Evidence 'after-reset-expected-absent.json'
        $recovery.postResetNegative = 'PASS'

        # Recovery is a fresh action plus a fresh separately captured result.
        # It cannot reuse the earlier positive assertion after reset.
        $global:LASTEXITCODE = 0
        & $functional -ConfirmFunctionalTest -WindowHwnd $handle -Action invoke `
            -Selector $action -ExpectedSelector $result -ExpectedValue $expected `
            -WinAppCli $exe -OutputRoot (Join-Path $OutputRoot 'recovery') *>&1 |
            Out-File -FilePath (Join-Path $OutputRoot 'recovery-run.log') -Encoding UTF8
        if ($LASTEXITCODE -ne 0) { throw 'UIA recovery action did not verify a new ACCEPTED state.' }
        $recovery.recoveryPositive = 'PASS'

        # One last independent read, after the recovery's own assertion.
        $global:LASTEXITCODE = 0
        & $exe ui wait-for $result -w $window --value $expected --timeout 1000 --json *>&1 |
            Out-File -FilePath (Join-Path $OutputRoot 'final-readback.json') -Encoding UTF8
        if ($LASTEXITCODE -ne 0) { throw 'Final independent readback failed.' }
        try {
            $final = ConvertFrom-Json -InputObject (
                [IO.File]::ReadAllText((Join-Path $OutputRoot 'final-readback.json'))) -ErrorAction Stop
        }
        catch { throw 'Final independent readback did not return JSON.' }
        if ($null -eq $final -or $final.found -cne $true -or $final.timedOut -ceq $true
            -or $null -ne $final.error) {
            throw 'Final independent readback did not confirm the repaired control.'
        }
        $recovery.finalReadback = 'PASS'
    }
    $process.Refresh()
    if ($process.HasExited -or $process.MainWindowHandle.ToInt64() -ne $handle) {
        throw 'Native fixture changed identity during UI acceptance.'
    }
    Write-Output ('NATIVE UIA E2E PASS: bound fixture PID=' + $process.Id + '; HWND=' +
        $window + '; expected nonce=' + $nonce + '; recovery=' + [bool]$RunRecoveryScenario +
        '; evidence=' + $OutputRoot)
    if ($RunRecoveryScenario) {
        Write-Output 'The WinApp CLI fixture recovery passed, NOT the autonomous PaRuX agent loop.'
    }
}
catch {
    Write-Error ('NATIVE UIA E2E FAIL: ' + $_.Exception.Message + '; evidence=' + $OutputRoot)
    exit 1
}
finally {
    if ($RunRecoveryScenario) {
        $recovery.executedUtc = [DateTime]::UtcNow.ToString('o')
        $recovery | ConvertTo-Json -Depth 4 |
            Out-File -FilePath (Join-Path $OutputRoot 'native-recovery-status.json') -Encoding UTF8
    }
    if ($null -ne $process) {
        try {
            $process.Refresh()
            if (-not $process.HasExited) {
                Stop-Process -Id $process.Id -Force -ErrorAction Stop
                $null = $process.WaitForExit(5000)
            }
        }
        catch { Write-Warning 'Disposable test process cleanup needs manual inspection.' }
        finally { $process.Dispose() }
    }
}
