param(
    [string]$Corpus = (Join-Path $PSScriptRoot '..\benchmarks\coding-agent\parux-corpus.json'),
    [string]$Workspace = (Join-Path $PSScriptRoot '..'),
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\artifacts\agent-benchmark'),
    [int]$PollSeconds = 2,
    [int]$TimeoutMinutes = 45
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Get-Bootstrap {
    # An isolated PaRuX data root (PARUX_DATA_ROOT) keeps benchmark tasks away from the user's own instance.
    $dataRoot = if ([string]::IsNullOrWhiteSpace($env:PARUX_DATA_ROOT)) { Join-Path $env:LOCALAPPDATA 'PaRuX' } else { $env:PARUX_DATA_ROOT }
    $path = Join-Path $dataRoot 'server\bootstrap.json'
    if (-not (Test-Path $path)) { throw "PaRuX Core bootstrap not found at $path. Start PaRuX locally first." }
    $bootstrap = Get-Content $path -Raw | ConvertFrom-Json
    if (-not $bootstrap.endpoint -or -not $bootstrap.token) { throw 'PaRuX Core bootstrap is incomplete.' }
    return $bootstrap
}

function Invoke-CoreJson {
    param([string]$Method, [string]$Path, $Body = $null)
    $uri = $script:Bootstrap.endpoint.TrimEnd('/') + $Path
    $headers = @{ 'X-PaRuX-Token' = $script:Bootstrap.token }
    if ($null -eq $Body) {
        return Invoke-RestMethod -Method $Method -Uri $uri -Headers $headers -TimeoutSec 60
    }
    $json = $Body | ConvertTo-Json -Depth 16
    return Invoke-RestMethod -Method $Method -Uri $uri -Headers $headers -ContentType 'application/json' -Body $json -TimeoutSec 60
}

function Get-TaskLogs([string]$TaskId) {
    $response = Invoke-CoreJson GET "/api/v1/orchestrator/tasks/$TaskId/logs"
    return @($response.logs)
}

function Get-DesktopWorktree([string]$TaskId) {
    try {
        # /desktop-worktree, not /worktree: the latter answered 405 and the catch below turned that
        # into "no worktree", so every case looked like it had produced no isolated checkout.
        $response = Invoke-CoreJson GET "/api/v1/orchestrator/tasks/$TaskId/desktop-worktree"
        return [string]$response.worktreePath
    } catch {
        return ''
    }
}

function Test-CommandEvidence {
    param([object[]]$Logs, [string]$Kind)
    $pattern = if ($Kind -eq 'test') {
        '(?i)(dotnet\s+test|gradlew?.*\btest\b|pytest|ctest|npm\s+test|npm\s+run\s+test|pnpm\s+test|cargo\s+test|go\s+test|flutter\s+test|phpunit|rspec)'
    } else {
        '(?i)(dotnet\s+build|gradlew?.*\b(build|assemble)\b|cmake\s+--build|meson\s+compile|npm\s+run\s+build|pnpm\s+build|cargo\s+build|go\s+build|mvnw?.*\b(package|verify)\b|flutter\s+analyze)'
    }
    $matches = @($Logs | Where-Object { $_.message -match $pattern })
    if ($matches.Count -eq 0) { return $null }
    return [bool](@($matches | Where-Object { $_.message -match '(?i)exit\s+0' }).Count -gt 0)
}

function Get-ChangedPaths([string]$Worktree) {
    if ([string]::IsNullOrWhiteSpace($Worktree) -or -not (Test-Path $Worktree -PathType Container)) { return @() }
    $tracked = @(& git -C $Worktree diff --name-only HEAD 2>$null)
    $untracked = @(& git -C $Worktree ls-files --others --exclude-standard 2>$null)
    return @($tracked + $untracked | ForEach-Object { ([string]$_).Trim().Replace('\\','/') } |
        Where-Object { $_ } | Sort-Object -Unique)
}

function Resolve-WorktreePath([string]$Worktree, [string]$RelativePath) {
    if ([string]::IsNullOrWhiteSpace($Worktree)) { return $null }
    $root = [IO.Path]::GetFullPath($Worktree).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    $relative = $RelativePath.Replace('/', [IO.Path]::DirectorySeparatorChar)
    $candidate = [IO.Path]::GetFullPath((Join-Path $root $relative))
    $prefix = $root + [IO.Path]::DirectorySeparatorChar
    if (-not $candidate.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) -and
        -not $candidate.Equals($root, [StringComparison]::OrdinalIgnoreCase)) {
        return $null
    }
    return $candidate
}

