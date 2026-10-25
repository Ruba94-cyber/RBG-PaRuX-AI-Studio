<#
.SYNOPSIS
    PaRuX GPU acceptance benchmark. Runs today with no discrete GPU attached, and produces the
    full measurement set the moment one is.

.DESCRIPTION
    Every GPU-only measurement PaRuX still owes acceptance evidence for, in one runnable harness,
    so that attaching the external GPU is the only remaining step - not "write the benchmark, then
    attach the GPU".

    Design rules this harness holds itself to:

      * Nothing is inferred. A measurement that cannot run records WHY it could not, never a zero
        and never an estimate. With no GPU attached the run is reported as
        EXTERNAL_GPU_NOT_PRESENT and the checks that do not need one still execute, so the harness
        itself is exercised rather than merely written.
      * PaRuX's own view is cross-checked against the hardware. nvidia-smi is the ground truth for
        driver, VRAM and utilization; /api/v1/runtime/status is what PaRuX believes. Disagreement
        between them is a finding, not a rounding difference to smooth over.
      * Every phase leaves the machine as it found it. Game Mode is turned back off, the text
        runtime is restored to the profile it started on, and a failure mid-run still runs the
        restore.
      * Failure paths are measured, not just success paths. A benchmark that only measures the
        happy path cannot tell a robust runtime from a lucky one, so OOM recovery, unload and VRAM
        release are first-class phases.

.PARAMETER BaseUrl
    PaRuX Core endpoint. Read from the machine's bootstrap.json when omitted.

.PARAMETER Phases
    Subset of phases to run. Default is all of them.

.PARAMETER CpuOnly
    Runs the phases that do not need a GPU against a CPU launch. Before anything is measured, the running
    llama-server command line must show --gpu-layers 0 and no device pin; otherwise the run is refused
    (exit code 2) so no result is ever labelled cpu without that evidence. GPU-only phases are skipped.

.EXAMPLE
    ./scripts/benchmark-gpu.ps1
    ./scripts/benchmark-gpu.ps1 -Phases detection,load,throughput
#>
[CmdletBinding()]
param(
    [string]$BaseUrl = '',
    [string]$Token = '',
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\artifacts\gpu-benchmark'),
    [ValidateSet('detection', 'backend', 'load', 'throughput', 'longcontext', 'switch',
                 'concurrency', 'oom', 'unload', 'gamemode')]
    [string[]]$Phases = @('detection', 'backend', 'load', 'throughput', 'longcontext', 'switch',
                          'concurrency', 'oom', 'unload', 'gamemode'),
    [int]$Repetitions = 5,
    [int]$ChatMaxTokens = 128,
    [int]$ConcurrentRequests = 4,
    [int]$LongContextChars = 24000,
    [switch]$CpuOnly
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# ---------------------------------------------------------------------------------------------
# Result model. Every phase returns one of these; nothing else is allowed to write the report.
# ---------------------------------------------------------------------------------------------
$script:Results = [System.Collections.Generic.List[object]]::new()
$script:RunLabel = if ($CpuOnly) { 'cpu' } else { 'default' }

function New-PhaseResult {
    param(
        [Parameter(Mandatory)][string]$Phase,
        [Parameter(Mandatory)][ValidateSet('measured', 'measured_fallback', 'external_gpu_not_present', 'unsupported', 'error', 'skipped')]
        [string]$Status,
        [string]$Detail = '',
        [hashtable]$Metrics = @{},
        # Physical-test gate: every result that can only be measured on a physical GPU carries it.
        [string]$Gate = ''
    )
    if (-not $Gate -and $Status -eq 'external_gpu_not_present') { $Gate = 'PHYSICAL_GPU_TEST_REQUIRED' }
    [pscustomobject]@{
        phase   = $Phase
        status  = $Status
        label   = $script:RunLabel
        gate    = if ($Gate) { $Gate } else { $null }
        detail  = $Detail
        metrics = $Metrics
    }
}

function Add-Phase { param([object]$Result) $script:Results.Add($Result); Write-PhaseLine $Result }

function Write-PhaseLine {
    param([object]$Result)
    $color = switch ($Result.status) {
        'measured'                 { 'Green' }
        'external_gpu_not_present' { 'Yellow' }
        'unsupported'              { 'Yellow' }
        'skipped'                  { 'DarkGray' }
        default                    { 'Red' }
    }
    Write-Host ("  [{0,-24}] {1}" -f $Result.status, $Result.phase) -ForegroundColor $color
    if ($Result.gate) { Write-Host ("      gate: {0}" -f $Result.gate) -ForegroundColor Yellow }
    if ($Result.detail) { Write-Host ("      {0}" -f $Result.detail) -ForegroundColor DarkGray }
    foreach ($key in ($Result.metrics.Keys | Sort-Object)) {
        Write-Host ("      {0,-28} {1}" -f $key, $Result.metrics[$key]) -ForegroundColor DarkGray
    }
}

# ---------------------------------------------------------------------------------------------
# Core connection
# ---------------------------------------------------------------------------------------------
function Resolve-Bootstrap {
    if ($BaseUrl -and $Token) { return @{ url = $BaseUrl.TrimEnd('/'); token = $Token } }
    $candidate = Join-Path $env:LOCALAPPDATA 'PaRuX\server\bootstrap.json'
    if (-not (Test-Path $candidate)) {
        throw "PaRuX Core bootstrap.json not found at $candidate. Start PaRuX, or pass -BaseUrl and -Token."
    }
    $json = Get-Content $candidate -Raw | ConvertFrom-Json
    @{
        url   = if ($BaseUrl) { $BaseUrl.TrimEnd('/') } else { ([string]$json.endpoint).TrimEnd('/') }
        token = if ($Token) { $Token } else { [string]$json.token }
    }
}

function Invoke-Core {
    param(
        [Parameter(Mandatory)][string]$Path,
        [string]$Method = 'Get',
        $Body = $null,
        [int]$TimeoutSec = 300
    )
    $headers = @{ 'X-PaRuX-Token' = $script:Core.token }
    $arguments = @{
        Method = $Method; Uri = "$($script:Core.url)$Path"; Headers = $headers; TimeoutSec = $TimeoutSec
    }
    if ($null -ne $Body) {
        $arguments.Body = ($Body | ConvertTo-Json -Depth 12)
        $arguments.ContentType = 'application/json'
    }
    Invoke-RestMethod @arguments
}

function Get-RuntimeStatus { Invoke-Core -Path '/api/v1/runtime/status' }

function Invoke-RuntimeAction {
    param([Parameter(Mandatory)][string]$Action)
    Invoke-Core -Path '/api/v1/runtime/actions' -Method Post -Body @{ action = $Action }
}

# ---------------------------------------------------------------------------------------------
# Hardware ground truth. nvidia-smi is queried directly rather than trusting PaRuX, because
# "PaRuX agrees with the hardware" is one of the things under test.
# ---------------------------------------------------------------------------------------------
function Invoke-NvidiaSmiBounded {
    # One nvidia-smi query under a hard deadline. A driver that hangs is abandoned and its process tree is killed,
    # so the 250 ms VRAM sampler and the phases cannot be held by it. Returns the stdout, or $null.
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string[]]$Arguments,
        [int]$TimeoutMilliseconds = 3000
    )
    $psi = [System.Diagnostics.ProcessStartInfo]::new()
    $psi.FileName = $Path
    foreach ($argument in $Arguments) { [void]$psi.ArgumentList.Add($argument) }
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $process = [System.Diagnostics.Process]::Start($psi)
    if (-not $process) { return $null }
    try {
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $null = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit($TimeoutMilliseconds)) {
            try { $process.Kill($true) } catch { try { $process.Kill() } catch { } }
            return $null
        }
        if ($process.ExitCode -ne 0) { return $null }
        return $stdoutTask.GetAwaiter().GetResult()
    }
    finally { $process.Dispose() }
}

