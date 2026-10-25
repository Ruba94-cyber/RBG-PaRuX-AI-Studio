param(
    [string]$Corpus = (Join-Path $PSScriptRoot '..\benchmarks\media-engine-race\parux-image-corpus.json'),
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\artifacts\media-engine-race'),
    [ValidateRange(1, 20)][int]$RunsPerCase = 1,
    [ValidateRange(1, 120)][int]$TimeoutMinutesPerRace = 30,
    [string[]]$CaseId = @(),
    [switch]$RequirePublicationProtocol
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Get-Bootstrap {
    $path = Join-Path $env:LOCALAPPDATA 'PaRuX\server\bootstrap.json'
    if (-not (Test-Path $path -PathType Leaf)) {
        throw "PaRuX Core bootstrap not found at $path. Start PaRuX locally first."
    }
    $bootstrap = Get-Content $path -Raw | ConvertFrom-Json
    if (-not $bootstrap.endpoint -or -not $bootstrap.token) {
        throw 'PaRuX Core bootstrap is incomplete.'
    }
    return $bootstrap
}

function Invoke-CoreJson {
    param(
        [Parameter(Mandatory=$true)][string]$Method,
        [Parameter(Mandatory=$true)][string]$Path,
        $Body = $null,
        [int]$TimeoutSeconds = 60
    )

    $uri = $script:Bootstrap.endpoint.TrimEnd('/') + $Path
    $headers = @{ 'X-PaRuX-Token' = $script:Bootstrap.token }
    if ($null -eq $Body) {
        return Invoke-RestMethod -Method $Method -Uri $uri -Headers $headers -TimeoutSec $TimeoutSeconds
    }
    $json = $Body | ConvertTo-Json -Depth 16
    return Invoke-RestMethod -Method $Method -Uri $uri -Headers $headers -ContentType 'application/json' -Body $json -TimeoutSec $TimeoutSeconds
}

function Get-Percentile {
    param([object[]]$Values, [double]$Percentile)
    $numbers = @($Values | Where-Object { $null -ne $_ } | ForEach-Object { [double]$_ } | Sort-Object)
    if ($numbers.Count -eq 0) { return $null }
    if ($numbers.Count -eq 1) { return [Math]::Round($numbers[0], 3) }
    $position = ([Math]::Max(0.0, [Math]::Min(100.0, $Percentile)) / 100.0) * ($numbers.Count - 1)
    $lower = [int][Math]::Floor($position)
    $upper = [int][Math]::Ceiling($position)
    if ($lower -eq $upper) { return [Math]::Round($numbers[$lower], 3) }
    $fraction = $position - $lower
    return [Math]::Round($numbers[$lower] + (($numbers[$upper] - $numbers[$lower]) * $fraction), 3)
}

function Get-Mean {
    param([object[]]$Values)
    $numbers = @($Values | Where-Object { $null -ne $_ } | ForEach-Object { [double]$_ })
    if ($numbers.Count -eq 0) { return $null }
    return [Math]::Round((($numbers | Measure-Object -Average).Average), 3)
}

function Get-PropertyValue {
    param($Object, [string]$Name)
    if ($null -eq $Object) { return $null }
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) { return $null }
    return $property.Value
}

