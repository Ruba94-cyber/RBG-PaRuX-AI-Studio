<#
.SYNOPSIS
  Runs real PaRuX coding tasks against local fixtures and records honest, per-run evidence.

.DESCRIPTION
  Creates desktop coding tasks through the same Core boundary the Chat /code handoff uses
  (POST /api/v1/orchestrator/coding-tasks + /logs + /schedule), polls each task to a terminal
  state, and records Core status, verification, step count, wall time, protocol trouble, the
  "Coding model:" attribution line, and the GGUF actually resident in llama-server while the task
  ran (process command line + nvidia-smi memory). A run counts as passed ONLY when Core reports
  status=completed AND verificationStatus=pass. Independently, the task worktree diff is applied to
  a fresh clone of the fixture at its baseline commit and the fixture's tests are run on the host.

  Requirements: a running PaRuX Server bound to -Endpoint whose data root is -DataRoot, and a
  running PaRuX desktop App on the same data root (the desktop worker claims coding tasks).
  Tasks run strictly one at a time so model attribution cannot mix.

.PARAMETER Fixtures
  JSON file: array of { name, path, objective }. path is a Git repository at its baseline commit.

.EXAMPLE
  pwsh tools/coding-model-benchmark.ps1 -DataRoot D:\bench\data -Fixtures D:\bench\fixtures.json `
       -Repetitions 1 -Label qwen-4b -ExpectedModelFile Qwen3.5-4B-Q4_K_M.gguf -OutDir D:\bench\results
#>
[CmdletBinding()]
param(
    [string]$Endpoint = "http://127.0.0.1:51123",
    [Parameter(Mandatory)] [string]$DataRoot,
    [Parameter(Mandatory)] [string]$Fixtures,
    [int]$Repetitions = 1,
    [int]$TaskTimeoutMinutes = 20,
    [Parameter(Mandatory)] [string]$OutDir,
    [string]$Label = "run",
    [string]$ExpectedModelFile = "",
    [string]$NuGetSource = (Join-Path $env:USERPROFILE ".nuget\packages"),
    [switch]$SkipHostCheck
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$HandoffSuffix = "`n`nCHAT HANDOFF CONTEXT (supporting evidence; the objective and repository state are authoritative):`n`n" +
    "Use the existing repository conventions, preserve unrelated user changes, run the discovered verification after the final code mutation, and report concrete blockers rather than fabricating success."
$TerminalStatuses = @("completed", "failed", "cancelled")
$ProtocolMarkers = @("invalid action protocol", "Paused after", "Coding attempt paused without verification",
    "Paused by the progress watchdog", "Paused at the runaway safety ceiling")

New-Item -ItemType Directory -Force $OutDir | Out-Null
$bootstrapPath = Join-Path $DataRoot "server\bootstrap.json"
if (-not (Test-Path $bootstrapPath)) { throw "No PaRuX bootstrap at $bootstrapPath; start PaRuX Server on this data root first." }
$token = (Get-Content $bootstrapPath -Raw | ConvertFrom-Json).token
$headers = @{ Authorization = "Bearer $token" }

function Invoke-Core([string]$Method, [string]$Path, $Body = $null) {
    $request = @{ Uri = "$Endpoint$Path"; Method = $Method; Headers = $headers; ContentType = "application/json"; TimeoutSec = 120 }
    if ($null -ne $Body) { $request.Body = ($Body | ConvertTo-Json -Depth 10 -Compress) }
    Invoke-RestMethod @request
}

function Get-ResidentModel {
    $proc = Get-CimInstance Win32_Process -Filter "Name='llama-server.exe'" -ErrorAction SilentlyContinue | Select-Object -First 1
    $model = $null
    if ($proc -and $proc.CommandLine -match '(?:^|\s)(?:-m|--model)\s+"?([^"]+?\.gguf)"?(?:\s|$)') { $model = Split-Path $Matches[1] -Leaf }
    $gpu = $null
    try { $gpu = [int]((& nvidia-smi --query-gpu=memory.used --format=csv,noheader,nounits 2>$null | Select-Object -First 1).Trim()) } catch { }
    [pscustomobject]@{ utc = (Get-Date).ToUniversalTime().ToString("o"); llamaPid = $(if ($proc) { $proc.ProcessId } else { $null }); model = $model; gpuMiB = $gpu }
}

function Invoke-HostCheck([string]$FixturePath, [string]$TaskId, [string]$WorkDir) {
    $worktree = Join-Path $FixturePath ".parux-agents\$TaskId"
    if (-not (Test-Path $worktree)) { return [pscustomobject]@{ performed = $false; reason = "task worktree not present (removed by PaRuX)"; testsPassed = $null } }
    $clone = Join-Path $WorkDir "hostcheck-$TaskId"
    if (Test-Path $clone) { Remove-Item -LiteralPath $clone -Recurse -Force }
    git clone -q --no-hardlinks $FixturePath $clone 2>&1 | Out-Null
    $patch = Join-Path $WorkDir "hostcheck-$TaskId.patch"
    # Read-only on the PaRuX worktree: tracked diff plus a copy of untracked (non-ignored) files.
    git -C $worktree diff --binary "--output=$patch" HEAD 2>&1 | Out-Null
    if (-not (Test-Path $patch)) { New-Item -ItemType File $patch | Out-Null }
    $applied = $true
    if ((Get-Item $patch).Length -gt 0) {
        git -C $clone apply --whitespace=nowarn $patch 2>&1 | Out-Null
        $applied = ($LASTEXITCODE -eq 0)
    }
    $untracked = @(git -C $worktree ls-files --others --exclude-standard)
    foreach ($relative in $untracked) {
        $target = Join-Path $clone $relative
        New-Item -ItemType Directory -Force (Split-Path $target) | Out-Null
        Copy-Item -LiteralPath (Join-Path $worktree $relative) -Destination $target -Force
    }
    if (-not $applied) { return [pscustomobject]@{ performed = $true; reason = "git apply of the task diff failed"; testsPassed = $false; patch = $patch } }
    $log = Join-Path $WorkDir "hostcheck-$TaskId-test.log"
    Push-Location $clone
    try {
        dotnet restore --source $NuGetSource 2>&1 | Out-File $log
        dotnet test --no-restore 2>&1 | Out-File $log -Append
        $exit = $LASTEXITCODE
    } finally { Pop-Location }
    Remove-Item -LiteralPath $clone -Recurse -Force -ErrorAction SilentlyContinue
    [pscustomobject]@{ performed = $true; reason = "dotnet test on a baseline clone + task diff"; testsPassed = ($exit -eq 0); exitCode = $exit; untrackedCopied = $untracked; patch = $patch; log = $log }
}

$fixtureList = Get-Content $Fixtures -Raw | ConvertFrom-Json
$runs = New-Object System.Collections.Generic.List[object]
$stamp = (Get-Date).ToString("yyyyMMdd-HHmmss")
$jsonPath = Join-Path $OutDir "benchmark-$Label-$stamp.json"
$mdPath = Join-Path $OutDir "benchmark-$Label-$stamp.md"

foreach ($rep in 1..$Repetitions) {
    foreach ($fx in $fixtureList) {
        $baseline = (git -C $fx.path rev-parse HEAD).Trim()
        $dirty = git -C $fx.path status --porcelain --untracked-files=no
        if ($dirty) { throw "Fixture $($fx.name) has tracked changes; restore its baseline before benchmarking." }

        $samples = New-Object System.Collections.Generic.List[object]
        $samples.Add((Get-ResidentModel))
        $task = Invoke-Core POST "/api/v1/orchestrator/coding-tasks" @{
            goal = $fx.objective + $HandoffSuffix; workspaceRoot = $fx.path
            codingPermissionProfile = "offline"; maxRepairAttempts = 2; additionalRepositories = @() }
        Invoke-Core POST "/api/v1/orchestrator/tasks/$($task.id)/logs" @{ message = "Benchmark task ($Label, fixture $($fx.name), repetition $rep) created through the Core coding boundary; coding permission profile=offline." } | Out-Null
        Invoke-Core POST "/api/v1/orchestrator/schedule" @{} | Out-Null
        Write-Host ("[{0}] {1} rep {2}: task {3}" -f $Label, $fx.name, $rep, $task.id)

        $deadline = (Get-Date).AddMinutes($TaskTimeoutMinutes)
        $timedOut = $false
        $status = $null
        while ($true) {
            Start-Sleep -Seconds 10
            $status = Invoke-Core GET "/api/v1/orchestrator/tasks/$($task.id)"
            $samples.Add((Get-ResidentModel))
            if ($TerminalStatuses -contains $status.status) { break }
            if ((Get-Date) -gt $deadline) {
                $timedOut = $true
                Invoke-Core POST "/api/v1/orchestrator/tasks/$($task.id)/cancel" @{} | Out-Null
                Start-Sleep -Seconds 15
                $status = Invoke-Core GET "/api/v1/orchestrator/tasks/$($task.id)"
                break
            }
        }

        $logs = @((Invoke-Core GET "/api/v1/orchestrator/tasks/$($task.id)/logs").logs)
        $messages = @($logs | ForEach-Object { $_.message })
        $steps = 0
        foreach ($m in $messages) { if ($m -match '^Step (\d+):') { $steps = [Math]::Max($steps, [int]$Matches[1]) } }
        $modelLine = $messages | Where-Object { $_ -like "Coding model:*" } | Select-Object -First 1
        $trouble = @($messages | Where-Object { $line = $_; $ProtocolMarkers | Where-Object { $line -like "*$_*" } })
        $verifyCmd = @($messages | Where-Object { $_ -match 'run_command .*"(dotnet (build|test)( --no-restore)?)" \(exit 0\)' })
        $residentModels = @($samples | Where-Object { $_.model } | ForEach-Object { $_.model } | Select-Object -Unique)
        $wall = ([datetime]$status.updatedUtc - [datetime]$status.createdUtc).TotalSeconds
        $corePassed = ($status.status -eq "completed" -and $status.verificationStatus -eq "pass")
        $hostCheck = if ($SkipHostCheck) { [pscustomobject]@{ performed = $false; reason = "skipped"; testsPassed = $null } } else { Invoke-HostCheck $fx.path $task.id $OutDir }
        $headAfter = (git -C $fx.path rev-parse HEAD).Trim()

        [IO.File]::WriteAllText((Join-Path $OutDir "task-$($task.id)-logs.json"), ($logs | ConvertTo-Json -Depth 5))
        $run = [pscustomobject]@{
            label = $Label; fixture = $fx.name; repetition = $rep; taskId = $task.id
            status = $status.status; stage = $status.stage; verificationStatus = $status.verificationStatus
            passed = $corePassed; timedOut = $timedOut; steps = $steps; wallSeconds = [Math]::Round($wall, 1)
            protocolTroubleLines = $trouble.Count; acceptedVerificationCommandSucceeded = ($verifyCmd.Count -gt 0)
            codingModelLog = $modelLine; expectedModel = $ExpectedModelFile
            residentModels = $residentModels
            residentMatchesExpected = if ($ExpectedModelFile) { ($residentModels.Count -eq 1 -and $residentModels[0] -ieq $ExpectedModelFile) } else { $null }
            maxGpuMiB = ($samples | Where-Object { $_.gpuMiB } | Measure-Object gpuMiB -Maximum).Maximum
            hostCheck = $hostCheck; fixtureHeadUnchanged = ($headAfter -eq $baseline)
        }
        $runs.Add($run)
        [IO.File]::WriteAllText($jsonPath, ($runs | ConvertTo-Json -Depth 6))
        Write-Host ("  -> {0}/{1} verification={2} passed={3} steps={4} wall={5}s resident={6} host={7}" -f $run.status, $run.stage, $run.verificationStatus, $run.passed, $run.steps, $run.wallSeconds, ($residentModels -join ","), $hostCheck.testsPassed)
    }
}

$passed = @($runs | Where-Object passed)
$verifiedTimes = @($passed | ForEach-Object { $_.wallSeconds } | Sort-Object)
$median = if ($verifiedTimes.Count -eq 0) { $null } elseif ($verifiedTimes.Count % 2) { $verifiedTimes[[int][Math]::Floor($verifiedTimes.Count / 2)] } else { ($verifiedTimes[$verifiedTimes.Count / 2 - 1] + $verifiedTimes[$verifiedTimes.Count / 2]) / 2 }
$md = New-Object System.Text.StringBuilder
[void]$md.AppendLine("# Coding model benchmark: $Label")
[void]$md.AppendLine("")
[void]$md.AppendLine("Core-verified: $($passed.Count)/$($runs.Count); median verified wall time: $median s; expected model: $ExpectedModelFile")
[void]$md.AppendLine("")
[void]$md.AppendLine("| fixture | rep | task | status | stage | verification | passed | steps | wall s | trouble | accepted cmd ok | resident | host tests |")
[void]$md.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|---|---|")
foreach ($r in $runs) {
    [void]$md.AppendLine("| $($r.fixture) | $($r.repetition) | $($r.taskId.Substring(0,8)) | $($r.status) | $($r.stage) | $($r.verificationStatus) | $($r.passed) | $($r.steps) | $($r.wallSeconds) | $($r.protocolTroubleLines) | $($r.acceptedVerificationCommandSucceeded) | $($r.residentModels -join ',') | $($r.hostCheck.testsPassed) |")
}
[IO.File]::WriteAllText($mdPath, $md.ToString())
Write-Host "Report: $jsonPath"
Write-Host "Report: $mdPath"
