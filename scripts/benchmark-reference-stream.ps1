<#
Reference-grade OpenAI-compatible streaming benchmark.
Never treats SSE chunks as tokens. Exact decode tok/s requires visible output, a non-truncated
finish reason, explicit usage, explicit zero reasoning-token usage and no reasoning deltas.
Responses that do not satisfy those conditions are INCONCLUSIVE, never estimated.
AssertProvider requires an operator-attested parity manifest: artifact bytes are locally re-hashed,
but the harness cannot prove which artifact a third-party runtime actually loaded.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string[]]$Cases,
    [Parameter(Mandatory = $true)] [string]$ModelIdentity,
    [string]$ParuxToken = '',
    [string]$Prompt = 'Explain how a hash table handles collisions, using exactly five concise sentences.',
    [string]$SystemPrompt = '',
    [string]$DeveloperPrompt = '',
    [int]$MaxTokens = 256,
    [ValidateRange(0.0, 2.0)] [double]$Temperature = 0,
    [ValidateRange(0.0, 1.0)] [double]$TopP = 1.0,
    [ValidateRange(-2.0, 2.0)] [double]$FrequencyPenalty = 0,
    [ValidateRange(-2.0, 2.0)] [double]$PresencePenalty = 0,
    [int]$Repetitions = 5,
    [ValidateSet('none', 'low', 'medium', 'high')] [string]$ReasoningEffort = 'none',
    [ValidateRange(1, 3600)] [int]$TimeoutSeconds = 120,
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\artifacts\reference-stream-benchmark'),
    [string]$ParityManifestPath = '',
    [string]$AssertProvider = '',
    [ValidateRange(0.0, 5.0)] [double]$TolerancePercent = 2.0,
    [ValidateRange(0.1, 5.0)] [double]$MinimumVisibleLatencyGainPercent = 2.0,
    [switch]$StatisticsSelfTest
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Add-Type -AssemblyName System.Net.Http
if ($Repetitions -lt 2) { throw 'Repetitions must be at least 2.' }
if ($MaxTokens -lt 16) { throw 'MaxTokens must be at least 16.' }
if ([string]::IsNullOrWhiteSpace($ModelIdentity)) { throw 'ModelIdentity is required.' }
if (-not [string]::IsNullOrWhiteSpace($AssertProvider) -and $Repetitions -lt 20) {
    throw 'AssertProvider requires at least 20 repeated measurements; fewer runs are smoke-only and cannot support p95 claims.'
}
if (-not [string]::IsNullOrWhiteSpace($AssertProvider) -and [string]::IsNullOrWhiteSpace($ParityManifestPath)) {
    throw 'AssertProvider requires -ParityManifestPath; caller-supplied model identity alone cannot establish comparable performance.'
}
if ($TolerancePercent -lt 0 -or $TolerancePercent -gt 5) {
    throw 'TolerancePercent must be between 0 and 5; larger tolerances cannot establish a meaningful speed advantage.'
}

function Has-Property([object]$Object, [string]$Name) {
    return $null -ne $Object -and $null -ne $Object.PSObject.Properties[$Name]
}

function Resolve-ParuxToken([string]$Provided) {
    if (-not [string]::IsNullOrWhiteSpace($Provided)) { return $Provided }
    $bootstrap = Join-Path $env:LOCALAPPDATA 'PaRuX\server\bootstrap.json'
    if (-not (Test-Path $bootstrap)) { return '' }
    try { return [string](Get-Content $bootstrap -Raw | ConvertFrom-Json).token } catch { return '' }
}

function Percentile([double[]]$Values, [double]$P) {
    if ($null -eq $Values -or $Values.Count -eq 0) { return $null }
    $sorted = @($Values | Sort-Object)
    if ($P -eq 50 -and $sorted.Count % 2 -eq 0) {
        return ([double]$sorted[$sorted.Count / 2 - 1] + [double]$sorted[$sorted.Count / 2]) / 2
    }
    $index = [Math]::Min($sorted.Count - 1, [Math]::Max(0, [int][Math]::Ceiling(($P / 100.0) * $sorted.Count) - 1))
    return [double]$sorted[$index]
}

function Parse-Case([string]$Spec) {
    $eq = $Spec.IndexOf('=')
    if ($eq -le 0) { throw "Invalid case '$Spec'. Expected name=baseUrl|modelId." }
    $name = $Spec.Substring(0, $eq).Trim()
    $rest = $Spec.Substring($eq + 1)
    $bar = $rest.IndexOf('|')
    if ($bar -le 0 -or $bar -ge ($rest.Length - 1)) { throw "Invalid case '$Spec'. Expected name=baseUrl|modelId." }
    return [pscustomobject]@{
        Name = $name
        BaseUrl = $rest.Substring(0, $bar).Trim().TrimEnd('/')
        Model = $rest.Substring($bar + 1).Trim()
    }
}

function Headers-For([string]$Name, [string]$Token) {
    $headers = @{}
    if ($Name -eq 'parux' -and -not [string]::IsNullOrWhiteSpace($Token)) { $headers['X-PaRuX-Token'] = $Token }
    return $headers
}

