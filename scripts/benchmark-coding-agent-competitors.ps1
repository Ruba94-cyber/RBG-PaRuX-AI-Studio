<#
.SYNOPSIS
  Same-machine deterministic coding-agent benchmark for PaRuX and external CLI agents.

.DESCRIPTION
  Runs the same repository-changing corpus against PaRuX and arbitrary external CLI agents. Every
  external case receives an isolated detached Git worktree at the exact same source commit. PaRuX
  uses its own isolated coding worktrees. After an agent stops, THIS HARNESS independently validates
  required files/text/scope and runs acceptance.verificationCommands itself. Agent prose, exit code,
  or self-reported tests can never substitute for external verification.

  Scope is audited from an immutable initial commit through committed changes, staged changes,
  unstaged changes and untracked files. Renames audit both source and destination paths. The corpus
  and this verifier are hash-pinned for the full run; changing either aborts the benchmark.

  A competitive WIN is intentionally fail-closed. It requires a representative 30-50 task corpus,
  the corpus-declared absolute success floor and mandatory cases, comparable predeclared budgets,
  zero manual interventions, and a strict pairwise win. Latency is compared only on cases both
  providers solved successfully; tied success counts with different solved-task sets are inconclusive,
  so failed/truncated work never becomes a speed advantage.

.EXAMPLE
  ./scripts/benchmark-coding-agent-competitors.ps1 -CompetitorSpecs `
    'codex=codex|<verified installed non-interactive args using {workspace} and {promptFile}>' `
    -ProviderMetadataJson .\artifacts\benchmark-provider-metadata.json
#>
[CmdletBinding()]
param(
  [string]$Corpus = '',
  [string]$Workspace = '',
  [string[]]$CompetitorSpecs = @(),
  [string]$ProviderMetadataJson = '',
  [string]$OutputDirectory = '',
  [ValidateRange(1,180)][int]$TimeoutMinutesPerCase = 45,
  [ValidateRange(1,60)][int]$VerificationTimeoutMinutes = 15,
  [switch]$SkipPaRuX,
  [switch]$FailIfPaRuXLoses,
  [switch]$ScopeAuditSelfTest,
  [switch]$VerdictSelfTest,
  # --- corpus v2 (self-contained fixtures, hidden verifiers, held-out/optimization split) ---
  [string]$TaskCorpusV2 = '',
  [ValidateSet('Run','PrepareManual','CollectManual')][string]$V2Action = 'Run',
  [ValidateSet('held-out','optimization','all')][string]$Split = 'held-out',
  [string[]]$ManualProviders = @(),
  [string[]]$OnlyTasks = @(),
  [switch]$V2SelfTest
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$isWindowsHost = [System.Environment]::OSVersion.Platform -eq [System.PlatformID]::Win32NT
if ([string]::IsNullOrWhiteSpace($PSScriptRoot)) { throw 'Could not resolve the benchmark script directory.' }
if ([string]::IsNullOrWhiteSpace($Corpus)) { $Corpus = Join-Path $PSScriptRoot '..\benchmarks\coding-agent\parux-eval-v1.json' }
if ([string]::IsNullOrWhiteSpace($Workspace)) { $Workspace = Join-Path $PSScriptRoot '..' }
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) { $OutputDirectory = Join-Path $PSScriptRoot '..\artifacts\coding-agent-competitive' }

function Resolve-RepoRoot([string]$value) {
  $root = (& git -C $value rev-parse --show-toplevel 2>$null | Select-Object -First 1)
  if ([string]::IsNullOrWhiteSpace([string]$root)) { throw "Not a Git repository: $value" }
  return [IO.Path]::GetFullPath(([string]$root).Trim())
}

function Parse-Spec([string]$spec) {
  $eq = $spec.IndexOf('=')
  if ($eq -le 0) { throw "Invalid competitor spec '$spec'. Expected name=executable|arguments-template." }
  $name = $spec.Substring(0,$eq).Trim(); $rest = $spec.Substring($eq+1); $bar = $rest.IndexOf('|')
  if ($bar -le 0) { throw "Invalid competitor spec '$spec'. Expected name=executable|arguments-template." }
  $exe = $rest.Substring(0,$bar).Trim(); $args = $rest.Substring($bar+1)
  if ($name -notmatch '^[A-Za-z][A-Za-z0-9._-]{0,31}$') { throw "Invalid competitor name '$name'." }
  if ([string]::IsNullOrWhiteSpace($exe)) { throw "Competitor '$name' has no executable." }
  return [pscustomobject]@{ Name=$name; Executable=$exe; Arguments=$args }
}

function Quote-Arg([string]$value) {
  if ($null -eq $value) { return '""' }
  return '"' + $value.Replace('"','\"') + '"'
}

function Expand-Arguments([string]$template,[string]$workspace,[string]$promptFile,[string]$prompt) {
  return $template.Replace('{workspace}',(Quote-Arg $workspace)).Replace('{promptFile}',(Quote-Arg $promptFile)).Replace('{prompt}',(Quote-Arg $prompt))
}

function Normalize-RepoPath([string]$value) {
  return $value.Trim().Replace('\','/')
}

function Add-NameStatusPaths([System.Collections.Generic.HashSet[string]]$set, [object[]]$lines) {
  foreach ($raw in @($lines)) {
    $line = [string]$raw
    if ([string]::IsNullOrWhiteSpace($line)) { continue }
    $parts = $line -split "`t"
    if ($parts.Count -lt 2) { continue }
    $status = $parts[0]
    if (($status.StartsWith('R',[StringComparison]::Ordinal) -or $status.StartsWith('C',[StringComparison]::Ordinal)) -and $parts.Count -ge 3) {
      [void]$set.Add((Normalize-RepoPath $parts[1]))
      [void]$set.Add((Normalize-RepoPath $parts[2]))
    } else {
      [void]$set.Add((Normalize-RepoPath $parts[1]))
    }
  }
}

function Get-ChangedPaths([string]$worktree,[string]$initialCommit) {
  $initial = (& git -C $worktree rev-parse "$initialCommit^{commit}" 2>$null | Select-Object -First 1)
  if ([string]::IsNullOrWhiteSpace([string]$initial)) { throw "Initial benchmark commit is unavailable in worktree: $initialCommit" }
  $final = (& git -C $worktree rev-parse 'HEAD^{commit}' 2>$null | Select-Object -First 1)
  if ([string]::IsNullOrWhiteSpace([string]$final)) { throw 'Could not resolve final worktree HEAD.' }

  & git -C $worktree merge-base --is-ancestor $initialCommit HEAD 2>$null
  $initialIsAncestor = ($LASTEXITCODE -eq 0)

  $paths = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
  $commits = @(& git -C $worktree rev-list --reverse "$($initial.Trim())..$($final.Trim())" 2>$null)
  if ($LASTEXITCODE -ne 0) { throw 'Could not enumerate commits after the immutable benchmark start.' }
  foreach ($commitValue in $commits) {
    $commit = ([string]$commitValue).Trim()
    if ([string]::IsNullOrWhiteSpace($commit)) { continue }
    $commitChanges = @(& git -C $worktree diff-tree --root --no-commit-id --name-status --find-renames -r -m $commit -- 2>$null)
    if ($LASTEXITCODE -ne 0) { throw "Could not inspect changes in audited commit $commit." }
    Add-NameStatusPaths $paths $commitChanges
  }
  Add-NameStatusPaths $paths @(& git -C $worktree diff --cached --name-status --find-renames -- 2>$null)
  Add-NameStatusPaths $paths @(& git -C $worktree diff --name-status --find-renames -- 2>$null)
  foreach ($path in @(& git -C $worktree ls-files --others --exclude-standard 2>$null)) {
    $normalized = Normalize-RepoPath ([string]$path)
    if ($normalized) { [void]$paths.Add($normalized) }
  }

  return [pscustomobject]@{
    InitialCommit = ([string]$initial).Trim()
    FinalCommit = ([string]$final).Trim()
    InitialIsAncestor = $initialIsAncestor
    Paths = @($paths | Sort-Object)
  }
}

function Resolve-SafePath([string]$root,[string]$relative) {
  $base = [IO.Path]::GetFullPath($root).TrimEnd([IO.Path]::DirectorySeparatorChar,[IO.Path]::AltDirectorySeparatorChar)
  $candidate = [IO.Path]::GetFullPath((Join-Path $base $relative.Replace('/',[IO.Path]::DirectorySeparatorChar)))
  $prefix = $base + [IO.Path]::DirectorySeparatorChar
  if (-not $candidate.Equals($base,[StringComparison]::OrdinalIgnoreCase) -and -not $candidate.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase)) { return $null }
  return $candidate
}