function Get-NvidiaSmi {
    $exe = Get-Command nvidia-smi -ErrorAction SilentlyContinue
    if (-not $exe) { return $null }
    try {
        $fields = 'name,driver_version,memory.total,memory.used,utilization.gpu,temperature.gpu,power.draw'
        $output = Invoke-NvidiaSmiBounded -Path $exe.Source -Arguments @("--query-gpu=$fields", '--format=csv,noheader,nounits')
        $line = if ($output) { @($output -split "`r?`n" | Where-Object { $_.Trim() })[0] } else { $null }
        if (-not $line) { return $null }
        $parts = $line -split ',\s*'
        [pscustomobject]@{
            Name           = $parts[0]
            Driver         = $parts[1]
            VramTotalMib   = [int]$parts[2]
            VramUsedMib    = [int]$parts[3]
            UtilizationPct = [int]$parts[4]
            TemperatureC   = [int]$parts[5]
            PowerWatts     = [double]$parts[6]
        }
    } catch { return $null }
}

# ---------------------------------------------------------------------------------------------
function Resolve-GpuGate {
    # The physical-GPU gate follows nvidia-smi alone: a card below PaRuX's heavy-profile bar is still a physical GPU,
    # so it is measured and labelled as one. PaRuX's heavy-profile flag is kept separate and decides which profile ran.
    param([Parameter(Mandatory)][bool]$NvidiaSmiPresent, [Parameter(Mandatory)][bool]$HeavyProfile)
    [pscustomobject]@{
        PhysicalGpuPresent = $NvidiaSmiPresent
        HeavyGpuProfile    = $HeavyProfile
        Gate               = if ($NvidiaSmiPresent) { '' } else { 'PHYSICAL_GPU_TEST_REQUIRED' }
    }
}

function Get-RunTeardownPlan {
    # What the harness must undo after a run. It removes a Gaming Mode marker only if this run created it, and it
    # restores the starting profile only when Gaming Mode was not already on, so the harness never turns AI back on
    # in the middle of a game it did not start.
    param([bool]$SentGameStart, [bool]$GamingWasActive, [string]$StartingProfile)
    $actions = @()
    if ($SentGameStart) { $actions += 'GameStop' }
    if (-not $GamingWasActive) {
        if ($StartingProfile -like '*Uncensored*') { $actions += 'Uncensored' }
        elseif ($StartingProfile) { $actions += 'Standard' }
    }
    return $actions
}

function Get-GameModePhasePlan {
    # The Game Mode hard-off phase starts and stops a game only when this run is the one that starts it. A game that is
    # already on was started by someone else, so GameStop is not sent: it would clear a marker this run did not create.
    param([Parameter(Mandatory)][bool]$GameAlreadyOn)
    if ($GameAlreadyOn) { return @() }
    return @('GameStart', 'GameStop')
}

