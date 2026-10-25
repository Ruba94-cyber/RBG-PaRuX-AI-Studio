param(
    [string]$Corpus = (Join-Path $PSScriptRoot '..\benchmarks\multi-agent\dependency-handoff.json'),
    [string]$Workspace = (Join-Path $PSScriptRoot '..'),
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\artifacts\agent-benchmark'),
    [int]$PollSeconds = 2,
    [int]$TimeoutMinutes = 45
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Get-Bootstrap {
    $path = Join-Path $env:LOCALAPPDATA 'PaRuX\server\bootstrap.json'
    if (-not (Test-Path $path)) { throw "PaRuX Core is not running. Bootstrap not found: $path" }
    $value = Get-Content $path -Raw | ConvertFrom-Json
    if (-not $value.endpoint -or -not $value.token) { throw 'PaRuX bootstrap is incomplete.' }
    return $value
}

function Invoke-CoreJson {
    param([string]$Method, [string]$Path, $Body = $null)
    $headers = @{ 'X-PaRuX-Token' = $script:Bootstrap.token }
    $uri = $script:Bootstrap.endpoint.TrimEnd('/') + $Path
    if ($null -eq $Body) {
        return Invoke-RestMethod -Method $Method -Uri $uri -Headers $headers -TimeoutSec 90
    }
    return Invoke-RestMethod -Method $Method -Uri $uri -Headers $headers -ContentType 'application/json' `
        -Body ($Body | ConvertTo-Json -Depth 16) -TimeoutSec 90
}

function New-AgentTask {
    param([string]$Goal, [string]$Role, [string[]]$DependsOn = @())
    return Invoke-CoreJson POST '/api/v1/orchestrator/tasks' @{
        goal = $Goal
        role = $Role
        dependsOnTaskIds = @($DependsOn)
        maxRepairAttempts = 1
    }
}

function Wait-Task([string]$TaskId, [DateTime]$Deadline) {
    $terminal = @('completed','failed','cancelled')
    while ([DateTime]::UtcNow -lt $Deadline) {
        $task = Invoke-CoreJson GET "/api/v1/orchestrator/tasks/$TaskId"
        if ($terminal -contains [string]$task.status) { return $task }
        Start-Sleep -Seconds ([Math]::Max(1, $PollSeconds))
    }
    throw "Task $TaskId did not reach a terminal state before the benchmark deadline."
}

function Get-ResultText($Task) {
    if ([string]::IsNullOrWhiteSpace([string]$Task.resultJson)) { return '' }
    try {
        $json = ([string]$Task.resultJson) | ConvertFrom-Json
        if ($json.PSObject.Properties.Name -contains 'response') { return [string]$json.response }
        if ($json.PSObject.Properties.Name -contains 'summary') { return [string]$json.summary }
    } catch { }
    return [string]$Task.resultJson
}

$script:Bootstrap = Get-Bootstrap
$spec = Get-Content $Corpus -Raw | ConvertFrom-Json
$token = [string]$spec.token
$artifact = [string]$spec.artifact
if ([string]::IsNullOrWhiteSpace($token) -or [string]::IsNullOrWhiteSpace($artifact)) {
    throw 'The multi-agent benchmark corpus must define token and artifact.'
}

$Workspace = [IO.Path]::GetFullPath($Workspace)
$gitRoot = (& git -C $Workspace rev-parse --show-toplevel 2>$null | Select-Object -First 1)
if ([string]::IsNullOrWhiteSpace([string]$gitRoot)) { throw "Workspace is not a Git repository: $Workspace" }
$Workspace = [IO.Path]::GetFullPath(([string]$gitRoot).Trim())
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null

$started = [DateTime]::UtcNow
$deadline = $started.AddMinutes($TimeoutMinutes)

# Stage 1: only this predecessor is told the opaque value.
$research = New-AgentTask -Role 'research' -Goal (
    "Produce a concise evidence note whose critical fact is exactly `"DEPENDENCY_TOKEN=$token`". " +
    "Preserve the token byte-for-byte. Do not invent a different value.")

# Stage 2: deliberately DOES NOT contain the token. Success therefore requires the dependency
# result handoff implemented by PaRuX; guessing cannot satisfy the deterministic acceptance check.
$coding = New-AgentTask -Role 'coding' -DependsOn @([string]$research.id) -Goal (
    "Read the direct predecessor evidence supplied by PaRuX. Find its DEPENDENCY_TOKEN value and create exactly one file at $artifact " +
    "whose only non-empty line is DEPENDENCY_TOKEN=<the predecessor value>. Do not invent the value and do not change unrelated files. Inspect git diff before reporting done.")
$null = Invoke-CoreJson PUT "/api/v1/orchestrator/tasks/$($coding.id)/workspace" @{ workspaceRoot = $Workspace }

# Stage 3: review is also not told the value. It must receive the coding result/diff through the
# same bounded predecessor-evidence protocol and report the token it actually observes there.
$review = New-AgentTask -Role 'review' -DependsOn @([string]$coding.id) -Goal (
    "Review the direct predecessor result supplied by PaRuX. State the exact DEPENDENCY_TOKEN value visible in that predecessor evidence, and briefly say whether the predecessor reports a narrowly scoped change. Do not guess a token that is absent from evidence.")

$null = Invoke-CoreJson POST '/api/v1/orchestrator/schedule'
Write-Host "Research: $($research.id)"
Write-Host "Coding  : $($coding.id)"
Write-Host "Review  : $($review.id)"

$researchFinal = Wait-Task ([string]$research.id) $deadline
$codingFinal = Wait-Task ([string]$coding.id) $deadline
$reviewFinal = Wait-Task ([string]$review.id) $deadline

$worktree = ''
try { $worktree = [string](Invoke-CoreJson GET "/api/v1/orchestrator/tasks/$($coding.id)/worktree").worktreePath } catch { }
$artifactText = ''
$artifactExists = $false
if (-not [string]::IsNullOrWhiteSpace($worktree) -and (Test-Path $worktree -PathType Container)) {
    $candidate = [IO.Path]::GetFullPath((Join-Path $worktree ($artifact.Replace('/', [IO.Path]::DirectorySeparatorChar))))
    $root = [IO.Path]::GetFullPath($worktree).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    if ($candidate.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -and
        (Test-Path $candidate -PathType Leaf)) {
        $artifactExists = $true
        $artifactText = (Get-Content $candidate -Raw).Trim()
    }
}

$researchText = Get-ResultText $researchFinal
$reviewText = Get-ResultText $reviewFinal
$expectedLine = "DEPENDENCY_TOKEN=$token"
$researchPassed = ([string]$researchFinal.status -eq 'completed' -and $researchText.Contains($expectedLine, [StringComparison]::Ordinal))
$codingPassed = ([string]$codingFinal.status -eq 'completed' -and $artifactExists -and [string]::Equals($artifactText, $expectedLine, [StringComparison]::Ordinal))
$reviewPassed = ([string]$reviewFinal.status -eq 'completed' -and $reviewText.Contains($token, [StringComparison]::Ordinal))
$passed = $researchPassed -and $codingPassed -and $reviewPassed

$report = [pscustomobject]@{
    schemaVersion = 1
    benchmark = [string]$spec.name
    startedUtc = $started.ToString('o')
    completedUtc = [DateTime]::UtcNow.ToString('o')
    success = $passed
    research = [pscustomobject]@{
        taskId = [string]$research.id
        status = [string]$researchFinal.status
        verificationStatus = [string]$researchFinal.verificationStatus
        preservedOpaqueFact = $researchPassed
    }
    coding = [pscustomobject]@{
        taskId = [string]$coding.id
        status = [string]$codingFinal.status
        verificationStatus = [string]$codingFinal.verificationStatus
        reviewableWorktreeAvailable = (-not [string]::IsNullOrWhiteSpace($worktree))
        deterministicArtifactExists = $artifactExists
        deterministicArtifactExact = $codingPassed
    }
    review = [pscustomobject]@{
        taskId = [string]$review.id
        status = [string]$reviewFinal.status
        verificationStatus = [string]$reviewFinal.verificationStatus
        recoveredOpaqueFact = $reviewPassed
    }
}

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$path = Join-Path $OutputDirectory "multi-agent-handoff-$stamp.json"
$report | ConvertTo-Json -Depth 12 | Set-Content $path -Encoding UTF8

Write-Host ''
Write-Host "Multi-agent dependency handoff: $(if ($passed) { 'PASS' } else { 'FAIL' })"
Write-Host "  research preserves fact : $researchPassed"
Write-Host "  coding consumes handoff  : $codingPassed"
Write-Host "  review consumes diff     : $reviewPassed"
Write-Host "Report: $path"
if (-not $passed) { exit 1 }
