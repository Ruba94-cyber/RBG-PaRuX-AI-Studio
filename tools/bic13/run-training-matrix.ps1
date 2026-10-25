param([Parameter(Mandatory)][string]$EvidenceRoot)
# Sequential methods; this driver creates fresh run-scoped logs and stops on the first failed method.
$ErrorActionPreference = 'Stop'
$one = Join-Path $PSScriptRoot 'run-training-method.ps1'
$root = (Resolve-Path -LiteralPath $EvidenceRoot).Path
$steps = @(
    @{ Method = 'dpo'; Steps = 60; Lr = 0.00005 },
    @{ Method = 'continued-pretraining'; Steps = 60; Lr = 0.0001 },
    @{ Method = 'grpo'; Steps = 30; Lr = 0.00001 })

# Preflight every fixed child output before creating a matrix run or launching any training.
foreach ($step in $steps) {
    $target = Join-Path $root "runs/$($step.Method)"
    if (Test-Path -LiteralPath $target) {
        if (-not (Test-Path -LiteralPath $target -PathType Container) -or @(Get-ChildItem -LiteralPath $target -Force).Count -gt 0) {
            throw "Existing output/evidence found for $($step.Method); matrix did not start. Use a fresh isolated EvidenceRoot."
        }
    }
}

$matrixRoot = Join-Path $root 'matrix-runs'
New-Item -ItemType Directory -Path $matrixRoot -Force | Out-Null
$runId = [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ') + '-' + [Guid]::NewGuid().ToString('N')
$runDir = Join-Path $matrixRoot $runId
New-Item -ItemType Directory -Path $runDir -ErrorAction Stop | Out-Null
$statusPath = Join-Path $runDir 'matrix-status.json'
$status = [ordered]@{ run_id = $runId; status = 'running'; complete = $false; started_utc = [DateTime]::UtcNow.ToString('o');
    current_method = $null; methods = @() }

function Save-RunStatus {
    $json = $status | ConvertTo-Json -Depth 8
    [IO.File]::WriteAllText($statusPath, $json + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
}

Save-RunStatus
$powershellExe = if ($PSVersionTable.PSEdition -eq 'Core') { Join-Path $PSHOME 'pwsh.exe' }
                else { Join-Path $PSHOME 'powershell.exe' }
foreach ($step in $steps) {
    $method = $step.Method
    $status.current_method = $method
    $status.methods += [ordered]@{ method = $method; status = 'running'; started_utc = [DateTime]::UtcNow.ToString('o') }
    Save-RunStatus
    $log = Join-Path $runDir "$method.console.log"
    try {
        & $powershellExe -NoProfile -ExecutionPolicy Bypass -File $one -Method $method -EvidenceRoot $root `
            -MaxSteps $step.Steps -LearningRate $step.Lr *> $log
        $code = $LASTEXITCODE
        if ($code -ne 0) { throw "child PowerShell exited with code $code" }
        $entry = $status.methods[-1]
        $entry.status = 'complete'
        $entry.exit_code = $code
        $entry.finished_utc = [DateTime]::UtcNow.ToString('o')
        Save-RunStatus
    } catch {
        $entry = $status.methods[-1]
        $entry.status = 'failed'
        $entry.error = [string]$_
        $entry.finished_utc = [DateTime]::UtcNow.ToString('o')
        $status.status = 'failed'
        $status.complete = $false
        $status.failure = "$method failed; later methods were not started"
        $status.finished_utc = [DateTime]::UtcNow.ToString('o')
        Save-RunStatus
        throw "Training matrix run $runId stopped after $method failed; see $log."
    }
}
$status.status = 'complete'
$status.complete = $true
$status.current_method = $null
$status.finished_utc = [DateTime]::UtcNow.ToString('o')
Save-RunStatus
Write-Output "Training matrix run $runId completed; inspect current-run matrix-status.json and each method's held-out evaluation."
