# Repeatable startup/idle measurement harness for PaRuX (Windows PowerShell 5.1).
# Measures, per run, in a throw-away isolated data root:
#   core_ready_ms   Core process start -> GET /api/v1/health returns 200
#   app_window_ms   App process start  -> main window found via UIA with an enabled Edit control (interactive)
#   *_idle          working set / private bytes and CPU of App and Core after an idle period
# Prints per-run values and medians. Nothing is estimated; every number comes from this machine.
# Usage:  powershell -File tools\measure-startup.ps1 -Mode all -Runs 3 -IdleSeconds 60
# Needs Release builds:  dotnet build src/PaRuX.Server -c Release ; dotnet build src/PaRuX.App -c Release -p:Platform=x64
# Uses the desktop and port 51123; stop any running PaRuX first. Kills what it starts.
param(
    [ValidateSet('core', 'app', 'all')][string]$Mode = 'all',
    [int]$Runs = 3,
    [int]$IdleSeconds = 60,
    [string]$Server = '',
    [string]$App = '',
    [string]$Json = ''
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $Server) { $Server = Join-Path $root 'src\PaRuX.Server\bin\Release\net8.0\PaRuX.Server.exe' }
if (-not $App) { $App = Join-Path $root 'src\PaRuX.App\bin\x64\Release\net8.0-windows10.0.19041.0\win-x64\PaRuX.exe' }
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes

function Median($v) { $s = @($v | Sort-Object); if ($s.Count -eq 0) { return $null }; $s[[int][math]::Floor(($s.Count - 1) / 2)] }

function Stop-Tree($names) {
    foreach ($n in $names) { Get-Process -Name $n -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue }
    Start-Sleep -Milliseconds 800
}

