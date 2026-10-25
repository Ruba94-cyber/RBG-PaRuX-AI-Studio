<#
.SYNOPSIS
  Non-elevated per-process network egress sampler (BIC-15 S3 evidence helper).

.DESCRIPTION
  Polls the Windows TCP/UDP tables (Get-NetTCPConnection / Get-NetUDPEndpoint, no elevation required)
  for the selected processes and their descendants, and records every connection whose remote
  endpoint is not loopback. Exit code 0 = no non-loopback activity observed, 1 = observed, 2 = misuse.

  HONEST LIMITS (do not present this as a packet capture):
   - Polling only sees connections that exist at a sample tick. A connection that opens and closes
     between ticks, or a single UDP datagram, is invisible. Lower -IntervalMs to narrow (never close) the gap.
   - UDP endpoints expose only the local bind; a socket bound to a non-loopback address is reported
     as 'udp-bind' (informational) but its destinations are unknown.
   - Processes started after the sampler and not descendants of a tracked PID are not covered unless
     -ProcessName is used (which re-resolves matching names on every tick).
   - It cannot prove absence of traffic by a different process or by the kernel. A real proof needs an
     elevated capture (see docs/STRICT_LOCAL_EGRESS_EVIDENCE.md: pktmon / netsh trace, or a Windows Firewall block rule).

.PARAMETER ProcessId        Root PID(s) to follow, including descendants.
.PARAMETER ProcessName      Process name(s) (no .exe) re-resolved every tick, including descendants.
.PARAMETER DurationSeconds  Total sampling time (default 30).
.PARAMETER IntervalMs       Sample period (default 100).
.PARAMETER OutFile          Optional JSON result path.
.PARAMETER StopWhenTrackedExit  End early once at least one tracked process was seen and none remain.
.PARAMETER SelfTest         Prove the detector: loopback-only child passes, a child dialing a
                            non-routable TEST-NET address (192.0.2.1) is flagged.
#>
[CmdletBinding()]
param(
    [int[]]$ProcessId = @(),
    [string[]]$ProcessName = @(),
    [int]$DurationSeconds = 30,
    [int]$IntervalMs = 100,
    [string]$OutFile = '',
    [switch]$StopWhenTrackedExit,
    [switch]$SelfTest
)

$ErrorActionPreference = 'Stop'

function Test-LoopbackAddress([string]$a) {
    if ([string]::IsNullOrEmpty($a)) { return $true }
    return ($a -eq '::1' -or $a -like '127.*' -or $a -eq '::ffff:127.0.0.1' -or $a -like '::ffff:127.*')
}

function Test-UnspecifiedAddress([string]$a) {
    return ($a -eq '0.0.0.0' -or $a -eq '::' -or [string]::IsNullOrEmpty($a))
}

function Get-DescendantPids([int[]]$roots) {
    $all = @{}
    foreach ($p in Get-CimInstance Win32_Process -Property ProcessId, ParentProcessId) { $all[[int]$p.ProcessId] = [int]$p.ParentProcessId }
    $set = [System.Collections.Generic.HashSet[int]]::new()
    foreach ($r in $roots) { [void]$set.Add($r) }
    $changed = $true
    while ($changed) {
        $changed = $false
        foreach ($kv in $all.GetEnumerator()) {
            if (-not $set.Contains($kv.Key) -and $set.Contains($kv.Value) -and $kv.Key -ne $kv.Value) { [void]$set.Add($kv.Key); $changed = $true }
        }
    }
    return @($set)
}

