param(
    [Parameter(Mandatory, ParameterSetName='Run')][ValidateSet('sft','dpo','continued-pretraining','grpo','vision-sft','tts-sft')][string]$Method,
    [Parameter(Mandatory, ParameterSetName='Run')][string]$EvidenceRoot,   # isolated root holding data/train-venv, datasets, runtime-src (never the installed PaRuX data)
    [string]$BaseModel = 'unsloth/Qwen2.5-0.5B-Instruct-bnb-4bit',
    [int]$MaxSteps = 60,
    [double]$LearningRate = 0.0002,
    [int]$MaxSeq = 512,
    [int]$BatchSize = 4,
    [int]$GradAccum = 2,
    [double]$DpoBeta = 0.1,
    [string]$GrpoReward = 'exact-reference',
    [switch]$EvalOnly,   # re-run only the reload + held-out evaluation of an already trained adapter
    [switch]$CpuOnly,    # explicit CPU profile; allowed only for TTS SFT
    [ValidateRange(1,1440)][int]$TimeoutMinutes = 240,
    [ValidateRange(1,360)][int]$EvaluationTimeoutMinutes = 90,
    [Parameter(Mandatory, ParameterSetName='SelfTest')][switch]$HarnessSelfTest
)
# Runs the shipped PaRuX trainer script (parux_train.py, extracted verbatim from TrainingLabService)
# with a config shaped exactly like the service writes, inside the isolated venv, then reloads the
# produced adapter with eval_adapter.py (plain transformers+peft) and scores base vs adapter on held-out data.
$ErrorActionPreference = 'Stop'

function ConvertTo-NvidiaSmiInt {
    param([string]$Value, [int]$ExitCode, [string]$Query)
    if ($ExitCode -ne 0 -or $null -eq $Value -or $Value.Trim() -notmatch '^\d+$') {
        throw "nvidia-smi $Query returned invalid telemetry (exit=$ExitCode, value='$Value'). Refusing to start unmonitored training."
    }
    return [int]$Value.Trim()
}

function Read-NvidiaSmiInt {
    param([string]$Query)
    if (-not (Get-Command nvidia-smi -ErrorAction SilentlyContinue)) {
        throw 'nvidia-smi is unavailable. Use -CpuOnly with -Method tts-sft for the explicit CPU-only profile.'
    }
    $value = @(& nvidia-smi "--query-gpu=$Query" '--format=csv,noheader,nounits' 2>$null)
    $exitCode = $LASTEXITCODE
    if ($value.Count -eq 0) { return ConvertTo-NvidiaSmiInt -Value $null -ExitCode $exitCode -Query $Query }
    return ConvertTo-NvidiaSmiInt -Value ([string]$value[0]) -ExitCode $exitCode -Query $Query
}

function Resolve-TrainingHardware {
    param([string]$TrainingMethod, [bool]$ForceCpu)
    if ($ForceCpu -and $TrainingMethod -ne 'tts-sft') {
        throw '-CpuOnly is supported only for the advertised tts-sft method.'
    }
    $needsCuda = -not ($TrainingMethod -eq 'tts-sft' -and $ForceCpu)
    return [pscustomobject]@{
        RequiresCuda = $needsCuda
        Profile = if ($needsCuda) { 'cuda-required' } else { 'cpu-tts-only' }
    }
}

function Stop-OwnedProcessTree {
    param([Diagnostics.Process]$Process)
    if (-not $Process.HasExited) {
        try { $Process.Kill($true); $null = $Process.WaitForExit(10000) } catch { }
        if (-not $Process.HasExited) {
            $taskkill = Join-Path $env:SystemRoot 'System32/taskkill.exe'
            if (-not (Test-Path $taskkill)) { throw 'Cannot terminate the owned trainer process tree on this Windows host.' }
            & $taskkill /PID $Process.Id /T /F *> $null
            $killExitCode = $LASTEXITCODE
            if ($killExitCode -ne 0 -and -not $Process.HasExited) {
                throw "taskkill could not terminate owned process tree $($Process.Id) (exit $killExitCode)."
            }
        }
        if (-not $Process.WaitForExit(10000)) { throw "Owned process $($Process.Id) remained alive after tree termination." }
    }
}