function New-EngineStats {
    param([string]$EngineId, [object[]]$Rows)
    $engineRows = @($Rows | Where-Object { $_.engineId -eq $EngineId })
    $rendered = @($engineRows | Where-Object { $_.renderSuccess })
    $judged = @($engineRows | Where-Object { $_.qualityEvaluationSucceeded })
    $accepted = @($engineRows | Where-Object { $_.qualityAccepted })
    $wins = @($engineRows | Where-Object { $_.won })
    $quality = @($accepted | ForEach-Object { $_.qualityScore } | Where-Object { $null -ne $_ })
    $latency = @($rendered | ForEach-Object { $_.renderElapsedMs } | Where-Object { $null -ne $_ })
    $judgeLatency = @($judged | ForEach-Object { $_.qualityEvaluationElapsedMs } | Where-Object { $null -ne $_ })
    $first = @($engineRows | Where-Object { $_.renderOrder -eq 1 })
    $second = @($engineRows | Where-Object { $_.renderOrder -eq 2 })

    return [pscustomobject]@{
        engineId = $EngineId
        engineVersion = (@($engineRows | Select-Object -ExpandProperty engineVersion -Unique) -join ',')
        appearances = $engineRows.Count
        wins = $wins.Count
        winRatePercent = if ($engineRows.Count -gt 0) { [Math]::Round(100.0 * $wins.Count / $engineRows.Count, 2) } else { 0 }
        renderSuccesses = $rendered.Count
        renderSuccessRatePercent = if ($engineRows.Count -gt 0) { [Math]::Round(100.0 * $rendered.Count / $engineRows.Count, 2) } else { 0 }
        judgeSuccesses = $judged.Count
        judgeSuccessRatePercent = if ($rendered.Count -gt 0) { [Math]::Round(100.0 * $judged.Count / $rendered.Count, 2) } else { 0 }
        qualityAccepted = $accepted.Count
        qualityAcceptanceRatePercent = if ($judged.Count -gt 0) { [Math]::Round(100.0 * $accepted.Count / $judged.Count, 2) } else { 0 }
        meanAcceptedQuality = Get-Mean $quality
        p50AcceptedQuality = Get-Percentile $quality 50
        p95AcceptedQuality = Get-Percentile $quality 95
        meanColdRenderMs = Get-Mean $latency
        p50ColdRenderMs = Get-Percentile $latency 50
        p95ColdRenderMs = Get-Percentile $latency 95
        meanJudgeMs = Get-Mean $judgeLatency
        renderFailures = @($engineRows | Where-Object { -not $_.renderSuccess }).Count
        judgeFailures = @($engineRows | Where-Object { $_.renderSuccess -and -not $_.qualityEvaluationSucceeded }).Count
        firstPositionRuns = $first.Count
        secondPositionRuns = $second.Count
        firstPositionWins = @($first | Where-Object { $_.won }).Count
        secondPositionWins = @($second | Where-Object { $_.won }).Count
    }
}

$script:Bootstrap = Get-Bootstrap
if (-not (Test-Path $Corpus -PathType Leaf)) { throw "Media benchmark corpus not found: $Corpus" }
$corpusDocument = Get-Content $Corpus -Raw | ConvertFrom-Json
if ([int]$corpusDocument.schemaVersion -ne 1) {
    throw "Unsupported media benchmark corpus schemaVersion: $($corpusDocument.schemaVersion)"
}
$cases = @($corpusDocument.cases)
if ($cases.Count -eq 0) { throw 'Media benchmark corpus contains no cases.' }
if ($CaseId.Count -gt 0) {
    $wanted = @($CaseId | ForEach-Object { [string]$_ })
    $cases = @($cases | Where-Object { $wanted -contains [string]$_.id })
    if ($cases.Count -eq 0) { throw 'None of the requested -CaseId values exists in the corpus.' }
}

$minimumRuns = [int]$corpusDocument.methodology.publicationMinimumRunsPerCase
if ($RequirePublicationProtocol -and $RunsPerCase -lt $minimumRuns) {
    throw "Publication protocol requires at least $minimumRuns run(s) per case; requested $RunsPerCase."
}

Write-Host '== PaRuX media Engine Race benchmark ==' -ForegroundColor Cyan
Write-Host "Corpus: $($corpusDocument.name); cases: $($cases.Count); runs/case: $RunsPerCase"
Write-Host 'Preflight: checking independent Diffusers renderer and semantic judge...'
$engineStatus = Invoke-CoreJson GET '/api/v2/media/engines/diffusers' $null 60
$judgeStatus = Invoke-CoreJson GET '/api/v2/media/engine-race/judge' $null 60
if (-not [bool]$engineStatus.ready) {
    throw "PaRuX-managed Diffusers engine is not ready: $($engineStatus.detail)"
}
if (-not [bool]$judgeStatus.ready) {
    throw "PaRuX semantic image judge is not ready: $($judgeStatus.detail)"
}

New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$startedUtc = [DateTime]::UtcNow
$candidateRows = New-Object System.Collections.Generic.List[object]
$raceRows = New-Object System.Collections.Generic.List[object]
$transportFailures = New-Object System.Collections.Generic.List[object]
$raceOrdinal = 0
$totalRaces = $cases.Count * $RunsPerCase