function Test-DeterministicAcceptance {
    param($Case, $Task, [object[]]$Logs, [string]$Worktree)

    $acceptance = $Case.acceptance
    if ($null -eq $acceptance) {
        return [pscustomobject]@{ Passed = $true; Failures = @(); ChangedPaths = @(); VerificationPassed = $null }
    }

    $failures = New-Object System.Collections.Generic.List[string]
    if ([string]::IsNullOrWhiteSpace($Worktree) -or -not (Test-Path $Worktree -PathType Container)) {
        $failures.Add('Desktop-only worktree is missing or inaccessible.')
        return [pscustomobject]@{ Passed = $false; Failures = @($failures); ChangedPaths = @(); VerificationPassed = $false }
    }

    foreach ($relative in @($acceptance.requiredFiles)) {
        $full = Resolve-WorktreePath $Worktree ([string]$relative)
        if ($null -eq $full -or -not (Test-Path $full -PathType Leaf)) {
            $failures.Add("Required file missing: $relative")
        }
    }

    foreach ($rule in @($acceptance.requiredText)) {
        $relative = [string]$rule.path
        $needle = [string]$rule.contains
        $full = Resolve-WorktreePath $Worktree $relative
        if ($null -eq $full -or -not (Test-Path $full -PathType Leaf)) {
            $failures.Add("Cannot inspect required text because file is missing: $relative")
            continue
        }
        $text = Get-Content $full -Raw
        if (-not $text.Contains($needle, [StringComparison]::Ordinal)) {
            $failures.Add("Required text not found in ${relative}: $needle")
        }
    }

    $changed = @(Get-ChangedPaths $Worktree)
    $allowed = @($acceptance.allowedChangedPathPrefixes | ForEach-Object { ([string]$_).Replace('\\','/') })
    if ($allowed.Count -gt 0) {
        foreach ($path in $changed) {
            $ok = $false
            foreach ($prefix in $allowed) {
                if ($path.Equals($prefix, [StringComparison]::OrdinalIgnoreCase) -or
                    $path.StartsWith($prefix.TrimEnd('/') + '/', [StringComparison]::OrdinalIgnoreCase)) {
                    $ok = $true; break
                }
            }
            if (-not $ok) { $failures.Add("Out-of-scope changed path: $path") }
        }
    }

    $buildPassed = Test-CommandEvidence $Logs 'build'
    $testsPassed = Test-CommandEvidence $Logs 'test'
    $verificationPassed = ($buildPassed -eq $true -or $testsPassed -eq $true)
    if ([bool]$acceptance.requireVerificationSuccess -and -not $verificationPassed) {
        $failures.Add('Required build/test verification success was not observed in real command logs.')
    }

    return [pscustomobject]@{
        Passed = ($failures.Count -eq 0)
        Failures = @($failures)
        ChangedPaths = $changed
        VerificationPassed = $verificationPassed
    }
}

$script:Bootstrap = Get-Bootstrap
$Workspace = [IO.Path]::GetFullPath($Workspace)
if (-not (Test-Path $Workspace -PathType Container)) { throw "Benchmark workspace does not exist: $Workspace" }
$gitRoot = (& git -C $Workspace rev-parse --show-toplevel 2>$null | Select-Object -First 1)
if ([string]::IsNullOrWhiteSpace([string]$gitRoot)) { throw "Benchmark workspace must be a Git repository: $Workspace" }
$Workspace = [IO.Path]::GetFullPath(([string]$gitRoot).Trim())

$corpusDocument = Get-Content $Corpus -Raw | ConvertFrom-Json
$tasks = @($corpusDocument.tasks)
if ($tasks.Count -eq 0) { throw 'Benchmark corpus contains no tasks.' }
if ([int]$corpusDocument.schemaVersion -lt 2) { Write-Warning 'Corpus schema predates deterministic acceptance rules.' }

New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$startedUtc = [DateTime]::UtcNow
$runtimeBefore = $null
try { $runtimeBefore = Invoke-CoreJson GET '/api/v1/runtime/performance/status' } catch { Write-Warning "Runtime performance snapshot unavailable: $($_.Exception.Message)" }

