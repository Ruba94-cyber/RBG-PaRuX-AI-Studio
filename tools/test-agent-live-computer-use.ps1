<#
.SYNOPSIS
Explicitly opted-in, REAL PaRuX Computer Use agent end-to-end acceptance.
.DESCRIPTION
Builds a disposable WinForms fixture, discovers its native HWND and exact UIA
result selector, and creates ONE genuine Core task through the regular parux CLI.
The human MUST independently authorize the task in PaRuX's normal UI/paired
client. This script never changes settings, registers an app, grants permissions
or invokes UI mutations itself. PASS requires Core completed/pass, a bounded
executor GUI receipt and a final independent WinApp inspect value on the same
fixture HWND. Source-only/WinApp-only tests cannot satisfy this lane.

The task goal and synthetic nonce are fixture-specific. Run from an interactive
desktop with PaRuX Windows already running and a working agent model.
.EXAMPLE
powershell -ExecutionPolicy Bypass -File tools\test-agent-live-computer-use.ps1 -ConfirmLiveAgentTest -CliPath C:\path\to\parux.exe -WinAppCli winapp
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][switch]$ConfirmLiveAgentTest,
    [Parameter(Mandatory=$true)][string]$CliPath,
    [string]$WinAppCli = 'winapp',
    [ValidateRange(60,1800)][int]$TimeoutSeconds = 600,
    [string]$OutputRoot
)
$ErrorActionPreference = 'Stop'
if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
    throw 'A real interactive Windows desktop is mandatory.'
}
if (-not $ConfirmLiveAgentTest -or -not [Environment]::UserInteractive) {
    throw 'Explicit opt-in and an interactive user authorization are required.'
}
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw '.NET 8 SDK is required to build the disposable WinForms fixture.'
}