function Start-BoundedProcess {
    param(
        [string]$FilePath, [string[]]$ArgumentList, [string]$WorkingDirectory,
        [string]$StdoutPath, [string]$StderrPath, [int]$TimeoutSeconds,
        [string]$GpuMemoryQuery
    )
    $quoted = foreach ($argument in $ArgumentList) { '"' + $argument.Replace('"', '\"') + '"' }
    $process = Start-Process -FilePath $FilePath -ArgumentList $quoted -WorkingDirectory $WorkingDirectory `
        -NoNewWindow -PassThru -RedirectStandardOutput $StdoutPath -RedirectStandardError $StderrPath
    $null = $process.Handle
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $peakMemoryMiB = 0
    $timedOut = $false
    $telemetryError = $null
    while (-not $process.HasExited) {
        $remainingMs = [Math]::Max(1, [Math]::Min(2000, $TimeoutSeconds * 1000 - $clock.ElapsedMilliseconds))
        if ($process.WaitForExit([int]$remainingMs)) { break }
        if ($clock.Elapsed.TotalSeconds -ge $TimeoutSeconds) {
            $timedOut = $true
            Stop-OwnedProcessTree -Process $process
            break
        }
        if ($GpuMemoryQuery) {
            try { $peakMemoryMiB = [Math]::Max($peakMemoryMiB, (Read-NvidiaSmiInt -Query $GpuMemoryQuery)) }
            catch {
                $telemetryError = $_.Exception.Message
                Stop-OwnedProcessTree -Process $process
                break
            }
        }
    }
    $process.WaitForExit()
    $clock.Stop()
    $exitCode = if ($timedOut) { 124 } elseif ($telemetryError) { 125 } else { $process.ExitCode }
    return [pscustomobject]@{
        Process = $process; TimedOut = $timedOut; TelemetryError = $telemetryError
        ExitCode = $exitCode; WallSeconds = $clock.Elapsed.TotalSeconds; PeakGpuMemoryMiB = $peakMemoryMiB
    }
}

function Invoke-BoundedEvaluator {
    param([string]$ScriptPath, [string[]]$Arguments, [string]$LogName)
    $stdoutPath = Join-Path $run "$LogName.stdout.log"
    $stderrPath = Join-Path $run "$LogName.stderr.log"
    $result = Start-BoundedProcess -FilePath $py -ArgumentList (@($ScriptPath) + $Arguments) `
        -WorkingDirectory $run -StdoutPath $stdoutPath -StderrPath $stderrPath `
        -TimeoutSeconds ($EvaluationTimeoutMinutes * 60)
    if ($result.TimedOut) { throw "Evaluation timed out after $EvaluationTimeoutMinutes minutes (see $stderrPath)." }
    if ($result.ExitCode -ne 0) { throw "Evaluation exited with code $($result.ExitCode) (see $stderrPath)." }
    Get-Content -LiteralPath $stdoutPath -Tail 1
}

if ($PSCmdlet.ParameterSetName -eq 'SelfTest') {
    $selfTestRoot = Join-Path (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path '.ai/artifacts/release-continuation-20261005/bic13/harness-selftest'
    New-Item -ItemType Directory -Force $selfTestRoot | Out-Null
    $powershellExe = (Get-Process -Id $PID).Path
    $timeout = Start-BoundedProcess -FilePath $powershellExe -ArgumentList @('-NoProfile','-Command','Start-Sleep -Seconds 30') `
        -WorkingDirectory $selfTestRoot -StdoutPath (Join-Path $selfTestRoot 'timeout.stdout.log') `
        -StderrPath (Join-Path $selfTestRoot 'timeout.stderr.log') -TimeoutSeconds 1
    if (-not $timeout.TimedOut -or -not $timeout.Process.HasExited) { throw 'Bounded-process timeout self-test failed.' }
    $success = Start-BoundedProcess -FilePath $powershellExe -ArgumentList @('-NoProfile','-Command','exit 0') `
        -WorkingDirectory $selfTestRoot -StdoutPath (Join-Path $selfTestRoot 'success.stdout.log') `
        -StderrPath (Join-Path $selfTestRoot 'success.stderr.log') -TimeoutSeconds 20
    if ($success.TimedOut -or $success.ExitCode -ne 0) { throw 'Bounded-process success self-test failed.' }
    $invalidTelemetryRejected = $false
    try { $null = ConvertTo-NvidiaSmiInt -Value 'not-a-number' -ExitCode 0 -Query 'utilization.gpu' }
    catch { $invalidTelemetryRejected = $true }
    $nonzeroTelemetryRejected = $false
    try { $null = ConvertTo-NvidiaSmiInt -Value '5' -ExitCode 1 -Query 'utilization.gpu' }
    catch { $nonzeroTelemetryRejected = $true }
    if (-not $invalidTelemetryRejected -or -not $nonzeroTelemetryRejected) {
        throw 'Malformed or failed GPU telemetry self-test failed.'
    }
    $cpuTts = Resolve-TrainingHardware -TrainingMethod 'tts-sft' -ForceCpu $true
    $cudaTts = Resolve-TrainingHardware -TrainingMethod 'tts-sft' -ForceCpu $false
    $cpuOtherRejected = $false
    try { $null = Resolve-TrainingHardware -TrainingMethod 'sft' -ForceCpu $true }
    catch { $cpuOtherRejected = $true }
    if ($cpuTts.RequiresCuda -or $cpuTts.Profile -ne 'cpu-tts-only' -or -not $cudaTts.RequiresCuda -or -not $cpuOtherRejected) {
        throw 'CPU-only profile self-test failed.'
    }
    'timeout-kills-owned-process=PASS','normal-exit=PASS','malformed-and-nonzero-telemetry-fail-closed=PASS',
    'explicit-cpu-profile-only-for-tts-sft=PASS' |
        Set-Content -LiteralPath (Join-Path $selfTestRoot 'result.txt')
    Get-Content (Join-Path $selfTestRoot 'result.txt')
    exit 0
}

$root = (Resolve-Path $EvidenceRoot).Path
$hardware = Resolve-TrainingHardware -TrainingMethod $Method -ForceCpu ([bool]$CpuOnly)
$requiresCuda = $hardware.RequiresCuda
$hardwareProfile = $hardware.Profile
$py = Join-Path $root 'data/train-venv/.venv/Scripts/python.exe'
$run = Join-Path $root "runs/$Method"
if (-not $EvalOnly -and (Test-Path $run) -and @(Get-ChildItem -LiteralPath $run -Force).Count -gt 0) {
    throw "Existing run evidence found for $Method. Use a fresh isolated EvidenceRoot so prior evidence is preserved."
}
if ($requiresCuda) {
    # Fail closed when telemetry is missing, nonzero or malformed; it must never become a false quiet window.
    $null = Read-NvidiaSmiInt -Query 'utilization.gpu'
    # Serialize against other heavy GPU work: require a quiet window (<15% util for 6 consecutive samples).
    $quiet = 0; $deadline = (Get-Date).AddMinutes(30)
    while ($quiet -lt 6 -and (Get-Date) -lt $deadline) {
        $u = Read-NvidiaSmiInt -Query 'utilization.gpu'
        if ($u -lt 15) { $quiet++ } else { $quiet = 0 }
        Start-Sleep -Seconds 2
    }
    if ($quiet -lt 6) { throw 'GPU did not reach a quiet window within 30 minutes; training was not started.' }
} else {
    # This matches TrainingRuntimeCatalog's CPU-only admission: TTS SFT is the only CPU method.
    # An empty value removes the variable on some PowerShell versions, exposing every GPU.
    $env:CUDA_VISIBLE_DEVICES = '-1'
}

New-Item -ItemType Directory -Force $run | Out-Null
$env:HF_HOME = Join-Path $root 'hf-home'
$env:HF_HUB_DISABLE_TELEMETRY = '1'
$env:PYTHONIOENCODING = 'utf-8'
$trainFile = @{ 'sft'='sft-train.jsonl'; 'dpo'='dpo-train.jsonl'; 'continued-pretraining'='cpt-train.jsonl'; 'grpo'='grpo-train.jsonl'; 'vision-sft'='vision-train.jsonl'; 'tts-sft'='tts-train.jsonl' }[$Method]
$dataDir = if ($Method -in 'vision-sft','tts-sft') { 'datasets-mm' } else { 'datasets' }
$config = [ordered]@{
    train_jsonl = (Join-Path $root "$dataDir/$trainFile"); dataset_dir = (Join-Path $root $dataDir)
    base_model = $BaseModel; batch_size = $BatchSize; gradient_accumulation_steps = $GradAccum
    lora_rank = 16; max_steps = $MaxSteps; learning_rate = $LearningRate
    output_dir = (Join-Path $run 'out'); method = $Method; dpo_beta = $DpoBeta; max_seq_length = $MaxSeq
    grpo_reward_mode = $GrpoReward; grpo_generations = 4; text_field = 'text'; image_field = 'image'; audio_field = 'audio'
    checkpoint_steps = [Math]::Max(1, [Math]::Min($MaxSteps, 50)); resume_checkpoint = $null
}
$configPath = Join-Path $run 'run-config.json'
if (-not $EvalOnly) {
    [IO.File]::WriteAllText($configPath, ($config | ConvertTo-Json), (New-Object Text.UTF8Encoding($false)))
}

$adapter = Join-Path $run 'out/lora-adapter'
$marker = Join-Path $run 'out/parux-training-complete'
if (-not $EvalOnly) {
$stdout = Join-Path $run 'train.stdout.log'; $stderr = Join-Path $run 'train.stderr.log'
$trainerScript = Join-Path $root 'runtime-src/parux_train.py'
$gpuMemoryQuery = if ($requiresCuda) { 'memory.used' } else { $null }
$outcome = Start-BoundedProcess -FilePath $py -ArgumentList @($trainerScript, $configPath) `
    -WorkingDirectory $run -StdoutPath $stdout -StderrPath $stderr `
    -TimeoutSeconds ($TimeoutMinutes * 60) -GpuMemoryQuery $gpuMemoryQuery
$proc = $outcome.Process
$adapterExists = Test-Path $adapter
$markerExists = Test-Path $marker
$trainingAccepted = $outcome.ExitCode -eq 0 -and $adapterExists -and $markerExists
if (-not $trainingAccepted -and $markerExists) {
    Remove-Item -LiteralPath $marker -Force
    $markerExists = $false
}
$summary = [ordered]@{
    method = $Method; exitCode = $outcome.ExitCode; trainerPid = $proc.Id; wallSeconds = [Math]::Round($outcome.WallSeconds, 1)
    timedOut = $outcome.TimedOut; timeoutMinutes = $TimeoutMinutes; telemetryError = $outcome.TelemetryError
    hardwareProfile = $hardwareProfile; peakGpuMemoryUsedMiB_systemWide = if ($requiresCuda) { $outcome.PeakGpuMemoryMiB } else { $null }
    adapterExists = $adapterExists; completionMarker = $markerExists
    checkpoints = @(Get-ChildItem (Join-Path $run 'out') -Directory -Filter 'checkpoint-*' -ErrorAction SilentlyContinue | ForEach-Object Name)
    baseModel = $BaseModel; maxSteps = $MaxSteps; learningRate = $LearningRate; maxSeq = $MaxSeq; batch = $BatchSize; gradAccum = $GradAccum
}
[IO.File]::WriteAllText((Join-Path $run "train-summary.json"), ($summary | ConvertTo-Json), (New-Object Text.UTF8Encoding($false)))
if (-not $trainingAccepted) {
    Write-Output ($summary | ConvertTo-Json -Compress); throw "training failed for $Method (see $stderr)"
}
}
if ($EvalOnly -and -not (Test-Path $marker)) { throw "no completed adapter for $Method" }

if ($Method -eq 'vision-sft') {
    $held = Join-Path $root 'datasets-mm/vision-heldout.jsonl'; $dd = Join-Path $root 'datasets-mm'
    Invoke-BoundedEvaluator -ScriptPath (Join-Path $PSScriptRoot 'eval_vision_adapter.py') `
        -Arguments @($adapter, $held, $dd, (Join-Path $run 'eval-adapter.json')) -LogName 'eval-adapter'
    Invoke-BoundedEvaluator -ScriptPath (Join-Path $PSScriptRoot 'eval_vision_adapter.py') `
        -Arguments @("base:$BaseModel", $held, $dd, (Join-Path $run 'eval-base.json')) -LogName 'eval-base'
} elseif ($Method -eq 'tts-sft') {
    $held = Join-Path $root 'datasets-mm/tts-heldout.jsonl'; $dd = Join-Path $root 'datasets-mm'
    Invoke-BoundedEvaluator -ScriptPath (Join-Path $PSScriptRoot 'eval_tts_adapter.py') `
        -Arguments @($adapter, $held, $dd, (Join-Path $run 'eval-adapter.json')) -LogName 'eval-adapter'
    Invoke-BoundedEvaluator -ScriptPath (Join-Path $PSScriptRoot 'eval_tts_adapter.py') `
        -Arguments @("base:$BaseModel", $held, $dd, (Join-Path $run 'eval-base.json')) -LogName 'eval-base'
} else {
    Invoke-BoundedEvaluator -ScriptPath (Join-Path $PSScriptRoot 'eval_adapter.py') `
        -Arguments @($adapter, (Join-Path $root 'datasets/heldout.jsonl'), (Join-Path $run 'eval-adapter.json'), "$Method-adapter") `
        -LogName 'eval-adapter'
    if (-not (Test-Path (Join-Path $root 'runs/eval-base.json'))) {
        Invoke-BoundedEvaluator -ScriptPath (Join-Path $PSScriptRoot 'eval_adapter.py') `
            -Arguments @("base:$BaseModel", (Join-Path $root 'datasets/heldout.jsonl'), (Join-Path $root 'runs/eval-base.json'), 'base') `
            -LogName 'eval-base'
    }
}
if (-not (Test-Path (Join-Path $run 'eval-adapter.json'))) { throw "adapter evaluation produced no result for $Method" }
if ($summary) { Write-Output ($summary | ConvertTo-Json -Compress) }
