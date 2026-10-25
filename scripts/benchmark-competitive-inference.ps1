[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$PaRuXEndpoint,
    [Parameter(Mandatory=$true)][string]$CompetitorEndpoint,
    [string]$PaRuXToken = '',
    [string]$CompetitorToken = '',
    [string]$PaRuXAuthHeader = 'X-PaRuX-Token',
    [string]$CompetitorAuthHeader = 'Authorization',
    [string]$PaRuXModel = 'parux-main',
    [string]$CompetitorModel = 'default',
    [int[]]$ContextTargets = @(8192, 32768, 65536, 131072, 180000),
    [int[]]$Concurrency = @(1, 2, 4, 8),
    [ValidateRange(1,20)][int]$RunsPerCell = 3,
    [ValidateRange(0,10)][int]$WarmupRuns = 1,
    [ValidateRange(64,4096)][int]$MaxOutputTokens = 512,
    [ValidateRange(30,3600)][int]$TimeoutSeconds = 900,
    [bool]$PauseBetweenStacks = $true,
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\artifacts\competitive-inference'),
    [switch]$FailIfPaRuXLosesMandatoryCell
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# Pure external HTTP benchmark. No competitor implementation is loaded by PaRuX. The same exact
# request body bytes are sent to both OpenAI-compatible endpoints. A completed HTTP response is not
# automatically comparable: winner metrics require visible output, a non-length finish reason,
# exact OpenAI usage and no hidden-reasoning ambiguity. Missing evidence is INCONCLUSIVE, never
# estimated as characters/4 and never allowed to promote a winner.
if (-not ('PaRuXCompetitiveHttpBench' -as [type])) {
    # Reference the assemblies that actually back the using directives below, resolved from the
    # running process rather than named as files. "System.Net.Http.dll" alone was enough on Windows
    # PowerShell's full framework, but under PowerShell 7 the compiler starts from a minimal set and
    # the very first using - System.Linq - failed with CS0234, so this benchmark could not run at all
    # in a freshly started shell.
    # Referencing the loaded runtime wholesale, rather than naming assemblies one at a time: the
    # first attempt named System.Net.Http.dll, the next added Linq and friends, and each round
    # surfaced another transitively required assembly (System.Net.Primitives for HttpStatusCode).
    # The set a benchmark needs is not worth maintaining by hand.
    $benchReferences = [AppDomain]::CurrentDomain.GetAssemblies() |
        Where-Object { -not $_.IsDynamic -and $_.Location } |
        Select-Object -ExpandProperty Location |
        Sort-Object -Unique

    Add-Type -Language CSharp -ReferencedAssemblies $benchReferences -TypeDefinition @'
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

public sealed class PaRuXBenchResult
{
    public bool Success { get; set; }
    public bool Comparable { get; set; }
    public string Error { get; set; }
    public string InconclusiveReason { get; set; }
    public string FinishReason { get; set; }
    public bool ReasoningObserved { get; set; }
    public int ReasoningTokens { get; set; }
    public double TtftMs { get; set; }
    public double EndToEndMs { get; set; }
    public int PromptTokens { get; set; }
    public int CompletionTokens { get; set; }
    public int OutputCharacters { get; set; }
    public string TokenSource { get; set; }
}

public static class PaRuXCompetitiveHttpBench
{
    private static readonly Regex PromptTokens = new Regex("\\\"prompt_tokens\\\"\\s*:\\s*(\\d+)", RegexOptions.Compiled);
    private static readonly Regex CompletionTokens = new Regex("\\\"completion_tokens\\\"\\s*:\\s*(\\d+)", RegexOptions.Compiled);
    private static readonly Regex ReasoningTokens = new Regex("\\\"reasoning_tokens\\\"\\s*:\\s*(\\d+)", RegexOptions.Compiled);
    private static readonly Regex VisibleContent = new Regex("\\\"content\\\"\\s*:\\s*\\\"((?:\\\\.|[^\\\"])*)\\\"", RegexOptions.Compiled);
    private static readonly Regex ReasoningContent = new Regex("\\\"(?:reasoning_content|reasoning|thinking)\\\"\\s*:\\s*\\\"((?:\\\\.|[^\\\"])*)\\\"", RegexOptions.Compiled);
    private static readonly Regex FinishReason = new Regex("\\\"finish_reason\\\"\\s*:\\s*\\\"([^\\\"]+)\\\"", RegexOptions.Compiled);

    public static PaRuXBenchResult[] RunMany(string endpoint, string authHeader, string token, string body, int concurrency, int timeoutSeconds, int requestedPromptTokens)
    {
        using (var client = new HttpClient())
        {
            client.Timeout = Timeout.InfiniteTimeSpan;
            if (!string.IsNullOrWhiteSpace(token))
            {
                if (string.Equals(authHeader, "Authorization", StringComparison.OrdinalIgnoreCase))
                    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Trim());
                else
                    client.DefaultRequestHeaders.TryAddWithoutValidation(authHeader.Trim(), token.Trim());
            }
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            Task<PaRuXBenchResult>[] tasks = Enumerable.Range(0, concurrency)
                .Select(_ => RunOne(client, endpoint, body, timeoutSeconds, requestedPromptTokens)).ToArray();
            return Task.WhenAll(tasks).GetAwaiter().GetResult();
        }
    }

    private static async Task<PaRuXBenchResult> RunOne(HttpClient client, string endpoint, string body, int timeoutSeconds, int requestedPromptTokens)
    {
        var result = new PaRuXBenchResult {
            PromptTokens = 0,
            CompletionTokens = 0,
            ReasoningTokens = 0,
            TokenSource = "unavailable",
            InconclusiveReason = "request did not complete"
        };
        long startTicks = Stopwatch.GetTimestamp();
        long firstVisibleTicks = -1;
        var watch = Stopwatch.StartNew();
        var raw = new StringBuilder();
        int outputCharacters = 0;
        bool reasoningObserved = false;
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds)))
        {
            try
            {
                using (var request = new HttpRequestMessage(HttpMethod.Post, endpoint))
                {
                    request.Content = new StringContent(body, Encoding.UTF8, "application/json");
                    using (HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false))
                    {
                        if (!response.IsSuccessStatusCode)
                        {
                            result.Error = "HTTP " + (int)response.StatusCode + ": " + (await response.Content.ReadAsStringAsync().ConfigureAwait(false));
                            result.EndToEndMs = watch.Elapsed.TotalMilliseconds;
                            return result;
                        }

                        using (Stream stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                        using (var reader = new StreamReader(stream, Encoding.UTF8))
                        {
                            while (true)
                            {
                                timeout.Token.ThrowIfCancellationRequested();
                                Task<string> read = reader.ReadLineAsync();
                                Task completed = await Task.WhenAny(read, Task.Delay(Timeout.Infinite, timeout.Token)).ConfigureAwait(false);
                                timeout.Token.ThrowIfCancellationRequested();
                                if (completed != read) throw new OperationCanceledException(timeout.Token);
                                string line = await read.ConfigureAwait(false);
                                if (line == null) break;
                                if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
                                string data = line.Substring(5).Trim();
                                if (data.Length == 0 || data == "[DONE]") continue;
                                raw.Append(data).Append('\n');

                                foreach (Match fragment in VisibleContent.Matches(data))
                                {
                                    string value = fragment.Groups[1].Value;
                                    if (value.Length == 0) continue;
                                    if (firstVisibleTicks < 0) firstVisibleTicks = Stopwatch.GetTimestamp();
                                    outputCharacters += value.Length;
                                }
                                foreach (Match fragment in ReasoningContent.Matches(data))
                                {
                                    if (fragment.Groups[1].Value.Length > 0) reasoningObserved = true;
                                }
                            }
                        }
                    }
                }

                watch.Stop();
                result.Success = true;
                result.EndToEndMs = watch.Elapsed.TotalMilliseconds;
                result.TtftMs = firstVisibleTicks >= 0
                    ? 1000.0 * (firstVisibleTicks - startTicks) / Stopwatch.Frequency
                    : 0;
                result.OutputCharacters = outputCharacters;
                result.ReasoningObserved = reasoningObserved;

                string evidence = raw.ToString();
                Match prompt = PromptTokens.Matches(evidence).Cast<Match>().LastOrDefault();
                Match completion = CompletionTokens.Matches(evidence).Cast<Match>().LastOrDefault();
                Match reasoning = ReasoningTokens.Matches(evidence).Cast<Match>().LastOrDefault();
                Match finish = FinishReason.Matches(evidence).Cast<Match>().LastOrDefault();
                if (reasoning != null && reasoning.Success)
                    result.ReasoningTokens = Int32.Parse(reasoning.Groups[1].Value);
                if (finish != null && finish.Success)
                    result.FinishReason = finish.Groups[1].Value;
                if (prompt != null && prompt.Success && completion != null && completion.Success)
                {
                    result.PromptTokens = Int32.Parse(prompt.Groups[1].Value);
                    result.CompletionTokens = Int32.Parse(completion.Groups[1].Value);
                    result.TokenSource = "openai-usage";
                }

                if (firstVisibleTicks < 0 || outputCharacters <= 0)
                    result.InconclusiveReason = "no visible content delta was observed";
                else if (string.IsNullOrWhiteSpace(result.FinishReason))
                    result.InconclusiveReason = "finish_reason was not observed";
                else if (string.Equals(result.FinishReason, "length", StringComparison.OrdinalIgnoreCase))
                    result.InconclusiveReason = "finish_reason=length; output was budget-truncated";
                else if (!string.Equals(result.TokenSource, "openai-usage", StringComparison.Ordinal))
                    result.InconclusiveReason = "exact token usage was not reported";
                else if (result.ReasoningObserved || result.ReasoningTokens > 0)
                    result.InconclusiveReason = "hidden reasoning was observed; visible decode interval is not comparable to total completion usage";
                else if (result.CompletionTokens <= 1)
                    result.InconclusiveReason = "too few exact completion tokens for decode-rate measurement";
                else
                {
                    result.Comparable = true;
                    result.InconclusiveReason = null;
                }
                return result;
            }
            catch (OperationCanceledException)
            {
                result.Error = "Timed out/cancelled after " + timeoutSeconds + " seconds.";
            }
            catch (Exception ex)
            {
                result.Error = ex.GetType().Name + ": " + ex.Message;
            }
        }
        result.EndToEndMs = watch.Elapsed.TotalMilliseconds;
        return result;
    }
}
'@
}

