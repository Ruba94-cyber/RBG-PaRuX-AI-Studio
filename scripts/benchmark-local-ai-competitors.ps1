<#
.SYNOPSIS
    Reproducible head-to-head benchmark of PaRuX against other local-AI runtimes over the
    OpenAI-compatible protocol they all expose.

.DESCRIPTION
    Measures embedding retrieval quality plus reference-grade streaming chat latency. Chat metrics
    are delegated to benchmark-reference-stream.ps1 so this script cannot regress to counting SSE
    chunks as tokens. Exact decode tok/s is emitted only when usage proves a non-reasoning stream;
    otherwise the result is explicitly inconclusive.

    The first request is reported separately as a first-request observation. It is NOT called a cold
    model load unless the caller has independently unloaded the model/runtime before invocation.

    A provider that does not expose an endpoint is recorded as unsupported rather than scored as
    zero. Failed or non-comparable chat responses are never dropped and never allowed to produce a
    winner by estimate.

.PARAMETER Providers
    One or more "name=baseUrl" pairs, e.g. parux=http://127.0.0.1:51123.

.PARAMETER ParuxToken
    Auth token for the PaRuX provider (read from its bootstrap.json when omitted).

.PARAMETER ChatModels
    Optional provider=modelId overrides. Every override must be advertised by that provider.
    Use this to select the same model alias where the runtimes expose one.

.PARAMETER ChatModelIdentity
    Caller-supplied identity label shared by the chat comparison. The default explicitly leaves
    underlying weight parity unverified; use a verified artifact hash only after checking each runtime.