foreach ($case in $cases) {
    $baseSeed = [long]$case.seed
    for ($run = 0; $run -lt $RunsPerCase; $run++) {
        $raceOrdinal++
        # 104729 is an odd prime: additional runs use different deterministic seeds and alternate
        # first/second candidate placement because production order is controlled by seed parity.
        $seed = $baseSeed + ([long]$run * 104729L)
        $request = @{
            requireAllManagedEngines = $true
            job = @{
                type = 'image'
                prompt = [string]$case.prompt
                width = [int]$case.width
                height = [int]$case.height
                steps = 9
                seed = $seed
            }
        }

        Write-Host ("[{0}/{1}] {2} · {3} · run {4}/{5} · seed {6}" -f $raceOrdinal,$totalRaces,$case.id,$case.category,($run + 1),$RunsPerCase,$seed)
        $watch = [Diagnostics.Stopwatch]::StartNew()
        try {
            # Evidence-only response prevents benchmark JSON transport from including any generated
            # Base64 PNG. Images still exist in each renderer's normal local output directory.
            $race = Invoke-CoreJson POST '/api/v2/media/engine-race/image?includeArtifact=false' $request ($TimeoutMinutesPerRace * 60)
            $watch.Stop()

            $measurements = @($race.measurements)
            $raceRows.Add([pscustomobject]@{
                caseId = [string]$case.id
                category = [string]$case.category
                run = $run + 1
                seed = $seed
                width = [int]$case.width
                height = [int]$case.height
                status = [string]$race.status
                winnerEngineId = [string]$race.winnerEngineId
                winnerEngineVersion = [string]$race.winnerEngineVersion
                requestWallTimeMs = [Math]::Round($watch.Elapsed.TotalMilliseconds, 0)
                selectionReason = [string]$race.selectionReason
                candidateCount = $measurements.Count
            })

            for ($index = 0; $index -lt $measurements.Count; $index++) {
                $measurement = $measurements[$index]
                $qualityScore = Get-PropertyValue $measurement 'qualityScore'
                $qualityElapsed = Get-PropertyValue $measurement 'qualityEvaluationElapsedMs'
                $candidateRows.Add([pscustomobject]@{
                    caseId = [string]$case.id
                    category = [string]$case.category
                    run = $run + 1
                    seed = $seed
                    width = [int]$case.width
                    height = [int]$case.height
                    renderOrder = $index + 1
                    engineId = [string]$measurement.engineId
                    engineVersion = [string]$measurement.engineVersion
                    raceStatus = [string]$race.status
                    won = (-not [string]::IsNullOrWhiteSpace([string]$race.winnerEngineId) -and [string]$measurement.engineId -eq [string]$race.winnerEngineId)
                    renderSuccess = [bool]$measurement.success
                    renderElapsedMs = [long]$measurement.elapsedMs
                    qualityEvaluationSucceeded = [bool]$measurement.qualityEvaluationSucceeded
                    qualityEvaluationElapsedMs = if ($null -ne $qualityElapsed) { [long]$qualityElapsed } else { $null }
                    qualityAccepted = [bool]$measurement.qualityAccepted
                    qualityScore = if ($null -ne $qualityScore) { [double]$qualityScore } else { $null }
                    qualityReason = [string]$measurement.qualityReason
                    generationError = [string]$measurement.error
                    qualityError = [string]$measurement.qualityError
                })
            }
        }
        catch {
            $watch.Stop()
            $failure = [pscustomobject]@{
                caseId = [string]$case.id
                category = [string]$case.category
                run = $run + 1
                seed = $seed
                requestWallTimeMs = [Math]::Round($watch.Elapsed.TotalMilliseconds, 0)
                error = $_.Exception.Message
            }
            $transportFailures.Add($failure)
            Write-Warning "[$($case.id)] race request failed: $($_.Exception.Message)"
        }
    }
}

$rows = @($candidateRows)
$races = @($raceRows)
$engineIds = @($rows | ForEach-Object { $_.engineId } | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Sort-Object -Unique)
$engineStats = @($engineIds | ForEach-Object { New-EngineStats ([string]$_) $rows })
$categoryStats = @()
foreach ($category in @($rows | Select-Object -ExpandProperty category -Unique | Sort-Object)) {
    foreach ($engineId in $engineIds) {
        $categoryRows = @($rows | Where-Object { $_.category -eq $category -and $_.engineId -eq $engineId })
        if ($categoryRows.Count -eq 0) { continue }
        $acceptedQuality = @($categoryRows | Where-Object { $_.qualityAccepted } | ForEach-Object { $_.qualityScore } | Where-Object { $null -ne $_ })
        $renderLatency = @($categoryRows | Where-Object { $_.renderSuccess } | ForEach-Object { $_.renderElapsedMs })
        $categoryStats += [pscustomobject]@{
            category = [string]$category
            engineId = [string]$engineId
            appearances = $categoryRows.Count
            wins = @($categoryRows | Where-Object { $_.won }).Count
            meanAcceptedQuality = Get-Mean $acceptedQuality
            p50ColdRenderMs = Get-Percentile $renderLatency 50
            renderFailures = @($categoryRows | Where-Object { -not $_.renderSuccess }).Count
            judgeFailures = @($categoryRows | Where-Object { $_.renderSuccess -and -not $_.qualityEvaluationSucceeded }).Count
        }
    }
}