function Normalize-Endpoint([string]$value) {
    $trimmed = $value.Trim().TrimEnd('/')
    if ($trimmed.EndsWith('/v1/chat/completions', [StringComparison]::OrdinalIgnoreCase)) { return $trimmed }
    return $trimmed + '/v1/chat/completions'
}

function New-ContextPrompt([int]$targetTokens) {
    $prefix = 'PaRuX competitive long-context benchmark. Preserve beginning marker BEGIN-314159 and ending marker END-271828. '
    $unit = 'Local inference must preserve constraints, evidence, ordering and exact state across a long context. '
    $suffix = ' END-271828. Reply by listing integers from 1 upward, comma-separated, with no prose, until the output limit.'
    $targetChars = [Math]::Max(1024, $targetTokens * 4)
    $builder = New-Object System.Text.StringBuilder
    [void]$builder.Append($prefix)
    while ($builder.Length + $unit.Length + $suffix.Length -lt $targetChars) { [void]$builder.Append($unit) }
    [void]$builder.Append($suffix)
    return $builder.ToString()
}

function New-RequestBody([string]$model, [string]$prompt) {
    return (@{
        model = $model; stream = $true; stream_options = @{ include_usage = $true };
        temperature = 0; max_tokens = $MaxOutputTokens;
        messages = @(@{ role = 'user'; content = $prompt })
    } | ConvertTo-Json -Depth 8 -Compress)
}

