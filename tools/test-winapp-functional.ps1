<#
.SYNOPSIS
Opt-in real Windows UIA behavioural assertion against an already-running TEST app.

.DESCRIPTION
Use only with an isolated/disposable test app. This script DOES change UI state:
it invokes one explicitly selected control or sets one explicit value, then waits
for a separately specified UIA element and expected value. It never starts the
target app, chooses selectors through AI, executes arbitrary shell commands,
approves a PaRuX agent, or claims a UI screenshot alone is a functional PASS.

.EXAMPLE
powershell -ExecutionPolicy Bypass -File tools\test-winapp-functional.ps1 -ConfirmFunctionalTest -WindowHwnd 131906 -Action invoke -Selector btn-run-a1b2 -ExpectedSelector lbl-status-c3d4 -ExpectedValue Done
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][switch]$ConfirmFunctionalTest,
    [Parameter(Mandatory=$true)][ValidateRange(1, [long]::MaxValue)][long]$WindowHwnd,
    [Parameter(Mandatory=$true)][ValidateSet('invoke','set-value')][string]$Action,
    [Parameter(Mandatory=$true)][string]$Selector,
    [string]$NewValue,
    [Parameter(Mandatory=$true)][string]$ExpectedSelector,
    [Parameter(Mandatory=$true)][string]$ExpectedValue,
    [ValidateRange(500,30000)][int]$TimeoutMs=5000,
    [string]$WinAppCli='winapp',
    [string]$OutputRoot
)

$ErrorActionPreference = 'Stop'
if (-not ([System.Environment]::OSVersion.Platform -eq [System.PlatformID]::Win32NT)) {
    throw 'Real functional UIA testing requires Windows.'
}
if (-not $ConfirmFunctionalTest) {
    throw 'Functional UI mutation requires explicit -ConfirmFunctionalTest.'
}
foreach ($control in @($Selector,$ExpectedSelector)) {
    if ($control -notmatch '^[A-Za-z0-9][A-Za-z0-9_-]{0,127}$') {
        throw 'Control selectors must be explicit observed UIA identifiers.'
    }
}
if ($Action -eq 'set-value' -and $null -eq $NewValue) {
    throw 'A set-value test requires -NewValue.'
}
if ($NewValue.Length -gt 512 -or $ExpectedValue.Length -gt 512) {
    throw 'Value exceeded the bounded test input size.'
}
$cli = Get-Command -Name $WinAppCli -ErrorAction Stop
if ($cli.CommandType -ne 'Application' -or
    -not $cli.Source.EndsWith('.exe', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'WinApp CLI must resolve to a native .exe, not a script, alias or batch file.'
}
$exe = $cli.Source
$repo = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $repo ('artifacts\validation\winapp-functional-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
}
New-Item -ItemType Directory -Path $OutputRoot -Force | Out-Null
$OutputRoot = (Resolve-Path -LiteralPath $OutputRoot).Path
$window = $WindowHwnd.ToString([System.Globalization.CultureInfo]::InvariantCulture)

function Read-UiEvidence {
    param([Parameter(Mandatory=$true)][string]$Name)
    $file = Join-Path $OutputRoot $Name
    $raw = [IO.File]::ReadAllText($file)
    if ([string]::IsNullOrWhiteSpace($raw) -or $raw.Length -gt 131072) {
        throw ('WinApp evidence was empty or oversized: ' + $Name)
    }
    try { $doc = ConvertFrom-Json -InputObject $raw -ErrorAction Stop }
    catch { throw ('WinApp did not return valid JSON: ' + $Name) }
    if ($null -eq $doc -or $doc -is [array] -or $doc -is [string]) {
        throw ('WinApp evidence was not a JSON object: ' + $Name)
    }
    if ($null -ne $doc.error -or $doc.success -ceq $false -or $doc.ok -ceq $false) {
        throw ('WinApp returned an action/error denial: ' + $Name)
    }
    return $doc
}

function Test-ElementSelector {
    param($Elements,[string]$Desired)
    foreach ($element in @($Elements)) {
        if ($null -eq $element) { continue }
        if ($element.selector -ceq $Desired) { return $true }
        if ($null -ne $element.children -and (Test-ElementSelector -Elements $element.children -Desired $Desired)) {
            return $true
        }
    }
    return $false
}

function Assert-UiInspect {
    param([string]$Name,[string[]]$Selectors)
    $observation = Read-UiEvidence -Name $Name
    if ($null -eq $observation.windows) { throw ('WinApp inspect had no windows: ' + $Name) }
    $owner = $false
    foreach ($entry in @($observation.windows)) {
        if ($null -eq $entry -or $null -eq $entry.hwnd) { continue }
        if ([string]$entry.hwnd -cne $window) { continue }
        $owner = $true
        foreach ($selector in $Selectors) {
            if (-not (Test-ElementSelector -Elements $entry.elements -Desired $selector)) {
                throw ('Expected UIA selector not observed in matching window: ' + $selector)
            }
        }
    }
    if (-not $owner) { throw ('WinApp inspect did not identify requested HWND: ' + $Name) }
}

function Invoke-Ui {
    param([string[]]$Arguments,[string]$Evidence)
    $global:LASTEXITCODE = 0
    & $exe @Arguments 2>&1 | Out-File -FilePath (Join-Path $OutputRoot $Evidence) -Encoding UTF8
    if ($LASTEXITCODE -ne 0) {
        throw ('WinApp UIA command failed: ' + $Evidence + ' exit=' + $LASTEXITCODE)
    }
    $null = Read-UiEvidence -Name $Evidence
}

$mutate = @('ui', $Action, $Selector)
if ($Action -eq 'set-value') { $mutate += $NewValue }
$mutate += @('-w', $window, '--json')

try {
    Invoke-Ui -Arguments @('ui','inspect','-w',$window,'--interactive','--json') -Evidence 'before.json'
    Assert-UiInspect -Name 'before.json' -Selectors @($Selector)
    Invoke-Ui -Arguments $mutate -Evidence 'action.json'
    Invoke-Ui -Arguments @('ui','wait-for',$ExpectedSelector,'-w',$window,
        '--value',$ExpectedValue,'--timeout',([string]$TimeoutMs),'--json') -Evidence 'assertion.json'
    $assertion = Read-UiEvidence -Name 'assertion.json'
    if ($assertion.found -cne $true -or $assertion.timedOut -ceq $true) {
        throw 'WinApp wait-for returned no positive state assertion (found:true, timedOut:false).'
    }
    Invoke-Ui -Arguments @('ui','inspect','-w',$window,'--json') -Evidence 'after.json'
    Assert-UiInspect -Name 'after.json' -Selectors @($ExpectedSelector)
    Write-Output ('FUNCTIONAL UIA PASS: independently asserted control=' + $ExpectedSelector + '; evidence=' + $OutputRoot)
}
catch {
    Write-Error ('FUNCTIONAL UIA FAIL: ' + $_.Exception.Message + '; evidence=' + $OutputRoot)
    exit 1
}