$completedRaces = @($races | Where-Object { $_.status -eq 'completed' })
$noWinnerRaces = @($races | Where-Object { $_.status -ne 'completed' })
$completedUtc = [DateTime]::UtcNow
$publicationProtocolSatisfied = ($RunsPerCase -ge $minimumRuns -and $transportFailures.Count -eq 0 -and $races.Count -eq $totalRaces)
$summary = [pscustomobject]@{
    schemaVersion = 1
    corpus = [string]$corpusDocument.name
    corpusSchemaVersion = [int]$corpusDocument.schemaVersion
    methodology = $corpusDocument.methodology
    startedUtc = $startedUtc.ToString('o')
    completedUtc = $completedUtc.ToString('o')
    totalWallTimeMs = [Math]::Round(($completedUtc - $startedUtc).TotalMilliseconds, 0)
    cases = $cases.Count
    runsPerCase = $RunsPerCase
    expectedRaces = $totalRaces
    completedRequests = $races.Count
    transportFailures = $transportFailures.Count
    racesWithWinner = $completedRaces.Count
    racesWithoutWinner = $noWinnerRaces.Count
    publicationMinimumRunsPerCase = $minimumRuns
    publicationProtocolSatisfied = $publicationProtocolSatisfied
    evidenceCaveat = 'A satisfied protocol means the local corpus was executed reproducibly. It does not prove market best-in-class until the same corpus/methodology is run against external competitors on the same hardware and current software versions.'
    engineStatus = $engineStatus
    judgeStatus = $judgeStatus
    engineStats = $engineStats
    categoryStats = $categoryStats
    races = $races
    candidates = $rows
    requestFailures = @($transportFailures)
}

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$jsonPath = Join-Path $OutputDirectory "media-engine-race-$stamp.json"
$csvPath = Join-Path $OutputDirectory "media-engine-race-candidates-$stamp.csv"
$raceCsvPath = Join-Path $OutputDirectory "media-engine-race-races-$stamp.csv"
$summary | ConvertTo-Json -Depth 20 | Set-Content -Path $jsonPath -Encoding UTF8
$rows | Export-Csv -Path $csvPath -NoTypeInformation -Encoding UTF8
$races | Export-Csv -Path $raceCsvPath -NoTypeInformation -Encoding UTF8

Write-Host ''
Write-Host "Media Engine Race: $($completedRaces.Count)/$totalRaces races produced a quality-approved winner; request failures: $($transportFailures.Count)."
foreach ($engine in $engineStats) {
    Write-Host ("{0}: wins={1}/{2} ({3}%); quality mean={4}; cold p50={5} ms; cold p95={6} ms; render failures={7}; judge failures={8}" -f `
        $engine.engineId,$engine.wins,$engine.appearances,$engine.winRatePercent,$engine.meanAcceptedQuality,$engine.p50ColdRenderMs,$engine.p95ColdRenderMs,$engine.renderFailures,$engine.judgeFailures)
}
if ($publicationProtocolSatisfied) {
    Write-Host 'Publication protocol coverage satisfied for this PaRuX-internal race corpus. External same-hardware competitor evidence is still required for a best-in-class claim.' -ForegroundColor Green
} else {
    Write-Warning "Publication protocol not satisfied. Use at least $minimumRuns runs/case and complete every request before treating the result as publication-grade evidence."
}
Write-Host "JSON: $jsonPath"
Write-Host "Candidates CSV: $csvPath"
Write-Host "Races CSV     : $raceCsvPath"

if ($RequirePublicationProtocol -and -not $publicationProtocolSatisfied) {
    throw 'Media benchmark did not satisfy the publication protocol.'
}
