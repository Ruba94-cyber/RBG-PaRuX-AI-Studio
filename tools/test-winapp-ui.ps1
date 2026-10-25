<#
.SYNOPSIS
Read-only Windows UI Automation smoke test with Microsoft's optional WinApp CLI.

.DESCRIPTION
Runs UIA status and interactive-control inspection against an explicitly named, already
running application. Saves raw JSON evidence locally under ignored artifacts/validation.
It does NOT install WinApp CLI, start apps, click, type, run shell supplied by a model,
approve permissions, or change PaRuX Computer Use settings. Screenshot is opt-in
because capture may foreground the window and may include private screen content.

.EXAMPLE
powershell -ExecutionPolicy Bypass -File tools\test-winapp-ui.ps1 -AppName notepad

.EXAMPLE
powershell -ExecutionPolicy Bypass -File tools\test-winapp-ui.ps1 -AppName PaRuX -CaptureScreenshot

.EXAMPLE
powershell -ExecutionPolicy Bypass -File tools\test-winapp-ui.ps1 -AppName MyTestApp -OnSandbox
#>
[CmdletBinding()]
param(
    [ValidateNotNullOrEmpty()]
    [string]$AppName = 'notepad',
    [ValidateNotNullOrEmpty()]
    [string]$WinAppCli = 'winapp',
    [switch]$OnSandbox,
    [switch]$CaptureScreenshot,
    [string]$OutputRoot
)

$ErrorActionPreference = 'Stop'
if (-not $IsWindows -and $PSVersionTable.PSEdition -eq 'Core') {
    throw 'WinApp UI Automation requires Windows.'
}

$resolved = Get-Command -Name $WinAppCli -ErrorAction Stop
if ($resolved.CommandType -ne 'Application') {
    throw 'The WinApp CLI must resolve to a native executable; aliases and scripts are refused.'
}
$executable = $resolved.Source
if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
    throw 'The selected WinApp executable does not exist.'
}

$repo = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $repo ('artifacts\validation\winapp-ui-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
}
New-Item -ItemType Directory -Path $OutputRoot -Force | Out-Null
$targetRoot = (Resolve-Path -LiteralPath $OutputRoot).Path
$target = $AppName.Trim()
if ($target.Length -gt 256) { throw 'The target app name is too long.' }

function Invoke-UiReadOnly {
    param([Parameter(Mandatory=$true)][string]$Verb,
          [Parameter(Mandatory=$true)][string[]]$OtherArgs)
    $arguments = @('ui', $Verb) + $OtherArgs
    if ($OnSandbox) { $arguments += @('--on', 'sandbox') }
    $output = & $executable @arguments 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw ("WinApp UIA " + $Verb + " failed (exit " + $LASTEXITCODE + "): " + ($output | Out-String))
    }
    return ($output | Out-String)
}

$status = Invoke-UiReadOnly -Verb 'status' -OtherArgs @('-a', $target, '--json')
[System.IO.File]::WriteAllText((Join-Path $targetRoot 'status.json'), $status, [System.Text.UTF8Encoding]::new($false))
$inspection = Invoke-UiReadOnly -Verb 'inspect' -OtherArgs @('-a', $target, '--interactive', '--json')
[System.IO.File]::WriteAllText((Join-Path $targetRoot 'inspection.json'), $inspection, [System.Text.UTF8Encoding]::new($false))

# Validate the results are parseable; tool failures must never count as a passing UI smoke.
try {
    $null = ConvertFrom-Json -InputObject $status -ErrorAction Stop
    $null = ConvertFrom-Json -InputObject $inspection -ErrorAction Stop
}
catch {
    throw 'WinApp CLI returned unparseable UIA JSON; the smoke test is NOT PASS.'
}

if ($CaptureScreenshot) {
    $screenshot = Join-Path $targetRoot 'screenshot.png'
    $null = Invoke-UiReadOnly -Verb 'screenshot' -OtherArgs @('-a', $target, '--output', $screenshot, '--json')
    if (-not (Test-Path -LiteralPath $screenshot -PathType Leaf)) {
        throw 'Screenshot capture did not create its output file.'
    }
}

Write-Output ("UIA SMOKE PASS: target=" + $target + "; sandbox=" + [bool]$OnSandbox +
    "; screenshot=" + [bool]$CaptureScreenshot + "; evidence=" + $targetRoot)
Write-Output 'This only establishes successful read-only UIA inspection; no end-to-end app behaviour was tested.'
