[CmdletBinding()]
param(
    [switch]$SkipBuild,
    [switch]$SkipServerChecks,
    [switch]$RunEngineRace,
    [switch]$RunAutoChatSmoke,
    [string]$ServerBaseUri,
    [string]$ServerToken,
    [string]$BaselineStableKey,
    [string]$CandidateStableKey,
    [string]$ExLlamaTag,
    [ValidateSet('chat','coding','long-context')]
    [string]$Workload = 'chat',
    [Nullable[double]]$BaselineQualityScore,
    [Nullable[double]]$CandidateQualityScore
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

function Write-Step([string]$Text) {
    Write-Host "`n=== $Text ==="
}

function Invoke-Checked([string]$FilePath, [string[]]$Arguments) {
    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Command failed with exit code ${LASTEXITCODE}: $FilePath $($Arguments -join ' ')"
    }
}

function Get-Bootstrap {
    $path = Join-Path $env:LOCALAPPDATA 'PaRuX\server\bootstrap.json'
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "PaRuX Server bootstrap not found at $path. Start PaRuX first or pass -ServerBaseUri and -ServerToken."
    }
    return Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
}

function Invoke-ParuxApi {
    param(
        [Parameter(Mandatory=$true)][string]$Method,
        [Parameter(Mandatory=$true)][string]$Path,
        $Body = $null,
        [switch]$Anonymous
    )

    $headers = @{}
    if (-not $Anonymous) {
        if ([string]::IsNullOrWhiteSpace($script:ResolvedToken)) {
            throw 'A PaRuX Server token is required for this endpoint.'
        }
        $headers['X-PaRuX-Token'] = $script:ResolvedToken
    }

    $uri = $script:ResolvedBaseUri.TrimEnd('/') + $Path
    $args = @{
        Method = $Method
        Uri = $uri
        Headers = $headers
        TimeoutSec = 900
    }
    if ($null -ne $Body) {
        $args['ContentType'] = 'application/json'
        $args['Body'] = ($Body | ConvertTo-Json -Depth 20 -Compress)
    }
    return Invoke-RestMethod @args
}

function Require-Value([string]$Name, [string]$Value) {
    if ([string]::IsNullOrWhiteSpace($Value)) {
        throw "$Name is required for -RunEngineRace."
    }
}

if (-not $SkipBuild) {
    Write-Step 'Restore solution'
    Invoke-Checked 'dotnet' @('restore', 'PaRuX.sln')

    Write-Step 'Release build'
    Invoke-Checked 'dotnet' @('build', 'PaRuX.sln', '-c', 'Release', '--no-restore')

    Write-Step 'Server regression tests'
    Invoke-Checked 'dotnet' @('test', 'tests/PaRuX.Server.Tests/PaRuX.Server.Tests.csproj', '-c', 'Release', '--no-restore')
}

if ($SkipServerChecks) {
    Write-Host '`nBIC-P0 compile/test phase completed. Server/GPU checks were skipped explicitly.'
    exit 0
}

$bootstrap = $null
if ([string]::IsNullOrWhiteSpace($ServerBaseUri) -or [string]::IsNullOrWhiteSpace($ServerToken)) {
    $bootstrap = Get-Bootstrap
}
$script:ResolvedBaseUri = if ([string]::IsNullOrWhiteSpace($ServerBaseUri)) { [string]$bootstrap.endpoint } else { $ServerBaseUri }
$script:ResolvedToken = if ([string]::IsNullOrWhiteSpace($ServerToken)) { [string]$bootstrap.token } else { $ServerToken }
if ([string]::IsNullOrWhiteSpace($script:ResolvedBaseUri)) { throw 'PaRuX Server endpoint could not be resolved.' }

Write-Step 'PaRuX Core health'
$health = Invoke-ParuxApi -Method GET -Path '/api/v1/health' -Anonymous
$health | ConvertTo-Json -Depth 10

Write-Step 'Inference Engine Registry'
$enginesResponse = Invoke-ParuxApi -Method GET -Path '/api/v1/runtime/engines'
$engines = @()
foreach ($engine in $enginesResponse) { $engines += $engine }
$ex = $engines | Where-Object { $_.id -eq 'exllamav3' } | Select-Object -First 1
if ($null -eq $ex) { throw 'ExLlamaV3 descriptor is missing from the Engine Registry.' }
# EngineImplementationState.Integrated == 0; LocalInferenceCapabilities.Text == 1.
if ([int]$ex.implementationState -ne 0) { throw "ExLlamaV3 adapter is not Integrated: $($ex.implementationState)" }
if (([int]$ex.capabilities -band 1) -ne 1) { throw 'ExLlamaV3 does not advertise Text capability.' }
if (([int]$ex.capabilities -band 6) -ne 0) { throw 'ExLlamaV3 incorrectly advertises Vision or ToolCalling before those adapters are implemented.' }
$ex | ConvertTo-Json -Depth 10

Write-Step 'Managed ExLlamaV3 packs'
$installed = Invoke-ParuxApi -Method GET -Path '/api/v1/runtime/engines/exllamav3/installed'
$installed | ConvertTo-Json -Depth 10

Write-Step 'Multi-format Model Hub variants'
$catalog = @(Invoke-ParuxApi -Method GET -Path '/api/v1/runtime/performance/engine-race/variants')
Write-Host ("Registered variants: " + $catalog.Count)

if (-not $RunEngineRace) {
    Write-Host '`nPreflight completed. Re-run with -RunEngineRace and exact variant/tag/quality inputs for the real GPU acceptance race.'
    exit 0
}