function New-Env {
    $d = Join-Path ([IO.Path]::GetTempPath()) ('parux-startup-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
    New-Item -ItemType Directory -Path $d, "$d\state" | Out-Null
    @{ OnboardingCompleted = $true; StudioModeEnabled = $false; BubbleMode = 'Disabled' } | ConvertTo-Json | Set-Content "$d\settings.json"
    $d
}

function Wait-Health($sw, $timeoutMs) {
    while ($sw.ElapsedMilliseconds -lt $timeoutMs) {
        try {
            $r = [Net.WebRequest]::Create('http://127.0.0.1:51123/api/v1/health'); $r.Timeout = 500
            $resp = $r.GetResponse(); $code = [int]$resp.StatusCode; $resp.Close()
            if ($code -eq 200) { return $sw.ElapsedMilliseconds }
        } catch { }
        Start-Sleep -Milliseconds 20
    }
    return $null
}

function Snapshot($proc) {
    $proc.Refresh()
    @{ ws = [math]::Round($proc.WorkingSet64 / 1MB, 1); priv = [math]::Round($proc.PrivateMemorySize64 / 1MB, 1); cpu = $proc.TotalProcessorTime.TotalSeconds }
}

function Measure-Idle($proc, $seconds) {
    # CPU is averaged over the second half of the window so the startup burst does not count as idle.
    $half = [int]($seconds / 2)
    Start-Sleep -Seconds $half
    $a = Snapshot $proc; $t0 = Get-Date
    Start-Sleep -Seconds ($seconds - $half)
    $b = Snapshot $proc; $dt = ((Get-Date) - $t0).TotalSeconds
    @{ ws = $b.ws; priv = $b.priv; cpuPct = [math]::Round((($b.cpu - $a.cpu) / $dt) * 100 / [Environment]::ProcessorCount, 3) }
}

function Run-Core {
    $d = New-Env
    $psi = New-Object Diagnostics.ProcessStartInfo $Server
    $psi.UseShellExecute = $false; $psi.CreateNoWindow = $true; $psi.WorkingDirectory = Split-Path $Server
    $psi.EnvironmentVariables['PARUX_DATA_ROOT'] = $d; $psi.EnvironmentVariables['PARUX_LOCALAI_STATE_DIR'] = "$d\state"
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $p = [Diagnostics.Process]::Start($psi)
    try {
        $ms = Wait-Health $sw 60000
        $idle = Measure-Idle $p $IdleSeconds
        @{ readyMs = $ms; ws = $idle.ws; priv = $idle.priv; cpuPct = $idle.cpuPct }
    } finally { if (-not $p.HasExited) { $p.Kill() }; Start-Sleep -Milliseconds 800; Remove-Item $d -Recurse -Force -ErrorAction SilentlyContinue }
}

function Run-App {
    $d = New-Env
    $psi = New-Object Diagnostics.ProcessStartInfo $App
    $psi.UseShellExecute = $false; $psi.WorkingDirectory = Split-Path $App
    $psi.EnvironmentVariables['PARUX_DATA_ROOT'] = $d; $psi.EnvironmentVariables['PARUX_LOCALAI_STATE_DIR'] = "$d\state"
    $psi.EnvironmentVariables['PARUX_SERVER_PATH'] = $Server
    $psi.EnvironmentVariables['PARUX_UI_TEST_ISOLATED'] = '1'
    $before = @(Get-Process -Name PaRuX -ErrorAction SilentlyContinue | ForEach-Object { $_.Id })
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $launcher = [Diagnostics.Process]::Start($psi)
    try {
        $appProc = $null; $interactiveMs = $null
        while ($sw.ElapsedMilliseconds -lt 90000 -and -not $interactiveMs) {
            $appProc = Get-Process -Name PaRuX -ErrorAction SilentlyContinue | Where-Object { $before -notcontains $_.Id } | Select-Object -First 1
            if ($appProc -and $appProc.MainWindowHandle -ne 0) {
                try {
                    $el = [Windows.Automation.AutomationElement]::FromHandle($appProc.MainWindowHandle)
                    if ($el.Current.Name -in @('PaRuX AI & Gaming OS', 'RBG PaRuX AI Studio')) {
                        $cond = New-Object Windows.Automation.PropertyCondition ([Windows.Automation.AutomationElement]::ControlTypeProperty), ([Windows.Automation.ControlType]::Edit)
                        $edit = $el.FindFirst([Windows.Automation.TreeScope]::Descendants, $cond)
                        if ($edit -and $edit.Current.IsEnabled) { $interactiveMs = $sw.ElapsedMilliseconds }
                    }
                } catch { }
            }
            if (-not $interactiveMs) { Start-Sleep -Milliseconds 25 }
        }
        $coreMs = Wait-Health $sw 90000
        $half = [int]($IdleSeconds / 2)
        Start-Sleep -Seconds $half
        $core = Get-Process -Name PaRuX.Server -ErrorAction SilentlyContinue | Select-Object -First 1
        $a1 = Snapshot $appProc; $c1 = if ($core) { Snapshot $core } else { $null }; $t0 = Get-Date
        Start-Sleep -Seconds ($IdleSeconds - $half)
        $a2 = Snapshot $appProc; $c2 = if ($core) { Snapshot $core } else { $null }; $dt = ((Get-Date) - $t0).TotalSeconds
        $n = [Environment]::ProcessorCount
        @{ interactiveMs = $interactiveMs; coreHealthMs = $coreMs
           appWs = $a2.ws; appPriv = $a2.priv; appCpuPct = [math]::Round((($a2.cpu - $a1.cpu) / $dt) * 100 / $n, 3)
           coreWs = $c2.ws; corePriv = $c2.priv; coreCpuPct = [math]::Round((($c2.cpu - $c1.cpu) / $dt) * 100 / $n, 3) }
    } finally {
        Stop-Tree @('PaRuX', 'PaRuX.Server', 'llama-server')
        Remove-Item $d -Recurse -Force -ErrorAction SilentlyContinue
    }
}

if (Get-Process -Name PaRuX, PaRuX.Server -ErrorAction SilentlyContinue) { throw 'A PaRuX process is already running; stop it first.' }
$result = @{ machine = $env:COMPUTERNAME; cores = [Environment]::ProcessorCount; idleSeconds = $IdleSeconds; core = @(); app = @() }
for ($i = 1; $i -le $Runs; $i++) {
    if ($Mode -ne 'app') { $r = Run-Core; $result.core += , $r; Write-Host ("core run $i : " + (($r.GetEnumerator() | ForEach-Object { $_.Key + '=' + $_.Value }) -join ' ')) }
    if ($Mode -ne 'core') { $r = Run-App; $result.app += , $r; Write-Host ("app  run $i : " + (($r.GetEnumerator() | ForEach-Object { $_.Key + '=' + $_.Value }) -join ' ')) }
}
$med = @{}
foreach ($k in 'core', 'app') { if ($result[$k].Count) { foreach ($f in $result[$k][0].Keys) { $med["$k.$f"] = Median ($result[$k] | ForEach-Object { $_[$f] }) } } }
Write-Host '--- medians ---'; $med.GetEnumerator() | Sort-Object Name | ForEach-Object { Write-Host ("{0,-22} {1}" -f $_.Name, $_.Value) }
if ($Json) { @{ runs = $result; medians = $med } | ConvertTo-Json -Depth 6 | Set-Content $Json }
