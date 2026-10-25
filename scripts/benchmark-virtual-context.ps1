param(
    [string]$Corpus = (Join-Path $PSScriptRoot '..\benchmarks\virtual-context\parux-corpus.json'),
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\artifacts\virtual-context-benchmark'),
    [switch]$RunModelAnswer
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Get-Bootstrap {
    $path = Join-Path $env:LOCALAPPDATA 'PaRuX\server\bootstrap.json'
    if (-not (Test-Path $path)) { throw "PaRuX Core bootstrap not found at $path. Start PaRuX locally first." }
    $bootstrap = Get-Content $path -Raw | ConvertFrom-Json
    if (-not $bootstrap.endpoint -or -not $bootstrap.token) { throw 'PaRuX Core bootstrap is incomplete.' }
    return $bootstrap
}

function Invoke-CoreJson {
    param([string]$Method, [string]$Path, $Body = $null, [int]$TimeoutSec = 180)
    $uri = $script:Bootstrap.endpoint.TrimEnd('/') + $Path
    $headers = @{ 'X-PaRuX-Token' = $script:Bootstrap.token }
    if ($null -eq $Body) {
        return Invoke-RestMethod -Method $Method -Uri $uri -Headers $headers -TimeoutSec $TimeoutSec
    }
    $json = $Body | ConvertTo-Json -Depth 20 -Compress
    return Invoke-RestMethod -Method $Method -Uri $uri -Headers $headers -ContentType 'application/json' -Body $json -TimeoutSec $TimeoutSec
}

function Invoke-CoreChatText {
    <#
        PaRuX Core's own chat route always answers as an SSE stream; "stream = false" in the request
        body does not change that. Reading it as a JSON object with .choices threw on every case, the
        error was caught per-case, and the run still printed "3/3 (100%)" - a retrieval number with no
        model evidence behind it. Collect the content deltas instead and return the answer text.
    #>
    param($Body, [int]$TimeoutSec = 600)

    $uri = $script:Bootstrap.endpoint.TrimEnd('/') + '/api/v1/chat/completions'
    $json = $Body | ConvertTo-Json -Depth 20 -Compress

    $request = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::Post, $uri)
    $request.Content = [System.Net.Http.StringContent]::new($json, [System.Text.Encoding]::UTF8, 'application/json')
    $request.Headers.Add('X-PaRuX-Token', $script:Bootstrap.token)

    $client = [System.Net.Http.HttpClient]::new()
    try {
        $client.Timeout = [TimeSpan]::FromSeconds($TimeoutSec)
        $response = $client.SendAsync($request, [System.Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
        if (-not $response.IsSuccessStatusCode) {
            throw "PaRuX Core returned HTTP $([int]$response.StatusCode) for the model-answer request."
        }

        $reader = [System.IO.StreamReader]::new($response.Content.ReadAsStreamAsync().GetAwaiter().GetResult())
        try {
            $text = [System.Text.StringBuilder]::new()
            while (-not $reader.EndOfStream) {
                $line = $reader.ReadLine()
                if ($null -eq $line -or -not $line.StartsWith('data:')) { continue }
                $payload = $line.Substring(5).Trim()
                if ($payload.Length -eq 0 -or $payload -eq '[DONE]') { if ($payload -eq '[DONE]') { break } else { continue } }

                try { $frame = $payload | ConvertFrom-Json } catch { continue }
                if ($null -eq $frame) { continue }

                # Frames are a mix of shapes: parux_event telemetry, an error object, and the content
                # deltas. Probe each one defensively - Set-StrictMode makes a missing member fatal.
                $errorMessage = $null
                try { $errorMessage = $frame.error.message } catch { $errorMessage = $null }
                if ($errorMessage) { throw "PaRuX Core reported a provider error: $errorMessage" }

                $fragment = $null
                try { $fragment = $frame.choices[0].delta.content } catch { $fragment = $null }
                if ($fragment) { [void]$text.Append([string]$fragment) }
            }
            return $text.ToString()
        }
        finally { $reader.Dispose() }
    }
    finally { $client.Dispose() }
}

function New-SyntheticConversation {
    param($Case)

    # ConversationWindowPlanner intentionally over-counts with a ~3 chars/token heuristic. Use a
    # 900-character filler payload (~300 heuristic tokens) so the generated document reaches the
    # requested logical scale with a few thousand messages rather than one pathological giant turn.
    $payload = ('f' * 900)
    $tokensPerFiller = 300
    $fillerCount = [Math]::Max(1, [int][Math]::Ceiling([double]$Case.targetLogicalTokens / $tokensPerFiller))
    $messages = [System.Collections.Generic.List[object]]::new()
    $messages.Add([ordered]@{ role = 'user'; content = [string]$Case.constraint })
    for ($i = 0; $i -lt $fillerCount; $i++) {
        $messages.Add([ordered]@{
            role = if (($i % 2) -eq 0) { 'assistant' } else { 'user' }
            content = "archived routine batch $i $payload"
        })
    }
    $messages.Add([ordered]@{ role = 'user'; content = [string]$Case.question })

    return [ordered]@{
        id = 'vctx-' + [Guid]::NewGuid().ToString('N')
        fillerCount = $fillerCount
        document = [ordered]@{ messages = $messages }
    }
}

$script:Bootstrap = Get-Bootstrap
$corpusDocument = Get-Content $Corpus -Raw | ConvertFrom-Json
$cases = @($corpusDocument.cases)
if ($cases.Count -eq 0) { throw 'Virtual-context benchmark corpus contains no cases.' }

New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$rows = @()

foreach ($case in $cases) {
    $synthetic = New-SyntheticConversation $case
    $conversationId = [string]$synthetic.id
    $started = [DateTime]::UtcNow
    $retrievalPassed = $false
    $modelPassed = $null
    $packChars = 0
    $selectedItems = 0
    $errorText = ''

    try {
        $documentJson = $synthetic.document | ConvertTo-Json -Depth 10 -Compress
        $now = [DateTime]::UtcNow.ToString('o')
        Invoke-CoreJson PUT "/api/v1/conversations/$conversationId" ([ordered]@{
            title = "Virtual Context Benchmark $($case.id)"
            createdUtc = $now
            updatedUtc = $now
            documentJson = $documentJson
        }) 300 | Out-Null

        # Deterministic acceptance: inspect the context selected by Core BEFORE a model sees it.
        # A lucky model completion cannot hide a retrieval regression.
        $pack = Invoke-CoreJson POST '/api/v1/context/build' ([ordered]@{
            query = [string]$case.question
            maxCharacters = 12000
            includeMemory = $false
            includeRag = $false
            includeGraph = $false
            includeArtifacts = $false
            includeWeb = $false
            includeRepository = $false
            includeConversationHistory = $true
            conversationId = $conversationId
        }) 300

        $promptText = [string]$pack.promptText
        $packChars = $promptText.Length
        $selectedItems = @($pack.items).Count
        $retrievalPassed = $promptText.Contains([string]$case.sentinel, [StringComparison]::Ordinal)

        if ($RunModelAnswer) {
            # Optional second layer: model quality. Only the current turn is sent; persisted Virtual
            # Context must supply the ancient state. This is deliberately scored separately from
            # deterministic retrievalPass.
            $answer = Invoke-CoreChatText ([ordered]@{
                model = 'parux-main'
                stream = $true
                conversationId = $conversationId
                includeConversationHistory = $true
                messages = @([ordered]@{ role = 'user'; content = [string]$case.question })
            }) 600
            $modelPassed = $answer.Contains([string]$case.sentinel, [StringComparison]::Ordinal)
        }
    }
    catch {
        $errorText = $_.Exception.Message
    }
    finally {
        try { Invoke-CoreJson DELETE "/api/v1/conversations/$conversationId" $null 60 | Out-Null } catch { }
    }

    $elapsed = ([DateTime]::UtcNow - $started).TotalMilliseconds
    $rows += [pscustomobject]@{
        caseId = [string]$case.id
        targetLogicalTokens = [long]$case.targetLogicalTokens
        fillerMessages = [int]$synthetic.fillerCount
        sentinel = [string]$case.sentinel
        retrievalPassed = [bool]$retrievalPassed
        modelAnswerPassed = $modelPassed
        contextPackCharacters = $packChars
        selectedContextItems = $selectedItems
        wallTimeMs = [Math]::Round($elapsed, 0)
        error = $errorText
    }
    Write-Host "[$($case.id)] logical=$($case.targetLogicalTokens) retrieval=$retrievalPassed model=$modelPassed chars=$packChars"
}

$retrievalSuccesses = @($rows | Where-Object retrievalPassed).Count
$modelRows = @($rows | Where-Object { $null -ne $_.modelAnswerPassed })
$modelSuccesses = @($modelRows | Where-Object modelAnswerPassed).Count
$summary = [pscustomobject]@{
    schemaVersion = 1
    corpus = [string]$corpusDocument.name
    deterministicRetrievalPassed = $retrievalSuccesses
    deterministicRetrievalTotal = $rows.Count
    deterministicRetrievalRatePercent = if ($rows.Count -gt 0) { [Math]::Round(100.0 * $retrievalSuccesses / $rows.Count, 1) } else { 0 }
    modelAnswerPassed = $modelSuccesses
    modelAnswerTotal = $modelRows.Count
    modelAnswerRatePercent = if ($modelRows.Count -gt 0) { [Math]::Round(100.0 * $modelSuccesses / $modelRows.Count, 1) } else { $null }
    results = $rows
}

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$jsonPath = Join-Path $OutputDirectory "virtual-context-$stamp.json"
$csvPath = Join-Path $OutputDirectory "virtual-context-$stamp.csv"
$summary | ConvertTo-Json -Depth 12 | Set-Content -Path $jsonPath -Encoding UTF8
$rows | Export-Csv -Path $csvPath -NoTypeInformation -Encoding UTF8

Write-Host ''
Write-Host "Virtual Context deterministic retrieval: $retrievalSuccesses/$($rows.Count) ($($summary.deterministicRetrievalRatePercent)%)"
if ($modelRows.Count -gt 0) { Write-Host "Model answer recall: $modelSuccesses/$($modelRows.Count) ($($summary.modelAnswerRatePercent)%)" }

# A model-answer run that scored nothing is a failed run, not a silent omission. Previously every
# case could error, the retrieval line still read "3/3 (100%)", and the script exited 0.
$unscored = @($rows | Where-Object { $null -eq $_.modelAnswerPassed }).Count
if ($RunModelAnswer -and $unscored -gt 0) {
    Write-Host "Model answer NOT scored for $unscored/$($rows.Count) case(s); the retrieval rate above covers retrieval only."
    foreach ($row in $rows | Where-Object { $null -eq $_.modelAnswerPassed -and $_.error }) {
        Write-Host "  [$($row.caseId)] $($row.error)"
    }
}
Write-Host "JSON: $jsonPath"
Write-Host "CSV : $csvPath"

if ($retrievalSuccesses -ne $rows.Count) { exit 2 }
if ($modelRows.Count -gt 0 -and $modelSuccesses -ne $modelRows.Count) { exit 3 }
if ($RunModelAnswer -and $unscored -gt 0) { exit 4 }