function Get-FileSha256([string]$path) {
  if (-not (Test-Path $path -PathType Leaf)) { throw "Required immutable benchmark input is missing: $path" }
  return (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-TextSha256([string]$value) {
  $sha = [Security.Cryptography.SHA256]::Create()
  try {
    $bytes = [Text.Encoding]::UTF8.GetBytes($value)
    return ([BitConverter]::ToString($sha.ComputeHash($bytes))).Replace('-','').ToLowerInvariant()
  } finally { $sha.Dispose() }
}

function Assert-ImmutableInputs($integrity) {
  $corpusNow = Get-FileSha256 $integrity.CorpusPath
  $harnessNow = Get-FileSha256 $integrity.HarnessPath
  if ($corpusNow -ne $integrity.CorpusSha256) { throw 'Benchmark corpus changed after the immutable run boundary was established.' }
  if ($harnessNow -ne $integrity.HarnessSha256) { throw 'Benchmark verifier/harness changed after the immutable run boundary was established.' }
}

function Invoke-ProcessCaptured([string]$file,[string]$arguments,[string]$workingDirectory,[int]$timeoutMinutes) {
  $stdout=[IO.Path]::GetTempFileName(); $stderr=[IO.Path]::GetTempFileName(); $watch=[Diagnostics.Stopwatch]::StartNew(); $timedOut=$false
  try {
    $process=Start-Process -FilePath $file -ArgumentList $arguments -WorkingDirectory $workingDirectory -NoNewWindow -PassThru -RedirectStandardOutput $stdout -RedirectStandardError $stderr
    if (-not $process.WaitForExit($timeoutMinutes*60*1000)) { $timedOut=$true; try{$process.Kill($true)}catch{}; try{$process.WaitForExit()}catch{} }
    $watch.Stop(); $out=if(Test-Path $stdout){Get-Content $stdout -Raw}else{''}; $err=if(Test-Path $stderr){Get-Content $stderr -Raw}else{''}
    return [pscustomobject]@{ ExitCode=if($timedOut){124}else{$process.ExitCode}; TimedOut=$timedOut; WallTimeMs=[Math]::Round($watch.Elapsed.TotalMilliseconds,0); Output=$out+"`n"+$err }
  } finally { Remove-Item $stdout,$stderr -Force -ErrorAction SilentlyContinue }
}

function Invoke-IndependentVerification($case,[string]$worktree) {
  $commands=@($case.acceptance.verificationCommands); $records=New-Object System.Collections.Generic.List[object]; $allPassed=$true
  foreach($commandValue in $commands) {
    $command=[string]$commandValue; if([string]::IsNullOrWhiteSpace($command)){continue}
    if($isWindowsHost){$run=Invoke-ProcessCaptured 'cmd.exe' ('/d /s /c '+(Quote-Arg $command)) $worktree $VerificationTimeoutMinutes}
    else{$run=Invoke-ProcessCaptured '/bin/sh' ('-lc '+(Quote-Arg $command)) $worktree $VerificationTimeoutMinutes}
    $passed=($run.ExitCode -eq 0 -and -not $run.TimedOut); if(-not $passed){$allPassed=$false}
    $records.Add([pscustomobject]@{command=$command;passed=$passed;exitCode=$run.ExitCode;timedOut=$run.TimedOut;wallTimeMs=$run.WallTimeMs;outputTail=if($run.Output.Length -gt 3000){$run.Output.Substring($run.Output.Length-3000)}else{$run.Output}})
  }
  if([bool]$case.acceptance.requireVerificationSuccess -and $commands.Count -eq 0){$allPassed=$false}
  return [pscustomobject]@{Passed=[bool]$allPassed;Records=$records.ToArray()}
}

function Test-Acceptance($case,[string]$worktree,[int]$agentExitCode,[string]$initialCommit) {
  $failures=New-Object System.Collections.Generic.List[string]; $acceptance=$case.acceptance
  if([string]::IsNullOrWhiteSpace($worktree)-or -not(Test-Path $worktree -PathType Container)){
    return [pscustomobject]@{Passed=$false;Failures=@('Reviewable worktree is missing.');ChangedPaths=@();Verification=[pscustomobject]@{Passed=$false;Records=@()};InitialCommit=$initialCommit;FinalCommit='';InitialIsAncestor=$false}
  }
  foreach($relative in @($acceptance.requiredFiles)){$full=Resolve-SafePath $worktree ([string]$relative);if($null -eq $full-or -not(Test-Path $full -PathType Leaf)){$failures.Add("Required file missing: $relative")}}
  foreach($rule in @($acceptance.requiredText)){$full=Resolve-SafePath $worktree ([string]$rule.path);if($null -eq $full-or -not(Test-Path $full -PathType Leaf)){$failures.Add("Cannot inspect missing file: $($rule.path)");continue};$text=Get-Content $full -Raw;if(-not $text.Contains([string]$rule.contains,[StringComparison]::Ordinal)){$failures.Add("Required text missing in $($rule.path): $($rule.contains)")}}

  $audit=Get-ChangedPaths $worktree $initialCommit
  if(-not $audit.InitialIsAncestor){$failures.Add("Final HEAD is not descended from immutable initial commit $($audit.InitialCommit).")}
  $allowed=@($acceptance.allowedChangedPathPrefixes|ForEach-Object{Normalize-RepoPath ([string]$_)})
  foreach($changedPath in @($audit.Paths)){
    $ok=$false
    foreach($prefix in $allowed){if($changedPath.Equals($prefix,[StringComparison]::OrdinalIgnoreCase)-or $changedPath.StartsWith($prefix.TrimEnd('/')+'/',[StringComparison]::OrdinalIgnoreCase)){$ok=$true;break}}
    if(-not $ok){$failures.Add("Out-of-scope changed path: $changedPath")}
  }

  $verification=Invoke-IndependentVerification $case $worktree
  if([bool]$acceptance.requireVerificationSuccess -and -not $verification.Passed){$failures.Add('Independent benchmark build/test verification failed or timed out.')}
  if($agentExitCode -ne 0){$failures.Add("Agent process exit code: $agentExitCode")}
  return [pscustomobject]@{Passed=($failures.Count -eq 0);Failures=@($failures);ChangedPaths=@($audit.Paths);Verification=$verification;InitialCommit=$audit.InitialCommit;FinalCommit=$audit.FinalCommit;InitialIsAncestor=$audit.InitialIsAncestor}
}

function New-IsolatedWorktree([string]$root,[string]$commit,[string]$name,[string]$caseId) {
  $safe=("$name-$caseId-"+(Get-Random)).Replace(':','-'); $dir=Join-Path ([IO.Path]::GetTempPath()) "parux-agent-race-$safe"
  & git -C $root worktree add --detach $dir $commit 2>&1|Out-Null
  if($LASTEXITCODE -ne 0-or -not(Test-Path $dir -PathType Container)){throw "Could not create isolated worktree for $name/$caseId."}
  $actual=(& git -C $dir rev-parse HEAD).Trim(); if($actual -ne $commit){throw "Isolated worktree source mismatch for $name/${caseId}: expected $commit, got $actual."}
  return $dir
}

function Remove-IsolatedWorktree([string]$root,[string]$dir){try{& git -C $root worktree remove --force $dir 2>$null|Out-Null}catch{};try{if(Test-Path $dir){Remove-Item $dir -Recurse -Force}}catch{}}

function Invoke-ExternalCase($spec,$case,[string]$root,[string]$initialCommit,$integrity) {
  Assert-ImmutableInputs $integrity
  $worktree=New-IsolatedWorktree $root $initialCommit $spec.Name ([string]$case.id)
  try {
    $promptFile=Join-Path $worktree '.parux-competitive-prompt.txt'; [IO.File]::WriteAllText($promptFile,[string]$case.goal,[Text.UTF8Encoding]::new($false)); $args=Expand-Arguments $spec.Arguments $worktree $promptFile ([string]$case.goal)
    $run=Invoke-ProcessCaptured $spec.Executable $args $worktree $TimeoutMinutesPerCase; Remove-Item $promptFile -Force -ErrorAction SilentlyContinue
    $acceptance=Test-Acceptance $case $worktree $run.ExitCode $initialCommit
    return [pscustomobject]@{provider=$spec.Name;caseId=[string]$case.id;success=[bool]$acceptance.Passed;timeout=$run.TimedOut;exitCode=$run.ExitCode;wallTimeMs=$run.WallTimeMs;initialCommit=$acceptance.InitialCommit;finalCommit=$acceptance.FinalCommit;acceptanceFailures=@($acceptance.Failures);changedPaths=@($acceptance.ChangedPaths);verification=@($acceptance.Verification.Records);outputTail=if($run.Output.Length -gt 4000){$run.Output.Substring($run.Output.Length-4000)}else{$run.Output}}
  } finally {
    try { Assert-ImmutableInputs $integrity } finally { Remove-IsolatedWorktree $root $worktree }
  }
}

function Get-PaRuXBootstrap(){
  if(-not $isWindowsHost){return $null}; $dataRoot=if([string]::IsNullOrWhiteSpace($env:PARUX_DATA_ROOT)){Join-Path $env:LOCALAPPDATA 'PaRuX'}else{$env:PARUX_DATA_ROOT}; $path=Join-Path $dataRoot 'server\bootstrap.json'; if(-not(Test-Path $path)){return $null}
  try{$value=Get-Content $path -Raw|ConvertFrom-Json;if($value.endpoint-and $value.token){return $value}}catch{};return $null
}

function Get-PaRuXWorktree($bootstrap,[string]$taskId){
  if($null -eq $bootstrap){return ''}; $headers=@{'X-PaRuX-Token'=$bootstrap.token}
  foreach($route in @("/api/v1/orchestrator/tasks/$taskId/desktop-worktree","/api/v1/orchestrator/tasks/$taskId/worktree")){
    try{$response=Invoke-RestMethod -Method GET -Uri ($bootstrap.endpoint.TrimEnd('/')+$route) -Headers $headers -TimeoutSec 30;if($response.worktreePath){return [string]$response.worktreePath}}catch{}
  };return ''
}

function Get-Median([double[]]$values) {
  $ordered=@($values|Sort-Object); if($ordered.Count -eq 0){return $null}
  $middle=[int][Math]::Floor($ordered.Count/2)
  if(($ordered.Count % 2) -eq 1){return [double]$ordered[$middle]}
  return ([double]$ordered[$middle-1]+[double]$ordered[$middle])/2.0
}

function Get-PairwiseVerdict(
  [object[]]$PaRuXRows,
  [object[]]$CompetitorRows,
  [bool]$PaRuXEligible,
  [bool]$CompetitorEligible
) {
  $pSuccessRows=@($PaRuXRows|Where-Object{[bool]$_.success})
  $oSuccessRows=@($CompetitorRows|Where-Object{[bool]$_.success})
  $pSuccessIds=@($pSuccessRows|ForEach-Object{[string]$_.caseId})
  $oSuccessIds=@($oSuccessRows|ForEach-Object{[string]$_.caseId})
  $successSetIdentical=@(Compare-Object -ReferenceObject $pSuccessIds -DifferenceObject $oSuccessIds -CaseSensitive).Count -eq 0
  $common=New-Object System.Collections.Generic.List[object]
  foreach($p in $pSuccessRows){
    $o=@($oSuccessRows|Where-Object{[string]$_.caseId -ceq [string]$p.caseId})|Select-Object -First 1
    if($null -ne $o){$common.Add([pscustomobject]@{caseId=[string]$p.caseId;paruxMs=[double]$p.wallTimeMs;otherMs=[double]$o.wallTimeMs})}
  }
  $pCommonMedian=Get-Median @($common|ForEach-Object{[double]$_.paruxMs})
  $oCommonMedian=Get-Median @($common|ForEach-Object{[double]$_.otherMs})
  $verdict='TIE'; $reason='identical_success_set_equal_latency'
  if(-not $PaRuXEligible){$verdict='LOSS';$reason='parux_below_success_floor_or_mandatory_failure'}
  elseif(-not $CompetitorEligible){$verdict='WIN';$reason='competitor_below_success_floor_or_mandatory_failure'}
  elseif($pSuccessRows.Count -gt $oSuccessRows.Count){$verdict='WIN';$reason='more_successful_tasks'}
  elseif($pSuccessRows.Count -lt $oSuccessRows.Count){$verdict='LOSS';$reason='fewer_successful_tasks'}
  elseif(-not $successSetIdentical){$verdict='INCONCLUSIVE';$reason='equal_success_count_but_different_task_sets'}
  elseif($common.Count -eq 0 -or $null -eq $pCommonMedian -or $null -eq $oCommonMedian){$verdict='INCONCLUSIVE';$reason='no_common_successful_tasks'}
  elseif($pCommonMedian -lt $oCommonMedian){$verdict='WIN';$reason='faster_on_identical_success_set'}
  elseif($pCommonMedian -gt $oCommonMedian){$verdict='LOSS';$reason='slower_on_identical_success_set'}
  return [pscustomobject]@{
    Verdict=$verdict;Reason=$reason;Common=$common;CommonSuccessfulCases=$common.Count
    PaRuXCommonMedianMs=$pCommonMedian;CompetitorCommonMedianMs=$oCommonMedian
    SuccessSetsIdentical=$successSetIdentical
    PaRuXOnlySuccesses=@($pSuccessIds|Where-Object{$_ -notin $oSuccessIds})
    CompetitorOnlySuccesses=@($oSuccessIds|Where-Object{$_ -notin $pSuccessIds})
  }
}

function Invoke-VerdictSelfTest {
  $emptyRows=@()
  $zero=Get-PairwiseVerdict -PaRuXRows $emptyRows -CompetitorRows $emptyRows -PaRuXEligible $false -CompetitorEligible $false
  if($zero.Verdict -eq 'WIN'){throw 'Zero-success providers must never produce a win.'}

  $paRuX=@(
    [pscustomobject]@{caseId='shared';success=$true;wallTimeMs=10},
    [pscustomobject]@{caseId='parux-only';success=$true;wallTimeMs=20},
    [pscustomobject]@{caseId='competitor-only';success=$false;wallTimeMs=1}
  )
  $competitor=@(
    [pscustomobject]@{caseId='shared';success=$true;wallTimeMs=1000},
    [pscustomobject]@{caseId='parux-only';success=$false;wallTimeMs=1},
    [pscustomobject]@{caseId='competitor-only';success=$true;wallTimeMs=1000}
  )
  $divergent=Get-PairwiseVerdict -PaRuXRows $paRuX -CompetitorRows $competitor -PaRuXEligible $true -CompetitorEligible $true
  $paruxOnlyCount=@($divergent.PaRuXOnlySuccesses).Count; $competitorOnlyCount=@($divergent.CompetitorOnlySuccesses).Count
  $divergentInvalid=$divergent.Verdict -ne 'INCONCLUSIVE' -or $divergent.SuccessSetsIdentical -or $divergent.CommonSuccessfulCases -ne 1 -or $paruxOnlyCount -ne 1 -or $competitorOnlyCount -ne 1
  if($divergentInvalid){
    throw 'Equal-sized but different success sets must be inconclusive even when one provider is faster on shared successes.'
  }

  $noOverlapPaRuX=@([pscustomobject]@{caseId='a';success=$true;wallTimeMs=1})
  $noOverlapCompetitor=@([pscustomobject]@{caseId='b';success=$true;wallTimeMs=100})
  $noOverlap=Get-PairwiseVerdict -PaRuXRows $noOverlapPaRuX -CompetitorRows $noOverlapCompetitor -PaRuXEligible $true -CompetitorEligible $true
  if($noOverlap.Verdict -ne 'INCONCLUSIVE' -or $noOverlap.CommonSuccessfulCases -ne 0){throw 'A latency comparison without common successes must be inconclusive.'}

  $samePaRuX=@(
    [pscustomobject]@{caseId='a';success=$true;wallTimeMs=100},
    [pscustomobject]@{caseId='b';success=$true;wallTimeMs=200}
  )
  $sameCompetitor=@(
    [pscustomobject]@{caseId='a';success=$true;wallTimeMs=200},
    [pscustomobject]@{caseId='b';success=$true;wallTimeMs=400}
  )
  $same=Get-PairwiseVerdict -PaRuXRows $samePaRuX -CompetitorRows $sameCompetitor -PaRuXEligible $true -CompetitorEligible $true
  if($same.Verdict -ne 'WIN' -or -not $same.SuccessSetsIdentical -or $same.CommonSuccessfulCases -ne 2){throw 'An identical success set should compare latency over every shared success.'}
  $slower=Get-PairwiseVerdict -PaRuXRows $sameCompetitor -CompetitorRows $samePaRuX -PaRuXEligible $true -CompetitorEligible $true
  if($slower.Verdict -ne 'LOSS'){throw 'A slower provider must not win on an identical success set.'}
  $belowFloor=Get-PairwiseVerdict -PaRuXRows $emptyRows -CompetitorRows $sameCompetitor -PaRuXEligible $false -CompetitorEligible $true
  if($belowFloor.Verdict -ne 'LOSS'){throw 'A provider below its absolute floor or mandatory-case requirement must not win.'}
  Write-Host 'BIC-03 competitive verdict self-test: PASS'
}

function Get-ProviderMetadataEntry($document,[string]$provider) {
  if($null -eq $document){return $null}; $property=$document.PSObject.Properties[$provider]; if($null -eq $property){return $null}; return $property.Value
}

function Test-ProviderMetadataComplete($metadata) {
  if($null -eq $metadata){return $false}
  foreach($field in @('productVersion','model','modelRevision','reasoningBudgetClass','outputBudget','permissionMode','interventionCount')){
    $property=$metadata.PSObject.Properties[$field]; if($null -eq $property -or [string]::IsNullOrWhiteSpace([string]$property.Value)){return $false}
  }
  return $true
}

function Invoke-ScopeAuditSelfTest {
  if (-not (Test-Path $Corpus -PathType Leaf)) { throw "Scope audit self-test could not resolve corpus: $Corpus" }
  if (-not (Test-Path $Workspace -PathType Container)) { throw "Scope audit self-test could not resolve workspace: $Workspace" }
  $root=Join-Path ([IO.Path]::GetTempPath()) ('parux-scope-audit-selftest-'+[Guid]::NewGuid().ToString('N')); New-Item -ItemType Directory -Path $root -Force|Out-Null
  try {
    & git -C $root init -q; & git -C $root config user.email 'parux-selftest@local'; & git -C $root config user.name 'PaRuX benchmark self-test'
    New-Item -ItemType Directory -Path (Join-Path $root 'allowed') -Force|Out-Null; Set-Content (Join-Path $root 'allowed/base.txt') 'base'; Set-Content (Join-Path $root 'delete-me.txt') 'delete'
    & git -C $root add .; & git -C $root commit -q -m baseline; $initial=(& git -C $root rev-parse HEAD).Trim()

    Set-Content (Join-Path $root 'forbidden-committed.txt') 'bad'; & git -C $root add forbidden-committed.txt; & git -C $root commit -q -m unauthorized-one
    Set-Content (Join-Path $root 'another-forbidden-commit.txt') 'bad again'; & git -C $root add another-forbidden-commit.txt; & git -C $root commit -q -m unauthorized-two
    Set-Content (Join-Path $root 'transient-forbidden.txt') 'committed then removed'; & git -C $root add transient-forbidden.txt; & git -C $root commit -q -m unauthorized-transient
    Remove-Item (Join-Path $root 'transient-forbidden.txt'); & git -C $root add -u; & git -C $root commit -q -m remove-transient-unauthorized
    Set-Content (Join-Path $root 'staged-only.txt') 'staged'; & git -C $root add staged-only.txt
    Set-Content (Join-Path $root 'untracked.txt') 'untracked'
    & git -C $root mv allowed/base.txt renamed-outside.txt
    Remove-Item (Join-Path $root 'delete-me.txt')
    $audit=Get-ChangedPaths $root $initial
    foreach($expected in @('forbidden-committed.txt','another-forbidden-commit.txt','transient-forbidden.txt','staged-only.txt','untracked.txt','allowed/base.txt','renamed-outside.txt','delete-me.txt')){
      if($expected -notin @($audit.Paths)){throw "Scope audit self-test missed $expected"}
    }
    if(-not $audit.InitialIsAncestor){throw 'Scope audit self-test unexpectedly lost initial ancestry.'}

    $corpusPath=Join-Path $root 'corpus.json'; $harnessPath=Join-Path $root 'verifier.ps1'
    Set-Content $corpusPath 'corpus-v1'; Set-Content $harnessPath 'verifier-v1'
    $integrity=[pscustomobject]@{CorpusPath=$corpusPath;CorpusSha256=(Get-FileSha256 $corpusPath);HarnessPath=$harnessPath;HarnessSha256=(Get-FileSha256 $harnessPath)}
    Assert-ImmutableInputs $integrity
    Set-Content $corpusPath 'corpus-tampered'
    $corpusTamperingRejected=$false
    try{Assert-ImmutableInputs $integrity}catch{$corpusTamperingRejected=$_.Exception.Message -like '*corpus changed*'}
    if(-not $corpusTamperingRejected){throw 'Corpus tampering was not rejected by the immutable-input guard.'}
    Set-Content $corpusPath 'corpus-v1'; Set-Content $harnessPath 'verifier-tampered'
    $harnessTamperingRejected=$false
    try{Assert-ImmutableInputs $integrity}catch{$harnessTamperingRejected=$_.Exception.Message -like '*verifier/harness changed*'}
    if(-not $harnessTamperingRejected){throw 'Verifier tampering was not rejected by the immutable-input guard.'}

    $legitimateRoot=Join-Path ([IO.Path]::GetTempPath()) ('parux-scope-legitimate-selftest-'+[Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $legitimateRoot -Force|Out-Null
    try{
      & git -C $legitimateRoot init -q; & git -C $legitimateRoot config user.email 'parux-selftest@local'; & git -C $legitimateRoot config user.name 'PaRuX benchmark self-test'
      New-Item -ItemType Directory -Path (Join-Path $legitimateRoot 'src') -Force|Out-Null
      Set-Content (Join-Path $legitimateRoot 'src/target.txt') 'baseline'; & git -C $legitimateRoot add .; & git -C $legitimateRoot commit -q -m baseline
      $legitimateInitial=(& git -C $legitimateRoot rev-parse HEAD).Trim()
      Set-Content (Join-Path $legitimateRoot 'src/target.txt') 'valid scoped change'; & git -C $legitimateRoot add .; & git -C $legitimateRoot commit -q -m scoped
      $case=[pscustomobject]@{acceptance=[pscustomobject]@{requiredFiles=@('src/target.txt');requiredText=@();allowedChangedPathPrefixes=@('src/target.txt');requireVerificationSuccess=$false;verificationCommands=@()}}
      $legitimate=Test-Acceptance $case $legitimateRoot 0 $legitimateInitial
      if(-not $legitimate.Passed -or $legitimate.InitialCommit -ne $legitimateInitial -or $legitimate.FinalCommit -eq $legitimateInitial -or -not $legitimate.InitialIsAncestor){throw 'A legitimate scoped commit or its source identities were not accepted.'}

      Set-Content (Join-Path $legitimateRoot 'forbidden.txt') 'unauthorized'; & git -C $legitimateRoot add .; & git -C $legitimateRoot commit -q -m unauthorized
      Remove-Item (Join-Path $legitimateRoot 'forbidden.txt'); & git -C $legitimateRoot add -u; & git -C $legitimateRoot commit -q -m remove-unauthorized
      $unauthorized=Test-Acceptance $case $legitimateRoot 0 $legitimateInitial
      if($unauthorized.Passed -or -not ($unauthorized.Failures -match 'Out-of-scope changed path: forbidden.txt')){throw 'A committed out-of-scope edit was not rejected by acceptance.'}
    } finally { try{Remove-Item $legitimateRoot -Recurse -Force -ErrorAction SilentlyContinue}catch{} }

    Write-Host 'BIC-02 scope and immutable-input self-test: PASS'
  } finally { try{Remove-Item $root -Recurse -Force -ErrorAction SilentlyContinue}catch{} }
}

# ============================================================================================
# Corpus v2 mode: self-contained fixtures (benchmarks/coding-agent/corpus-v2), hidden verifiers,
# optimization / held-out split. The SAME task set, budgets and verifier run against every provider.
#   -TaskCorpusV2 <parux-corpus-v2.json> -V2Action Run            CLI providers via -CompetitorSpecs
#   -TaskCorpusV2 <...> -V2Action PrepareManual -ManualProviders  GUI apps (e.g. Locally Uncensored)
#   -TaskCorpusV2 <...> -V2Action CollectManual -ManualProviders  verify what the operator produced
# Provider invocation is a pluggable adapter: 'name=exe|args' with {workspace} {promptFile} {prompt}
# {resultFile}. Nothing here launches a GUI application; the manual protocol is documented in the
# PROTOCOL.md that PrepareManual writes. Verification is always performed by corpus_tool.py verify.
# ============================================================================================
function Get-V2Tooling {
  $py=Get-Command python -ErrorAction SilentlyContinue|Select-Object -First 1
  if($null -eq $py){$py=Get-Command python3 -ErrorAction SilentlyContinue|Select-Object -First 1}
  if($null -eq $py){throw 'python is required for the corpus v2 tool.'}
  $tool=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\benchmarks\coding-agent\corpus-v2\corpus_tool.py'))
  if(-not(Test-Path $tool)){throw "Corpus tool missing: $tool"}
  return [pscustomobject]@{Python=$py.Source;Tool=$tool;CorpusRoot=(Split-Path $tool -Parent)}
}

function Invoke-V2Tool($tooling,[string[]]$toolArgs) {
  $out=& $tooling.Python $tooling.Tool @toolArgs 2>$null
  return [pscustomobject]@{ExitCode=$LASTEXITCODE;Output=((@($out)|ForEach-Object{[string]$_}) -join "`n")}
}

function Invoke-V2Process([string]$file,[string]$arguments,[string]$cwd,[int]$timeoutSeconds) {
  $stdout=[IO.Path]::GetTempFileName(); $stderr=[IO.Path]::GetTempFileName(); $timedOut=$false; $watch=[Diagnostics.Stopwatch]::StartNew()
  try {
    $p=Start-Process -FilePath $file -ArgumentList $arguments -WorkingDirectory $cwd -NoNewWindow -PassThru -RedirectStandardOutput $stdout -RedirectStandardError $stderr
    if(-not $p.WaitForExit($timeoutSeconds*1000)){$timedOut=$true;try{$p.Kill($true)}catch{}}
    try{$p.WaitForExit()}catch{}
    $watch.Stop()
    return [pscustomobject]@{ExitCode=if($timedOut){124}else{$p.ExitCode};TimedOut=$timedOut;WallTimeMs=[int64]$watch.ElapsedMilliseconds}
  } finally { Remove-Item $stdout,$stderr -Force -ErrorAction SilentlyContinue }
}

function Read-V2AdapterResult([string]$resultFile) {
  $steps=$null; $interventions=0
  if(Test-Path $resultFile){
    try{
      $r=Get-Content $resultFile -Raw|ConvertFrom-Json
      if($null -ne $r.PSObject.Properties['steps'] -and $null -ne $r.steps){$steps=[int]$r.steps}
      if($null -ne $r.PSObject.Properties['interventions'] -and $null -ne $r.interventions){$interventions=[int]$r.interventions}
    }catch{}
  }
  return [pscustomobject]@{Steps=$steps;Interventions=$interventions}
}

function Complete-V2Row($tooling,$task,[string]$provider,[string]$measurement,[int64]$wallMs,[bool]$timedOut,[int]$agentExit,$steps,[int]$interventions,[string]$workdir) {
  $verifyRun=Invoke-V2Tool $tooling @('verify','--task',[string]$task.id,'--workdir',$workdir)
  $v=$null; try{$v=$verifyRun.Output|ConvertFrom-Json}catch{}
  $timeBudgetMs=[int64]$task.budgets.timeSeconds*1000
  $timeExceeded=($timedOut -or $wallMs -gt $timeBudgetMs)
  $stepsExceeded=($null -ne $steps -and [int]$steps -gt [int]$task.budgets.steps)
  $status='FAIL'
  if($null -eq $v){$status='VERIFIER_ERROR'}
  elseif($v.status -eq 'SKIPPED_TOOLCHAIN'){$status='SKIPPED_TOOLCHAIN'}
  elseif($v.status -eq 'PASS' -and -not $timeExceeded -and -not $stepsExceeded){$status='PASS'}
  elseif($timeExceeded){$status='TIMEOUT'}
  $tail=''; if($null -ne $v){$tail=[string]$v.outputTail}
  return [pscustomobject]@{
    provider=$provider; caseId=[string]$task.id; split=[string]$task.split; language=[string]$task.language; kind=[string]$task.kind
    success=($status -eq 'PASS'); status=$status; wallTimeMs=[double]$wallMs; timedOut=$timedOut; timeBudgetSeconds=[int]$task.budgets.timeSeconds
    steps=$steps; stepBudget=[int]$task.budgets.steps; stepsSource=$(if($null -eq $steps){'unavailable'}elseif($measurement -eq 'harness-timed'){'adapter-reported'}else{$measurement}); stepsExceeded=$stepsExceeded
    interventions=$interventions; agentExitCode=$agentExit; measurement=$measurement
    verifierExitCode=$(if($null -ne $v){$v.verifierExitCode}else{$null}); outOfScopePaths=$(if($null -ne $v){@($v.outOfScopePaths)}else{@()})
    changedPaths=$(if($null -ne $v){@($v.changedPaths)}else{@()}); verifierOutputTail=$tail
  }
}

function New-V2Workspace($tooling,$task,[string]$dest) {
  if(Test-Path $dest){Remove-Item $dest -Recurse -Force}
  New-Item -ItemType Directory -Force -Path (Split-Path $dest -Parent)|Out-Null
  $r=Invoke-V2Tool $tooling @('materialize','--task',[string]$task.id,'--dest',$dest)
  if($r.ExitCode -ne 0){throw "Could not materialize task $($task.id): $($r.Output)"}
  return ($r.Output|ConvertFrom-Json)
}

function Invoke-V2CliCase($tooling,$spec,$task,[string]$outDir) {
  $base=Join-Path $outDir ("work\"+$spec.Name+"\"+$task.id); $ws=Join-Path $base 'workspace'; $m=New-V2Workspace $tooling $task $ws
  $resultFile=Join-Path $base 'adapter-result.json'; $wall=0; $timedOut=$false; $exit=0; $measurement='harness-timed'
  if($spec.Executable -eq '@oracle'){
    # SELF-TEST ADAPTER ONLY: copies the reference solution. Its rows can never support a claim.
    Copy-Item (Join-Path $tooling.CorpusRoot ("tasks\"+$task.id+"\reference\*")) $ws -Recurse -Force; $measurement='self-test-oracle'
  } elseif($spec.Executable -eq '@noop'){
    $measurement='self-test-noop'
  } else {
    $prompt=[IO.File]::ReadAllText($m.promptFile).Trim()
    $args=(Expand-Arguments $spec.Arguments $ws $m.promptFile $prompt).Replace('{resultFile}',(Quote-Arg $resultFile))
    $run=Invoke-V2Process $spec.Executable $args $ws ([int]$task.budgets.timeSeconds)
    $wall=$run.WallTimeMs; $timedOut=$run.TimedOut; $exit=$run.ExitCode
  }
  $ar=Read-V2AdapterResult $resultFile
  return (Complete-V2Row $tooling $task $spec.Name $measurement $wall $timedOut $exit $ar.Steps $ar.Interventions $ws)
}

function Write-V2ManualProtocol([string]$path,$doc,[string]$splitName) {
  $text=@'
# Manual protocol for GUI coding agents (for example Locally Uncensored)

This protocol exists because a GUI application cannot be driven by this harness without fabricating
automation that was never verified. Everything the operator records below is labelled
`operator-attested` in the result file and can never be silently upgraded to harness-measured.

Corpus: {{NAME}} v{{VERSION}}, split: {{SPLIT}}.

1. Same machine, same session, same power profile for every provider. Record product version, model,
   model revision / quantization, reasoning budget class, output budget and permission mode in a
   provider-metadata JSON (same schema as the CLI path). Use the identical model weights where the
   products allow it; otherwise record the difference - the verdict will not be a WIN.
2. For each task directory under manual/<provider>/<task>/: open the `workspace` folder as the agent
   working directory. Paste PROMPT.txt verbatim as the only instruction. Do not add hints.
3. Do not intervene. Any extra prompt, manual file edit, retry click or tool approval beyond the
   provider's normal permission mode increments `interventions` in manual-run.json. Runs with
   interventions > 0 are recorded but make the verdict INCONCLUSIVE.
4. Time: `startedUtc` = the moment the prompt is submitted; `endedUtc` = the moment the agent reports
   completion, or the time budget (see PROMPT.txt header in manual-run.json) elapses - then stop the
   agent. Record in ISO-8601 UTC.
5. Steps: count of tool calls the application displays (define the counting rule in `notes`). If the
   application cannot show a step count, leave `steps` null: the result then carries
   stepsEnforced=false and cannot produce a WIN.
6. Never run or read the hidden verifier or reference solutions. The harness verifies with
   `-V2Action CollectManual` after all runs are finished.
7. Alternate provider order between tasks (A,B then B,A ...) to spread time-of-day and thermal bias.
8. Held-out tasks must not be run for tuning. Tune only on split=optimization.
'@
  $text=$text.Replace('{{NAME}}',[string]$doc.name).Replace('{{VERSION}}',[string]$doc.corpusVersion).Replace('{{SPLIT}}',$splitName)
  [IO.File]::WriteAllText($path,$text,[Text.UTF8Encoding]::new($false))
}

function Invoke-V2PrepareManual($tooling,$doc,$tasks,[string]$outDir) {
  if($ManualProviders.Count -eq 0){throw '-ManualProviders is required for PrepareManual.'}
  $root=Join-Path $outDir 'manual'; New-Item -ItemType Directory -Force -Path $root|Out-Null
  Write-V2ManualProtocol (Join-Path $root 'PROTOCOL.md') $doc $Split
  foreach($provider in $ManualProviders){
    foreach($task in $tasks){
      $base=Join-Path $root ($provider+"\"+$task.id); $ws=Join-Path $base 'workspace'; $m=New-V2Workspace $tooling $task $ws
      Copy-Item $m.promptFile (Join-Path $base 'PROMPT.txt') -Force; Remove-Item $m.promptFile -Force -ErrorAction SilentlyContinue
      $template=[ordered]@{taskId=[string]$task.id;provider=$provider;timeBudgetSeconds=[int]$task.budgets.timeSeconds;stepBudget=[int]$task.budgets.steps;startedUtc=$null;endedUtc=$null;steps=$null;interventions=$null;notes=''}
      ($template|ConvertTo-Json)|Set-Content (Join-Path $base 'manual-run.json') -Encoding UTF8
    }
  }
  Write-Host "Manual workspaces prepared under $root. Follow $root\PROTOCOL.md, then rerun with -V2Action CollectManual."
}

function Invoke-V2CollectManual($tooling,$tasks,[string]$outDir) {
  $rows=New-Object System.Collections.Generic.List[object]; $root=Join-Path $outDir 'manual'
  foreach($provider in $ManualProviders){
    foreach($task in $tasks){
      $base=Join-Path $root ($provider+"\"+$task.id); $ws=Join-Path $base 'workspace'; $runFile=Join-Path $base 'manual-run.json'
      $r=$null; if(Test-Path $runFile){try{$r=Get-Content $runFile -Raw|ConvertFrom-Json}catch{}}
      $complete=($null -ne $r -and $null -ne $r.startedUtc -and $null -ne $r.endedUtc -and $null -ne $r.interventions -and (Test-Path $ws))
      if(-not $complete){
        $rows.Add([pscustomobject]@{provider=$provider;caseId=[string]$task.id;split=[string]$task.split;language=[string]$task.language;kind=[string]$task.kind;success=$false;status='NOT_RUN';wallTimeMs=0.0;timedOut=$false;timeBudgetSeconds=[int]$task.budgets.timeSeconds;steps=$null;stepBudget=[int]$task.budgets.steps;stepsSource='unavailable';stepsExceeded=$false;interventions=0;agentExitCode=0;measurement='operator-attested';verifierExitCode=$null;outOfScopePaths=@();changedPaths=@();verifierOutputTail='manual-run.json incomplete or workspace missing'})
        continue
      }
      $wall=[int64](([DateTime]::Parse([string]$r.endedUtc).ToUniversalTime()-[DateTime]::Parse([string]$r.startedUtc).ToUniversalTime()).TotalMilliseconds)
      $steps=$null; if($null -ne $r.steps){$steps=[int]$r.steps}
      $rows.Add((Complete-V2Row $tooling $task $provider 'operator-attested' $wall $false 0 $steps ([int]$r.interventions) $ws))
    }
  }
  return $rows
}

function Get-V2Summary($doc,$tasks,$rows,$metadataDocument,[string]$corpusSha256,[string]$mode) {
  $policy=$doc.evaluationPolicy; $floor=[double]$policy.absoluteSuccessFloorPercent
  $selectedIds=@($tasks|ForEach-Object{[string]$_.id}); $mandatory=@($policy.mandatoryCaseIds|ForEach-Object{[string]$_}|Where-Object{$_ -in $selectedIds})
  $heldOutTotal=@($doc.tasks|Where-Object{$_.split -eq 'held-out'}).Count
  $claimable=($Split -eq 'held-out' -and $selectedIds.Count -eq $heldOutTotal -and $heldOutTotal -ge [int]$policy.minimumHeldOutTasks)
  $providers=@($rows|ForEach-Object{$_.provider}|Select-Object -Unique); $providerSummary=@()
  foreach($provider in $providers){
    $sample=@($rows|Where-Object{$_.provider -eq $provider}); $ok=@($sample|Where-Object{$_.success})
    $okIds=@($ok|ForEach-Object{[string]$_.caseId}); $mandatoryFailures=@($mandatory|Where-Object{$id=$_; -not $okIds.Contains($id)})
    $metadata=Get-ProviderMetadataEntry $metadataDocument $provider
    $providerSummary+=[pscustomobject]@{
      provider=$provider; tasks=$sample.Count; completeTaskSet=($sample.Count -eq $selectedIds.Count -and @($sample|ForEach-Object{$_.caseId}|Select-Object -Unique).Count -eq $selectedIds.Count)
      successes=$ok.Count; successRatePercent=$(if($sample.Count){[Math]::Round(100.0*$ok.Count/$sample.Count,1)}else{0})
      medianSuccessfulWallTimeMs=Get-Median @($ok|ForEach-Object{[double]$_.wallTimeMs}); mandatoryPassed=($mandatoryFailures.Count -eq 0); mandatoryFailures=$mandatoryFailures
      skippedToolchain=@($sample|Where-Object{$_.status -eq 'SKIPPED_TOOLCHAIN'}).Count; notRun=@($sample|Where-Object{$_.status -eq 'NOT_RUN'}).Count
      interventionsTotal=[int](@($sample|ForEach-Object{[int]$_.interventions})|Measure-Object -Sum).Sum
      stepsEnforced=(@($sample|Where-Object{$null -eq $_.steps}).Count -eq 0)
      measurements=@($sample|ForEach-Object{$_.measurement}|Select-Object -Unique)
      metadataComplete=(Test-ProviderMetadataComplete $metadata); metadata=$metadata
    }
  }
  $fingerprints=@(); $metadataComparable=($providerSummary.Count -gt 0)
  foreach($s in $providerSummary){
    if(-not $s.metadataComplete){$metadataComparable=$false;continue}
    if([int]$s.metadata.interventionCount -ne 0){$metadataComparable=$false}
    $fingerprints+=([string]$s.metadata.reasoningBudgetClass+'|'+[string]$s.metadata.outputBudget+'|'+[string]$s.metadata.permissionMode)
  }
  if(@($fingerprints|Select-Object -Unique).Count -gt 1){$metadataComparable=$false}
  $allMeasurements=@($providerSummary|ForEach-Object{$_.measurements}|Select-Object -Unique)
  $fairness=[pscustomobject]@{
    claimableSplitAndSize=$claimable
    allProvidersCompleteTaskSet=(@($providerSummary|Where-Object{-not $_.completeTaskSet}).Count -eq 0)
    noSkippedOrUnrunTasks=(@($providerSummary|Where-Object{$_.skippedToolchain -gt 0 -or $_.notRun -gt 0}).Count -eq 0)
    zeroInterventions=(@($providerSummary|Where-Object{$_.interventionsTotal -gt 0}).Count -eq 0)
    stepBudgetEnforcedForAllProviders=(@($providerSummary|Where-Object{-not $_.stepsEnforced}).Count -eq 0)
    sameMeasurementMethodForAllProviders=($allMeasurements.Count -eq 1)
    noSelfTestAdapters=(@($allMeasurements|Where-Object{$_ -like 'self-test-*'}).Count -eq 0)
    providerMetadataComparable=$metadataComparable
    sameCorpusAndHiddenVerifiers=$true
  }
  $fairnessOk=(@($fairness.PSObject.Properties|Where-Object{-not [bool]$_.Value}).Count -eq 0)
  $paruxSummary=$providerSummary|Where-Object{$_.provider -eq 'PaRuX'}|Select-Object -First 1
  $pairwise=@(); $overall='NOT_A_CLAIM'
  if($claimable -and $null -ne $paruxSummary){
    $overall='INCONCLUSIVE'; $win=$false; $loss=$false; $inc=$false
    foreach($other in @($providerSummary|Where-Object{$_.provider -ne 'PaRuX'})){
      $pE=($paruxSummary.successRatePercent -ge $floor -and $paruxSummary.mandatoryPassed); $oE=($other.successRatePercent -ge $floor -and $other.mandatoryPassed)
      $c=Get-PairwiseVerdict @($rows|Where-Object{$_.provider -eq 'PaRuX'}) @($rows|Where-Object{$_.provider -eq $other.provider}) $pE $oE
      if($c.Verdict -eq 'WIN'){$win=$true}; if($c.Verdict -eq 'LOSS'){$loss=$true}; if($c.Verdict -eq 'INCONCLUSIVE'){$inc=$true}
      $pairwise+=[pscustomobject]@{competitor=$other.provider;verdict=$c.Verdict;verdictReason=$c.Reason;successSetsIdentical=$c.SuccessSetsIdentical;commonSuccessfulCases=$c.CommonSuccessfulCases;paRuXOnlySuccesses=$c.PaRuXOnlySuccesses;competitorOnlySuccesses=$c.CompetitorOnlySuccesses}
    }
    # A loss is reportable even when fairness is imperfect; a WIN or TIE is never granted without full fairness.
    if($loss){$overall='LOSS'} elseif(-not $fairnessOk -or $inc){$overall='INCONCLUSIVE'} elseif($win){$overall='WIN'} else {$overall='TIE'}
  }
  return [pscustomobject]@{
    schema='parux-coding-agent-result/2'; corpus=[string]$doc.name; corpusVersion=[string]$doc.corpusVersion; corpusSha256=$corpusSha256
    taskHashes=@($tasks|ForEach-Object{[pscustomobject]@{id=$_.id;fixtureSha256=$_.fixtureSha256;verifierSha256=$_.verifierSha256}})
    split=$Split; mode=$mode; selectedTaskCount=$selectedIds.Count
    evaluationPolicy=[pscustomobject]@{absoluteSuccessFloorPercent=$floor;mandatoryCaseIds=$mandatory;tuningRule=[string]$policy.tuningRule}
    fairness=$fairness; providers=$providerSummary; pairwise=$pairwise; paRuXCompetitiveVerdict=$overall; paRuXBestInThisRun=($overall -eq 'WIN'); results=$rows.ToArray()
  }
}

function Invoke-V2Mode {
  $tooling=Get-V2Tooling
  $corpusFull=[IO.Path]::GetFullPath((Resolve-Path $TaskCorpusV2).Path)
  $expected=[IO.Path]::GetFullPath((Join-Path $tooling.CorpusRoot '..\parux-corpus-v2.json'))
  if($corpusFull -ne $expected){throw "The corpus v2 tool is bound to $expected; refusing a different corpus file."}
  $check=Invoke-V2Tool $tooling @('check'); if($check.ExitCode -ne 0){throw "Corpus integrity check failed:`n$($check.Output)"}
  $doc=Get-Content $corpusFull -Raw|ConvertFrom-Json; $corpusSha256=Get-FileSha256 $corpusFull
  $tasks=@($doc.tasks|Where-Object{$Split -eq 'all' -or $_.split -eq $Split}); if($OnlyTasks.Count -gt 0){$tasks=@($tasks|Where-Object{$_.id -in $OnlyTasks})}
  if($tasks.Count -eq 0){throw 'No tasks selected.'}
  New-Item -ItemType Directory -Force -Path $OutputDirectory|Out-Null
  $metadataDocument=$null; if(-not [string]::IsNullOrWhiteSpace($ProviderMetadataJson)){$metadataDocument=Get-Content ([IO.Path]::GetFullPath((Resolve-Path $ProviderMetadataJson).Path)) -Raw|ConvertFrom-Json}
  if($V2Action -eq 'PrepareManual'){Invoke-V2PrepareManual $tooling $doc $tasks $OutputDirectory;return}
  $rows=New-Object System.Collections.Generic.List[object]
  if($V2Action -eq 'CollectManual'){foreach($r in @(Invoke-V2CollectManual $tooling $tasks $OutputDirectory)){$rows.Add($r)}}
  else{
    $specs=@($CompetitorSpecs|ForEach-Object{Parse-Spec $_}); if($specs.Count -eq 0){throw 'Run needs at least one adapter via -CompetitorSpecs name=exe|args.'}
    foreach($spec in $specs){Write-Host "== $($spec.Name) ==" -ForegroundColor Cyan;foreach($task in $tasks){Write-Host "[$($task.id)] ($($task.split))";$rows.Add((Invoke-V2CliCase $tooling $spec $task $OutputDirectory))}}
  }
  $summary=Get-V2Summary $doc $tasks $rows $metadataDocument $corpusSha256 $V2Action
  $stamp=Get-Date -Format 'yyyyMMdd-HHmmss'; $json=Join-Path $OutputDirectory "coding-agent-v2-$stamp.json"; $csv=Join-Path $OutputDirectory "coding-agent-v2-$stamp.csv"
  $summary|ConvertTo-Json -Depth 30|Set-Content $json -Encoding UTF8
  $rows|Select-Object provider,caseId,split,status,success,wallTimeMs,steps,interventions,measurement|Export-Csv $csv -NoTypeInformation -Encoding UTF8
  $summary.providers|Select-Object provider,tasks,successes,successRatePercent,mandatoryPassed,stepsEnforced,interventionsTotal|Format-Table -AutoSize|Out-Host
  Write-Host "Verdict: $($summary.paRuXCompetitiveVerdict)  (NOT_A_CLAIM = not the full held-out split)"; Write-Host "JSON: $json"; Write-Host "CSV : $csv"
  if($FailIfPaRuXLoses -and $summary.paRuXCompetitiveVerdict -ne 'WIN'){throw "Corpus v2 gate did not produce a valid PaRuX WIN (verdict: $($summary.paRuXCompetitiveVerdict))."}
}

function Invoke-V2SelfTestMain {
  $tooling=Get-V2Tooling
  $check=Invoke-V2Tool $tooling @('check'); if($check.ExitCode -ne 0){throw "Corpus integrity check failed:`n$($check.Output)"}
  $doc=Get-Content (Join-Path $tooling.CorpusRoot '..\parux-corpus-v2.json') -Raw|ConvertFrom-Json
  $picked=@(); foreach($lang in @('python','javascript','typescript','csharp')){$t=@($doc.tasks|Where-Object{$_.language -eq $lang -and $_.kind -eq 'bugfix'}|Select-Object -First 1); if($t.Count -gt 0){$picked+=$t}}
  $out=Join-Path ([IO.Path]::GetTempPath()) ("parux-v2-selftest-"+[Guid]::NewGuid().ToString('N')); New-Item -ItemType Directory -Force -Path $out|Out-Null
  try {
    $oracle=Parse-Spec 'oracle=@oracle|-'; $noop=Parse-Spec 'noop=@noop|-'; $ran=0
    foreach($task in $picked){
      $o=Invoke-V2CliCase $tooling $oracle $task $out; $n=Invoke-V2CliCase $tooling $noop $task $out
      if($o.status -eq 'SKIPPED_TOOLCHAIN'){Write-Host "SKIPPED (toolchain missing): $($task.id)";continue}
      if($o.status -ne 'PASS'){throw "Oracle (reference) did not pass hidden verifier for $($task.id): $($o.verifierOutputTail)"}
      if($n.status -eq 'PASS'){throw "No-op agent passed the hidden verifier for $($task.id): the baseline must fail."}
      $ran++
    }
    if($ran -eq 0){throw 'V2 self-test could not run any task (no toolchains available).'}
    # self-test adapters must never be able to produce a claim, even with perfect rows.
    $rows=New-Object System.Collections.Generic.List[object]; foreach($task in @($doc.tasks|Where-Object{$_.split -eq 'held-out'})){$rows.Add([pscustomobject]@{provider='PaRuX';caseId=$task.id;split='held-out';language=$task.language;kind=$task.kind;success=$true;status='PASS';wallTimeMs=1.0;steps=1;interventions=0;measurement='self-test-oracle'})}
    foreach($task in @($doc.tasks|Where-Object{$_.split -eq 'held-out'})){$rows.Add([pscustomobject]@{provider='Rival';caseId=$task.id;split='held-out';language=$task.language;kind=$task.kind;success=$false;status='FAIL';wallTimeMs=1.0;steps=1;interventions=0;measurement='self-test-oracle'})}
    $script:Split='held-out'; $s=Get-V2Summary $doc @($doc.tasks|Where-Object{$_.split -eq 'held-out'}) $rows $null 'selftest' 'selftest'
    if($s.paRuXCompetitiveVerdict -eq 'WIN'){throw 'Self-test adapter rows must never yield a competitive WIN.'}
    $script:Split='optimization'; $s2=Get-V2Summary $doc @($doc.tasks|Where-Object{$_.split -eq 'optimization'}) $rows $null 'selftest' 'selftest'
    if($s2.paRuXCompetitiveVerdict -ne 'NOT_A_CLAIM'){throw 'The optimization split must never produce a competitive claim.'}
    Write-Host "BIC-03 corpus v2 runner self-test: PASS ($ran language(s) executed oracle PASS / no-op FAIL)"
  } finally { Remove-Item $out -Recurse -Force -ErrorAction SilentlyContinue }
}

if($ScopeAuditSelfTest){Invoke-ScopeAuditSelfTest;return}
if($VerdictSelfTest){Invoke-VerdictSelfTest;return}
if($V2SelfTest){Invoke-V2SelfTestMain;return}
if(-not [string]::IsNullOrWhiteSpace($TaskCorpusV2)){Invoke-V2Mode;return}

$Workspace=Resolve-RepoRoot $Workspace
$sourceCommit=(& git -C $Workspace rev-parse 'HEAD^{commit}').Trim(); if(-not $sourceCommit){throw 'Could not resolve immutable source commit.'}
$corpusPath=[IO.Path]::GetFullPath((Resolve-Path $Corpus).Path); $harnessPath=[IO.Path]::GetFullPath($PSCommandPath)
$integrity=[pscustomobject]@{CorpusPath=$corpusPath;CorpusSha256=Get-FileSha256 $corpusPath;HarnessPath=$harnessPath;HarnessSha256=Get-FileSha256 $harnessPath}
$corpusDocument=Get-Content $corpusPath -Raw|ConvertFrom-Json; $tasks=@($corpusDocument.tasks)
if($tasks.Count -eq 0){throw 'Benchmark corpus contains no tasks.'}; if([int]$corpusDocument.schemaVersion -lt 4){throw 'Competitive benchmark requires corpus schemaVersion 4+ with evaluationPolicy and independent verification commands.'}
if($SkipPaRuX -and $CompetitorSpecs.Count -eq 0){throw 'No providers selected.'}
$specs=@($CompetitorSpecs|ForEach-Object{Parse-Spec $_}); if(@($specs|ForEach-Object{$_.Name}|Select-Object -Unique).Count -ne $specs.Count){throw 'Competitor names must be unique.'}

$policy=$corpusDocument.evaluationPolicy; if($null -eq $policy){throw 'Corpus evaluationPolicy is required.'}
$successFloor=[double]$policy.absoluteSuccessFloorPercent; if($successFloor -le 0 -or $successFloor -gt 100){throw 'evaluationPolicy.absoluteSuccessFloorPercent must be in (0,100].'}
$mandatoryIds=@($policy.mandatoryCaseIds|ForEach-Object{[string]$_}); foreach($id in $mandatoryIds){if($id -notin @($tasks.id)){throw "Mandatory case '$id' does not exist in corpus."}}
$representativeCorpus=($tasks.Count -ge 30 -and $tasks.Count -le 50 -and [bool]$policy.heldOutForOptimization)
$verifierMaterial=@($tasks|ForEach-Object{[pscustomobject]@{id=$_.id;acceptance=$_.acceptance}})|ConvertTo-Json -Depth 20 -Compress; $verifierSha256=Get-TextSha256 $verifierMaterial

$metadataDocument=$null
if(-not [string]::IsNullOrWhiteSpace($ProviderMetadataJson)){$metadataDocument=Get-Content ([IO.Path]::GetFullPath((Resolve-Path $ProviderMetadataJson).Path)) -Raw|ConvertFrom-Json}

New-Item -ItemType Directory -Force -Path $OutputDirectory|Out-Null; $started=[DateTime]::UtcNow; $rows=New-Object System.Collections.Generic.List[object]
Assert-ImmutableInputs $integrity

if(-not $SkipPaRuX){
  $paruxDir=Join-Path $OutputDirectory 'parux'; New-Item -ItemType Directory -Force -Path $paruxDir|Out-Null
  & (Join-Path $PSScriptRoot 'benchmark-coding-agent.ps1') -Corpus $corpusPath -Workspace $Workspace -OutputDirectory $paruxDir -TimeoutMinutes $TimeoutMinutesPerCase
  $latest=Get-ChildItem $paruxDir -Filter 'coding-agent-*.json'|Sort-Object LastWriteTimeUtc -Descending|Select-Object -First 1; if($null -eq $latest){throw 'PaRuX benchmark did not produce JSON evidence.'}
  $evidence=Get-Content $latest.FullName -Raw|ConvertFrom-Json; $bootstrap=Get-PaRuXBootstrap
  foreach($oldRow in @($evidence.results)){
    Assert-ImmutableInputs $integrity
    $case=@($tasks|Where-Object{$_.id -eq $oldRow.caseId})|Select-Object -First 1; if($null -eq $case){throw "PaRuX result references unknown case $($oldRow.caseId)."}
    $worktree=Get-PaRuXWorktree $bootstrap ([string]$oldRow.taskId); $acceptance=Test-Acceptance $case $worktree 0 $sourceCommit
    $rows.Add([pscustomobject]@{provider='PaRuX';caseId=[string]$oldRow.caseId;success=([bool]$oldRow.orchestratorCompleted-and[bool]$acceptance.Passed);timeout=$false;exitCode=0;wallTimeMs=[double]$oldRow.wallTimeMs;initialCommit=$acceptance.InitialCommit;finalCommit=$acceptance.FinalCommit;acceptanceFailures=@($acceptance.Failures);changedPaths=@($acceptance.ChangedPaths);verification=@($acceptance.Verification.Records);outputTail=[string]$oldRow.error})
  }
}

foreach($spec in $specs){Write-Host "== $($spec.Name) ==" -ForegroundColor Cyan;foreach($case in $tasks){Write-Host "[$($case.id)] running isolated worktree";$rows.Add((Invoke-ExternalCase $spec $case $Workspace $sourceCommit $integrity))}}
Assert-ImmutableInputs $integrity

$providers=@($rows.provider|Select-Object -Unique); $providerSummary=@()
foreach($provider in $providers){
  $sample=@($rows|Where-Object{$_.provider -eq $provider}); $ok=@($sample|Where-Object{$_.success}); $mandatoryFailures=@($mandatoryIds|Where-Object{$id=$_; -not @($ok.caseId).Contains($id)})
  $metadata=Get-ProviderMetadataEntry $metadataDocument $provider; $metadataComplete=Test-ProviderMetadataComplete $metadata
  $providerSummary+=[pscustomobject]@{
    provider=$provider; tasks=$sample.Count; successes=$ok.Count; successRatePercent=if($sample.Count){[Math]::Round(100.0*$ok.Count/$sample.Count,1)}else{0};
    medianSuccessfulWallTimeMs=Get-Median @($ok|ForEach-Object{[double]$_.wallTimeMs}); mandatoryPassed=($mandatoryFailures.Count -eq 0); mandatoryFailures=$mandatoryFailures;
    metadataComplete=$metadataComplete; metadata=$metadata
  }
}

$budgetFingerprints=@()
$metadataComparable=$true
foreach($summaryRow in $providerSummary){
  if(-not $summaryRow.metadataComplete){$metadataComparable=$false;continue}
  if([int]$summaryRow.metadata.interventionCount -ne 0){$metadataComparable=$false}
  $budgetFingerprints+=([string]$summaryRow.metadata.reasoningBudgetClass+'|'+[string]$summaryRow.metadata.outputBudget+'|'+[string]$summaryRow.metadata.permissionMode)
}
if(@($budgetFingerprints|Select-Object -Unique).Count -gt 1){$metadataComparable=$false}

$paruxSummary=$providerSummary|Where-Object provider -eq 'PaRuX'|Select-Object -First 1; $pairwise=@(); $overallVerdict='INCONCLUSIVE'; $strictWinSeen=$false; $lossSeen=$false; $inconclusiveSeen=$false
if($null -ne $paruxSummary){
  foreach($otherSummary in @($providerSummary|Where-Object provider -ne 'PaRuX')){
    $pRows=@($rows|Where-Object{$_.provider -eq 'PaRuX'}); $oRows=@($rows|Where-Object{$_.provider -eq $otherSummary.provider})
    $pEligible=($paruxSummary.successRatePercent -ge $successFloor -and $paruxSummary.mandatoryPassed); $oEligible=($otherSummary.successRatePercent -ge $successFloor -and $otherSummary.mandatoryPassed)
    $comparison=Get-PairwiseVerdict $pRows $oRows $pEligible $oEligible; $verdict=$comparison.Verdict
    if($verdict -eq 'WIN'){$strictWinSeen=$true}; if($verdict -eq 'LOSS'){$lossSeen=$true}; if($verdict -eq 'INCONCLUSIVE'){$inconclusiveSeen=$true}
    $pairwise+=[pscustomobject]@{competitor=$otherSummary.provider;verdict=$verdict;verdictReason=$comparison.Reason;successSetsIdentical=$comparison.SuccessSetsIdentical;commonSuccessfulCases=$comparison.CommonSuccessfulCases;paRuXCommonMedianMs=$comparison.PaRuXCommonMedianMs;competitorCommonMedianMs=$comparison.CompetitorCommonMedianMs;paRuXOnlySuccesses=$comparison.PaRuXOnlySuccesses;competitorOnlySuccesses=$comparison.CompetitorOnlySuccesses}
  }

  if(-not $representativeCorpus -or -not $metadataComparable){$overallVerdict='INCONCLUSIVE'}
  elseif($lossSeen){$overallVerdict='LOSS'}
  elseif($inconclusiveSeen){$overallVerdict='INCONCLUSIVE'}
  elseif($strictWinSeen){$overallVerdict='WIN'}
  else{$overallVerdict='TIE'}
}

$summary=[pscustomobject]@{
  schemaVersion=3; benchmark='same-machine-coding-agent-deterministic-v3'; sourceCommit=$sourceCommit; finalHarnessCommit=(& git -C $Workspace rev-parse 'HEAD^{commit}').Trim(); corpus=[string]$corpusDocument.name;
  corpusSha256=$integrity.CorpusSha256; verifierSha256=$verifierSha256; harnessSha256=$integrity.HarnessSha256; startedUtc=$started.ToString('o'); completedUtc=[DateTime]::UtcNow.ToString('o');
  evaluationPolicy=[pscustomobject]@{absoluteSuccessFloorPercent=$successFloor;mandatoryCaseIds=$mandatoryIds;representativeTaskCount=$representativeCorpus;heldOutForOptimization=[bool]$policy.heldOutForOptimization};
  fairness=[pscustomobject]@{sameSourceCommit=$true;isolatedWorktrees=$true;sameIndependentVerifier=$true;sameMachine=$true;agentSelfReportsIgnoredForAcceptance=$true;immutableCorpusAndHarness=$true;providerMetadataComparable=$metadataComparable;latencyComparedOnlyOnCommonSuccesses=$true};
  providers=$providerSummary; pairwise=$pairwise; paRuXCompetitiveVerdict=$overallVerdict; paRuXBestInThisRun=($overallVerdict -eq 'WIN'); results=$rows.ToArray()
}
$stamp=Get-Date -Format 'yyyyMMdd-HHmmss'; $json=Join-Path $OutputDirectory "coding-agent-competitive-$stamp.json"; $csv=Join-Path $OutputDirectory "coding-agent-competitive-$stamp.csv"
$summary|ConvertTo-Json -Depth 30|Set-Content $json -Encoding UTF8; $rows|Select-Object provider,caseId,success,timeout,exitCode,wallTimeMs,initialCommit,finalCommit,@{n='failures';e={[string]::Join('; ',@($_.acceptanceFailures))}}|Export-Csv $csv -NoTypeInformation -Encoding UTF8
Write-Host ''; $providerSummary|Format-Table -AutoSize|Out-Host; $pairwise|Format-Table -AutoSize|Out-Host; Write-Host "Verdict: $overallVerdict"; Write-Host "JSON: $json"; Write-Host "CSV : $csv"
if($FailIfPaRuXLoses -and $overallVerdict -ne 'WIN'){throw "Competitive coding-agent gate did not produce a valid PaRuX WIN (verdict: $overallVerdict). Ties, failed fairness checks and inconclusive evidence are not best-in-class wins."}