function Receive-ConcurrencyOutcomes {
    # One outcome per request. A request still running at the batch deadline is a failure, not a missing row, and
    # the deadline bounds the whole batch rather than each job separately.
    param([Parameter(Mandatory)][object[]]$Jobs, [int]$DeadlineSeconds = 600)
    $deadline = [DateTime]::UtcNow.AddSeconds($DeadlineSeconds)
    foreach ($job in $Jobs) {
        $remaining = [int][Math]::Ceiling(($deadline - [DateTime]::UtcNow).TotalSeconds)
        if ($remaining -gt 0 -and $job.State -in @('Running', 'NotStarted')) { $null = Wait-Job -Job $job -Timeout $remaining }
    }
    $outcomes = foreach ($job in $Jobs) {
        if ($job.State -eq 'Completed') {
            $received = @(Receive-Job -Job $job)
            if ($received.Count -gt 0) { $received[-1] }
            else { @{ ok = $false; ms = $null; error = 'the request produced no result' } }
        }
        elseif ($job.State -in @('Running', 'NotStarted')) {
            @{ ok = $false; ms = $null; error = "the request did not finish by the batch deadline (state: $($job.State))" }
        }
        else {
            @{ ok = $false; ms = $null; error = "the request ended without a result (state: $($job.State))" }
        }
    }
    return $outcomes
}

# CPU launch verification. A CPU-only run is real only if the running llama-server was started
# with CPU arguments (-ngl 0, no device pin). The command line is the evidence: a Core that still
# launched Vulkan0 is refused rather than labelled cpu.
# ---------------------------------------------------------------------------------------------
function Get-LlamaServerLaunch {
    $processes = @(Get-CimInstance -ClassName Win32_Process -Filter "Name = 'llama-server.exe'" -ErrorAction SilentlyContinue)
    if ($processes.Count -eq 0) { return $null }
    $commandLine = [string]$processes[0].CommandLine
    $layers = [regex]::Match($commandLine, '(?:--gpu-layers|-ngl)\s+(\S+)')
    [pscustomobject]@{
        ProcessId    = [int]$processes[0].ProcessId
        GpuLayers    = if ($layers.Success) { $layers.Groups[1].Value } else { $null }
        DevicePinned = [regex]::IsMatch($commandLine, '(?:--device|-dev)\s+\S+')
    }
}

function Get-CpuLaunchRefusal {
    # Returns $null for a verified CPU launch, otherwise the reason the run must not be labelled cpu.
    $launch = Get-LlamaServerLaunch
    if (-not $launch) { return 'no llama-server is running; start a text profile on this host before a CPU-only run' }
    if ($launch.GpuLayers -ne '0') { return "llama-server was launched with --gpu-layers '$($launch.GpuLayers)', not 0" }
    if ($launch.DevicePinned) { return 'llama-server was launched with a device pin, so it is not a CPU launch' }
    return $null
}

function Measure-VramWhile {
    <#  Samples real VRAM/utilization on a background runspace while $Action runs, so peak VRAM is
        an observed maximum rather than a before/after difference that misses the actual peak. #>
    param([Parameter(Mandatory)][scriptblock]$Action)

    $samples = [System.Collections.Concurrent.ConcurrentBag[object]]::new()
    $stop = [System.Threading.CancellationTokenSource]::new()
    $sampler = [System.Threading.Tasks.Task]::Run({
        while (-not $stop.IsCancellationRequested) {
            $snapshot = Get-NvidiaSmi
            if ($snapshot) { $samples.Add($snapshot) }
            Start-Sleep -Milliseconds 250
        }
    }.GetNewClosure())

    try { $value = & $Action }
    finally { $stop.Cancel(); try { $sampler.Wait(2000) | Out-Null } catch { } }

    $collected = @($samples)
    [pscustomobject]@{
        Value          = $value
        PeakVramMib    = if ($collected) { ($collected | Measure-Object VramUsedMib -Maximum).Maximum } else { $null }
        PeakUtilizatio = if ($collected) { ($collected | Measure-Object UtilizationPct -Maximum).Maximum } else { $null }
        PeakTempC      = if ($collected) { ($collected | Measure-Object TemperatureC -Maximum).Maximum } else { $null }
        SampleCount    = $collected.Count
    }
}