$cli = (Resolve-Path -LiteralPath $CliPath -ErrorAction Stop).Path
if (-not (Test-Path -LiteralPath $cli -PathType Leaf) -or
    -not $cli.EndsWith('.exe', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Pass the absolute path of the built native PaRuX CLI executable.'
}
$winappCommand = Get-Command -Name $WinAppCli -ErrorAction Stop
if ($winappCommand.CommandType -ne 'Application'
    -or -not $winappCommand.Source.EndsWith('.exe', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'WinApp CLI must be an already installed native .exe.'
}
$winapp = $winappCommand.Source

$repo = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $repo ('artifacts\validation\live-agent-gui-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
}
New-Item -ItemType Directory -Path $OutputRoot -Force | Out-Null
$OutputRoot = (Resolve-Path -LiteralPath $OutputRoot).Path
$buildRoot = Join-Path $OutputRoot 'fixture'
$fixtureProject = Join-Path $repo 'tools\PaRuX.UiAcceptanceFixture\PaRuX.UiAcceptanceFixture.csproj'
$nonce = [guid]::NewGuid().ToString('N')
$fixture = $null
$taskId = $null
$taskTerminal = $false
$window = [long]0
$failureMessage = $null
$report = [ordered]@{
    scenario = 'real-parux-computer-use-agent-e2e'
    schemaVersion = 1
    outcome = 'NOT_RUN'
    reasonCode = 'preflight_not_complete'
    executionMode = 'real-core-and-windows-worker'
    cliStatus = 'NOT_RUN'
    originalGuiControlNegative = 'NOT_RUN'
    agentTaskCreated = 'NOT_RUN'
    userAuthorization = 'NOT_RUN'
    coreVerifier = 'NOT_RUN'
    workerGuiReceipt = 'NOT_RUN'
    nativePostcondition = 'NOT_RUN'
    taskId = $null
    gitCommit = 'UNKNOWN'
    executedUtc = [DateTime]::UtcNow.ToString('o')
}
try {
    $commit = (& git -C $repo rev-parse HEAD 2>$null | Select-Object -First 1)
    if ($LASTEXITCODE -eq 0 -and $commit -match '^[a-f0-9]{40}$') {
        $report.gitCommit = $commit
    }
} catch { }

# Do not record raw model messages, authentication bootstrap, full task JSON,
# screenshots, native process paths or control contents in the public report.
function Read-BoundedWinAppTree {
    param([long]$Hwnd)
    $global:LASTEXITCODE = 0
    $rawLines = @(& $winapp ui inspect -w ([string]$Hwnd) --interactive --json 2>&1)
    if ($LASTEXITCODE -ne 0) { throw 'WinApp inspect failed.' }
    $raw = $rawLines -join [Environment]::NewLine
    if ([string]::IsNullOrWhiteSpace($raw) -or $raw.Length -gt 131072) {
        throw 'WinApp observation was empty or oversized.'
    }
    try { $tree = ConvertFrom-Json -InputObject $raw -ErrorAction Stop }
    catch { throw 'WinApp UIA observation was not JSON.' }
    if ($null -eq $tree -or $tree -is [array] -or
        $null -eq $tree.windows -or $null -ne $tree.error) {
        throw 'WinApp UIA returned no trusted windows array.'
    }
    $owners = @($tree.windows | Where-Object { [string]$_.hwnd -ceq [string]$Hwnd })
    if ($owners.Count -ne 1) { throw 'The observed HWND was missing or duplicated.' }
    return $owners[0]
}

function Find-ControlsByAutomationId {
    param($Elements,[string]$AutomationId)
    $found = [System.Collections.Generic.List[object]]::new()
    foreach ($element in @($Elements)) {
        if ($null -eq $element) { continue }
        $matched = $false
        foreach ($key in @('automationId','automation_id','automationID','id','name')) {
            $prop = $element.PSObject.Properties[$key]
            if ($null -ne $prop -and [string]$prop.Value -ceq $AutomationId) {
                $matched = $true
                break
            }
        }
        if ($matched) { $found.Add($element) }
        if ($null -ne $element.children) {
            foreach ($child in @(Find-ControlsByAutomationId -Elements $element.children -AutomationId $AutomationId)) {
                $found.Add($child)
            }
        }
    }
    return $found.ToArray()
}

function Read-OneResultValue {
    param([long]$Hwnd,[string]$AutomationId,[string]$Selector)
    $owner = Read-BoundedWinAppTree -Hwnd $Hwnd
    $matches = @(Find-ControlsByAutomationId -Elements $owner.elements -AutomationId $AutomationId)
    if ($matches.Count -ne 1 -or [string]$matches[0].selector -cne $Selector) {
        throw 'The result selector is missing, ambiguous or changed.'
    }
    if ($null -eq $matches[0].value) { throw 'Result control has no observed UIA value.' }
    return [string]$matches[0].value
}

function Get-CoreTask {
    param([string]$Id)
    $global:LASTEXITCODE = 0
    # Raw stdout contains the task's goal/result. Keep it only in memory.
    $lines = @(& $cli agent show $Id --json 2>&1)
    if ($LASTEXITCODE -ne 0) { throw 'Local PaRuX CLI could not read its own Core task.' }
    $raw = $lines -join [Environment]::NewLine
    if ($raw.Length -gt 131072) { throw 'Task response exceeded the local acceptance bound.' }
    try { $task = ConvertFrom-Json -InputObject $raw -ErrorAction Stop }
    catch { throw 'Local CLI did not produce a machine-readable Core task.' }
    if ([string]$task.id -cne $Id -or [string]$task.role -cne 'computer-use') {
        throw 'Core returned the wrong task identity or role.'
    }
    return $task
}

try {
    $global:LASTEXITCODE = 0
    & $cli status *> $null
    if ($LASTEXITCODE -ne 0) { throw 'PaRuX Core is not running; launch PaRuX Windows first.' }
    $report.cliStatus = 'PASS'

    $global:LASTEXITCODE = 0
    & dotnet publish $fixtureProject -c Release -o $buildRoot --nologo *>&1 |
        Out-File -FilePath (Join-Path $OutputRoot 'fixture-build.log') -Encoding UTF8
    if ($LASTEXITCODE -ne 0) { throw 'Disposable fixture did not compile.' }
    $fixtureExe = Join-Path $buildRoot 'PaRuX.UiAcceptanceFixture.exe'
    if (-not (Test-Path -LiteralPath $fixtureExe -PathType Leaf)) {
        throw 'The disposable test executable was not produced.'
    }
    $fixture = Start-Process -FilePath $fixtureExe -ArgumentList @($nonce) -PassThru
    if ($null -eq $fixture -or $fixture.Id -le 0) { throw 'Fixture failed to launch.' }
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    while ([DateTime]::UtcNow -lt $deadline) {
        $fixture.Refresh()
        if ($fixture.HasExited) { throw 'Fixture terminated before its HWND became available.' }
        $window = $fixture.MainWindowHandle.ToInt64()
        if ($window -gt 0 -and
            $fixture.MainWindowTitle -ceq ('PaRuX Disposable UI Acceptance ' + $nonce)) { break }
        [Threading.Thread]::Sleep(150)
    }
    if ($window -le 0) { throw 'Fixture did not expose a unique native window.' }

    $owner = Read-BoundedWinAppTree -Hwnd $window
    $run = @(Find-ControlsByAutomationId -Elements $owner.elements -AutomationId 'parux-fixture-run')
    $result = @(Find-ControlsByAutomationId -Elements $owner.elements -AutomationId 'parux-fixture-result')
    if ($run.Count -ne 1 -or $result.Count -ne 1 -or
        [string]::IsNullOrWhiteSpace([string]$run[0].selector) -or
        [string]::IsNullOrWhiteSpace([string]$result[0].selector)) {
        throw 'Fixture must expose unique Run and Result selectors.'
    }
    $selector = [string]$result[0].selector
    if ($selector -notmatch '^[A-Za-z0-9][A-Za-z0-9_-]{0,127}$') {
        throw 'Result selector was not valid for Computer Use.'
    }
    if ((Read-OneResultValue -Hwnd $window -AutomationId 'parux-fixture-result' -Selector $selector) -cne 'PENDING') {
        throw 'The fixture must start with PENDING; no agent mutation may predate task creation.'
    }
    $report.originalGuiControlNegative = 'PASS'

    Write-Output 'Disposable fixture started. To continue, use PaRuX Windows Computer Use Settings:'
    Write-Output '1. Keep Full Computer Use disabled. Use task-scoped approval.'
    Write-Output '2. Find the running PaRuX Disposable UI Acceptance app and explicitly permit its executable.'
    Write-Output '3. Keep PaRuX Windows running and a local agent model available.'
    Write-Output '4. When prompted during execution, approve ONLY the task targeting this disposable fixture.'
    Write-Output ('Fixture executable for review: ' + $fixtureExe)
    $answer = Read-Host 'After approving the fixture executable (not the task yet), type TEST to create the real task'
    if ($answer -cne 'TEST') { throw 'User declined the disposable live agent test.' }

    # The task goal is scoped to a single disposable window and contains only
    # synthetic test nonce, exact observed selector and a single result value.
    # A model can read the desired value, but only genuine UIA proof and the
    # native observed value can establish it was actually reached.
    $expected = 'ACCEPTED-' + $nonce
    $goal = 'Test ONLY the already running disposable Windows application titled PaRuX Disposable UI Acceptance ' + $nonce +
        '. If this window is not in the foreground, use focus_window with the exact title ' +
        'PaRuX Disposable UI Acceptance ' + $nonce +
        ' (this app must already be user-approved). Press its Run acceptance action button, ' +
        'then use ui_assert on the observed result selector ' + $selector +
        ' with property value and exact expected value ' + $expected +
        '. Report done only after the exact UI assertion passes. ' +
        'Do not open apps, use browser or shell, or interact with unrelated windows and private content.'
    $global:LASTEXITCODE = 0
    $createLines = @(& $cli agent run $goal --role computer-use --max-repair 0 --detach 2>&1)
    if ($LASTEXITCODE -ne 0) { throw 'PaRuX CLI failed to create a real Computer Use task.' }
    $created = ($createLines -join [Environment]::NewLine)
    $match = [regex]::Match($created,'(?m)^Agent task:\s*([a-f0-9]{32})\s*· role computer-use')
    if (-not $match.Success) { throw 'Could not read task ID from the CLI response.' }
    $taskId = $match.Groups[1].Value
    $report.taskId = $taskId
    $report.agentTaskCreated = 'PASS'
    Write-Output ('Core created genuine task ' + $taskId + '. Authorize this task in PaRuX Windows or paired device.')
    Write-Output 'Do not click Run or Reset manually; the test must observe the real agent doing it.'

    $end = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    $task = $null
    while ([DateTime]::UtcNow -lt $end) {
        [Threading.Thread]::Sleep(1500)
        $task = Get-CoreTask -Id $taskId
        # Claiming alone does not prove which authority was used.
        # Final acceptance requires the worker's per-task approval label.
        if ([string]$task.status -cin @('completed','failed','cancelled','blocked')) {
            $taskTerminal = $true
            break
        }
        $fixture.Refresh()
        if ($fixture.HasExited -or $fixture.MainWindowHandle.ToInt64() -ne $window) {
            throw 'Fixture process/window changed during the agent test.'
        }
    }
    if (-not $taskTerminal) { throw 'Agent did not reach a terminal Core state within the bounded test horizon.' }
    if ([string]$task.status -cne 'completed' -or [string]$task.verificationStatus -cne 'pass') {
        throw 'Core did not independently verify this Computer Use task as completed/pass.'
    }
    $report.coreVerifier = 'PASS'
    if ([string]$task.goal -cne $goal) {
        throw 'Core task goal changed during live acceptance.'
    }
    if ([string]::IsNullOrWhiteSpace([string]$task.resultJson)) {
        throw 'Core recorded no worker execution receipt.'
    }
    try { $resultJson = ConvertFrom-Json -InputObject ([string]$task.resultJson) -ErrorAction Stop }
    catch { throw 'Core stored an invalid worker result JSON.' }
    if ($resultJson.authorization -cne 'task-scoped-remote-approval') {
        throw 'Live acceptance requires a specifically approved task; Full Computer Use is not valid test authority.'
    }
    $report.userAuthorization = 'PASS'
    $receipt = $resultJson.guiRunReceipt
    if ($null -eq $receipt
        -or $receipt.schemaVersion -ne 1
        -or $receipt.outcome -cne 'ready_for_independent_verifier'
        -or $receipt.assurance -cne 'executor-reported-not-independently-attested'
        -or $receipt.guiTestRequired -cne $true
        -or $receipt.readyForIndependentVerifier -cne $true
        -or $receipt.finalControlReadbackVerified -cne $true
        -or $receipt.uiAssertionsPassed -lt 1
        -or $receipt.finalReadbackChecks -lt 1
        -or $receipt.actionsDelivered -lt 1
        -or $receipt.actionsDelivered -gt $receipt.actionsAttempted
        -or $receipt.stepsTaken -gt 15
        -or $resultJson.guiFinalReadbackVerified -cne $true) {
        throw 'Worker receipt did not report actual bounded GUI actions and a final exact control check.'
    }
    $report.workerGuiReceipt = 'PASS'

    $fixture.Refresh()
    if ($fixture.HasExited -or $fixture.MainWindowHandle.ToInt64() -ne $window) {
        throw 'Disposable test process/window changed before independent postcondition readback.'
    }
    if ((Read-OneResultValue -Hwnd $window -AutomationId 'parux-fixture-result' -Selector $selector) -cne $expected) {
        throw 'Independent WinApp CLI readback did not confirm the result in the original fixture window.'
    }
    $report.nativePostcondition = 'PASS'
    $report.outcome = 'PASS'
    $report.reasonCode = 'real_agent_core_and_native_postcondition_verified'
    Write-Output ('REAL PARUX GUI AGENT E2E PASS: ' + $taskId)
    Write-Output 'Only this disposable task passed; this is not universal app compatibility.'
}
catch {
    $report.outcome = 'FAIL'
    $report.reasonCode = 'required_evidence_missing_or_invalid'
    $failureMessage = $_.Exception.Message
}
finally {
    # Only cancel this test's own unfinished task; never touch unrelated tasks
    # or persistent authorization settings, even after errors/timeouts.
    if ($null -ne $taskId -and -not $taskTerminal) {
        try { & $cli agent stop $taskId *> $null } catch { }
    }
    if ($null -ne $fixture) {
        try {
            $fixture.Refresh()
            if (-not $fixture.HasExited) {
                Stop-Process -Id $fixture.Id -Force -ErrorAction Stop
                $null = $fixture.WaitForExit(5000)
            }
        }
        catch { Write-Warning 'Disposable fixture cleanup needs manual inspection.' }
        finally { $fixture.Dispose() }
    }
    $report.executedUtc = [DateTime]::UtcNow.ToString('o')
    $report | ConvertTo-Json -Depth 5 |
        Out-File -FilePath (Join-Path $OutputRoot 'real-agent-acceptance.json') -Encoding UTF8
}

if ($report.outcome -ne 'PASS') {
    # Throw only AFTER finally persisted the sanitized status file and stopped
    # resources owned by this test. Do not print the raw Core task/result.
    throw ('REAL PARUX GUI AGENT E2E FAIL: ' + $failureMessage +
        '; sanitized status=' + (Join-Path $OutputRoot 'real-agent-acceptance.json'))
}
