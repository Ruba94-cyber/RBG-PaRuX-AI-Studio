<#
.SYNOPSIS
    Profiles a real PaRuX Core process: idle cost, cost under load, and whether either grows.

.DESCRIPTION
    The performance evidence PaRuX had was measurement (latency, throughput) rather than profiling:
    nothing had ever looked at what the process itself costs to keep running. This answers the
    questions a local-first product is actually judged on, because Core runs on the user's own
    machine alongside their work:

      * what does Core cost at idle - working set, managed heap, threads;
      * what does it cost while serving;
      * does either grow over a sustained run, which is what a leak looks like from outside.

    Deliberately GPU-free. Every endpoint driven here answers without a model loaded, so the
    numbers are real on this machine today rather than blocked behind the external GPU.

    Honest by construction: the load phase reports how many requests actually succeeded, and a
    phase that cannot run says so instead of reporting zeros. Counters come from dotnet-counters
    against the real process, not from inside the code being measured.

.PARAMETER SampleSeconds
    How long each phase is sampled. The default is long enough for the managed heap to settle
    through at least one gen-2 collection.
#>
[CmdletBinding()]
param(
    [string]$CoreExe = "",
    [int]$SampleSeconds = 45,
    [int]$LoadRequests = 400,
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\artifacts\profile')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($CoreExe)) {
    $CoreExe = Join-Path $repoRoot 'src\PaRuX.Server\bin\Release\net8.0\win-x64\publish\PaRuX.Server.exe'
}
if (-not (Test-Path -LiteralPath $CoreExe)) {
    throw "PaRuX Core not found at $CoreExe. Publish it first: dotnet publish src\PaRuX.Server -c Release -r win-x64 --self-contained false"
}

$counters = Get-Command dotnet-counters -ErrorAction SilentlyContinue
if (-not $counters) { throw "dotnet-counters is not installed. Run: dotnet tool install --global dotnet-counters" }

New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$stamp = (Get-Date).ToString('yyyyMMdd-HHmmss')

function Resolve-Bootstrap {
    $path = Join-Path $env:LOCALAPPDATA 'PaRuX\server\bootstrap.json'
    if (-not (Test-Path $path)) { return $null }
    try {
        $json = Get-Content $path -Raw | ConvertFrom-Json
        @{ url = ([string]$json.endpoint).TrimEnd('/'); token = [string]$json.token }
    } catch { $null }
}