.EXAMPLE
    ./scripts/benchmark-local-ai-competitors.ps1 `
        -Providers 'parux=http://127.0.0.1:51123','lmstudio=http://127.0.0.1:1234','ollama=http://127.0.0.1:11434'
#>
[CmdletBinding()]
param(
    [string[]]$Providers = @('parux=http://127.0.0.1:51123'),
    [string]$ParuxToken = '',
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\artifacts\competitor-benchmark'),
    [int]$TopK = 5,
    [int]$ChatMaxTokens = 128,
    [string]$ChatSystemPrompt = '',
    [string]$ChatDeveloperPrompt = '',
    [ValidateRange(0.0, 2.0)] [double]$ChatTemperature = 0,
    [ValidateRange(0.0, 1.0)] [double]$ChatTopP = 1.0,
    [ValidateRange(-2.0, 2.0)] [double]$ChatFrequencyPenalty = 0,
    [ValidateRange(-2.0, 2.0)] [double]$ChatPresencePenalty = 0,
    [string[]]$ChatModels = @(),
    [string]$ChatModelIdentity = 'provider-advertised-api-ids; underlying-weight-parity-unverified',
    [string]$ChatParityManifestPath = '',
    [ValidateRange(2,20)][int]$Repetitions = 3
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$Corpus = @(
    @{ id = 'astronomy_1'; topic = 'astronomy'; text = 'Telescopes gather starlight through a curved mirror or lens, letting astronomers resolve faint galaxies and distant nebulae the naked eye cannot detect.' }
    @{ id = 'astronomy_2'; topic = 'astronomy'; text = "A planet's orbit around its star is an ellipse, and Kepler's laws describe how orbital speed changes as it moves nearer or farther." }
    @{ id = 'astronomy_3'; topic = 'astronomy'; text = 'Space probes bound for the outer planets use gravity assists from Jupiter and Saturn to reach the edge of the solar system without enormous fuel loads.' }
    @{ id = 'cooking_1'; topic = 'cooking'; text = "A sharp chef's knife and a proper grip make dicing onions faster and safer than a dull blade, which is far more likely to slip." }
    @{ id = 'cooking_2'; topic = 'cooking'; text = 'Baking bread relies on gluten development and yeast fermentation; kneading builds structure while the yeast produces carbon dioxide that makes it rise.' }
    @{ id = 'cooking_3'; topic = 'cooking'; text = 'A classic pan sauce is built by deglazing the fond left in the skillet with wine or stock, then whisking in cold butter off the heat.' }
    @{ id = 'networking_1'; topic = 'networking'; text = 'DNS resolves a human-readable domain name into an IP address by querying a hierarchy of nameservers from the root down to the authoritative server.' }
    @{ id = 'networking_2'; topic = 'networking'; text = 'A router forwards packets between networks by consulting its routing table and choosing the next hop from the destination address in each header.' }
    @{ id = 'networking_3'; topic = 'networking'; text = 'TCP establishes a reliable connection with a three-way handshake and retransmits any segment whose acknowledgment does not arrive before the timeout.' }
    @{ id = 'gardening_1'; topic = 'gardening'; text = 'Healthy garden soil needs organic matter, so mixing in compost improves drainage in heavy clay and water retention in sandy ground.' }
    @{ id = 'gardening_2'; topic = 'gardening'; text = 'Pruning fruit trees in late winter while they are dormant encourages strong new growth and better airflow through the canopy in spring.' }
    @{ id = 'gardening_3'; topic = 'gardening'; text = 'A compost pile needs a balance of nitrogen-rich green material and carbon-rich brown material, turned occasionally to keep decomposition aerobic.' }
    @{ id = 'finance_1'; topic = 'finance'; text = 'A zero-based budget assigns every unit of income to a category - spending, saving or debt repayment - so nothing is left unaccounted for.' }
    @{ id = 'finance_2'; topic = 'finance'; text = 'When a central bank raises interest rates, variable-rate mortgage payments climb and borrowing to expand a business becomes more expensive.' }
    @{ id = 'finance_3'; topic = 'finance'; text = 'Contributing to a tax-advantaged retirement account can lower taxable income now while the investments grow tax-deferred until withdrawal.' }
)

$Queries = @(
    @{ text = 'how can stargazers make out very dim clusters of stars far away'; topic = 'astronomy' }
    @{ text = 'why do deep space missions swing past the giant planets to save propellant'; topic = 'astronomy' }
    @{ text = 'what blade technique reduces the risk of cutting yourself preparing vegetables'; topic = 'cooking' }
    @{ text = 'what makes a loaf expand while it proves and bakes'; topic = 'cooking' }
    @{ text = 'how is a website hostname turned into a numeric address'; topic = 'networking' }
    @{ text = 'how does a device decide where to send data next across linked networks'; topic = 'networking' }
    @{ text = 'how do I improve heavy waterlogged ground before planting'; topic = 'gardening' }
    @{ text = 'when is the right season to cut back dormant orchard trees'; topic = 'gardening' }
    @{ text = 'a method where all incoming money is allocated so none is unassigned'; topic = 'finance' }
    @{ text = 'what happens to variable home loan repayments when rates go up'; topic = 'finance' }
)

function Resolve-ParuxToken {
    param([string]$Provided)
    if (-not [string]::IsNullOrWhiteSpace($Provided)) { return $Provided }
    $bootstrap = Join-Path $env:LOCALAPPDATA 'PaRuX\server\bootstrap.json'
    if (Test-Path $bootstrap) { return (Get-Content $bootstrap -Raw | ConvertFrom-Json).token }
    return ''
}

function New-Headers {
    param([string]$Name, [string]$Token)
    $headers = @{ 'Content-Type' = 'application/json' }
    if ($Name -eq 'parux' -and -not [string]::IsNullOrWhiteSpace($Token)) {
        $headers['X-PaRuX-Token'] = $Token
    }
    return $headers
}

function Get-ProviderModels {
    param([string]$BaseUrl, [hashtable]$Headers)
    try {
        $response = Invoke-RestMethod -Method Get -Uri "$BaseUrl/v1/models" -Headers $Headers -TimeoutSec 30
        return @($response.data)
    } catch { return @() }
}

function Invoke-Embedding {
    param([string]$BaseUrl, [hashtable]$Headers, [string]$Model, [string]$Text)
    $payload = @{ input = $Text }
    if (-not [string]::IsNullOrWhiteSpace($Model)) { $payload['model'] = $Model }
    $body = $payload | ConvertTo-Json -Depth 5 -Compress
    $response = Invoke-RestMethod -Method Post -Uri "$BaseUrl/v1/embeddings" -Headers $Headers -Body $body -TimeoutSec 120
    return , [double[]]$response.data[0].embedding
}

function Get-CosineSimilarity {
    param([double[]]$A, [double[]]$B)
    $dot = 0.0; $na = 0.0; $nb = 0.0
    $n = [Math]::Min($A.Length, $B.Length)
    for ($i = 0; $i -lt $n; $i++) {
        $dot += $A[$i] * $B[$i]; $na += $A[$i] * $A[$i]; $nb += $B[$i] * $B[$i]
    }
    if ($na -le 0 -or $nb -le 0) { return 0.0 }
    return $dot / ([Math]::Sqrt($na) * [Math]::Sqrt($nb))
}

function Get-Percentile {
    param([double[]]$Values, [double]$Percentile)
    if ($Values.Count -eq 0) { return $null }
    $sorted = $Values | Sort-Object
    $index = [Math]::Min($sorted.Count - 1, [Math]::Max(0, [int][Math]::Ceiling($Percentile / 100.0 * $sorted.Count) - 1))
    return [double]$sorted[$index]
}

function Measure-EmbeddingQuality {
    param([string]$Name, [string]$BaseUrl, [hashtable]$Headers, [string]$Model)

    $result = [ordered]@{ supported = $false; reason = $null }
    try { $null = Invoke-Embedding -BaseUrl $BaseUrl -Headers $Headers -Model $Model -Text 'probe' }
    catch {
        $result.reason = "no usable /v1/embeddings: $($_.Exception.Message)"
        return $result
    }

    $docVectors = @{}
    foreach ($doc in $Corpus) {
        $docVectors[$doc.id] = Invoke-Embedding -BaseUrl $BaseUrl -Headers $Headers -Model $Model -Text $doc.text
    }

    $latencies = New-Object System.Collections.Generic.List[double]
    $recallSum = 0.0; $mrrSum = 0.0; $ndcgSum = 0.0
    foreach ($query in $Queries) {
        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        $queryVector = Invoke-Embedding -BaseUrl $BaseUrl -Headers $Headers -Model $Model -Text $query.text
        $sw.Stop()
        $latencies.Add($sw.Elapsed.TotalMilliseconds)
        $ranked = $Corpus |
            ForEach-Object { [pscustomobject]@{ id = $_.id; topic = $_.topic; score = (Get-CosineSimilarity -A $queryVector -B $docVectors[$_.id]) } } |
            Sort-Object -Property score -Descending | Select-Object -First $TopK
        $relevantTotal = ($Corpus | Where-Object { $_.topic -eq $query.topic }).Count
        $hits = @($ranked | Where-Object { $_.topic -eq $query.topic })
        $recallSum += ($hits.Count / [double]$relevantTotal)
        $firstRelevantRank = 0
        for ($i = 0; $i -lt $ranked.Count; $i++) {
            if ($ranked[$i].topic -eq $query.topic) { $firstRelevantRank = $i + 1; break }
        }
        if ($firstRelevantRank -gt 0) { $mrrSum += 1.0 / $firstRelevantRank }
        $dcg = 0.0
        for ($i = 0; $i -lt $ranked.Count; $i++) {
            if ($ranked[$i].topic -eq $query.topic) { $dcg += 1.0 / [Math]::Log(($i + 2), 2) }
        }
        $ideal = 0.0
        for ($i = 0; $i -lt [Math]::Min($TopK, $relevantTotal); $i++) { $ideal += 1.0 / [Math]::Log(($i + 2), 2) }
        if ($ideal -gt 0) { $ndcgSum += $dcg / $ideal }
    }

    $count = [double]$Queries.Count
    $result.supported = $true
    $result['model'] = $Model
    $result['dimensions'] = (Invoke-Embedding -BaseUrl $BaseUrl -Headers $Headers -Model $Model -Text 'dimension probe').Length
    $result["recallAt$TopK"] = [Math]::Round($recallSum / $count, 4)
    $result['mrr'] = [Math]::Round($mrrSum / $count, 4)
    $result["ndcgAt$TopK"] = [Math]::Round($ndcgSum / $count, 4)
    $result['medianLatencyMs'] = [Math]::Round((Get-Percentile -Values $latencies.ToArray() -Percentile 50), 2)
    $result['p95LatencyMs'] = [Math]::Round((Get-Percentile -Values $latencies.ToArray() -Percentile 95), 2)
    return $result
}

function Measure-ChatLatencyBatch {
    param([object[]]$Targets, [string]$Token)
    $results = [ordered]@{}
    foreach ($target in $Targets) {
        $results[$target.Name] = [ordered]@{ supported = $false; comparable = $false; reason = 'no chat model advertised' }
    }
    $eligible = @($Targets | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_.Model) })
    if ($eligible.Count -eq 0) { return ,$results }

    $referenceScript = Join-Path $PSScriptRoot 'benchmark-reference-stream.ps1'
    if (-not (Test-Path $referenceScript)) { throw "Reference streaming benchmark is missing: $referenceScript" }
    $referenceOutput = Join-Path $OutputDirectory 'reference-stream-chat'
    $caseSpecs = @($eligible | ForEach-Object { "$($_.Name)=$($_.BaseUrl.TrimEnd('/'))|$($_.Model)" })

    try {
        $referenceArguments = @{
            Cases = $caseSpecs
            ModelIdentity = $ChatModelIdentity
            ParuxToken = $Token
            Prompt = 'Explain what a hash table is in three sentences.'
            SystemPrompt = $ChatSystemPrompt
            DeveloperPrompt = $ChatDeveloperPrompt
            MaxTokens = $ChatMaxTokens
            Temperature = $ChatTemperature
            TopP = $ChatTopP
            FrequencyPenalty = $ChatFrequencyPenalty
            PresencePenalty = $ChatPresencePenalty
            Repetitions = $Repetitions
            TimeoutSeconds = 600
            OutputDirectory = $referenceOutput
        }
        if (-not [string]::IsNullOrWhiteSpace($ChatParityManifestPath)) {
            # The reference harness re-hashes the listed local model artifacts and validates the
            # declared runtime/context/warm-state parity before it can admit a speed assertion.
            # The manifest remains an operator attestation; provider-to-file loading is not
            # independently provable through the common OpenAI-compatible API.
            $referenceArguments['ParityManifestPath'] = $ChatParityManifestPath
        }
        & $referenceScript @referenceArguments

        $reference = Get-Content (Join-Path $referenceOutput 'reference-stream-benchmark.json') -Raw | ConvertFrom-Json
        foreach ($target in $eligible) {
            $property = $reference.providers.PSObject.Properties[$target.Name]
            if ($null -eq $property) {
                $results[$target.Name].reason = "Reference report did not contain provider '$($target.Name)'."
                continue
            }
            $entry = $property.Value
            if (-not $entry.reachable) {
                $results[$target.Name].reason = [string]$entry.error
                $results[$target.Name]['requestedRuns'] = $entry.requestedRuns
                $results[$target.Name]['completedRuns'] = $entry.completedRuns
                continue
            }

            $result = $results[$target.Name]
            $result.supported = $true
            $result.comparable = [bool]$entry.exactTokenRateAvailableForAllRuns
            $result['model'] = $target.Model
            $result['firstRequestMs'] = $entry.firstRequestMs
            $result['requestedRuns'] = $entry.requestedRuns
            $result['completedRuns'] = $entry.completedRuns
            $result['medianTtftMs'] = $entry.medianTtftMs
            $result['p95TtftMs'] = $entry.p95TtftMs
            $result['medianLastVisibleMs'] = $entry.medianLastVisibleMs
            $result['p95LastVisibleMs'] = $entry.p95LastVisibleMs
            $result['latencyEvidence'] = $reference.latencyEvidence
            $result['parityEvidenceStatus'] = $reference.parityEvidence.status
            $result['speedAssertionEligible'] = $reference.parityEvidence.speedAssertionEligible
            $result['parityManifestSha256'] = $reference.parityEvidence.manifestSha256
            $result['providerSchedule'] = $reference.providerSchedule.strategy
            $result['medianOutputCharsPerSecond'] = $entry.medianOutputCharsPerSecond
            $result['medianDecodeTokensPerSecond'] = $entry.medianDecodeTokensPerSecond
            if (-not $result.comparable) {
                $result.reason = 'exact non-reasoning token evidence was not available for every run; decode comparison is inconclusive'
            }
        }
    } catch {
        foreach ($target in $eligible) {
            $results[$target.Name].reason = "reference chat benchmark failed: $($_.Exception.Message)"
        }
    }
    return ,$results
}

$token = Resolve-ParuxToken -Provided $ParuxToken
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null

$chatModelOverrides = @{}
foreach ($mapping in $ChatModels) {
    $separator = $mapping.IndexOf('=')
    if ($separator -le 0 -or $separator -ge ($mapping.Length - 1)) {
        throw "Invalid ChatModels entry '$mapping'. Expected provider=modelId."
    }
    $overrideName = $mapping.Substring(0, $separator).Trim()
    $overrideModel = $mapping.Substring($separator + 1).Trim()
    if ($chatModelOverrides.ContainsKey($overrideName)) { throw "ChatModels contains duplicate provider '$overrideName'." }
    $chatModelOverrides[$overrideName] = $overrideModel
}

$report = [ordered]@{
    schemaVersion = 5
    generatedUtc = (Get-Date).ToUniversalTime().ToString('o')
    machine = [ordered]@{
        os = [System.Runtime.InteropServices.RuntimeInformation]::OSDescription
        processorCount = [Environment]::ProcessorCount
        note = 'Record actual active accelerators separately; this harness does not infer hardware from product name.'
    }
    corpusDocuments = $Corpus.Count
    queries = $Queries.Count
    topK = $TopK
    chatBenchmark = [ordered]@{
        modelIdentityLabel = $ChatModelIdentity
        modelIdentitySource = 'caller-supplied label; runtime model bytes are not independently inspected'
        modelWeightParity = if ([string]::IsNullOrWhiteSpace($ChatParityManifestPath)) {
            'not independently verified; no parity manifest supplied'
        } else {
            'operator-attested manifest; artifact files are re-hashed, but provider-to-file loading is not independently verified'
        }
        parityManifestSource = if ([string]::IsNullOrWhiteSpace($ChatParityManifestPath)) { 'not-provided' } else { 'operator-attested; see reference-stream parity evidence' }
        prompt = 'Explain what a hash table is in three sentences.'
        systemInstructions = $ChatSystemPrompt
        developerInstructions = $ChatDeveloperPrompt
        toolSchemas = 'none'
        temperature = $ChatTemperature
        topP = $ChatTopP
        frequencyPenalty = $ChatFrequencyPenalty
        presencePenalty = $ChatPresencePenalty
        reasoningEffort = 'none'
        maxTokens = $ChatMaxTokens
        repetitions = $Repetitions
        providerSchedule = 'paired-round-robin with rotating start provider'
    }
    providers = [ordered]@{}
}

$providerNames = @{}
$chatTargets = [System.Collections.Generic.List[object]]::new()
foreach ($spec in $Providers) {
    $separator = $spec.IndexOf('=')
    if ($separator -le 0 -or $separator -ge ($spec.Length - 1)) {
        throw "Invalid provider '$spec'. Expected name=baseUrl."
    }
    $name = $spec.Substring(0, $separator).Trim()
    $baseUrl = $spec.Substring($separator + 1).Trim().TrimEnd('/')
    if ($name -notmatch '^[A-Za-z0-9_.-]+$') { throw "Invalid provider name '$name'. Use letters, digits, '.', '_' or '-'." }
    if ($providerNames.ContainsKey($name)) { throw "Duplicate provider name '$name'." }
    $providerNames[$name] = $true
    Write-Host "== $name ($baseUrl)" -ForegroundColor Cyan
    $headers = New-Headers -Name $name -Token $token
    $models = @(Get-ProviderModels -BaseUrl $baseUrl -Headers $headers)
    if ($models.Count -eq 0) {
        Write-Warning '  unreachable or no models; recorded as unavailable'
        $report.providers[$name] = [ordered]@{ reachable = $false }
        continue
    }

    $ids = @($models | ForEach-Object { $_.id })
    $embeddingModel = @($ids | Where-Object { $_ -match 'embed' }) | Select-Object -First 1
    $advertisedChatModel = @($ids | Where-Object { $_ -notmatch 'embed' }) | Select-Object -First 1
    $chatModel = if ($chatModelOverrides.ContainsKey($name)) { [string]$chatModelOverrides[$name] } else { [string]$advertisedChatModel }
    Write-Host "  embedding model: $(if ($embeddingModel) { $embeddingModel } else { '(none)' })"
    Write-Host "  chat model:      $(if ($chatModel) { $chatModel } else { '(none)' })"

    $chatResult = [ordered]@{ supported = $false; comparable = $false; reason = $null }
    if ([string]::IsNullOrWhiteSpace($chatModel)) {
        $chatResult.reason = 'no chat model advertised'
    } elseif ($chatModelOverrides.ContainsKey($name) -and $ids -notcontains $chatModel) {
        $chatResult.reason = "requested chat model '$chatModel' is not advertised by this provider"
    } else {
        $chatResult.reason = 'awaiting paired multi-provider measurement'
        $chatTargets.Add([pscustomobject]@{ Name = $name; BaseUrl = $baseUrl; Model = $chatModel })
    }

    $report.providers[$name] = [ordered]@{
        reachable = $true
        models = $ids
        embedding = (Measure-EmbeddingQuality -Name $name -BaseUrl $baseUrl -Headers $headers -Model $embeddingModel)
        chat = $chatResult
    }
}

foreach ($overrideName in $chatModelOverrides.Keys) {
    if (-not $providerNames.ContainsKey($overrideName)) { throw "ChatModels references unknown provider '$overrideName'." }
}

if ($chatTargets.Count -gt 0) {
    $chatResults = Measure-ChatLatencyBatch -Targets $chatTargets.ToArray() -Token $token
    foreach ($target in $chatTargets) {
        $providerResult = $report.providers[$target.Name]
        $providerResult['chat'] = $chatResults[$target.Name]
    }
}

$jsonPath = Join-Path $OutputDirectory 'competitor-benchmark.json'
$report | ConvertTo-Json -Depth 14 | Set-Content -Path $jsonPath -Encoding UTF8

$md = New-Object System.Text.StringBuilder
$null = $md.AppendLine('# Local-AI runtime comparison')
$null = $md.AppendLine()
$null = $md.AppendLine("Generated: $($report.generatedUtc)")
$null = $md.AppendLine("Machine: $($report.machine.os), $($report.machine.processorCount) logical processors.")
$null = $md.AppendLine("Chat identity label: $ChatModelIdentity (caller supplied; it is not proof of parity). The same prompt, optional system/developer instructions, tool-free request profile, sampling settings, reasoning effort and output limit are sent to every provider.")
$null = $md.AppendLine("Parity manifest: $($report.chatBenchmark.parityManifestSource). Even a matching manifest is an operator attestation: the harness re-hashes listed artifact files but cannot independently prove which file a third-party provider loaded.")
$null = $md.AppendLine('Chat providers are measured together with a rotating paired round-robin schedule. The first request is not a cold-load claim; model residency and cache state are not independently controlled. The model identity label does not prove identical weights, tokenizer, templates or provider-side runtime settings.')
$null = $md.AppendLine()
$null = $md.AppendLine("## Embedding retrieval quality ($($report.corpusDocuments) documents, $($report.queries) paraphrased queries, top-$TopK)")
$null = $md.AppendLine()
$null = $md.AppendLine("| Provider | Model | Dims | Recall@$TopK | MRR | nDCG@$TopK | Median ms | p95 ms |")
$null = $md.AppendLine('|---|---|---:|---:|---:|---:|---:|---:|')
foreach ($name in $report.providers.Keys) {
    $p = $report.providers[$name]
    if (-not $p.reachable) { $null = $md.AppendLine("| $name | _unreachable_ | | | | | | |"); continue }
    $e = $p.embedding
    if (-not $e.supported) { $null = $md.AppendLine("| $name | _$($e.reason)_ | | | | | | |"); continue }
    $null = $md.AppendLine("| $name | $($e.model) | $($e.dimensions) | $($e["recallAt$TopK"]) | $($e.mrr) | $($e["ndcgAt$TopK"]) | $($e.medianLatencyMs) | $($e.p95LatencyMs) |")
}
$null = $md.AppendLine()
$null = $md.AppendLine('## Chat latency and decode throughput')
$null = $md.AppendLine()
$null = $md.AppendLine('| Provider | Model | First request ms | Median TTFT ms | p95 TTFT ms | Median last visible ms | p95 last visible ms | Exact decode tok/s | Comparable |')
$null = $md.AppendLine('|---|---|---:|---:|---:|---:|---:|---:|---|')
foreach ($name in $report.providers.Keys) {
    $p = $report.providers[$name]
    if (-not $p.reachable) { $null = $md.AppendLine("| $name | _unreachable_ | | | | | | | no |"); continue }
    $c = $p.chat
    if (-not $c.supported) { $null = $md.AppendLine("| $name | _$($c.reason)_ | | | | | | | no |"); continue }
    $rate = if ($null -eq $c.medianDecodeTokensPerSecond) { 'inconclusive' } else { [string]$c.medianDecodeTokensPerSecond }
    $comparable = if ($c.comparable) { 'yes' } else { 'no' }
    $null = $md.AppendLine("| $name | $($c.model) | $($c.firstRequestMs) | $($c.medianTtftMs) | $($c.p95TtftMs) | $($c.medianLastVisibleMs) | $($c.p95LastVisibleMs) | $rate | $comparable |")
}
$null = $md.AppendLine()
$null = $md.AppendLine('First request is not a cold-load claim. Chat metrics come from benchmark-reference-stream.ps1; TTFT and last-visible latency are separately measured from request submission to non-empty content deltas. p95 is smoke-only below 20 repetitions. SSE chunks are never counted as tokens. Exact decode tok/s is withheld when usage or zero-reasoning evidence is missing. Unsupported/inconclusive cases are never scored as zero or treated as a win.')

$mdPath = Join-Path $OutputDirectory 'competitor-benchmark.md'
$md.ToString() | Set-Content -Path $mdPath -Encoding UTF8
Write-Host ''
Write-Host "JSON:     $jsonPath" -ForegroundColor Green
Write-Host "Markdown: $mdPath" -ForegroundColor Green