# ---------------------------------------------------------------------------------------------
# Chat measurement. TTFT is measured on the streamed response, because that is the latency a user
# actually feels; decode rate excludes the first token so a slow load cannot flatter it.
# ---------------------------------------------------------------------------------------------
function Measure-Chat {
    param(
        [Parameter(Mandatory)][string]$Prompt,
        [int]$MaxTokens = 128
    )
    $body = @{
        model = 'local'
        messages = @(@{ role = 'user'; content = $Prompt })
        stream = $true
        max_tokens = $MaxTokens
    } | ConvertTo-Json -Depth 8

    $request = [System.Net.Http.HttpRequestMessage]::new(
        [System.Net.Http.HttpMethod]::Post, "$($script:Core.url)/api/v1/chat/completions")
    $request.Headers.Add('X-PaRuX-Token', $script:Core.token)
    $request.Content = [System.Net.Http.StringContent]::new($body, [System.Text.Encoding]::UTF8, 'application/json')

    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    $ttft = $null; $tokens = 0
    $response = $script:Http.SendAsync($request, [System.Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
    try {
        $response.EnsureSuccessStatusCode() | Out-Null
        $stream = $response.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
        $reader = [System.IO.StreamReader]::new($stream)
        while (-not $reader.EndOfStream) {
            $line = $reader.ReadLine()
            if (-not $line -or -not $line.StartsWith('data:')) { continue }
            $payload = $line.Substring(5).Trim()
            if ($payload -eq '[DONE]') { break }
            try { $chunk = $payload | ConvertFrom-Json } catch { continue }
            $delta = $chunk.choices[0].delta
            if ($delta -and $delta.PSObject.Properties.Name -contains 'content' -and $delta.content) {
                if ($null -eq $ttft) { $ttft = $watch.Elapsed.TotalMilliseconds }
                $tokens++
            }
        }
    } finally { $response.Dispose() }
    $watch.Stop()

    $decodeMs = if ($null -ne $ttft) { $watch.Elapsed.TotalMilliseconds - $ttft } else { 0 }
    [pscustomobject]@{
        TtftMs          = $ttft
        TotalMs         = $watch.Elapsed.TotalMilliseconds
        Chunks          = $tokens
        TokensPerSecond = if ($decodeMs -gt 0 -and $tokens -gt 1) { ($tokens - 1) / ($decodeMs / 1000.0) } else { $null }
    }
}

function Get-Percentile {
    param([double[]]$Values, [double]$Percentile)
    if (-not $Values -or $Values.Count -eq 0) { return $null }
    $sorted = $Values | Sort-Object
    $index = [Math]::Ceiling(($Percentile / 100.0) * $sorted.Count) - 1
    $sorted[[Math]::Max(0, [Math]::Min($index, $sorted.Count - 1))]
}

function Wait-TextHealthy {
    param([int]$TimeoutSeconds = 420)
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        try {
            $status = Get-RuntimeStatus
            $text = $status.backends | Where-Object { $_.backend -like 'text-*' -and $_.healthy }
            if ($text) { return $true }
        } catch { }
        Start-Sleep -Milliseconds 500
    }
    $false
}

# =============================================================================================
# Phases
# =============================================================================================

function Invoke-DetectionPhase {
    $smi = Get-NvidiaSmi
    $status = Get-RuntimeStatus

    if (-not $smi) {
        return New-PhaseResult -Phase 'GPU detection' -Status 'external_gpu_not_present' `
            -Detail 'nvidia-smi reported no GPU. PaRuX''s own view is still recorded so the two can be compared once one is attached.' `
            -Metrics @{
                'parux.gpuDetected'  = $status.gpuDetected
                'parux.vramTotalMib' = $status.vramTotalMib
            }
    }

    # A disagreement here is a real defect in hardware detection, so it is called out rather than
    # averaged away. PaRuX's own bar for "heavy GPU" is 12 GB, which is why a small GPU can be
    # present and correctly not qualify.
    $agrees = ($smi.VramTotalMib -ge 12000) -eq [bool]$status.gpuDetected
    New-PhaseResult -Phase 'GPU detection' -Status 'measured' `
        -Detail $(if ($agrees) { 'PaRuX agrees with the hardware.' }
                  else { 'MISMATCH: PaRuX''s gpuDetected disagrees with the installed VRAM.' }) `
        -Metrics @{
            'gpu.name'            = $smi.Name
            'gpu.driver'          = $smi.Driver
            'gpu.vramTotalMib'    = $smi.VramTotalMib
            'gpu.idleVramUsedMib' = $smi.VramUsedMib
            'gpu.idleTempC'       = $smi.TemperatureC
            'parux.gpuDetected'   = $status.gpuDetected
            'parux.vramTotalMib'  = $status.vramTotalMib
            'detection.agrees'    = $agrees
        }
}

function Invoke-BackendPhase {
    $status = Get-RuntimeStatus
    $cuda = $status.backends | Where-Object { $_.backend -eq 'text-cuda' }
    $vulkan = $status.backends | Where-Object { $_.backend -eq 'text-vulkan' }
    $metrics = @{
        'backend.cudaInstalled'   = if ($cuda) { $cuda.installed } else { $false }
        'backend.vulkanInstalled' = if ($vulkan) { $vulkan.installed } else { $false }
        'runtime.activeProfile'   = $status.activeProfile
        'runtime.activeModelPath' = $status.activeModelPath
        'runtime.activeHasVision' = $status.activeHasVisionSupport
    }
    try {
        $installed = Invoke-Core -Path '/api/v1/runtime/builds/installed'
        $metrics['backend.installedBuilds'] = ($installed | ForEach-Object { "$($_.backend):$($_.tag)" }) -join ', '
    } catch { $metrics['backend.installedBuilds'] = "(unavailable: $($_.Exception.Message))" }

    if (-not $script:HasGpu) {
        return New-PhaseResult -Phase 'Backend selection' -Status 'external_gpu_not_present' `
            -Detail 'The CUDA backend cannot be exercised without an NVIDIA GPU; installed-state is still recorded.' `
            -Metrics $metrics
    }
    New-PhaseResult -Phase 'Backend selection' -Status 'measured' -Metrics $metrics
}

function Invoke-LoadPhase {
    if (-not $script:HasGpu) {
        return New-PhaseResult -Phase 'Cold and warm model load' -Status 'external_gpu_not_present' `
            -Detail 'A cold-load figure taken on the small no-GPU fallback profile would not be comparable to the GPU profile this phase exists to measure, so it is not recorded as one.'
    }
    if (-not $script:HeavyGpuProfile) {
        # A physical GPU is present, so this is not an absence. It is below the heavy-profile bar, so a load would run
        # the fallback profile, whose numbers are not comparable. The phase says so instead of recording them.
        return New-PhaseResult -Phase 'Cold and warm model load' -Status 'unsupported' `
            -Detail 'The GPU is present but below PaRuX''s heavy-profile bar, so a load would run the fallback profile. A cold-load figure on that profile would not be comparable, so it is not recorded as one.'
    }

    Invoke-RuntimeAction -Action 'Off' | Out-Null
    Start-Sleep -Seconds 2

    $cold = Measure-VramWhile {
        $watch = [System.Diagnostics.Stopwatch]::StartNew()
        Invoke-RuntimeAction -Action 'Standard' | Out-Null
        $healthy = Wait-TextHealthy
        $watch.Stop()
        [pscustomobject]@{ Healthy = $healthy; Ms = $watch.Elapsed.TotalMilliseconds }
    }
    if (-not $cold.Value.Healthy) {
        return New-PhaseResult -Phase 'Cold and warm model load' -Status 'error' `
            -Detail 'The runtime never became healthy after a cold start.'
    }

    # Warm load is the idempotent re-request against an already-serving process. It measures the
    # reuse path, not a second load, and a warm figure close to the cold one means reuse is broken.
    $warmWatch = [System.Diagnostics.Stopwatch]::StartNew()
    Invoke-RuntimeAction -Action 'Standard' | Out-Null
    $warmWatch.Stop()

    New-PhaseResult -Phase 'Cold and warm model load' -Status 'measured' -Metrics @{
        'load.coldMs'        = [int]$cold.Value.Ms
        'load.warmMs'        = [int]$warmWatch.Elapsed.TotalMilliseconds
        'load.peakVramMib'   = $cold.PeakVramMib
        'load.vramSamples'   = $cold.SampleCount
    }
}

function Invoke-ThroughputPhase {
    if (-not (Wait-TextHealthy -TimeoutSeconds 60)) {
        return New-PhaseResult -Phase 'TTFT and decode throughput' -Status 'error' -Detail 'No healthy text runtime.'
    }

    $prompt = 'Explain in three sentences why prompt caching reduces time to first token.'
    $measurement = Measure-VramWhile {
        $runs = @()
        for ($i = 0; $i -lt $Repetitions; $i++) { $runs += Measure-Chat -Prompt $prompt -MaxTokens $ChatMaxTokens }
        $runs
    }
    $runs = @($measurement.Value | Where-Object { $null -ne $_.TtftMs })
    if (-not $runs) {
        return New-PhaseResult -Phase 'TTFT and decode throughput' -Status 'error' -Detail 'No streamed token arrived.'
    }

    $ttfts = [double[]]($runs | ForEach-Object { $_.TtftMs })
    $rates = [double[]]($runs | Where-Object { $_.TokensPerSecond } | ForEach-Object { $_.TokensPerSecond })
    # Numbers from the fallback profile are real but come from a different model, so they carry their own status.
    $status = if ($script:HeavyGpuProfile) { 'measured' } else { 'measured_fallback' }
    New-PhaseResult -Phase 'TTFT and decode throughput' -Status $status `
        -Detail $(if ($script:HeavyGpuProfile) { '' } else { 'Measured on the fallback profile. Real, but NOT comparable to a heavy GPU run - it is a different model.' }) `
        -Metrics @{
            'chat.runs'            = $runs.Count
            'chat.ttftP50Ms'       = [int](Get-Percentile -Values $ttfts -Percentile 50)
            'chat.ttftP95Ms'       = [int](Get-Percentile -Values $ttfts -Percentile 95)
            'chat.decodeP50TokS'   = if ($rates) { [Math]::Round((Get-Percentile -Values $rates -Percentile 50), 2) } else { $null }
            'chat.peakVramMib'     = $measurement.PeakVramMib
            'chat.peakUtilization' = $measurement.PeakUtilizatio
            'chat.peakTempC'       = $measurement.PeakTempC
        }
}

function Invoke-LongContextPhase {
    $filler = ('The quick brown fox jumps over the lazy dog. ' * [Math]::Ceiling($LongContextChars / 45))
    $prompt = $filler.Substring(0, $LongContextChars) + "`n`nIn one sentence: what animal is mentioned?"
    try {
        $measurement = Measure-VramWhile { Measure-Chat -Prompt $prompt -MaxTokens 64 }
    } catch {
        return New-PhaseResult -Phase 'Long context' -Status 'error' -Detail $_.Exception.Message
    }
    $run = $measurement.Value
    if ($null -eq $run.TtftMs) {
        return New-PhaseResult -Phase 'Long context' -Status 'error' -Detail 'No streamed token arrived for the long prompt.'
    }
    New-PhaseResult -Phase 'Long context' -Status 'measured' -Metrics @{
        'longContext.chars'          = $LongContextChars
        'longContext.ttftMs'         = [int]$run.TtftMs
        'longContext.promptCharsPerS' = [int]($LongContextChars / ($run.TtftMs / 1000.0))
        'longContext.peakVramMib'    = $measurement.PeakVramMib
    }
}

function Invoke-SwitchPhase {
    if (-not $script:HasGpu) {
        return New-PhaseResult -Phase 'Model switch' -Status 'external_gpu_not_present' `
            -Detail 'Both profiles collapse onto the same small fallback model without a heavy GPU, so a "switch" here would swap a model for itself and measure nothing.'
    }
    $timings = @{}
    foreach ($action in @('Uncensored', 'Standard')) {
        $watch = [System.Diagnostics.Stopwatch]::StartNew()
        $result = Invoke-RuntimeAction -Action $action
        $healthy = Wait-TextHealthy
        $watch.Stop()
        $timings["switch.to$($action)Ms"] = [int]$watch.Elapsed.TotalMilliseconds
        $timings["switch.to$($action)Healthy"] = $healthy
        if (-not $result.success) { $timings["switch.to$($action)Message"] = $result.message }
    }
    New-PhaseResult -Phase 'Model switch' -Status 'measured' -Metrics $timings
}

function Invoke-ConcurrencyPhase {
    $prompt = 'List three prime numbers.'
    $jobs = 1..$ConcurrentRequests | ForEach-Object {
        Start-ThreadJob -ScriptBlock {
            param($url, $token, $prompt, $maxTokens)
            $body = @{
                model = 'local'; stream = $false; max_tokens = $maxTokens
                messages = @(@{ role = 'user'; content = $prompt })
            } | ConvertTo-Json -Depth 8
            $watch = [System.Diagnostics.Stopwatch]::StartNew()
            try {
                Invoke-RestMethod -Method Post -Uri "$url/api/v1/chat/completions" `
                    -Headers @{ 'X-PaRuX-Token' = $token } -Body $body -ContentType 'application/json' -TimeoutSec 300 | Out-Null
                @{ ok = $true; ms = $watch.Elapsed.TotalMilliseconds }
            } catch {
                @{ ok = $false; ms = $watch.Elapsed.TotalMilliseconds; error = $_.Exception.Message }
            }
        } -ArgumentList $script:Core.url, $script:Core.token, $prompt, 48
    }
    $outcomes = @(Receive-ConcurrencyOutcomes -Jobs @($jobs) -DeadlineSeconds 600)
    $jobs | Remove-Job -Force -ErrorAction SilentlyContinue

    $succeeded = @($outcomes | Where-Object { $_.ok })
    New-PhaseResult -Phase 'Concurrent requests' -Status 'measured' `
        -Detail 'A single llama-server slot serializes these by design; the point is that none is lost or corrupted, not that they run in parallel.' `
        -Metrics @{
            'concurrency.requested' = $ConcurrentRequests
            'concurrency.succeeded' = $succeeded.Count
            # Every request that did not succeed counts, including one still running at the deadline.
            'concurrency.failed'    = $ConcurrentRequests - $succeeded.Count
            'concurrency.maxMs'     = if ($outcomes) { [int](($outcomes | ForEach-Object { $_.ms } | Measure-Object -Maximum).Maximum) } else { $null }
        }
}

function Invoke-OomPhase {
    if (-not $script:HasGpu) {
        return New-PhaseResult -Phase 'OOM refusal and recovery' -Status 'external_gpu_not_present' `
            -Detail 'Needs a real VRAM ceiling to push against.'
    }
    # The pass condition is an HONEST REFUSAL followed by a still-working runtime - not a
    # successful load. A runtime that accepts a model it cannot fit and then dies is the failure
    # this phase exists to catch.
    $library = Invoke-Core -Path '/api/v1/models/library'
    $status = Get-RuntimeStatus
    $tooBig = $library |
        Where-Object { $_.sizeBytes -gt ($status.vramTotalMib * 1MB * 1.5) } |
        Sort-Object sizeBytes -Descending | Select-Object -First 1
    if (-not $tooBig) {
        return New-PhaseResult -Phase 'OOM refusal and recovery' -Status 'skipped' `
            -Detail 'No managed model in the library is large enough to exceed this GPU''s VRAM, so there is nothing to refuse.'
    }

    $refused = $false; $message = ''
    try {
        $result = Invoke-Core -Path "/api/v1/models/library/$([uri]::EscapeDataString($tooBig.fileName))/load" `
            -Method Post -Body @{ role = 'standard' }
        $refused = -not $result.success
        $message = $result.message
    } catch { $refused = $true; $message = $_.Exception.Message }

    Invoke-RuntimeAction -Action 'Standard' | Out-Null
    $recovered = Wait-TextHealthy

    New-PhaseResult -Phase 'OOM refusal and recovery' -Status 'measured' -Metrics @{
        'oom.candidate'        = $tooBig.fileName
        'oom.candidateSizeMib' = [int]($tooBig.sizeBytes / 1MB)
        'oom.refusedHonestly'  = $refused
        'oom.message'          = $message
        'oom.recovered'        = $recovered
    }
}

function Invoke-UnloadPhase {
    if (-not $script:HasGpu) {
        return New-PhaseResult -Phase 'Unload and VRAM release' -Status 'external_gpu_not_present' `
            -Detail 'VRAM release cannot be observed without a GPU reporting VRAM.'
    }
    $before = (Get-NvidiaSmi).VramUsedMib
    Invoke-Core -Path '/api/v1/models/library/unload' -Method Post | Out-Null
    Start-Sleep -Seconds 5
    $after = (Get-NvidiaSmi).VramUsedMib

    New-PhaseResult -Phase 'Unload and VRAM release' -Status 'measured' -Metrics @{
        'unload.vramBeforeMib'   = $before
        'unload.vramAfterMib'    = $after
        'unload.releasedMib'     = $before - $after
        'unload.releasedPercent' = if ($before -gt 0) { [Math]::Round(100.0 * ($before - $after) / $before, 1) } else { $null }
    }
}

function Invoke-GameModePhase {
    # Gaming Mode is a hard-off for heavy AI (architecture rule 9), so the pass condition is that
    # the text runtime is really gone and its VRAM really returned - not merely that a flag flipped.
    # A game already on was not started by this run, so the phase sends neither GameStart nor GameStop for it.
    $gameAlreadyOn = [bool](Get-RuntimeStatus).gamingActive
    $ownsGame = @(Get-GameModePhasePlan -GameAlreadyOn $gameAlreadyOn) -contains 'GameStart'
    $beforeVram = if ($script:HasGpu) { (Get-NvidiaSmi).VramUsedMib } else { $null }
    if ($ownsGame) {
        $script:SentGameStart = $true
        Invoke-RuntimeAction -Action 'GameStart' | Out-Null
    }
    Start-Sleep -Seconds 4
    $during = Get-RuntimeStatus
    $duringVram = if ($script:HasGpu) { (Get-NvidiaSmi).VramUsedMib } else { $null }
    $textStillRunning = @($during.backends | Where-Object { $_.backend -like 'text-*' -and $_.running }).Count -gt 0

    $blocked = $false; $blockMessage = ''
    try {
        $attempt = Invoke-RuntimeAction -Action 'Uncensored'
        $blocked = -not $attempt.success
        $blockMessage = $attempt.message
    } catch { $blocked = $true; $blockMessage = $_.Exception.Message }

    if ($ownsGame) {
        Invoke-RuntimeAction -Action 'GameStop' | Out-Null
        $script:SentGameStart = $false
    }

    New-PhaseResult -Phase 'Game Mode hard-off' -Status 'measured' -Metrics @{
        'gameMode.reported'         = $during.gamingActive
        'gameMode.textStopped'      = -not $textStillRunning
        'gameMode.startBlocked'     = $blocked
        'gameMode.blockMessage'     = $blockMessage
        'gameMode.vramBeforeMib'    = $beforeVram
        'gameMode.vramDuringMib'    = $duringVram
        'gameMode.vramReleasedMib'  = if ($null -ne $beforeVram) { $beforeVram - $duringVram } else { $null }
    }
}

# =============================================================================================
# Run
# =============================================================================================
$script:Core = Resolve-Bootstrap
$script:Http = [System.Net.Http.HttpClient]::new()
$script:Http.Timeout = [TimeSpan]::FromMinutes(10)

Write-Host "PaRuX GPU benchmark" -ForegroundColor Cyan
Write-Host "  Core: $($script:Core.url)"

$smi = Get-NvidiaSmi
$initialStatus = Get-RuntimeStatus
$gpuGate = Resolve-GpuGate -NvidiaSmiPresent ($null -ne $smi) -HeavyProfile ([bool]$initialStatus.gpuDetected)
$script:HasGpu = $gpuGate.PhysicalGpuPresent
$script:HeavyGpuProfile = $gpuGate.HeavyGpuProfile
$script:GamingWasActive = [bool]$initialStatus.gamingActive
$script:SentGameStart = $false
$startingProfile = $initialStatus.activeProfile

# Gaming Mode is a hard-off. A benchmark loads models and sends chat requests, so it is refused outright during a game
# rather than run with some phases skipped.
if ($script:GamingWasActive) {
    Write-Host '  Gaming Mode is active. The benchmark is refused so that no AI work starts during a game.' -ForegroundColor Red
    exit 2
}

if (-not $script:HasGpu) {
    Write-Host "  EXTERNAL_GPU_NOT_PRESENT - GPU-only phases are recorded with the PHYSICAL_GPU_TEST_REQUIRED gate, not skipped silently." -ForegroundColor Yellow
} else {
    Write-Host "  GPU: $($smi.Name) (driver $($smi.Driver), $($smi.VramTotalMib) MiB)" -ForegroundColor Green
}

if ($CpuOnly) {
    $refusal = Get-CpuLaunchRefusal
    if ($refusal) {
        Write-Host "  CPU-only run refused: $refusal." -ForegroundColor Red
        exit 2
    }
    Write-Host '  CPU launch verified: llama-server runs with --gpu-layers 0 and no device pin.' -ForegroundColor Green
}
Write-Host ''

# GPU-only phases measure VRAM behaviour and have no meaning on a CPU launch.
$gpuOnlyPhases = @('load', 'switch', 'oom', 'unload', 'gamemode')

$phaseMap = [ordered]@{
    detection    = { Invoke-DetectionPhase }
    backend      = { Invoke-BackendPhase }
    load         = { Invoke-LoadPhase }
    throughput   = { Invoke-ThroughputPhase }
    longcontext  = { Invoke-LongContextPhase }
    switch       = { Invoke-SwitchPhase }
    concurrency  = { Invoke-ConcurrencyPhase }
    oom          = { Invoke-OomPhase }
    unload       = { Invoke-UnloadPhase }
    gamemode     = { Invoke-GameModePhase }
}

try {
    foreach ($name in $phaseMap.Keys) {
        if ($Phases -notcontains $name) { continue }
        if ($CpuOnly -and $gpuOnlyPhases -contains $name) {
            $gate = if ($script:HasGpu) { '' } else { 'PHYSICAL_GPU_TEST_REQUIRED' }
            Add-Phase (New-PhaseResult -Phase $name -Status 'skipped' -Gate $gate `
                -Detail 'CPU-only run: this phase measures GPU behaviour and is not run on a CPU launch.')
            continue
        }
        try { Add-Phase (& $phaseMap[$name]) }
        catch {
            # One phase failing must not abandon the rest; the failure is itself a recorded result.
            Add-Phase (New-PhaseResult -Phase $name -Status 'error' -Detail $_.Exception.Message)
        }
    }
}
finally {
    # Leave the machine as it was found, even after a mid-run failure. Gaming Mode is read again here: a game that started
    # during the run must not have its AI profile restored underneath it, and a state that cannot be read counts as on.
    try { $gamingNow = [bool](Get-RuntimeStatus).gamingActive } catch { $gamingNow = $true }
    try {
        $gamingWasActive = $script:GamingWasActive -or $gamingNow
        foreach ($action in @(Get-RunTeardownPlan -SentGameStart $script:SentGameStart -GamingWasActive $gamingWasActive -StartingProfile $startingProfile)) {
            Invoke-RuntimeAction -Action $action | Out-Null
            if ($action -eq 'GameStop') { $script:SentGameStart = $false }
        }
    } catch { Write-Warning "Restore step failed: $($_.Exception.Message)" }
    $script:Http.Dispose()
}

New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$stamp = (Get-Date).ToString('yyyyMMdd-HHmmss')
$report = [pscustomobject]@{
    measuredUtc     = (Get-Date).ToUniversalTime().ToString('o')
    runLabel        = $script:RunLabel
    externalGpu     = if ($script:HasGpu) { 'present' } else { 'EXTERNAL_GPU_NOT_PRESENT' }
    physicalGpuTest = if ($script:HasGpu) { $null } else { 'PHYSICAL_GPU_TEST_REQUIRED' }
    gpu             = $smi
    paruxGpuView    = [pscustomobject]@{
        gpuDetected  = $initialStatus.gpuDetected
        vramTotalMib = $initialStatus.vramTotalMib
        ramTotalBytes = $initialStatus.ramTotalBytes
    }
    startingProfile = $startingProfile
    phases          = $script:Results
}

$jsonPath = Join-Path $OutputDirectory "gpu-benchmark-$stamp.json"
$report | ConvertTo-Json -Depth 12 | Set-Content -Path $jsonPath -Encoding UTF8

$markdown = [System.Text.StringBuilder]::new()
[void]$markdown.AppendLine("# PaRuX GPU benchmark - $stamp")
[void]$markdown.AppendLine()
[void]$markdown.AppendLine("External GPU: **$($report.externalGpu)**")
if ($report.physicalGpuTest) { [void]$markdown.AppendLine("Physical GPU test: **$($report.physicalGpuTest)**") }
[void]$markdown.AppendLine("Run label: **$($report.runLabel)**")
if ($smi) { [void]$markdown.AppendLine("GPU: $($smi.Name), driver $($smi.Driver), $($smi.VramTotalMib) MiB") }
[void]$markdown.AppendLine()
[void]$markdown.AppendLine('| Phase | Status | Detail |')
[void]$markdown.AppendLine('| --- | --- | --- |')
foreach ($result in $script:Results) {
    [void]$markdown.AppendLine("| $($result.phase) | ``$($result.status)`` | $($result.detail -replace '\|', '\|') |")
}
foreach ($result in $script:Results) {
    if ($result.metrics.Count -eq 0) { continue }
    [void]$markdown.AppendLine()
    [void]$markdown.AppendLine("## $($result.phase)")
    [void]$markdown.AppendLine()
    [void]$markdown.AppendLine('| Metric | Value |')
    [void]$markdown.AppendLine('| --- | --- |')
    foreach ($key in ($result.metrics.Keys | Sort-Object)) {
        [void]$markdown.AppendLine("| $key | $($result.metrics[$key]) |")
    }
}
$mdPath = Join-Path $OutputDirectory "gpu-benchmark-$stamp.md"
$markdown.ToString() | Set-Content -Path $mdPath -Encoding UTF8

Write-Host ''
Write-Host "JSON:     $jsonPath" -ForegroundColor Green
Write-Host "Markdown: $mdPath" -ForegroundColor Green

$errors = @($script:Results | Where-Object { $_.status -eq 'error' })
if ($errors) {
    Write-Host "$($errors.Count) phase(s) errored." -ForegroundColor Red
    exit 1
}
exit 0