# One task at a time with a per-case deadline. Creating every task up front let Core run several
# concurrently (GPU contention, queue time counted as wall time) and gave PaRuX one 45-minute
# budget for the whole corpus while external agents get that budget per case.
$created = @()
$terminal = @('completed','failed','cancelled')
$finalById = @{}
foreach ($case in $tasks) {
    $request = @{
        goal = [string]$case.goal
        role = 'coding'
        maxRepairAttempts = if ($null -ne $case.maxRepairAttempts) { [int]$case.maxRepairAttempts } else { 1 }
    }
    $task = Invoke-CoreJson POST '/api/v1/orchestrator/tasks' $request
    # Repository authority is granted through /repositories, as a set with one primary.
    $null = Invoke-CoreJson PUT "/api/v1/orchestrator/tasks/$($task.id)/repositories" @{
        primaryWorkspaceRoot = $Workspace
        additionalRepositories = @()
    }
    $null = Invoke-CoreJson POST '/api/v1/orchestrator/schedule'
    $entry = [pscustomobject]@{ Case = $case; TaskId = [string]$task.id }
    $created += $entry
    Write-Host "[$($case.id)] created task $($task.id) with task-scoped repository authority"

    $deadline = [DateTime]::UtcNow.AddMinutes($TimeoutMinutes)
    while (-not $finalById.ContainsKey($entry.TaskId)) {
        $current = Invoke-CoreJson GET "/api/v1/orchestrator/tasks/$($entry.TaskId)"
        if ($null -ne $current -and $terminal -contains [string]$current.status) {
            $finalById[$entry.TaskId] = $current
            Write-Host "[$($case.id)] terminal: $($current.status)"
            break
        }
        if ([DateTime]::UtcNow -gt $deadline) {
            Write-Warning "[$($case.id)] exceeded $TimeoutMinutes minute(s); cancelling."
            try { $null = Invoke-CoreJson POST "/api/v1/orchestrator/tasks/$($entry.TaskId)/cancel" } catch { }
            Start-Sleep -Seconds 5
            $finalById[$entry.TaskId] = Invoke-CoreJson GET "/api/v1/orchestrator/tasks/$($entry.TaskId)"
            break
        }
        Start-Sleep -Seconds ([Math]::Max(1, $PollSeconds))
    }
}

$rows = @()
foreach ($entry in $created) {
    $task = $finalById[$entry.TaskId]
    $worktree = Get-DesktopWorktree $entry.TaskId
    $logs = Get-TaskLogs $entry.TaskId
    $stepLogs = @($logs | Where-Object { $_.message -match '^Step\s+\d+:' })
    $commandLogs = @($logs | Where-Object { $_.message -match 'run_command' })
    $browserLogs = @($logs | Where-Object { $_.message -match '^Step\s+\d+: browser_' })
    $protocolErrors = @($logs | Where-Object { $_.message -match '(?i)invalid action protocol|protocol correction' })
    $watchdogs = @($logs | Where-Object { $_.message -match '(?i)progress watchdog' })
    $contextLine = @($logs | Where-Object { $_.message -match '(?i)(Observed active model context|Active model context is unknown)' }) | Select-Object -First 1
    $sandboxCommands = @($logs | Where-Object { $_.message -match 'run_command \[AppContainer\]' })
    $result = $null
    if ($task.resultJson) {
        try { $result = $task.resultJson | ConvertFrom-Json } catch { }
    }

    $acceptance = Test-DeterministicAcceptance $entry.Case $task $logs $worktree
    $orchestratorCompleted = ([string]$task.status -eq 'completed')
    $benchmarkSuccess = ($orchestratorCompleted -and [bool]$acceptance.Passed)
    $wallMs = ([DateTime]$task.updatedUtc - [DateTime]$task.createdUtc).TotalMilliseconds

    $rows += [pscustomobject]@{
        caseId = [string]$entry.Case.id
        taskId = [string]$task.id
        status = [string]$task.status
        success = $benchmarkSuccess
        orchestratorCompleted = $orchestratorCompleted
        deterministicAcceptance = [bool]$acceptance.Passed
        acceptanceFailures = @($acceptance.Failures)
        changedPaths = @($acceptance.ChangedPaths)
        verificationStatus = [string]$task.verificationStatus
        verificationEvidencePassed = $acceptance.VerificationPassed
        wallTimeMs = [Math]::Round($wallMs, 0)
        agentSteps = $stepLogs.Count
        toolCommands = $commandLogs.Count
        sandboxedCommands = $sandboxCommands.Count
        browserActions = $browserLogs.Count
        protocolErrors = $protocolErrors.Count
        progressWatchdogEvents = $watchdogs.Count
        repairAttemptsUsed = [int]$task.repairAttemptsUsed
        maxRepairAttempts = [int]$task.maxRepairAttempts
        goalReported = if ($null -ne $result) { [bool]$result.goalReported } else { $false }
        buildPassed = Test-CommandEvidence $logs 'build'
        testsPassed = Test-CommandEvidence $logs 'test'
        contextBudgetEvidence = if ($null -ne $contextLine) { [string]$contextLine.message } else { $null }
        error = [string]$task.error
        reviewableWorktreeAvailable = (-not [string]::IsNullOrWhiteSpace($worktree) -and (Test-Path $worktree -PathType Container))
    }
}