Require-Value 'BaselineStableKey' $BaselineStableKey
Require-Value 'CandidateStableKey' $CandidateStableKey
Require-Value 'ExLlamaTag' $ExLlamaTag
if (-not $BaselineQualityScore.HasValue -or -not $CandidateQualityScore.HasValue) {
    throw 'Both -BaselineQualityScore and -CandidateQualityScore are required for a comparable promotion race.'
}

$baseline = $catalog | Where-Object { $_.variant.stableKey -eq $BaselineStableKey } | Select-Object -First 1
$candidate = $catalog | Where-Object { $_.variant.stableKey -eq $CandidateStableKey } | Select-Object -First 1
if ($null -eq $baseline) { throw "Baseline variant not found: $BaselineStableKey" }
if ($null -eq $candidate) { throw "Candidate variant not found: $CandidateStableKey" }
if (-not $baseline.structureValidated) { throw 'Baseline variant is not structurally validated.' }
if (-not $candidate.structureValidated) { throw 'Candidate variant is not structurally validated.' }
if ([string]$baseline.variant.logicalModelId -ne [string]$candidate.variant.logicalModelId) {
    throw 'Comparable race requires the same logical model.'
}
if ([string]$baseline.variant.revision -ne [string]$candidate.variant.revision) {
    throw 'Comparable race requires the same exact revision.'
}
if ([int]$baseline.variant.format -ne 1) { throw 'Baseline must be a GGUF variant.' }
if ([int]$candidate.variant.format -ne 2) { throw 'Candidate must be an EXL3 variant.' }
if (@($installed.installed) -notcontains $ExLlamaTag) {
    throw "Requested ExLlamaV3 pack is not installed: $ExLlamaTag"
}

Write-Step 'Ensure no text model owns VRAM before Engine Race'
Write-Host 'The race is intentionally non-destructive: it will not change AI mode or kill a model for you.'
Write-Host 'If this step fails with "Stop the active text model", stop the current text model from PaRuX and run the command again.'

$raceRequest = [ordered]@{
    baselineEngineId = 'llama-cpp-cuda'
    baselineVariant = $baseline.variant
    candidateEngineId = 'exllamav3'
    candidateVariant = $candidate.variant
    workload = $Workload
    sameWeightsAndQualityComparable = $true
    baselineQualityScore = [double]$BaselineQualityScore.Value
    candidateQualityScore = [double]$CandidateQualityScore.Value
    sustainedValidation = $true
    candidateEngineVersion = $ExLlamaTag
}

Write-Step 'Real llama.cpp CUDA vs ExLlamaV3 Engine Race'
$race = Invoke-ParuxApi -Method POST -Path '/api/v1/runtime/performance/engine-race' -Body $raceRequest
$race | ConvertTo-Json -Depth 30
if ($race.result.status -ne 'completed') {
    throw "Engine Race did not complete: $($race.result.error)"
}
if ($null -eq $race.result.baselineMeasurement -or $null -eq $race.result.candidateMeasurement) {
    throw 'Engine Race completed without both measurements.'
}

Write-Step 'Persisted Engine Race evidence'
$history = @(Invoke-ParuxApi -Method GET -Path '/api/v1/runtime/performance/engine-races?maximum=10')
$history | Select-Object -First 3 | ConvertTo-Json -Depth 30

if (-not $RunAutoChatSmoke) {
    if ($race.result.promotion.promote) {
        Write-Host '`nCandidate passed the promotion guardrails. Use -RunAutoChatSmoke to verify that AUTO serves through ExLlamaV3.'
    } else {
        Write-Host '`nCandidate did not pass promotion guardrails. This is a valid race result; llama.cpp remains authoritative.'
    }
    exit 0
}

if (-not $race.result.promotion.promote) {
    throw 'AUTO ExLlama smoke was requested, but the candidate did not win the promotion gate.'
}

Write-Step 'AUTO Chat serving smoke'
$chatBody = [ordered]@{
    model = 'parux-auto:balanced'
    stream = $true
    preferLowLatency = $true
    messages = @(
        [ordered]@{ role = 'user'; content = 'Reply with exactly: PARUX_ENGINE_SMOKE_OK' }
    )
}

$headers = @{ 'X-PaRuX-Token' = $script:ResolvedToken }
$chatUri = $script:ResolvedBaseUri.TrimEnd('/') + '/api/v1/chat/completions'
$response = Invoke-WebRequest -Method POST -Uri $chatUri -Headers $headers -ContentType 'application/json' -Body ($chatBody | ConvertTo-Json -Depth 10 -Compress) -TimeoutSec 900
$raw = [string]$response.Content
if ($raw -notmatch '"engineId"\s*:\s*"exllamav3"') {
    throw 'AUTO Chat did not report engineId=exllamav3 in model_routed SSE.'
}
if ($raw -notmatch 'PARUX_ENGINE_SMOKE_OK') {
    throw 'AUTO Chat did not produce the expected smoke-test content.'
}
if ($raw -notmatch '\[DONE\]') {
    throw 'AUTO Chat stream did not terminate with [DONE].'
}

Write-Step 'Lifecycle fallback smoke'
$offBody = @{ action = 'Off' }
$off = Invoke-ParuxApi -Method POST -Path '/api/v1/runtime/actions' -Body $offBody
if (-not $off.success) { throw "Runtime Off action failed: $($off.message)" }
$healthAfterOff = Invoke-ParuxApi -Method GET -Path '/api/v1/health' -Anonymous
if ($healthAfterOff.providerReady) { throw 'Provider remained ready after explicit Off; alternative engine lifecycle drain failed.' }

Write-Host '`nBIC-P0 multi-engine acceptance harness completed successfully.'
Write-Host 'NOTE: the final Off action is intentional. Restore Auto/On from PaRuX when you want local AI active again.'
