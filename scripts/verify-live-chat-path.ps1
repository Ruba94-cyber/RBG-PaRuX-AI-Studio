# Verifies the real PaRuX server's chat path end to end, through its own API, rather than
# inferring latency from unit tests or from llama-server directly.
#
# It exists because several changes in this area (parallel-slot un-pinning, the VRAM probe cache,
# the remote-provider gate) are only meaningful if the user-visible request still works and is
# still fast. A green test suite does not demonstrate either.
#
# Reports TTFT measured from the streamed response, not from request completion: TTFT is the
# latency a user actually feels, and measuring it any other way flatters the result.

param(
    # Matches ServerState.DefaultEndpoint. Overridable for a server started on another port.
    [string]$BaseUrl = "http://127.0.0.1:51123",
    [int]$Runs = 3,
    [string]$Prompt = "Reply with exactly one short sentence about local inference.",
    [string]$Token
)

$ErrorActionPreference = "Stop"

# Force invariant number formatting. On an it-IT host "{0:N0}" renders 59395 as "59.395", which
# reads as 59 ms to anyone scanning the output - precisely the kind of formatting artefact that
# turns a 59-second result into a reported success. Timings are printed as plain integers.
[System.Threading.Thread]::CurrentThread.CurrentCulture = [System.Globalization.CultureInfo]::InvariantCulture

# Every route except loopback /api/v1/health requires the local API token. Read it from the
# bootstrap file the server itself publishes, so this script needs no manual copy/paste and cannot
# drift from the running server's token.
if (-not $Token) {
    $bootstrap = Join-Path $env:LOCALAPPDATA "PaRuX\server\bootstrap.json"
    if (Test-Path $bootstrap) {
        try { $Token = (Get-Content $bootstrap -Raw | ConvertFrom-Json).token } catch { }
    }
}
if (-not $Token) {
    Write-Host "FAIL: no API token. Start PaRuX.Server (it publishes bootstrap.json) or pass -Token." -ForegroundColor Red
    exit 1
}
$AuthHeaders = @{ "X-PaRuX-Token" = $Token }

function Get-Json($path) {
    try { return Invoke-RestMethod -Uri "$BaseUrl$path" -Headers $AuthHeaders -TimeoutSec 20 }
    catch { return $null }
}

Write-Host "== PaRuX live chat verification ==" -ForegroundColor Cyan
Write-Host "Target: $BaseUrl"

# 1. Confirm the server is actually up before claiming anything about it.
$health = Get-Json "/api/v1/health"
if ($null -eq $health) {
    Write-Host "FAIL: server is not reachable at $BaseUrl. Start PaRuX.Server first." -ForegroundColor Red
    exit 1
}
Write-Host "Health: OK"

# 2. The remote opt-in must be OFF by default. If this ever reports true on a machine where the
#    user did not enable it, that is a defect, not a benchmark detail.
$policy = Get-Json "/api/v1/ai/backends/remote-policy"
if ($null -ne $policy) {
    Write-Host ("Remote provider opt-in: {0} (key stored: {1})" -f $policy.allowRemoteProviders, $policy.hasApiKey)
    if ($policy.allowRemoteProviders) {
        Write-Host "NOTE: remote inference is ENABLED on this machine - timings below may leave the device." -ForegroundColor Yellow
    }
}

# 3. Report which provider will actually serve the request, so a local result is never silently
#    attributed to a remote one or vice versa.
$external = Get-Json "/api/v1/ai/backends/external"
if ($null -ne $external -and $external.configured) {
    Write-Host ("Provider: {0} at {1} (remote: {2}, reachable: {3})" -f `
        $external.providerName, $external.endpoint, $external.isRemote, $external.reachable)
} else {
    Write-Host "Provider: PaRuX-managed local runtime"
}

# 4. Measure TTFT and total latency over the streaming chat endpoint.
$results = @()
for ($i = 1; $i -le $Runs; $i++) {
    $body = @{
        messages = @(@{ role = "user"; content = $Prompt })
        stream   = $true
    } | ConvertTo-Json -Depth 5

    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $ttft = $null
    $chars = 0

    try {
        $req = [System.Net.HttpWebRequest]::Create("$BaseUrl/api/v1/chat/completions")
        $req.Method = "POST"
        $req.ContentType = "application/json"
        $req.Headers.Add("X-PaRuX-Token", $Token)
        $req.Timeout = 180000
        $req.ReadWriteTimeout = 180000
        $bytes = [System.Text.Encoding]::UTF8.GetBytes($body)
        $req.ContentLength = $bytes.Length
        $stream = $req.GetRequestStream()
        $stream.Write($bytes, 0, $bytes.Length)
        $stream.Close()

        $resp = $req.GetResponse()
        $reader = New-Object System.IO.StreamReader($resp.GetResponseStream())
        while (-not $reader.EndOfStream) {
            $line = $reader.ReadLine()
            if ([string]::IsNullOrWhiteSpace($line)) { continue }
            if ($line -notlike "data:*") { continue }
            $payload = $line.Substring(5).Trim()
            if ($payload -eq "[DONE]") { break }
            # First chunk carrying actual content is the first token the user sees. Role-only
            # deltas are not content and must not be counted as first token.
            try {
                $obj = $payload | ConvertFrom-Json
                $delta = $obj.choices[0].delta.content
                if (-not [string]::IsNullOrEmpty($delta)) {
                    if ($null -eq $ttft) { $ttft = $sw.Elapsed.TotalMilliseconds }
                    $chars += $delta.Length
                }
            } catch { }
        }
        $reader.Close(); $resp.Close()
    } catch {
        Write-Host ("Run {0}: FAILED - {1}" -f $i, $_.Exception.Message) -ForegroundColor Red
        continue
    }

    $sw.Stop()
    $total = $sw.Elapsed.TotalMilliseconds
    if ($null -eq $ttft) {
        Write-Host ("Run {0}: no content received in {1} ms" -f $i, [int]$total) -ForegroundColor Red
        continue
    }
    $results += [pscustomobject]@{ Run = $i; TtftMs = [math]::Round($ttft); TotalMs = [math]::Round($total); Chars = $chars }
    Write-Host ("Run {0}: TTFT {1} ms, total {2} ms, {3} chars" -f $i, [int]$ttft, [int]$total, $chars)
}

if ($results.Count -eq 0) {
    Write-Host "FAIL: no successful runs - nothing can be claimed about latency." -ForegroundColor Red
    exit 1
}

$med = ($results.TtftMs | Sort-Object)[[int](($results.Count - 1) / 2)]
Write-Host ""
Write-Host ("Median TTFT over {0} successful run(s): {1} ms" -f $results.Count, [int]$med) -ForegroundColor Green
$results | Format-Table -AutoSize