$runtimeAfter = $null
try { $runtimeAfter = Invoke-CoreJson GET '/api/v1/runtime/performance/status' } catch { }
$codingProfiles = if ($null -ne $runtimeAfter) { @($runtimeAfter.profiles | Where-Object { $_.workload -eq 'coding' } | Sort-Object tunedUtc -Descending) } else { @() }
$profile = $codingProfiles | Select-Object -First 1

$completedUtc = [DateTime]::UtcNow
$successfulRows = @($rows | Where-Object success)
$deterministicRows = @($rows | Where-Object deterministicAcceptance)
$summary = [pscustomobject]@{
    schemaVersion = 3
    corpus = [string]$corpusDocument.name
    workspaceIdentity = [IO.Path]::GetFileName($Workspace.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar))
    startedUtc = $startedUtc.ToString('o')
    completedUtc = $completedUtc.ToString('o')
    totalWallTimeMs = [Math]::Round(($completedUtc - $startedUtc).TotalMilliseconds, 0)
    tasks = $rows.Count
    successfulTasks = $successfulRows.Count
    successRatePercent = if ($rows.Count -gt 0) { [Math]::Round(100.0 * $successfulRows.Count / $rows.Count, 1) } else { 0 }
    deterministicAcceptanceCount = $deterministicRows.Count
    deterministicAcceptanceRatePercent = if ($rows.Count -gt 0) { [Math]::Round(100.0 * $deterministicRows.Count / $rows.Count, 1) } else { 0 }
    totalAgentSteps = ($rows | Measure-Object agentSteps -Sum).Sum
    totalToolCommands = ($rows | Measure-Object toolCommands -Sum).Sum
    totalSandboxedCommands = ($rows | Measure-Object sandboxedCommands -Sum).Sum
    totalBrowserActions = ($rows | Measure-Object browserActions -Sum).Sum
    totalProtocolErrors = ($rows | Measure-Object protocolErrors -Sum).Sum
    totalRepairAttempts = ($rows | Measure-Object repairAttemptsUsed -Sum).Sum
    runtimeEvidence = if ($null -ne $profile) { [pscustomobject]@{
        modelPath = [IO.Path]::GetFileName([string]$profile.modelPath)
        workload = [string]$profile.workload
        ttftMilliseconds = $profile.timeToFirstTokenMilliseconds
        promptTokensPerSecond = $profile.promptTokensPerSecond
        decodeTokensPerSecond = $profile.decodeTokensPerSecond
        sustainedDecodeTokensPerSecond = $profile.sustainedDecodeTokensPerSecond
        concurrentDecodeTokensPerSecond = $profile.concurrentDecodeTokensPerSecond
        concurrentRequests = $profile.concurrentRequests
        tunedUtc = $profile.tunedUtc
    } } else { $null }
    results = $rows
}

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$jsonPath = Join-Path $OutputDirectory "coding-agent-$stamp.json"
$csvPath = Join-Path $OutputDirectory "coding-agent-$stamp.csv"
$summary | ConvertTo-Json -Depth 16 | Set-Content -Path $jsonPath -Encoding UTF8
$rows | Select-Object caseId,taskId,status,success,orchestratorCompleted,deterministicAcceptance,verificationStatus,verificationEvidencePassed,wallTimeMs,agentSteps,toolCommands,sandboxedCommands,browserActions,protocolErrors,repairAttemptsUsed,maxRepairAttempts,goalReported,buildPassed,testsPassed,reviewableWorktreeAvailable,error |
    Export-Csv -Path $csvPath -NoTypeInformation -Encoding UTF8

Write-Host ''
Write-Host "PaRuX coding-agent benchmark: $($summary.successfulTasks)/$($summary.tasks) fully succeeded ($($summary.successRatePercent)%)"
Write-Host "Deterministic worktree acceptance: $($summary.deterministicAcceptanceCount)/$($summary.tasks) ($($summary.deterministicAcceptanceRatePercent)%)"
Write-Host "Agent steps: $($summary.totalAgentSteps); tool commands: $($summary.totalToolCommands); AppContainer commands: $($summary.totalSandboxedCommands); repairs: $($summary.totalRepairAttempts); protocol errors: $($summary.totalProtocolErrors)"
if ($null -ne $summary.runtimeEvidence) {
    Write-Host "Runtime evidence: TTFT=$($summary.runtimeEvidence.ttftMilliseconds) ms; decode=$($summary.runtimeEvidence.decodeTokensPerSecond) tok/s; concurrent=$($summary.runtimeEvidence.concurrentDecodeTokensPerSecond) tok/s"
}
foreach ($row in @($rows | Where-Object { -not $_.success })) {
    Write-Warning "[$($row.caseId)] failed benchmark acceptance/status. $([string]::Join('; ', @($row.acceptanceFailures))) $($row.error)"
}
Write-Host "JSON: $jsonPath"
Write-Host "CSV : $csvPath"