function Invoke-EgressSample {
    param([int[]]$Pids, [string[]]$Names, [int]$Seconds, [int]$Interval, [switch]$StopEarly)

    $violations = [ordered]@{}
    $loopbackSeen = [System.Collections.Generic.HashSet[string]]::new()
    $udpBinds = [ordered]@{}
    $trackedSeen = [System.Collections.Generic.HashSet[int]]::new()
    $samples = 0
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $everTracked = $false
    while ($sw.Elapsed.TotalSeconds -lt $Seconds) {
        $roots = [System.Collections.Generic.List[int]]::new()
        foreach ($p in $Pids) { if (Get-Process -Id $p -ErrorAction SilentlyContinue) { $roots.Add($p) } }
        foreach ($n in $Names) { foreach ($proc in Get-Process -Name $n -ErrorAction SilentlyContinue) { $roots.Add($proc.Id) } }
        $tracked = if ($roots.Count -gt 0) { Get-DescendantPids $roots.ToArray() } else { @() }
        foreach ($t in $tracked) { [void]$trackedSeen.Add($t) }
        if ($tracked.Count -gt 0) { $everTracked = $true }
        elseif ($StopEarly -and $everTracked) { break }

        if ($tracked.Count -gt 0) {
            $samples++
            $tcp = Get-NetTCPConnection -ErrorAction SilentlyContinue | Where-Object { $tracked -contains [int]$_.OwningProcess }
            foreach ($c in $tcp) {
                $remote = [string]$c.RemoteAddress
                # Listen/Bound rows have no peer (remote 0.0.0.0/::); they are not connections.
                if ($c.State -eq 'Listen' -or $c.State -eq 'Bound' -or (Test-UnspecifiedAddress $remote)) { continue }
                if (Test-LoopbackAddress $remote) { [void]$loopbackSeen.Add("tcp:$($c.OwningProcess):$remote`:$($c.RemotePort)"); continue }
                $key = "tcp|$($c.OwningProcess)|$remote|$($c.RemotePort)"
                if (-not $violations.Contains($key)) {
                    $violations[$key] = [ordered]@{
                        protocol = 'tcp'; pid = [int]$c.OwningProcess; remoteAddress = $remote; remotePort = [int]$c.RemotePort
                        localAddress = [string]$c.LocalAddress; state = [string]$c.State; firstSeenMs = [int]$sw.ElapsedMilliseconds
                    }
                }
            }
            $udp = Get-NetUDPEndpoint -ErrorAction SilentlyContinue | Where-Object { $tracked -contains [int]$_.OwningProcess }
            foreach ($u in $udp) {
                $la = [string]$u.LocalAddress
                if (Test-LoopbackAddress $la) { continue }
                $key = "udp|$($u.OwningProcess)|$la|$($u.LocalPort)"
                if (-not $udpBinds.Contains($key)) {
                    $udpBinds[$key] = [ordered]@{
                        protocol = 'udp-bind'; pid = [int]$u.OwningProcess; localAddress = $la; localPort = [int]$u.LocalPort
                        wildcardBind = (Test-UnspecifiedAddress $la)
                    }
                }
            }
        }
        Start-Sleep -Milliseconds $Interval
    }
    return [ordered]@{
        schema = 'parux.egress-sample.v1'
        method = 'polled Get-NetTCPConnection/Get-NetUDPEndpoint, non-elevated; NOT a packet capture'
        startedUtc = [DateTime]::UtcNow.AddSeconds(-$sw.Elapsed.TotalSeconds).ToString('o')
        durationMs = [int]$sw.ElapsedMilliseconds
        intervalMs = $Interval
        samplesWithTrackedProcess = $samples
        trackedProcessIds = @($trackedSeen | Sort-Object)
        loopbackConnectionsObserved = $loopbackSeen.Count
        nonLoopbackTcp = @($violations.Values)
        udpNonLoopbackBinds = @($udpBinds.Values)
        verdict = $(if ($samples -eq 0) { 'NO_TRACKED_PROCESS' } elseif ($violations.Count -gt 0) { 'NON_LOOPBACK_OBSERVED' } else { 'LOOPBACK_ONLY_AT_SAMPLED_INSTANTS' })
    }
}

function Write-Result($result, [string]$path) {
    $json = $result | ConvertTo-Json -Depth 6
    if ($path) {
        $dir = Split-Path -Parent $path
        if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
        Set-Content -Path $path -Value $json -Encoding UTF8
    }
    $json
}

if ($SelfTest) {
    $psExe = (Get-Process -Id $PID).Path
    $loopbackScript = @'
$l=[System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback,0);$l.Start()
$port=$l.LocalEndpoint.Port
$c=[System.Net.Sockets.TcpClient]::new();$c.Connect('127.0.0.1',$port)
$a=$l.AcceptTcpClient()
Start-Sleep -Seconds 4
'@
    $egressScript = @'
$l=[System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback,0);$l.Start()
$c=[System.Net.Sockets.TcpClient]::new();$c.Connect('127.0.0.1',$l.LocalEndpoint.Port)
$e=[System.Net.Sockets.TcpClient]::new()
try { $e.ConnectAsync('192.0.2.1',9).Wait(4000) | Out-Null } catch {}
Start-Sleep -Seconds 1
'@
    $failures = @()
    foreach ($case in @(@{ name = 'loopback-only'; script = $loopbackScript; expectViolation = $false },
                        @{ name = 'testnet-dial'; script = $egressScript; expectViolation = $true })) {
        $child = Start-Process -FilePath $psExe -ArgumentList @('-NoProfile', '-NonInteractive', '-Command', $case.script) -PassThru -WindowStyle Hidden
        $res = Invoke-EgressSample -Pids @($child.Id) -Names @() -Seconds 12 -Interval 50 -StopEarly
        if (-not $child.HasExited) { try { $child.Kill() } catch {} }
        $flagged = $res.nonLoopbackTcp.Count -gt 0
        $ok = ($flagged -eq $case.expectViolation) -and ($res.loopbackConnectionsObserved -ge 1)
        Write-Host ("self-test {0}: flagged={1} loopbackSeen={2} verdict={3} => {4}" -f $case.name, $flagged, $res.loopbackConnectionsObserved, $res.verdict, $(if ($ok) { 'PASS' } else { 'FAIL' }))
        if (-not $ok) { $failures += $case.name }
    }
    if ($failures.Count -gt 0) { Write-Error ("self-test failed: " + ($failures -join ',')); exit 1 }
    Write-Host 'self-test PASS'
    exit 0
}

if ($ProcessId.Count -eq 0 -and $ProcessName.Count -eq 0) {
    Write-Error 'Specify -ProcessId and/or -ProcessName, or -SelfTest.'
    exit 2
}
$result = Invoke-EgressSample -Pids $ProcessId -Names $ProcessName -Seconds $DurationSeconds -Interval $IntervalMs -StopEarly:$StopWhenTrackedExit
Write-Result $result $OutFile | Out-Null
Write-Host ("verdict: {0}; nonLoopbackTcp={1}; loopbackSeen={2}; samples={3}" -f $result.verdict, $result.nonLoopbackTcp.Count, $result.loopbackConnectionsObserved, $result.samplesWithTrackedProcess)
if ($result.verdict -eq 'NON_LOOPBACK_OBSERVED') { exit 1 }
if ($result.verdict -eq 'NO_TRACKED_PROCESS') { exit 3 }
exit 0