function Invoke-ReferenceRun([object]$Case, [hashtable]$Headers, [string]$PromptText, [int]$TokenLimit) {
    $messages = [System.Collections.Generic.List[object]]::new()
    if (-not [string]::IsNullOrEmpty($SystemPrompt)) { $messages.Add(@{ role = 'system'; content = $SystemPrompt }) }
    if (-not [string]::IsNullOrEmpty($DeveloperPrompt)) { $messages.Add(@{ role = 'developer'; content = $DeveloperPrompt }) }
    $messages.Add(@{ role = 'user'; content = $PromptText })
    $payload = @{
        model = $Case.Model
        messages = @($messages.ToArray())
        max_tokens = $TokenLimit
        temperature = $Temperature
        top_p = $TopP
        frequency_penalty = $FrequencyPenalty
        presence_penalty = $PresencePenalty
        reasoning_effort = $ReasoningEffort
        stream = $true
        stream_options = @{ include_usage = $true }
    } | ConvertTo-Json -Depth 8 -Compress

    $request = [System.Net.Http.HttpRequestMessage]::new('POST', "$($Case.BaseUrl)/v1/chat/completions")
    foreach ($key in $Headers.Keys) { $null = $request.Headers.TryAddWithoutValidation($key, [string]$Headers[$key]) }
    $request.Content = [System.Net.Http.StringContent]::new($payload, [System.Text.Encoding]::UTF8, 'application/json')
    $client = [System.Net.Http.HttpClient]::new()
    $client.Timeout = [TimeSpan]::FromSeconds($TimeoutSeconds)
    $clock = [System.Diagnostics.Stopwatch]::StartNew()
    $firstMs = $null
    $lastVisibleMs = $null
    $completionTokens = $null
    $reasoningObserved = $false
    $reasoningTokens = $null
    $finishReason = $null
    $text = [System.Text.StringBuilder]::new()

    try {
        $response = $client.SendAsync($request, [System.Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
        try {
            if (-not $response.IsSuccessStatusCode) {
                $body = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
                throw "HTTP $([int]$response.StatusCode): $body"
            }
            $stream = $response.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
            try {
                $reader = [System.IO.StreamReader]::new($stream)
                try {
                    while ($true) {
                        # ResponseHeadersRead stops HttpClient's timeout at the headers. Bound
                        # each stream read by the remaining TOTAL deadline, including silent SSE.
                        $remainingMs = [int][Math]::Ceiling($TimeoutSeconds * 1000 - $clock.Elapsed.TotalMilliseconds)
                        if ($remainingMs -le 0) { throw 'Streaming request exceeded its total deadline.' }
                        $read = $reader.ReadLineAsync()
                        if (-not $read.Wait($remainingMs)) { throw 'Streaming request exceeded its total deadline.' }
                        $line = $read.GetAwaiter().GetResult()
                        if ($null -eq $line) { break }
                        if ([string]::IsNullOrWhiteSpace($line) -or -not $line.StartsWith('data:')) { continue }
                        $data = $line.Substring(5).Trim()
                        if ($data -eq '[DONE]') { break }
                        try { $chunk = $data | ConvertFrom-Json } catch { continue }

                        if ((Has-Property $chunk 'usage') -and (Has-Property $chunk.usage 'completion_tokens') -and $null -ne $chunk.usage.completion_tokens) {
                            $completionTokens = [int]$chunk.usage.completion_tokens
                            if ((Has-Property $chunk.usage 'completion_tokens_details') -and
                                (Has-Property $chunk.usage.completion_tokens_details 'reasoning_tokens')) {
                                $reasoningTokens = $chunk.usage.completion_tokens_details.reasoning_tokens
                            }
                        }
                        if (-not (Has-Property $chunk 'choices') -or $null -eq $chunk.choices -or $chunk.choices.Count -eq 0) { continue }
                        $delta = $chunk.choices[0].delta
                        if ((Has-Property $chunk.choices[0] 'finish_reason') -and $chunk.choices[0].finish_reason) {
                            $finishReason = [string]$chunk.choices[0].finish_reason
                        }
                        foreach ($field in @('reasoning', 'reasoning_content', 'thinking')) {
                            if ((Has-Property $delta $field) -and $delta.$field) { $reasoningObserved = $true }
                        }
                        if (-not (Has-Property $delta 'content')) { continue }
                        $content = [string]$delta.content
                        if ([string]::IsNullOrEmpty($content)) { continue }
                        $visibleMs = $clock.Elapsed.TotalMilliseconds
                        if ($null -eq $firstMs) { $firstMs = $visibleMs }
                        $lastVisibleMs = $visibleMs
                        $null = $text.Append($content)
                    }
                } finally { $reader.Dispose() }
            } finally { $stream.Dispose() }
        } finally { $response.Dispose() }
    } finally {
        $clock.Stop()
        $request.Dispose()
        $client.Dispose()
    }

    if ($null -eq $firstMs -or $null -eq $lastVisibleMs) { throw 'No streamed visible content delta was observed.' }
    $decodeSeconds = [Math]::Max(0.001, ($clock.Elapsed.TotalMilliseconds - [double]$firstMs) / 1000.0)
    $inconclusiveReason = $null
    if ([string]::IsNullOrWhiteSpace($finishReason)) {
        $inconclusiveReason = 'finish_reason was not observed'
    } elseif ($finishReason -eq 'length') {
        $inconclusiveReason = 'finish_reason=length; output was budget-truncated'
    } elseif ($reasoningObserved) {
        $inconclusiveReason = 'reasoning deltas were observed; visible decode interval is not comparable to total completion usage'
    } elseif ($null -eq $reasoningTokens) {
        $inconclusiveReason = 'usage did not explicitly report reasoning_tokens=0'
    } elseif ([int]$reasoningTokens -ne 0) {
        $inconclusiveReason = 'usage reports hidden reasoning tokens'
    } elseif ($null -eq $completionTokens -or [int]$completionTokens -le 1) {
        $inconclusiveReason = 'exact completion token usage is missing or too small'
    }

    $exactRate = $null
    if ($null -eq $inconclusiveReason) {
        $exactRate = ([int]$completionTokens - 1) / $decodeSeconds
    }
    return [pscustomobject]@{
        comparable = ($null -eq $inconclusiveReason)
        inconclusiveReason = $inconclusiveReason
        totalMs = [Math]::Round($clock.Elapsed.TotalMilliseconds, 2)
        ttftMs = [Math]::Round([double]$firstMs, 2)
        lastVisibleMs = [Math]::Round([double]$lastVisibleMs, 2)
        completionTokens = $completionTokens
        completionTokensPerSecondEndToEnd = if ($null -eq $completionTokens) { $null } else {
            [Math]::Round($completionTokens / [Math]::Max(.001, $clock.Elapsed.TotalSeconds), 2)
        }
        reasoningObserved = $reasoningObserved
        reasoningTokens = $reasoningTokens
        finishReason = $finishReason
        output = $text.ToString()
        outputChars = $text.Length
        charsPerSecond = [Math]::Round(($text.Length / $decodeSeconds), 2)
        exactTokensPerSecond = if ($null -eq $exactRate) { $null } else { [Math]::Round($exactRate, 2) }
    }
}

$token = Resolve-ParuxToken $ParuxToken
$parsed = @($Cases | ForEach-Object { Parse-Case $_ })
if (@($parsed.Name | Select-Object -Unique).Count -ne $parsed.Count) { throw 'Case names must be unique.' }
$parityEvidence = $null
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null

$report = [ordered]@{
    schemaVersion = 5
    generatedUtc = (Get-Date).ToUniversalTime().ToString('o')
    modelIdentity = $ModelIdentity
    modelIdentitySource = 'caller-supplied identity label; underlying artifact/revision parity is not independently verified'
    parityEvidence = $parityEvidence
    prompt = $Prompt
    requestProfile = [ordered]@{
        systemPrompt = $SystemPrompt
        developerPrompt = $DeveloperPrompt
        userPrompt = $Prompt
        tools = @()
        temperature = $Temperature
        topP = $TopP
        frequencyPenalty = $FrequencyPenalty
        presencePenalty = $PresencePenalty
        reasoningEffort = $ReasoningEffort
        maxTokens = $MaxTokens
        stream = $true
        includeUsage = $true
    }
    maxTokens = $MaxTokens
    repetitions = $Repetitions
    latencyEvidence = if ($Repetitions -ge 20) { 'repeated-sample-size' } else { 'smoke-only' }
    reasoningEffort = $ReasoningEffort
    providerSchedule = [ordered]@{
        strategy = 'paired-round-robin'
        firstRequestOrder = @($parsed | ForEach-Object { $_.Name })
        repeatedRoundStartRotates = $true
    }
    machine = [ordered]@{
        os = [System.Runtime.InteropServices.RuntimeInformation]::OSDescription
        processorCount = [Environment]::ProcessorCount
    }
    providers = [ordered]@{}
}

function Get-MedianBootstrapConfidenceInterval([double[]]$Values, [int]$Seed, [int]$BootstrapSamples = 4000) {
    if ($null -eq $Values -or $Values.Count -lt 20) { return $null }
    $random = New-Object System.Random($Seed)
    $medians = New-Object 'double[]' $BootstrapSamples
    for ($sampleIndex = 0; $sampleIndex -lt $BootstrapSamples; $sampleIndex++) {
        $resample = New-Object 'double[]' $Values.Count
        for ($index = 0; $index -lt $Values.Count; $index++) {
            $resample[$index] = $Values[$random.Next(0, $Values.Count)]
        }
        $medians[$sampleIndex] = [double](Percentile $resample 50)
    }
    return [ordered]@{
        pointEstimate = [Math]::Round((Percentile $Values 50), 2)
        lower95 = [double](Percentile $medians 2.5)
        lower95Display = [Math]::Round((Percentile $medians 2.5), 2)
        upper95 = [double](Percentile $medians 97.5)
        upper95Display = [Math]::Round((Percentile $medians 97.5), 2)
        method = 'paired percentile bootstrap confidence interval of median within-round relative improvement'
        confidenceLevel = 0.95
        bootstrapSamples = $BootstrapSamples
        pairedSamples = $Values.Count
    }
}

function Test-MinimumVisibleLatencyGain([object]$Confidence, [double]$RequiredGainPercent) {
    return $null -ne $Confidence -and [double]$Confidence.lower95 -ge $RequiredGainPercent
}

if ($StatisticsSelfTest) {
    $belowThreshold = [ordered]@{ lower95 = 1.995 }
    $atThreshold = [ordered]@{ lower95 = 2.0 }
    $equalLatency = [ordered]@{ lower95 = 0.0 }
    if (Test-MinimumVisibleLatencyGain $belowThreshold 2.0) { throw 'Bootstrap threshold self-test failed: 1.995% must not meet a 2% minimum.' }
    if (-not (Test-MinimumVisibleLatencyGain $atThreshold 2.0)) { throw 'Bootstrap threshold self-test failed: an exact 2% lower bound must meet a 2% minimum.' }
    if (Test-MinimumVisibleLatencyGain $equalLatency 0.1) { throw 'Bootstrap threshold self-test failed: a tie must not meet a positive minimum.' }
    Write-Host 'PASS bootstrap threshold and zero-gain self-test.' -ForegroundColor Green
    exit 0
}

function Get-Sha256File([string]$Path) {
    $stream = [System.IO.File]::OpenRead($Path)
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        return [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-', '').ToLowerInvariant()
    } finally {
        $sha.Dispose()
        $stream.Dispose()
    }
}

function Get-Sha256Bytes([byte[]]$Bytes) {
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($sha.ComputeHash($Bytes)).Replace('-', '').ToLowerInvariant() }
    finally { $sha.Dispose() }
}

function Get-ManifestInteger([object]$Value, [string]$Name, [long]$Minimum, [long]$Maximum) {
    if ($null -eq $Value -or $Value.GetType().FullName -notin @('System.Int32', 'System.Int64')) {
        throw "Parity manifest $Name must be a JSON integer number."
    }
    [long]$integer = $Value
    if ($integer -lt $Minimum -or $integer -gt $Maximum) {
        throw "Parity manifest $Name must be between $Minimum and $Maximum."
    }
    return [int]$integer
}

function Read-ParityManifest([string]$Path, [object[]]$CasesToCheck, [int]$RunCount) {
    $script:ParityArtifactSnapshots = @()
    $script:ParityManifestResolvedPath = $null
    $script:ParityManifestInitialSha256 = $null
    if ([string]::IsNullOrWhiteSpace($Path)) {
        return [ordered]@{
            status = 'not-provided'
            speedAssertionEligible = $false
            reason = 'No artifact/runtime parity manifest was supplied.'
            providerEvidence = [ordered]@{}
            manifestSha256 = $null
            verifiedBy = $null
            verifiedUtc = $null
            runtimeAssociationIndependentlyVerified = $false
            postRunIntegrityVerified = $null
        }
    }
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "Parity manifest not found: $Path" }

    try {
        $resolvedManifestPath = (Resolve-Path -LiteralPath $Path).Path
        $manifestBytes = [System.IO.File]::ReadAllBytes($resolvedManifestPath)
        $manifestText = [System.Text.Encoding]::UTF8.GetString($manifestBytes)
        if ($manifestText.Length -gt 0 -and [int][char]$manifestText[0] -eq 0xFEFF) {
            $manifestText = $manifestText.Substring(1)
        }
        $manifest = $manifestText | ConvertFrom-Json
    } catch { throw "Parity manifest is not valid JSON: $($_.Exception.Message)" }
    $script:ParityManifestResolvedPath = $resolvedManifestPath
    $script:ParityManifestInitialSha256 = Get-Sha256Bytes $manifestBytes
    if ($manifest.schemaVersion -ne 1) { throw 'Parity manifest schemaVersion must be 1.' }
    if ([string]::IsNullOrWhiteSpace([string]$manifest.verifiedBy)) { throw 'Parity manifest requires verifiedBy.' }
    try {
        $verifiedUtc = [DateTimeOffset]::Parse(
            [string]$manifest.verifiedUtc,
            [System.Globalization.CultureInfo]::InvariantCulture,
            [System.Globalization.DateTimeStyles]::RoundtripKind)
    } catch { throw 'Parity manifest verifiedUtc must be an ISO-8601 timestamp.' }
    $age = [DateTimeOffset]::UtcNow - $verifiedUtc.ToUniversalTime()
    if ($age.TotalMinutes -lt -5 -or $age.TotalHours -gt 4) {
        throw 'Parity manifest must be current (no more than 4 hours old and no more than 5 minutes in the future).'
    }
    if (-not (Has-Property $manifest 'providers') -or $null -eq $manifest.providers) {
        throw 'Parity manifest requires a providers object.'
    }

    $providerEvidence = [ordered]@{}
    $artifactHashByPath = @{}
    $reference = $null
    foreach ($case in $CasesToCheck) {
        $entryProperty = $manifest.providers.PSObject.Properties[[string]$case.Name]
        if ($null -eq $entryProperty) { throw "Parity manifest is missing provider '$($case.Name)'." }
        $entry = $entryProperty.Value
        if ([string]$entry.apiModelId -cne [string]$case.Model) {
            throw "Parity manifest API model for '$($case.Name)' does not match the requested model id."
        }
        if ([string]::IsNullOrWhiteSpace([string]$entry.artifactPath) -or
            -not (Test-Path -LiteralPath ([string]$entry.artifactPath) -PathType Leaf)) {
            throw "Parity manifest artifactPath for '$($case.Name)' must name an existing model file."
        }
        $artifactPath = (Resolve-Path -LiteralPath ([string]$entry.artifactPath)).Path
        if (-not $artifactHashByPath.ContainsKey($artifactPath)) {
            $artifactHashByPath[$artifactPath] = Get-Sha256File $artifactPath
        }
        $actualArtifactHash = [string]$artifactHashByPath[$artifactPath]
        if ([string]$entry.artifactSha256 -notmatch '^[A-Fa-f0-9]{64}$' -or
            -not [string]::Equals([string]$entry.artifactSha256, $actualArtifactHash, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Parity manifest artifact SHA-256 for '$($case.Name)' does not match the file on disk."
        }
        if ([string]$entry.runtimeSettingsSha256 -notmatch '^[A-Fa-f0-9]{64}$') {
            throw "Parity manifest runtimeSettingsSha256 for '$($case.Name)' must be a SHA-256 value."
        }
        if ([string]$entry.chatTemplateSha256 -notmatch '^[A-Fa-f0-9]{64}$') {
            throw "Parity manifest chatTemplateSha256 for '$($case.Name)' must be a SHA-256 value."
        }
        foreach ($integerField in @('contextTokens', 'parallel', 'warmupRequests')) {
            if (-not (Has-Property $entry $integerField)) {
                throw "Parity manifest $integerField for '$($case.Name)' must be a JSON integer number."
            }
        }
        $contextTokens = Get-ManifestInteger $entry.contextTokens "contextTokens for '$($case.Name)'" 1 4194304
        $parallel = Get-ManifestInteger $entry.parallel "parallel for '$($case.Name)'" 1 128
        $warmupRequests = Get-ManifestInteger $entry.warmupRequests "warmupRequests for '$($case.Name)'" 1 10000
        if ($entry.warmStateConfirmed -ne $true) {
            throw "Parity manifest must attest a confirmed warm state and at least one prior warm-up request for '$($case.Name)'."
        }
        if ([string]::IsNullOrWhiteSpace([string]$entry.verificationNote)) {
            throw "Parity manifest requires a verificationNote for '$($case.Name)'."
        }

        $normalized = [ordered]@{
            apiModelId = [string]$case.Model
            artifactSha256 = $actualArtifactHash
            runtimeSettingsSha256 = ([string]$entry.runtimeSettingsSha256).ToLowerInvariant()
            chatTemplateSha256 = ([string]$entry.chatTemplateSha256).ToLowerInvariant()
            contextTokens = $contextTokens
            parallel = $parallel
            warmStateConfirmed = $true
            warmupRequests = $warmupRequests
        }
        if ($null -eq $reference) { $reference = $normalized }
        else {
            foreach ($field in @('artifactSha256', 'runtimeSettingsSha256', 'chatTemplateSha256', 'contextTokens', 'parallel', 'warmStateConfirmed', 'warmupRequests')) {
                if ([string]$normalized[$field] -cne [string]$reference[$field]) {
                    throw "Parity manifest mismatch: provider '$($case.Name)' differs in '$field'; competitive performance is inconclusive."
                }
            }
        }
        $providerEvidence[$case.Name] = $normalized
        $script:ParityArtifactSnapshots += [pscustomobject]@{
            Provider = [string]$case.Name
            Path = $artifactPath
            Sha256 = $actualArtifactHash
        }
    }

    return [ordered]@{
        status = 'operator-attested'
        speedAssertionEligible = ($CasesToCheck.Count -ge 2 -and $RunCount -ge 20)
        reason = if ($CasesToCheck.Count -lt 2) {
            'At least two providers are required.'
        } elseif ($RunCount -lt 20) {
            'At least 20 measured repetitions are required; this run is smoke-only.'
        } else { $null }
        providerEvidence = $providerEvidence
        manifestSha256 = $script:ParityManifestInitialSha256
        verifiedBy = [string]$manifest.verifiedBy
        verifiedUtc = $verifiedUtc.ToUniversalTime().ToString('o')
        runtimeAssociationIndependentlyVerified = $false
        postRunIntegrityVerified = $null
        scope = 'The harness re-hashed each listed artifact and checked matching declared runtime settings, context, concurrency and warm-up. It cannot independently prove a third-party process loaded the listed file.'
    }
}

function Update-ParityIntegrity([System.Collections.IDictionary]$Evidence) {
    if ($Evidence.status -ne 'operator-attested') { return }
    $failures = [System.Collections.Generic.List[string]]::new()
    try {
        $currentManifestHash = Get-Sha256File $script:ParityManifestResolvedPath
        if (-not [string]::Equals($currentManifestHash, $script:ParityManifestInitialSha256, [StringComparison]::OrdinalIgnoreCase)) {
            $failures.Add('The parity manifest changed during the benchmark.')
        }
    } catch { $failures.Add('The parity manifest could not be re-read after measurement.') }
    foreach ($snapshot in $script:ParityArtifactSnapshots) {
        try {
            $currentArtifactHash = Get-Sha256File $snapshot.Path
            if (-not [string]::Equals($currentArtifactHash, $snapshot.Sha256, [StringComparison]::OrdinalIgnoreCase)) {
                $failures.Add("Provider '$($snapshot.Provider)' model-file bytes changed during the benchmark.")
            }
        } catch { $failures.Add("Provider '$($snapshot.Provider)' model file could not be re-hashed after measurement.") }
    }
    $Evidence.postRunIntegrityVerified = ($failures.Count -eq 0)
    $Evidence.manifestBytesStable = ($failures -notcontains 'The parity manifest changed during the benchmark.' -and
        $failures -notcontains 'The parity manifest could not be re-read after measurement.')
    $Evidence.artifactBytesStable = -not ($failures | Where-Object { $_ -match 'model-file bytes changed|model file could not be re-hashed' })
    if ($failures.Count -gt 0) {
        $Evidence.status = 'invalidated-during-run'
        $Evidence.speedAssertionEligible = $false
        $Evidence.reason = $failures -join ' '
    }
}

$parityEvidence = Read-ParityManifest -Path $ParityManifestPath -CasesToCheck $parsed -RunCount $Repetitions
if (-not [string]::IsNullOrWhiteSpace($AssertProvider) -and -not $parityEvidence.speedAssertionEligible) {
    throw 'AssertProvider requires a valid parity manifest covering at least two providers.'
}
$report['parityEvidence'] = $parityEvidence
$report['speedAssertion'] = [ordered]@{
    mode = 'speed-only; output quality is not scored by this harness'
    subjectProvider = if ([string]::IsNullOrWhiteSpace($AssertProvider)) { $null } else { $AssertProvider }
    allowedRegressionPercent = $TolerancePercent
    requiredVisibleLatencyGainPercent = $MinimumVisibleLatencyGainPercent
    visibleLatencyWinRule = 'At least one paired median TTFT or median last-visible improvement must have a 95% bootstrap lower confidence bound at or above the required gain.'
    confidenceMethod = 'paired percentile bootstrap of median within-round relative improvement'
    bootstrapSamples = 4000
    result = if ([string]::IsNullOrWhiteSpace($AssertProvider)) { 'not-requested' } else { 'pending' }
    comparisons = @()
    failures = @()
}

$measurements = [ordered]@{}
foreach ($case in $parsed) {
    $measurements[$case.Name] = [pscustomobject]@{
        Case = $case
        Headers = (Headers-For $case.Name $token)
        First = $null
        Runs = [System.Collections.Generic.List[object]]::new()
        Error = $null
    }
}

# Collect each provider's initial observation before any provider gets repeated warm requests.
foreach ($case in $parsed) {
    $measurement = $measurements[$case.Name]
    Write-Host "== first request: $($case.Name) · $($case.Model)" -ForegroundColor Cyan
    try {
        $measurement.First = Invoke-ReferenceRun $case $measurement.Headers $Prompt $MaxTokens
    } catch {
        $measurement.Error = "Initial request failed: $($_.Exception.Message)"
        Write-Warning "$($case.Name): $($measurement.Error)"
    }
}

# Rotate the starting provider on every repeated round. This balances which endpoint goes first
# while keeping one request in flight at a time. The endpoints must still be configured so their
# simultaneous resident-model footprint is representative and does not force paging or swapping.
for ($round = 0; $round -lt $Repetitions; $round++) {
    for ($offset = 0; $offset -lt $parsed.Count; $offset++) {
        $position = ($round + $offset) % $parsed.Count
        $case = $parsed[$position]
        $measurement = $measurements[$case.Name]
        if (-not [string]::IsNullOrWhiteSpace([string]$measurement.Error)) { continue }
        try {
            $sample = Invoke-ReferenceRun $case $measurement.Headers $Prompt $MaxTokens
            $sample | Add-Member -NotePropertyName repetition -NotePropertyValue ($round + 1)
            $sample | Add-Member -NotePropertyName schedulePosition -NotePropertyValue ($offset + 1)
            $measurement.Runs.Add($sample)
        } catch {
            $measurement.Error = "Repeated request $($round + 1) failed: $($_.Exception.Message)"
            Write-Warning "$($case.Name): $($measurement.Error)"
        }
    }
}

foreach ($case in $parsed) {
    $measurement = $measurements[$case.Name]
    $runs = @($measurement.Runs.ToArray())
    if (-not [string]::IsNullOrWhiteSpace([string]$measurement.Error) -or $runs.Count -ne $Repetitions) {
        $providerError = if (-not [string]::IsNullOrWhiteSpace([string]$measurement.Error)) {
            [string]$measurement.Error
        } else {
            "Only $($runs.Count) of $Repetitions repeated requests completed."
        }
        $report.providers[$case.Name] = [ordered]@{
            reachable = $false
            model = $case.Model
            error = $providerError
            requestedRuns = $Repetitions
            completedRuns = $runs.Count
            firstRequestMs = if ($null -eq $measurement.First) { $null } else { $measurement.First.totalMs }
            runs = $runs
        }
        continue
    }

    $first = $measurement.First
    $ttft = [double[]]@($runs | ForEach-Object { $_.ttftMs })
    $lastVisible = [double[]]@($runs | ForEach-Object { $_.lastVisibleMs })
    $chars = [double[]]@($runs | ForEach-Object { $_.charsPerSecond })
    $endToEndTokens = [double[]]@($runs | Where-Object { $null -ne $_.completionTokensPerSecondEndToEnd } |
        ForEach-Object { $_.completionTokensPerSecondEndToEnd })
    $comparableRuns = @($runs | Where-Object { $_.comparable })
    $exact = [double[]]@($comparableRuns | ForEach-Object { $_.exactTokensPerSecond })
    $allComparable = $comparableRuns.Count -eq $runs.Count
    $report.providers[$case.Name] = [ordered]@{
        reachable = $true
        model = $case.Model
        firstRequestMs = $first.totalMs
        firstRequestComparable = [bool]$first.comparable
        firstRequestInconclusiveReason = $first.inconclusiveReason
        requestedRuns = $Repetitions
        completedRuns = $runs.Count
        allRunsComparable = $allComparable
        comparableRuns = $comparableRuns.Count
        totalRuns = $runs.Count
        medianTtftMs = [Math]::Round((Percentile $ttft 50), 2)
        p95TtftMs = [Math]::Round((Percentile $ttft 95), 2)
        medianLastVisibleMs = [Math]::Round((Percentile $lastVisible 50), 2)
        p95LastVisibleMs = [Math]::Round((Percentile $lastVisible 95), 2)
        medianOutputCharsPerSecond = [Math]::Round((Percentile $chars 50), 2)
        medianCompletionTokensPerSecondEndToEnd = if ($endToEndTokens.Count -eq $runs.Count) {
            [Math]::Round((Percentile $endToEndTokens 50), 2)
        } else { $null }
        exactTokenRateAvailableForAllRuns = $allComparable
        medianDecodeTokensPerSecond = if ($allComparable) { [Math]::Round((Percentile $exact 50), 2) } else { $null }
        runs = $runs
    }
}

Update-ParityIntegrity $parityEvidence
$report['parityEvidence'] = $parityEvidence
$report['speedAssertion'].speedAssertionEligible = [bool]$parityEvidence.speedAssertionEligible -and
    ($parityEvidence.postRunIntegrityVerified -ne $false)

$speedGateFailures = [System.Collections.Generic.List[string]]::new()
$speedComparisons = [System.Collections.Generic.List[object]]::new()
if (-not [string]::IsNullOrWhiteSpace($AssertProvider)) {
    if (-not $report.providers.Contains($AssertProvider)) {
        $speedGateFailures.Add("AssertProvider '$AssertProvider' is missing.")
    } else {
        $subject = $report.providers[$AssertProvider]
        if (-not $subject.reachable) { $speedGateFailures.Add("AssertProvider '$AssertProvider' is unreachable.") }
        if (-not $parityEvidence.speedAssertionEligible -or -not $parityEvidence.postRunIntegrityVerified) {
            $speedGateFailures.Add('Parity evidence was absent, ineligible or changed during the measured run.')
        }
        $factor = 1.0 + ($TolerancePercent / 100.0)
        if ($report.providers.Count -lt 2) { $speedGateFailures.Add('A competitive gate requires at least one competitor.') }
        if ($subject.reachable -and -not $subject.allRunsComparable) {
            $speedGateFailures.Add("$AssertProvider does not have comparable evidence for every measured run.")
        }
        foreach ($name in $report.providers.Keys) {
            if ($name -eq $AssertProvider) { continue }
            $other = $report.providers[$name]
            if (-not $other.reachable) { $speedGateFailures.Add("Competitor $name has no comparable result."); continue }
            if (-not $other.allRunsComparable) { $speedGateFailures.Add("Competitor $name has one or more inconclusive runs."); continue }
            if (-not $subject.reachable) { continue }
            if ($null -eq $subject.medianDecodeTokensPerSecond -or $null -eq $other.medianDecodeTokensPerSecond) {
                $speedGateFailures.Add("Decode comparison with $name lacks exact non-reasoning token evidence.")
                continue
            }
            $metrics = @(
                @{ Name = 'medianTtftMs'; Display = 'TTFT'; Direction = 'lower' },
                @{ Name = 'p95TtftMs'; Display = 'p95 TTFT'; Direction = 'lower' },
                @{ Name = 'medianLastVisibleMs'; Display = 'last visible'; Direction = 'lower' },
                @{ Name = 'p95LastVisibleMs'; Display = 'p95 last visible'; Direction = 'lower' },
                @{ Name = 'medianDecodeTokensPerSecond'; Display = 'decode tok/s'; Direction = 'higher' }
            )
            $visibleWin = $false
            $deltas = [ordered]@{}
            foreach ($metric in $metrics) {
                $subjectValue = [double]$subject[$metric.Name]
                $otherValue = [double]$other[$metric.Name]
                if ($otherValue -le 0) { $speedGateFailures.Add("Competitor $name has a non-positive $($metric.Display) measurement."); continue }
                $deltaPercent = if ($metric.Direction -eq 'lower') {
                    100.0 * ($subjectValue - $otherValue) / $otherValue
                } else {
                    100.0 * ($subjectValue - $otherValue) / $otherValue
                }
                $deltas[$metric.Name] = [Math]::Round($deltaPercent, 2)
                if ($metric.Direction -eq 'lower') {
                    if ($subjectValue -gt ($otherValue * $factor)) {
                        $speedGateFailures.Add("$($metric.Display): $AssertProvider $subjectValue vs $name $otherValue")
                    }
                } else {
                    if (($subjectValue * $factor) -lt $otherValue) {
                        $speedGateFailures.Add("$($metric.Display): $AssertProvider $subjectValue vs $name $otherValue")
                    }
                }
            }
            $pairedLatencyConfidence = [ordered]@{}
            foreach ($latencyMetric in @(
                @{ Name = 'ttftMs'; Seed = 260926 },
                @{ Name = 'lastVisibleMs'; Seed = 260927 }
            )) {
                $subjectByRound = @{}
                $otherByRound = @{}
                foreach ($run in $subject.runs) { $subjectByRound[[int]$run.repetition] = [double]$run.PSObject.Properties[$latencyMetric.Name].Value }
                foreach ($run in $other.runs) { $otherByRound[[int]$run.repetition] = [double]$run.PSObject.Properties[$latencyMetric.Name].Value }
                $pairedImprovements = [System.Collections.Generic.List[double]]::new()
                for ($round = 1; $round -le $Repetitions; $round++) {
                    if (-not $subjectByRound.ContainsKey($round) -or -not $otherByRound.ContainsKey($round)) { continue }
                    $competitorLatency = [double]$otherByRound[$round]
                    if ($competitorLatency -le 0) { continue }
                    $pairedImprovements.Add(100.0 * ($competitorLatency - [double]$subjectByRound[$round]) / $competitorLatency)
                }
                $confidence = Get-MedianBootstrapConfidenceInterval -Values ([double[]]$pairedImprovements.ToArray()) -Seed $latencyMetric.Seed
                $pairedLatencyConfidence[$latencyMetric.Name] = $confidence
                if (Test-MinimumVisibleLatencyGain $confidence $MinimumVisibleLatencyGainPercent) { $visibleWin = $true }
            }
            if (-not $visibleWin) {
                $speedGateFailures.Add("$AssertProvider did not demonstrate a paired median TTFT or last-visible latency improvement over $name whose 95% bootstrap lower bound meets $MinimumVisibleLatencyGainPercent%.")
            }
            $speedComparisons.Add([ordered]@{
                competitor = $name
                subjectMinusCompetitorPercent = $deltas
                pairedMedianLatencyImprovement = $pairedLatencyConfidence
            })
        }
    }
    $report['speedAssertion'].result = if ($speedGateFailures.Count -eq 0) { 'pass' } else { 'fail' }
    $report['speedAssertion'].comparisons = @($speedComparisons.ToArray())
    $report['speedAssertion'].failures = @($speedGateFailures.ToArray())
}

$jsonPath = Join-Path $OutputDirectory 'reference-stream-benchmark.json'
$report | ConvertTo-Json -Depth 12 | Set-Content -Path $jsonPath -Encoding UTF8
$md = [System.Text.StringBuilder]::new()
$null = $md.AppendLine('# Reference streaming benchmark')
$null = $md.AppendLine()
$null = $md.AppendLine("Model identity label: $ModelIdentity (caller-supplied; it is not proof of parity).")
$null = $md.AppendLine("Parity attestation: $($parityEvidence.status); speed assertion eligible=$($parityEvidence.speedAssertionEligible). The manifest's artifact bytes are re-hashed locally, but provider-to-file loading is not independently verified by this harness.")
$null = $md.AppendLine("Speed assertion: $($report.speedAssertion.result); maximum regression=$($report.speedAssertion.allowedRegressionPercent)%; required visible-latency gain=$($report.speedAssertion.requiredVisibleLatencyGainPercent)%.")
$null = $md.AppendLine("Visible-latency win rule: $($report.speedAssertion.visibleLatencyWinRule) The 20-round minimum is a floor, not a guarantee of statistical significance.")
foreach ($comparison in $report.speedAssertion.comparisons) {
    $null = $md.AppendLine("Speed deltas vs $($comparison.competitor) (subject minus competitor, percent): $($comparison.subjectMinusCompetitorPercent | ConvertTo-Json -Compress).")
    $null = $md.AppendLine("Paired latency improvement confidence evidence: $($comparison.pairedMedianLatencyImprovement | ConvertTo-Json -Depth 5 -Compress).")
}
foreach ($failure in $report.speedAssertion.failures) { $null = $md.AppendLine("Speed gate failure: $failure") }
$null = $md.AppendLine("Request profile: system/developer prompts are included identically when supplied; tools are not used; temperature=$Temperature, top_p=$TopP, frequency_penalty=$FrequencyPenalty, presence_penalty=$PresencePenalty, reasoning=$ReasoningEffort, max_tokens=$MaxTokens.")
$null = $md.AppendLine("Generated: $($report.generatedUtc)")
$null = $md.AppendLine('Provider order: one initial request per provider, then paired requests in round-robin order with a rotating start provider. Endpoints must fit their representative simultaneous resident-model footprint; otherwise the run measures resource contention or swapping, not a clean latency comparison.')
$null = $md.AppendLine()
$null = $md.AppendLine('| Provider | API model | First request ms | Median TTFT ms | p95 TTFT ms | Median last visible ms | p95 last visible ms | Chars/s | End-to-end tok/s | Exact decode tok/s | Comparable |')
$null = $md.AppendLine('|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---|')
foreach ($name in $report.providers.Keys) {
    $entry = $report.providers[$name]
    if (-not $entry.reachable) { $null = $md.AppendLine("| $name | $($entry.model) | error | | | | | | | | no |") ; continue }
    $exactText = if ($null -eq $entry.medianDecodeTokensPerSecond) { 'inconclusive' } else { [string]$entry.medianDecodeTokensPerSecond }
    $comparableText = if ($entry.allRunsComparable) { 'yes' } else { 'no' }
    $null = $md.AppendLine("| $name | $($entry.model) | $($entry.firstRequestMs) | $($entry.medianTtftMs) | $($entry.p95TtftMs) | $($entry.medianLastVisibleMs) | $($entry.p95LastVisibleMs) | $($entry.medianOutputCharsPerSecond) | $($entry.medianCompletionTokensPerSecondEndToEnd) | $exactText | $comparableText |")
}
$null = $md.AppendLine()
$null = $md.AppendLine("First request does not prove a cold model load, and this script does not independently unload or verify a provider's warm/cold cache state. TTFT is request-submit to the first non-empty visible content delta; last-visible is request-submit to the last non-empty visible content delta and is distinct from stream closure. p95 is smoke-only below 20 repeated samples; 20 is a minimum sample-count floor, not a statistical confidence guarantee, and AssertProvider requires at least 20 repetitions. Exact decode tok/s requires visible output, non-truncated finish_reason, exact usage, explicit reasoning_tokens=0 and no reasoning deltas; otherwise it is withheld as inconclusive. End-to-end token throughput includes all reported completion tokens. Quality must be assessed separately from the saved outputs; this report alone cannot prove superiority.")
$mdPath = Join-Path $OutputDirectory 'reference-stream-benchmark.md'
$md.ToString() | Set-Content -Path $mdPath -Encoding UTF8

if (-not [string]::IsNullOrWhiteSpace($AssertProvider) -and $speedGateFailures.Count -gt 0) {
    $speedGateFailures | ForEach-Object { Write-Error $_ }
    throw "$AssertProvider did not meet the reference-performance gate. This speed-only gate does not establish output quality or best-in-class status. Optimize and rerun on identical hardware/model settings."
}

Write-Host "JSON: $jsonPath" -ForegroundColor Green
Write-Host "Markdown: $mdPath" -ForegroundColor Green