function Wait-CoreHealthy {
    param([int]$TimeoutSeconds = 90)
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $bootstrap = Resolve-Bootstrap
        if ($bootstrap) {
            try {
                # Deliberately probing a TOKEN-PROTECTED endpoint rather than /api/v1/health.
                # health answers 200 even with a wrong token, so using it to prove readiness also
                # accepted a stale one: Core rewrites bootstrap.json with a fresh token as it
                # finishes starting, and an earlier version of this script read the file too early
                # and then had 5 of its 6 endpoints answer 401 for the whole load phase - which
                # looked like a product failure and was entirely this harness's fault.
                Invoke-RestMethod -Method Get -Uri "$($bootstrap.url)/api/v1/runtime/status" `
                    -Headers @{ 'X-PaRuX-Token' = $bootstrap.token } -TimeoutSec 5 | Out-Null
                return $bootstrap
            } catch { }
        }
        Start-Sleep -Milliseconds 500
    }
    return $null
}

# ---------------------------------------------------------------------------------------------
# Counter sampling. The CSV dotnet-counters writes is parsed back rather than eyeballed, so the
# report carries real minimum/maximum/last values instead of one lucky instant.
# ---------------------------------------------------------------------------------------------
function Measure-Phase {
    param([string]$Name, [int]$ProcessId, [int]$Seconds, [scriptblock]$During)

    $csv = Join-Path $OutputDirectory "$Name-$stamp.csv"
    $collect = Start-Process -FilePath $counters.Source -PassThru -WindowStyle Hidden -ArgumentList @(
        'collect', '--process-id', $ProcessId, '--refresh-interval', '1',
        '--format', 'csv', '--output', $csv,
        '--counters', 'System.Runtime'
    )

    try {
        if ($During) { & $During } else { Start-Sleep -Seconds $Seconds }
    }
    finally {
        # dotnet-counters stops cleanly on Ctrl-C only; killing it still flushes what it wrote.
        try { Stop-Process -Id $collect.Id -Force -ErrorAction SilentlyContinue } catch { }
        Start-Sleep -Seconds 2
    }

    if (-not (Test-Path $csv)) { return $null }
    # dotnet-counters writes its own header:
    #   Timestamp,Provider,Counter Name,Counter Type,Mean/Increment
    # Reading it rather than assuming a column layout - an earlier version of this script supplied
    # its own -Header, which silently misaligned every field and reported "no samples captured"
    # over a CSV that was perfectly well populated.
    $rows = Import-Csv -Path $csv |
        Where-Object { $_.'Counter Name' -and $_.'Mean/Increment' -match '^-?[0-9.]+([eE][-+]?[0-9]+)?$' }

    $summary = @{}
    foreach ($group in ($rows | Group-Object 'Counter Name')) {
        $values = $group.Group | ForEach-Object { [double]$_.'Mean/Increment' }
        if (-not $values) { continue }
        $summary[$group.Name] = [pscustomobject]@{
            Min = ($values | Measure-Object -Minimum).Minimum
            Max = ($values | Measure-Object -Maximum).Maximum
            Last = $values[-1]
            Samples = $values.Count
        }
    }
    return $summary
}

function Write-Phase {
    param([string]$Title, $Summary)
    Write-Host ""
    Write-Host "== $Title" -ForegroundColor Cyan
    if ($null -eq $Summary -or $Summary.Count -eq 0) {
        Write-Host "   no counter samples captured" -ForegroundColor Yellow
        return
    }
    foreach ($name in @(
        'Working Set (MB)', 'GC Heap Size (MB)', 'ThreadPool Thread Count',
        'Gen 0 GC Count (Count / 1 sec)', 'Gen 2 GC Count (Count / 1 sec)',
        'Exception Count (Count / 1 sec)', 'ThreadPool Queue Length', 'Allocation Rate (B / 1 sec)')) {
        if (-not $Summary.ContainsKey($name)) { continue }
        $s = $Summary[$name]
        Write-Host ("   {0,-32} min {1,10:N1}  max {2,10:N1}  last {3,10:N1}" -f $name, $s.Min, $s.Max, $s.Last)
    }
}

Write-Host "PaRuX Core profiling" -ForegroundColor Cyan
Write-Host "  Core: $CoreExe"

$core = Start-Process -FilePath $CoreExe -PassThru -WindowStyle Hidden
Write-Host "  started PID $($core.Id), waiting for health..."
$bootstrap = Wait-CoreHealthy
if (-not $bootstrap) {
    try { Stop-Process -Id $core.Id -Force } catch { }
    throw "PaRuX Core did not become healthy; nothing was measured."
}
Write-Host "  healthy at $($bootstrap.url)" -ForegroundColor Green

$succeeded = 0
$failed = 0
try {
    $idle = Measure-Phase -Name 'idle' -ProcessId $core.Id -Seconds $SampleSeconds
    Write-Phase -Title "Idle ($SampleSeconds s)" -Summary $idle

    $headers = @{ 'X-PaRuX-Token' = $bootstrap.token }
    # Endpoints chosen because every one answers without a model loaded, so this measures PaRuX's
    # own overhead rather than inference.
    $paths = @('/api/v1/health', '/api/v1/runtime/status', '/api/v1/models/roles',
               '/api/v1/runtime/diagnostics', '/api/v1/conversations', '/api/v1/memory')

    $load = Measure-Phase -Name 'load' -ProcessId $core.Id -Seconds $SampleSeconds -During {
        for ($i = 0; $i -lt $LoadRequests; $i++) {
            $path = $paths[$i % $paths.Count]
            try {
                Invoke-RestMethod -Method Get -Uri "$($bootstrap.url)$path" -Headers $headers -TimeoutSec 20 | Out-Null
                $script:succeeded++
            } catch { $script:failed++ }
        }
    }
    Write-Phase -Title "Under load ($LoadRequests requests: $succeeded ok, $failed failed)" -Summary $load

    $settle = Measure-Phase -Name 'settle' -ProcessId $core.Id -Seconds $SampleSeconds
    Write-Phase -Title "After load, idle again ($SampleSeconds s)" -Summary $settle

    $report = [pscustomobject]@{
        measuredUtc = (Get-Date).ToUniversalTime().ToString('o')
        coreExe = $CoreExe
        requests = [pscustomobject]@{ attempted = $LoadRequests; succeeded = $succeeded; failed = $failed }
        idle = $idle
        underLoad = $load
        afterLoad = $settle
    }
    $jsonPath = Join-Path $OutputDirectory "profile-$stamp.json"
    $report | ConvertTo-Json -Depth 8 | Set-Content -Path $jsonPath -Encoding UTF8
    Write-Host ""
    Write-Host "JSON: $jsonPath" -ForegroundColor Green
}
finally {
    try { Stop-Process -Id $core.Id -Force -ErrorAction SilentlyContinue } catch { }
    Write-Host "  Core stopped."
}