function Percentile([double[]]$values, [double]$p) {
    $data = @($values | Sort-Object)
    if ($data.Count -eq 0) { return $null }
    if ($data.Count -eq 1) { return [Math]::Round($data[0],3) }
    $pos = ([Math]::Max(0,[Math]::Min(100,$p))/100.0)*($data.Count-1)
    $lo=[int][Math]::Floor($pos); $hi=[int][Math]::Ceiling($pos)
    if ($lo -eq $hi) { return [Math]::Round($data[$lo],3) }
    return [Math]::Round($data[$lo]+(($data[$hi]-$data[$lo])*($pos-$lo)),3)
}
function Mean([double[]]$values) {
    if ($values.Count -eq 0) { return $null }
    return [Math]::Round((($values | Measure-Object -Average).Average),3)
}

# [Math]::Round has no overload for every numeric type PowerShell may hand it, and the resulting
# "Argument types do not match" surfaced attributed to the function's closing line, hiding which
# measurement produced it. Round through a double, and let a missing measurement stay missing.
function Round3($value) {
    if ($null -eq $value) { return $null }
    return [Math]::Round([double]$value, 3)
}

function Invoke-StackMatrix([string]$name,[string]$endpoint,[string]$authHeader,[string]$token,[string]$model) {
    $rows = New-Object System.Collections.Generic.List[object]
    foreach ($context in $ContextTargets) {
        $prompt = New-ContextPrompt $context
        $body = New-RequestBody $model $prompt
        foreach ($c in $Concurrency) {
            Write-Host ("{0}: context≈{1:N0}, concurrency={2}" -f $name,$context,$c)
            for ($warm=0; $warm -lt $WarmupRuns; $warm++) {
                [void][PaRuXCompetitiveHttpBench]::RunMany($endpoint,$authHeader,$token,$body,$c,$TimeoutSeconds,$context)
            }
            for ($run=1; $run -le $RunsPerCell; $run++) {
                $batchWatch=[Diagnostics.Stopwatch]::StartNew()
                $results=[PaRuXCompetitiveHttpBench]::RunMany($endpoint,$authHeader,$token,$body,$c,$TimeoutSeconds,$context)
                $batchWatch.Stop()
                $comparable=@($results | Where-Object { $_.Success -and $_.Comparable })
                # Measure-Object over an empty set has no Sum to read under Set-StrictMode, and the
                # resulting terminating error killed the run before it could report the far more
                # useful fact underneath it: that no request in the cell was comparable, and why.
                $aggregateTokens = if ($comparable.Count -gt 0) {
                    ($comparable | Measure-Object -Property CompletionTokens -Sum).Sum
                } else { 0 }
                if ($comparable.Count -eq 0) {
                    $firstProblem = @($results | Where-Object { $_.Error }) | Select-Object -First 1
                    $detail = if ($firstProblem) { $firstProblem.Error } else { 'no error reported; responses were not comparable' }
                    Write-Host ("  [{0}] context≈{1} concurrency={2}: 0/{3} comparable - {4}" -f $name, $context, $c, $results.Count, $detail)
                }
                $aggregateTps=if($comparable.Count -eq $results.Count -and $batchWatch.Elapsed.TotalSeconds -gt 0){$aggregateTokens/$batchWatch.Elapsed.TotalSeconds}else{$null}
                for($i=0;$i -lt $results.Count;$i++){
                    $r=$results[$i]
                    $decodeSeconds=if($r.Comparable){[Math]::Max(0.001,($r.EndToEndMs-$r.TtftMs)/1000.0)}else{$null}
                    $decodeTps=if($r.Comparable){($r.CompletionTokens-1)/$decodeSeconds}else{$null}
                    # This remains a client-observed prompt/TTFT ratio, never a kernel prefill timer.
                    $effectivePrefill=if($r.Comparable -and $r.TtftMs -gt 0){$r.PromptTokens/($r.TtftMs/1000.0)}else{$null}
                    # One request that cannot be turned into a row must not destroy the whole matrix:
                    # the measurements already taken are still evidence, and the reason belongs in the
                    # output rather than in a terminating error with no request attached to it.
                    try {
                    $rows.Add([pscustomobject]@{
                        stack=$name; endpoint=$endpoint; model=$model; contextTargetTokens=$context;
                        promptCharacters=$prompt.Length; concurrency=$c; run=$run; requestIndex=$i+1;
                        success=[bool]$r.Success; comparable=[bool]$r.Comparable; error=[string]$r.Error;
                        inconclusiveReason=[string]$r.InconclusiveReason; finishReason=[string]$r.FinishReason;
                        reasoningObserved=[bool]$r.ReasoningObserved; reasoningTokens=$r.ReasoningTokens;
                        outputCharacters=$r.OutputCharacters; ttftMs=if($r.TtftMs -gt 0){Round3 $r.TtftMs}else{$null};
                        endToEndMs=Round3 $r.EndToEndMs; promptTokens=$r.PromptTokens;
                        completionTokens=$r.CompletionTokens; tokenSource=$r.TokenSource;
                        effectivePromptTokensPerSecond=Round3 $effectivePrefill;
                        decodeTokensPerSecond=Round3 $decodeTps;
                        aggregateBatchTokensPerSecond=Round3 $aggregateTps
                    })
                    }
                    catch {
                        Write-Host ("  [{0}] context≈{1} concurrency={2} request {3}: measurement recorded as unusable - {4}" `
                            -f $name,$context,$c,($i+1),$_.Exception.Message)
                        $rows.Add([pscustomobject]@{
                            stack=$name; endpoint=$endpoint; model=$model; contextTargetTokens=$context;
                            promptCharacters=$prompt.Length; concurrency=$c; run=$run; requestIndex=$i+1;
                            success=$false; comparable=$false; error=[string]$_.Exception.Message;
                            inconclusiveReason='row-construction-failed'; finishReason=''; reasoningObserved=$false;
                            reasoningTokens=$null; outputCharacters=$null; ttftMs=$null; endToEndMs=$null;
                            promptTokens=$null; completionTokens=$null; tokenSource='';
                            effectivePromptTokensPerSecond=$null; decodeTokensPerSecond=$null;
                            aggregateBatchTokensPerSecond=$null
                        })
                    }
                }
            }
        }
    }
    # $rows.ToArray(), not @($rows): wrapping the List in the array subexpression operator raised
    # "Argument types do not match" from the function's closing line, with no measurement attached
    # to it, which is a confusing way to lose a completed benchmark pass.
    return ,$rows.ToArray()
}

$parux=Normalize-Endpoint $PaRuXEndpoint
$competitor=Normalize-Endpoint $CompetitorEndpoint
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
Write-Host '== External same-hardware competitive inference benchmark ==' -ForegroundColor Cyan
Write-Host 'No vendor value is imported. All numbers come from requests executed by this script.'
Write-Host 'For one-GPU fairness, only the stack currently being measured should have its model resident.'

$started=[DateTime]::UtcNow
$all=@()

# Name the stack that failed and where. An unqualified "Argument types do not match" with no stack
# attached to it was, in practice, indistinguishable from the benchmark simply not working.
function Invoke-StackMatrixChecked([string]$name,[string]$endpoint,[string]$authHeader,[string]$token,[string]$model) {
    try { return Invoke-StackMatrix $name $endpoint $authHeader $token $model }
    catch {
        Write-Host ("{0} stack failed: {1}" -f $name, $_.Exception.Message) -ForegroundColor Red
        Write-Host $_.ScriptStackTrace
        throw
    }
}

$all += Invoke-StackMatrixChecked 'PaRuX' $parux $PaRuXAuthHeader $PaRuXToken $PaRuXModel
if($PauseBetweenStacks){
    Write-Host ''
    Write-Host 'PaRuX pass complete. Stop/unload PaRuX GPU inference, start/load the competitor on the SAME machine/GPU/driver, then press Enter.' -ForegroundColor Yellow
    [void](Read-Host)
}
$all += Invoke-StackMatrixChecked 'Competitor' $competitor $CompetitorAuthHeader $CompetitorToken $CompetitorModel

$cells=New-Object System.Collections.Generic.List[object]
foreach($context in $ContextTargets){
    foreach($c in $Concurrency){
        $stats=@{}
        foreach($stack in @('PaRuX','Competitor')){
            $sample=@($all|Where-Object{$_.stack -eq $stack -and $_.contextTargetTokens -eq $context -and $_.concurrency -eq $c})
            $transportOk=@($sample|Where-Object{$_.success})
            $ok=@($sample|Where-Object{$_.success -and $_.comparable})
            $stats[$stack]=[pscustomobject]@{
                attempts=$sample.Count; transportSuccesses=$transportOk.Count; comparableSuccesses=$ok.Count;
                transportSuccessRatePercent=if($sample.Count){[Math]::Round(100.0*$transportOk.Count/$sample.Count,2)}else{0};
                comparableRatePercent=if($sample.Count){[Math]::Round(100.0*$ok.Count/$sample.Count,2)}else{0};
                p50TtftMs=Percentile ([double[]]@($ok|ForEach-Object{$_.ttftMs})) 50;
                p95TtftMs=Percentile ([double[]]@($ok|ForEach-Object{$_.ttftMs})) 95;
                meanDecodeTps=Mean ([double[]]@($ok|ForEach-Object{$_.decodeTokensPerSecond})) ;
                p50DecodeTps=Percentile ([double[]]@($ok|ForEach-Object{$_.decodeTokensPerSecond})) 50;
                meanAggregateTps=Mean ([double[]]@($ok|Where-Object{$null -ne $_.aggregateBatchTokensPerSecond}|ForEach-Object{$_.aggregateBatchTokensPerSecond}));
                meanEffectivePromptTps=Mean ([double[]]@($ok|ForEach-Object{$_.effectivePromptTokensPerSecond}))
            }
        }
        $p=$stats['PaRuX'];$x=$stats['Competitor']
        $win=$p.comparableRatePercent -ge 99 -and $x.comparableRatePercent -ge 99 -and
            $null -ne $p.p50DecodeTps -and $null -ne $x.p50DecodeTps -and
            $null -ne $p.p50TtftMs -and $null -ne $x.p50TtftMs -and
            $null -ne $p.meanAggregateTps -and $null -ne $x.meanAggregateTps -and
            $p.p50DecodeTps -ge $x.p50DecodeTps -and $p.p50TtftMs -le $x.p50TtftMs -and
            $p.meanAggregateTps -ge $x.meanAggregateTps
        $inconclusive=$p.comparableRatePercent -lt 99 -or $x.comparableRatePercent -lt 99
        $cells.Add([pscustomobject]@{contextTargetTokens=$context;concurrency=$c;parux=$p;competitor=$x;paRuXWin=$win;inconclusive=$inconclusive})
    }
}

# The per-request rows are the evidence; write them even if the summary cannot be assembled, so a
# run that ends inconclusive still leaves behind what it measured instead of nothing at all.
$rawStamp=Get-Date -Format 'yyyyMMdd-HHmmss'
$rawCsv=Join-Path $OutputDirectory "competitive-inference-requests-$rawStamp.csv"
$all|Export-Csv $rawCsv -NoTypeInformation -Encoding UTF8
Write-Host "Per-request evidence: $rawCsv"

$losses=@($cells|Where-Object{-not $_.paRuXWin})
$inconclusive=@($cells|Where-Object{$_.inconclusive})
$summary=[pscustomobject]@{
    schemaVersion=4; benchmark='external-openai-compatible-same-hardware-v4';
    startedUtc=$started.ToString('o');completedUtc=[DateTime]::UtcNow.ToString('o');
    fairness=[pscustomobject]@{
        samePromptBytes=$true;sequentialStacks=$true;manualGpuResidencyHandoff=$PauseBetweenStacks;
        contextTargets=$ContextTargets;concurrency=$Concurrency;maxOutputTokens=$MaxOutputTokens;
        runsPerCell=$RunsPerCell;warmupRuns=$WarmupRuns;
        paRuXAuthHeader=$PaRuXAuthHeader;competitorAuthHeader=$CompetitorAuthHeader;
        note='Before publication verify same physical GPU/driver and equivalent model weights/quantization/quality. Effective prompt tok/s is client-observed exact prompt usage divided by visible TTFT, not a server-kernel-only timing. Empty output, missing usage, hidden reasoning and finish_reason=length are inconclusive and cannot win.'
    };
    mandatoryCells=$cells.Count;paRuXWins=@($cells|Where-Object{$_.paRuXWin}).Count;paRuXLosses=$losses.Count;
    inconclusiveCells=$inconclusive.Count;
    bestInClassRuntimeGatePassed=($losses.Count -eq 0 -and $inconclusive.Count -eq 0);
    caveat='Runtime speed alone never proves overall product best-in-class; agent task completion and quality gates are independent mandatory gates.';
    cells=$cells.ToArray()
}

$stamp=Get-Date -Format 'yyyyMMdd-HHmmss'
$json=Join-Path $OutputDirectory "competitive-inference-$stamp.json"
$csv=Join-Path $OutputDirectory "competitive-inference-requests-$stamp.csv"
$summary|ConvertTo-Json -Depth 16|Set-Content $json -Encoding UTF8
$all|Export-Csv $csv -NoTypeInformation -Encoding UTF8
Write-Host ''
Write-Host ("PaRuX won {0}/{1} mandatory runtime cells; {2} cell(s) inconclusive." -f $summary.paRuXWins,$summary.mandatoryCells,$summary.inconclusiveCells)
Write-Host "JSON: $json"
Write-Host "CSV : $csv"
if($losses.Count -gt 0){Write-Warning("PaRuX did not win {0} mandatory cell(s). Do not claim runtime best-in-class from this run." -f $losses.Count)}
if($FailIfPaRuXLosesMandatoryCell -and $losses.Count -gt 0){throw 'Competitive runtime gate failed or was inconclusive: PaRuX did not beat the competitor with valid evidence in every mandatory context/concurrency cell.'}