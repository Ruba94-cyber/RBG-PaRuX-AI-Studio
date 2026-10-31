<#
.SYNOPSIS
Opt-in native WinForms MODAL-dialog acceptance (not a PaRuX agent run).
.DESCRIPTION
Uses one disposable per-run fixture PID; Win32 proves distinct native HWND,
GW_OWNER relation, modal parent disabled/enabled transition and WinApp checks
an action on the modal followed by a fresh main-window UIA readback.
Never touches persistent PaRuX settings or external processes.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][switch]$ConfirmModalTest,
    [string]$WinAppCli = 'winapp',
    [string]$OutputRoot
)
$ErrorActionPreference = 'Stop'
if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
    throw 'Native modal test requires interactive Windows.'
}
if (-not $ConfirmModalTest -or -not [Environment]::UserInteractive) {
    throw 'An explicit opt-in and an interactive desktop are required.'
}
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw '.NET SDK must be installed.'
}
$command = Get-Command -Name $WinAppCli -ErrorAction Stop
if ($command.CommandType -ne 'Application' -or
    -not $command.Source.EndsWith('.exe', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'WinApp CLI must be an installed executable.'
}
$exe = $command.Source
$repo = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $repo ('artifacts\validation\native-modal-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
}
New-Item -ItemType Directory -Path $OutputRoot -Force | Out-Null
$OutputRoot = (Resolve-Path -LiteralPath $OutputRoot).Path
$nonce = [guid]::NewGuid().ToString('N')
$fixture = $null
$failureMessage = $null
$status = [ordered]@{
    scenario = 'disposable-owned-modal-dialog'
    outcome = 'NOT_RUN'
    mainWindowFound = 'NOT_RUN'
    modalWindowOwnedByMainPid = 'NOT_RUN'
    parentDisabledWhileModal = 'NOT_RUN'
    modalConfirmDelivered = 'NOT_RUN'
    parentReenabled = 'NOT_RUN'
    mainFinalUiaReadback = 'NOT_RUN'
    realAgentOrchestration = 'NOT_RUN'
    executedUtc = [DateTime]::UtcNow.ToString('o')
}

if (-not ('PaRuXTestOnly.ModalWin32' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
namespace PaRuXTestOnly {
    public static class ModalWin32 {
        private delegate bool Callback(IntPtr hwnd, IntPtr unused);
        [DllImport("user32.dll")] private static extern bool EnumWindows(Callback cb, IntPtr value);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
        [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hwnd, uint code);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int length);
        [DllImport("user32.dll")] private static extern bool IsWindowEnabled(IntPtr hwnd);
        public static bool Enabled(long hwnd) {
            return IsWindowEnabled(new IntPtr(hwnd));
        }
        public static int Pid(long hwnd) {
            uint pid;
            GetWindowThreadProcessId(new IntPtr(hwnd), out pid);
            return (int)pid;
        }
        public static long[] Owned(int pid, long owner, string exactTitle) {
            var result = new List<long>();
            EnumWindows((h, _) => {
                uint current;
                GetWindowThreadProcessId(h, out current);
                if (current == pid && GetWindow(h, 4).ToInt64() == owner) {
                    var label = new StringBuilder(256);
                    GetWindowText(h, label, label.Capacity);
                    if (String.Equals(label.ToString(), exactTitle, StringComparison.Ordinal))
                        result.Add(h.ToInt64());
                }
                return true;
            }, IntPtr.Zero);
            return result.ToArray();
        }
    }
}
'@ -ErrorAction Stop
}

function Inspect-OneWindow {
    param([long]$Hwnd, [string]$OutputFile)
    $global:LASTEXITCODE = 0
    & $exe ui inspect -w ([string]$Hwnd) --json *>&1 |
        Out-File -FilePath $OutputFile -Encoding UTF8
    if ($LASTEXITCODE -ne 0) { throw 'WinApp native inspect did not succeed.' }
    $raw = [IO.File]::ReadAllText($OutputFile)
    if ($raw.Length -gt 131072 -or [string]::IsNullOrWhiteSpace($raw)) {
        throw 'WinApp returned empty or oversized UIA evidence.'
    }
    try { $tree = ConvertFrom-Json -InputObject $raw -ErrorAction Stop }
    catch { throw 'WinApp returned malformed UIA JSON.' }
    if ($null -eq $tree -or $null -ne $tree.error) { throw 'WinApp reported an observation error.' }
    $owners = @($tree.windows | Where-Object { [string]$_.hwnd -ceq [string]$Hwnd })
    if ($owners.Count -ne 1) { throw 'UIA tree did not identify the requested native HWND.' }
    return $owners[0]
}

function Find-Selectors {
    param($Elements, [string]$AutomationId)
    foreach ($element in @($Elements)) {
        if ($null -eq $element) { continue }
        foreach ($key in @('automationId','automation_id','automationID','id','name')) {
            $property = $element.PSObject.Properties[$key]
            if ($null -ne $property -and [string]$property.Value -ceq $AutomationId) {
                if ([string]$element.selector -match '^[A-Za-z0-9][A-Za-z0-9_-]{0,127}$') {
                    Write-Output ([string]$element.selector)
                }
                break
            }
        }
        if ($null -ne $element.children) {
            Find-Selectors -Elements $element.children -AutomationId $AutomationId
        }
    }
}

try {
    $build = Join-Path $OutputRoot 'fixture'
    $project = Join-Path $repo 'tools\PaRuX.UiAcceptanceFixture\PaRuX.UiAcceptanceFixture.csproj'
    $global:LASTEXITCODE = 0
    & dotnet publish $project -c Release -o $build --nologo *>&1 |
        Out-File -FilePath (Join-Path $OutputRoot 'fixture-build.log') -Encoding UTF8
    if ($LASTEXITCODE -ne 0) { throw 'Modal acceptance fixture build failed.' }
    $exePath = Join-Path $build 'PaRuX.UiAcceptanceFixture.exe'
    if (-not (Test-Path -LiteralPath $exePath -PathType Leaf)) {
        throw 'Disposable WinForms fixture executable is missing.'
    }
    $fixture = Start-Process -FilePath $exePath -ArgumentList @($nonce) -PassThru
    if ($null -eq $fixture -or $fixture.Id -le 0) {
        throw 'Disposable modal fixture could not start.'
    }
    $main = [long]0
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    while ([DateTime]::UtcNow -lt $deadline) {
        $fixture.Refresh()
        if ($fixture.HasExited) { throw 'Fixture exited during initialization.' }
        $main = $fixture.MainWindowHandle.ToInt64()
        if ($main -gt 0 -and $fixture.MainWindowTitle -ceq ('PaRuX Disposable UI Acceptance ' + $nonce)) {
            break
        }
        [Threading.Thread]::Sleep(120)
    }
    if ($main -le 0 -or [PaRuXTestOnly.ModalWin32]::Pid($main) -ne $fixture.Id) {
        throw 'The original main HWND was not proven to belong to the disposable process.'
    }
    $mainTree = Inspect-OneWindow -Hwnd $main -OutputFile (Join-Path $OutputRoot 'main-inspect.json')
    $openSelectors = @(Find-Selectors -Elements $mainTree.elements -AutomationId 'parux-fixture-open-modal')
    $resultSelectors = @(Find-Selectors -Elements $mainTree.elements -AutomationId 'parux-fixture-result')
    if ($openSelectors.Count -ne 1 -or $resultSelectors.Count -ne 1) {
        throw 'Open modal and Result selectors must each be uniquely observed.'
    }
    if (-not [PaRuXTestOnly.ModalWin32]::Enabled($main)) {
        throw 'The original main HWND was unexpectedly disabled.'
    }
    $status.mainWindowFound = 'PASS'

    $global:LASTEXITCODE = 0
    & $exe ui invoke $openSelectors[0] -w ([string]$main) --json *>&1 |
        Out-File -FilePath (Join-Path $OutputRoot 'modal-open.json') -Encoding UTF8
    if ($LASTEXITCODE -ne 0) { throw 'WinApp failed to open modal dialog.' }
    try { $opened = ConvertFrom-Json -InputObject (
        [IO.File]::ReadAllText((Join-Path $OutputRoot 'modal-open.json'))) -ErrorAction Stop }
    catch { throw 'Modal open action did not return JSON.' }
    if ($null -eq $opened -or $null -ne $opened.error -or
        $opened.success -ceq $false -or $opened.ok -ceq $false) {
        throw 'Modal open action was not delivered.'
    }

    $modal = [long]0
    $deadline = [DateTime]::UtcNow.AddSeconds(12)
    while ([DateTime]::UtcNow -lt $deadline) {
        $matches = @([PaRuXTestOnly.ModalWin32]::Owned(
            $fixture.Id, $main, ('PaRuX Fixture Modal ' + $nonce)))
        if ($matches.Count -gt 1) { throw 'The modal dialog HWND is ambiguous.' }
        if ($matches.Count -eq 1 -and $matches[0] -gt 0) {
            $modal = [long]$matches[0]
            break
        }
        [Threading.Thread]::Sleep(120)
    }
    if ($modal -le 0 -or $modal -eq $main) {
        throw 'No distinct same-PID owned modal HWND appeared.'
    }
    $status.modalWindowOwnedByMainPid = 'PASS'
    if ([PaRuXTestOnly.ModalWin32]::Enabled($main)) {
        throw 'The main HWND stayed enabled: this was not a genuine modal dialog.'
    }
    $status.parentDisabledWhileModal = 'PASS'

    $modalTree = Inspect-OneWindow -Hwnd $modal -OutputFile (Join-Path $OutputRoot 'modal-inspect.json')
    $confirmSelectors = @(Find-Selectors -Elements $modalTree.elements -AutomationId 'parux-fixture-modal-confirm')
    if ($confirmSelectors.Count -ne 1) {
        throw 'A unique modal Confirm selector was not observed.'
    }
    $global:LASTEXITCODE = 0
    & $exe ui invoke $confirmSelectors[0] -w ([string]$modal) --json *>&1 |
        Out-File -FilePath (Join-Path $OutputRoot 'modal-confirm.json') -Encoding UTF8
    if ($LASTEXITCODE -ne 0) { throw 'WinApp failed to confirm the modal dialog.' }
    try { $confirmed = ConvertFrom-Json -InputObject (
        [IO.File]::ReadAllText((Join-Path $OutputRoot 'modal-confirm.json'))) -ErrorAction Stop }
    catch { throw 'Modal confirm did not return JSON.' }
    if ($null -eq $confirmed -or $null -ne $confirmed.error -or
        $confirmed.success -ceq $false -or $confirmed.ok -ceq $false) {
        throw 'Modal Confirm action was not delivered.'
    }
    $status.modalConfirmDelivered = 'PASS'

    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    while ([DateTime]::UtcNow -lt $deadline) {
        if ([PaRuXTestOnly.ModalWin32]::Enabled($main)) { break }
        [Threading.Thread]::Sleep(100)
    }
    if (-not [PaRuXTestOnly.ModalWin32]::Enabled($main)) {
        throw 'Main window remained blocked after modal dismissal.'
    }
    $status.parentReenabled = 'PASS'
    $fixture.Refresh()
    if ($fixture.HasExited -or $fixture.MainWindowHandle.ToInt64() -ne $main) {
        throw 'Main window/process identity changed during modal test.'
    }

    $expected = 'MODAL-ACCEPTED-' + $nonce
    $global:LASTEXITCODE = 0
    & $exe ui wait-for $resultSelectors[0] -w ([string]$main) --value $expected --timeout 2000 --json *>&1 |
        Out-File -FilePath (Join-Path $OutputRoot 'modal-final-result.json') -Encoding UTF8
    if ($LASTEXITCODE -ne 0) { throw 'Main result was not independently observed.' }
    try { $result = ConvertFrom-Json -InputObject (
        [IO.File]::ReadAllText((Join-Path $OutputRoot 'modal-final-result.json'))) -ErrorAction Stop }
    catch { throw 'Final WinApp result was not JSON.' }
    if ($null -eq $result -or $result.found -cne $true -or
        $result.timedOut -ceq $true -or $null -ne $result.error) {
        throw 'Fresh main-window UIA evidence did not prove the modal postcondition.'
    }
    $status.mainFinalUiaReadback = 'PASS'
    $status.outcome = 'PASS'
    Write-Output 'NATIVE MODAL UIA E2E PASS: process owner, disabled parent, confirmed modal and final main control.'
    Write-Output 'This is not a live PaRuX agent acceptance result.'
}
catch {
    $status.outcome = 'FAIL'
    $failureMessage = $_.Exception.Message
}
finally {
    if ($null -ne $fixture) {
        try {
            $fixture.Refresh()
            if (-not $fixture.HasExited) {
                Stop-Process -Id $fixture.Id -Force -ErrorAction Stop
                $null = $fixture.WaitForExit(5000)
            }
        }
        catch { Write-Warning 'Inspect cleanup of the disposable modal fixture.' }
        finally { $fixture.Dispose() }
    }
    $status.executedUtc = [DateTime]::UtcNow.ToString('o')
    $status | ConvertTo-Json -Depth 4 |
        Out-File -FilePath (Join-Path $OutputRoot 'native-modal-status.json') -Encoding UTF8
}
if ($status.outcome -ne 'PASS') {
    throw ('NATIVE MODAL UIA E2E FAIL: ' + $failureMessage)
}
