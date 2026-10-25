[CmdletBinding()]
param(
    [switch]$IncludeAndroid,
    [switch]$IncludeUiSmoke,
    [switch]$SkipRestore
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Push-Location $repo
try {
    Write-Host '== PaRuX software-complete validation ==' -ForegroundColor Cyan
    Write-Host ('Repository: ' + $repo)

    # Ask the runtime, not the environment block. $env:OS is an inherited variable: a shell
    # started with a sanitized environment (a service, a task host, an automation harness) is
    # still Windows but has no OS variable, and the gate then refuses to run on the very machine
    # it is meant to run on. OSVersion.Platform is answered by the running .NET itself and works
    # identically on Windows PowerShell 5.1 and PowerShell 7.
    if ([System.Environment]::OSVersion.Platform -ne [System.PlatformID]::Win32NT) {
        throw 'The authoritative PaRuX software-complete gate must run on Windows. Use the development PC; do not substitute GitHub Actions.'
    }
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        throw 'dotnet SDK is required for the local software-complete gate.'
    }
    if (-not (Test-Path 'PaRuX.sln' -PathType Leaf)) {
        throw 'Canonical solution PaRuX.sln is missing.'
    }

    # The dated closeout is historical evidence. Its source-complete marker and its
    # reconciliation note intentionally mention PARTIAL, so scanning the whole document
    # for a legacy status (as the old gate did) makes the authoritative gate reject the
    # current, truthful plan before any software validation runs.
    $planPath = 'PARUX_EXECUTION_PLAN.md'
    if (-not (Test-Path $planPath -PathType Leaf)) {
        throw 'Canonical execution plan is missing.'
    }
    $plan = Get-Content $planPath -Raw -Encoding UTF8
    $statusMatch = [regex]::Match($plan, '(?ms)^## Current status\s+(?<body>.*?)(?=^## )')
    if (-not $statusMatch.Success) {
        throw 'Canonical execution plan has no parseable Current status section.'
    }
    $currentStatus = $statusMatch.Groups['body'].Value
    $currentStatusLine = @($currentStatus -split "`r?`n" | Where-Object { $_.Trim().Length -gt 0 })[0]
    if ($currentStatusLine -notmatch '\bPARTIAL\b' -or
        $currentStatusLine -notmatch 'PHYSICAL_PC_ACCEPTANCE_REQUIRED' -or
        $currentStatusLine -match 'SOURCE_SOFTWARE_COMPLETE|REFERENCE_VERIFIED') {
        throw 'Canonical execution plan has an invalid active software/physical-PC status.'
    }

    $backlogMatch = [regex]::Match($plan, '(?ms)^## Active competitive-gap backlog — 2026-09-10\s+(?<body>.*?)(?=^## |\z)')
    if (-not $backlogMatch.Success) {
        throw 'Canonical active competitive-gap backlog is missing.'
    }
    $activeBacklog = $backlogMatch.Groups['body'].Value
    $bicRows = [regex]::Matches($activeBacklog, '(?m)^\|\s*(?<id>BIC-\d{2})\s*\|\s*(?<priority>P[012])\s*\|\s*(?<status>DONE|PARTIAL|MISSING|BLOCKED-EXTERNAL)\s*\|')
    $expectedBicIds = @(1..17 | ForEach-Object { 'BIC-' + $_.ToString('00') })
    $actualBicIds = @($bicRows | ForEach-Object { $_.Groups['id'].Value })
    $uniqueBicIds = @($actualBicIds | Sort-Object -Unique)
    if ($bicRows.Count -ne $expectedBicIds.Count -or
        $uniqueBicIds.Count -ne $expectedBicIds.Count -or
        (($uniqueBicIds -join ',') -ne ($expectedBicIds -join ','))) {
        throw 'Canonical backlog must contain exactly one well-formed table row for every BIC-01 through BIC-17, with a valid priority and lifecycle status.'
    }
    $bic16 = @($bicRows | Where-Object { $_.Groups['id'].Value -eq 'BIC-16' })[0]
    if ($bic16.Groups['priority'].Value -ne 'P0') {
        throw 'BIC-16 must retain its canonical P0 priority.'
    }
    $openBic = @($bicRows | Where-Object { $_.Groups['status'].Value -ne 'DONE' }).Count
    if ($openBic -gt 0) {
        throw ('Cannot claim software-complete while ' + $openBic + ' active BIC row(s) remain open.')
    }

    $closeoutPath = 'docs/PARUX_MAIN_SOFTWARE_CLOSEOUT_2026-09-05.md'
    if (-not (Test-Path $closeoutPath -PathType Leaf)) {
        throw 'Historical main software closeout document is missing.'
    }
    $closeout = Get-Content $closeoutPath -Raw
    if ($closeout -notmatch '(?i)historical' -or
        $closeout -notmatch 'SOURCE_SOFTWARE_COMPLETE' -or
        $closeout -notmatch 'PHYSICAL_PC_ACCEPTANCE_REQUIRED') {
        throw 'Historical main closeout no longer carries its dated source/physical-PC evidence.'
    }
    if ($closeout -match 'PHYSICAL_ANDROID_TEST_REQUIRED|PHYSICAL_PHONE_ACCEPTANCE_REQUIRED|PHYSICAL_DEVICE_ACCEPTANCE_REQUIRED') {
        throw 'Historical main closeout reintroduced a physical-phone/device release blocker. Only the target Windows PC may remain blocking.'
    }

    $validationRoot = Join-Path $repo 'artifacts\validation\software-complete'
    if (Test-Path $validationRoot) { Remove-Item $validationRoot -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $validationRoot | Out-Null
    $appPublish = Join-Path $validationRoot 'app-win-x64'
    $cliPublish = Join-Path $validationRoot 'cli-win-x64'
    $corePublish = Join-Path $validationRoot 'core-win-x64'
    $updateHelperPublish = Join-Path $validationRoot 'update-helper-win-x64'
    $winMlPublish = Join-Path $validationRoot 'winml-win-x64'
    $testResults = Join-Path $validationRoot 'test-results'
    New-Item -ItemType Directory -Force -Path $testResults | Out-Null

    if (-not $SkipRestore) {
        dotnet restore PaRuX.sln
        if ($LASTEXITCODE -ne 0) { throw 'dotnet restore PaRuX.sln failed.' }

        # Self-contained RID publishes need runtime-specific assets; restore them explicitly rather
        # than assuming a solution restore happened to populate the win-x64 target graph.
        dotnet restore src/PaRuX.App/PaRuX.App.csproj -r win-x64 -p:Platform=x64
        if ($LASTEXITCODE -ne 0) { throw 'Desktop win-x64 restore failed.' }
        dotnet restore src/PaRuX.Cli/PaRuX.Cli.csproj -r win-x64
        if ($LASTEXITCODE -ne 0) { throw 'CLI win-x64 restore failed.' }
        dotnet restore src/PaRuX.Server/PaRuX.Server.csproj -r win-x64
        if ($LASTEXITCODE -ne 0) { throw 'Core win-x64 restore failed.' }
        dotnet restore src/PaRuX.UpdateHelper/PaRuX.UpdateHelper.csproj -r win-x64
        if ($LASTEXITCODE -ne 0) { throw 'UpdateHelper win-x64 restore failed.' }
        dotnet restore src/PaRuX.WinMlWorker/PaRuX.WinMlWorker.csproj -r win-x64
        if ($LASTEXITCODE -ne 0) { throw 'WinML worker win-x64 restore failed.' }
        dotnet restore tools/PaRuX.ReleaseTool/PaRuX.ReleaseTool.csproj
        if ($LASTEXITCODE -ne 0) { throw 'ReleaseTool restore failed.' }
    }

    # The canonical solution contains App, Core, Contracts, Intelligence, tests, CLI, UpdateHelper
    # and WinML worker. Force its only supported release platform explicitly.
    dotnet build PaRuX.sln -c Release -p:Platform=x64 --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Release x64 solution build failed.' }

    # Do not rely only on the solution-build test assembly: test the project directly so newly-added
    # linked App policy sources and server tests are compiled in exactly the test project's context.
    # Keep console output minimal: normal verbosity emits tens of thousands of ASP.NET request-log
    # lines and can perturb the wall-clock-sensitive real overhead checks on a busy physical PC.
    # The TRX retains complete failure diagnostics without changing any test threshold or coverage.
    dotnet test tests/PaRuX.Server.Tests/PaRuX.Server.Tests.csproj -c Release --no-restore `
        --logger 'console;verbosity=minimal' `
        --logger 'trx;LogFileName=server-tests.trx' `
        --results-directory $testResults
    if ($LASTEXITCODE -ne 0) { throw 'Server test suite failed.' }

    if ($IncludeUiSmoke) {
        # UIA requires an unlocked, interactive Windows desktop and is intentionally opt-in.
        & (Join-Path $repo 'tools\test-ui.ps1') -Suite Smoke
        if ($LASTEXITCODE -ne 0) { throw 'FlaUI smoke suite failed.' }
    }

    # Publish every Windows executable harvested or shipped by the installer. This closes the gap
    # where Core/CLI could publish while Desktop/UpdateHelper/WinML failed only at packaging time.
    dotnet publish src/PaRuX.App/PaRuX.App.csproj -c Release -r win-x64 --self-contained true --no-restore -p:Platform=x64 -o $appPublish
    if ($LASTEXITCODE -ne 0) { throw 'Self-contained PaRuX Desktop publish failed.' }
    if (-not (Test-Path (Join-Path $appPublish 'PaRuX.exe') -PathType Leaf)) {
        throw 'Desktop publish completed without PaRuX.exe.'
    }

    dotnet publish src/PaRuX.Cli/PaRuX.Cli.csproj -c Release -r win-x64 --self-contained true --no-restore -o $cliPublish
    if ($LASTEXITCODE -ne 0) { throw 'Self-contained PaRuX CLI publish failed.' }
    if (-not (Test-Path (Join-Path $cliPublish 'parux.exe') -PathType Leaf)) {
        throw 'CLI publish completed without parux.exe.'
    }

    dotnet publish src/PaRuX.Server/PaRuX.Server.csproj -c Release -r win-x64 --self-contained true --no-restore -o $corePublish
    if ($LASTEXITCODE -ne 0) { throw 'Self-contained PaRuX Core publish failed.' }
    if (-not (Test-Path (Join-Path $corePublish 'PaRuX.Server.exe') -PathType Leaf)) {
        throw 'Core publish completed without PaRuX.Server.exe.'
    }

    dotnet publish src/PaRuX.UpdateHelper/PaRuX.UpdateHelper.csproj -c Release -r win-x64 --self-contained true --no-restore -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $updateHelperPublish
    if ($LASTEXITCODE -ne 0) { throw 'Self-contained PaRuX UpdateHelper publish failed.' }
    if (-not (Test-Path (Join-Path $updateHelperPublish 'PaRuX.UpdateHelper.exe') -PathType Leaf)) {
        throw 'UpdateHelper publish completed without PaRuX.UpdateHelper.exe.'
    }

    dotnet publish src/PaRuX.WinMlWorker/PaRuX.WinMlWorker.csproj -c Release -r win-x64 --self-contained true --no-restore -o $winMlPublish
    if ($LASTEXITCODE -ne 0) { throw 'Self-contained PaRuX WinML worker publish failed.' }
    if (-not (Test-Path (Join-Path $winMlPublish 'PaRuX.WinMlWorker.exe') -PathType Leaf)) {
        throw 'WinML worker publish completed without PaRuX.WinMlWorker.exe.'
    }

    dotnet build tools/PaRuX.ReleaseTool/PaRuX.ReleaseTool.csproj -c Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'PaRuX.ReleaseTool build failed.' }

    function Test-ReviewedShellExecuteHit([Microsoft.PowerShell.Commands.MatchInfo]$hit) {
        $lines = @(Get-Content -LiteralPath $hit.Path)
        $index = [Math]::Max(0, $hit.LineNumber - 1)
        $from = [Math]::Max(0, $index - 3)
        $to = [Math]::Min($lines.Count - 1, $index + 3)
        if ($to -ge $from) {
            $context = ($lines[$from..$to] -join "`n")
            if ($context -match 'parux:shell-execute-reviewed') { return $true }
        }

        # CLI `agent open` is the only intentionally compact reviewed form that predates the
        # source marker convention. It opens the user-selected editor against a Core-owned task
        # worktree; the path is passed through ArgumentList.
        $fullPath = [IO.Path]::GetFullPath($hit.Path)
        $repoPrefix = $repo.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
        $relative = if ($fullPath.StartsWith($repoPrefix, [StringComparison]::OrdinalIgnoreCase)) {
            $fullPath.Substring($repoPrefix.Length).Replace('\', '/')
        } else { '' }
        return $relative -eq 'src/PaRuX.Cli/Program.cs' -and
            $hit.Line -match 'FileName\s*=\s*editor\s*,\s*UseShellExecute\s*=\s*true'
    }

    $forbiddenRuntimePatterns = @(
        'ProcessStartInfo\s*\{[^}]*FileName\s*=\s*"python(?:3)?"',
        'FileName\s*=\s*"uv"',
        'UseShellExecute\s*=\s*true'
    )
    $runtimeFiles = Get-ChildItem src -Recurse -File -Include *.cs,*.kt,*.kts,*.py
    foreach ($pattern in $forbiddenRuntimePatterns) {
        $hits = $runtimeFiles | Select-String -Pattern $pattern | Where-Object {
            if ($_.Line -match 'parux:shell-execute-reviewed') { return $false }
            if ($pattern -eq 'UseShellExecute\s*=\s*true' -and (Test-ReviewedShellExecuteHit $_)) { return $false }
            return $true
        }
        if ($hits) {
            $hits | ForEach-Object { Write-Host $_.Path ':' $_.LineNumber $_.Line }
            throw ('Static runtime invariant failed: ' + $pattern)
        }
    }

    # GitHub-hosted builds are intentionally forbidden for this project. Fail closed if a workflow
    # is reintroduced: a warning is too weak because it can burn hosted minutes merely by existing.
    if (Test-Path '.github/workflows') {
        $activeWorkflows = @(Get-ChildItem '.github/workflows' -File -ErrorAction SilentlyContinue |
            Where-Object { $_.Extension -in '.yml', '.yaml' })
        if ($activeWorkflows.Count -gt 0) {
            $activeWorkflows | ForEach-Object { Write-Host ('Forbidden GitHub workflow: ' + $_.FullName) }
            throw 'GitHub Actions workflow files are forbidden. Keep build/test/release validation local.'
        }
    }

    $roadmap = Get-Content PARUX_EXECUTION_PLAN.md -Raw
    if ($roadmap -notmatch 'GitHub Actions remains disabled') {
        throw 'Canonical roadmap no longer states the no-Actions validation rule.'
    }

    # Cross-surface coding-agent invariants.
    $coreEndpoints = Get-Content src/PaRuX.Server/MobileControlEndpoints.cs -Raw
    $taskStore = Get-Content src/PaRuX.Server/Orchestration/TaskStore.cs -Raw
    $batchDelegationStore = Get-Content src/PaRuX.Server/Orchestration/TaskStore.BatchDelegation.cs -Raw
    $manualResumeStore = Get-Content src/PaRuX.Server/Orchestration/TaskStore.ManualResume.cs -Raw
    $codingAgentSource = Get-Content src/PaRuX.App/Services/CodingAgentService.cs -Raw
    $codingDelegationSource = Get-Content src/PaRuX.App/Services/CodingAgentDelegationService.cs -Raw
    $batchDelegationTests = Get-Content tests/PaRuX.Server.Tests/TaskStoreBatchDelegationTests.cs -Raw
    $cliSource = Get-Content src/PaRuX.Cli/Program.cs -Raw
    $pairedTests = Get-Content tests/PaRuX.Server.Tests/PairedDeviceAbuseMiddlewareTests.cs -Raw

    if ($coreEndpoints -notmatch '/api/v1/orchestrator/tasks/\{id\}/resume' -or
        $coreEndpoints -notmatch 'AgentTaskResumeService') {
        throw 'Core-authoritative desktop resume endpoint is missing.'
    }
    if ($coreEndpoints -notmatch 'AgentRepositoryAuthoritySetStore\.Current\.Replace' -or
        $coreEndpoints -notmatch 'AdditionalRepositories') {
        throw 'Atomic multi-repository coding creation boundary is missing.'
    }
    if ($taskStore -notmatch 'missing agent\.delegate' -or
        $taskStore -notmatch 'EnsureRootCodingDelegationCapability') {
        throw 'Core-authoritative bounded coding delegation invariant is missing.'
    }
    if ($coreEndpoints -notmatch '/api/v1/orchestrator/tasks/\{parentId\}/delegate-batch' -or
        $coreEndpoints -notmatch 'PreparedChildTaskIds' -or
        $coreEndpoints -notmatch 'CommitPreparedDelegationBatch' -or
        $coreEndpoints -notmatch 'IsDesktopOwner') {
        throw 'Desktop-only atomic coding delegation endpoint has drifted or is incomplete.'
    }
    if ($batchDelegationStore -notmatch 'DelegatedAuthorityPendingStage' -or
        $batchDelegationStore -notmatch 'PreparedChildTaskIds' -or
        $batchDelegationStore -notmatch 'CommitPreparedDelegationBatch' -or
        $batchDelegationStore -notmatch 'BeginTransaction' -or
        $batchDelegationStore -notmatch 'Waiting for delegated child ') {
        throw 'Coding delegation no longer guarantees recoverable prepare plus atomic parent/child commit semantics.'
    }
    if ($codingAgentSource -notmatch 'case "delegate_tasks"' -or
        $codingAgentSource -notmatch 'DelegateManyAsync' -or
        $codingAgentSource -notmatch 'children must contain between 2 and 6 items') {
        throw 'Coding Agent parallel fan-out action/schema/validation has drifted.'
    }
    if ($codingDelegationSource -notmatch 'DelegateBatchAsync' -or
        $codingDelegationSource -notmatch 'KickSchedulerBestEffortAsync' -or
        $codingDelegationSource -match 'PauseParentAsync') {
        throw 'Coding delegation bridge must use the Core-atomic batch protocol for both single and parallel delegation.'
    }
    if ($batchDelegationTests -notmatch 'Interrupted_prepare_retry_recovers_matching_authority_pending_children' -or
        $batchDelegationTests -notmatch 'Atomic_commit_queues_every_prepared_child_and_pauses_parent_together' -or
        $batchDelegationTests -notmatch 'Commit_preflight_failure_leaves_parent_and_unmodified_peer_uncommitted' -or
        $batchDelegationTests -notmatch 'Single_child_batch_is_supported_for_the_safe_delegate_task_protocol') {
        throw 'Atomic coding delegation recovery/concurrency regression coverage is incomplete.'
    }
    if ($manualResumeStore -notmatch 'status = \$running' -or
        $manualResumeStore -notmatch 'verification_status = NULL' -or
        $manualResumeStore -notmatch 'status = \$expected') {
        throw 'Atomic compare-and-set manual resume invariant is missing.'
    }
    if ($cliSource -notmatch '/api/v1/orchestrator/tasks/\{id\}/resume' -or
        $cliSource -notmatch '--repo') {
        throw 'CLI has drifted from Core-authoritative resume or atomic multi-repository coding.'
    }
    if ($pairedTests -notmatch '/api/v1/orchestrator/tasks/task-1/resume' -or
        $pairedTests -notmatch '/api/v1/orchestrator/tasks/task-1/repositories' -or
        $pairedTests -notmatch '/api/v1/orchestrator/tasks/task-1/delegate-batch') {
        throw 'Paired-device regression coverage for desktop coding authority is missing.'
    }

    # Media Engine Race is a release-level invariant, not a UI convenience.
    $mediaEndpoints = Get-Content src/PaRuX.Server/ManagedMediaV2Endpoints.cs -Raw
    $mediaExtensions = Get-Content src/PaRuX.Server/MediaGenerationRaceExtensions.cs -Raw
    $mediaRaceService = Get-Content src/PaRuX.Server/MediaEngineRaceService.cs -Raw
    $comfyRace = Get-Content src/PaRuX.Server/ManagedComfyImageRaceEngine.cs -Raw
    $diffusersRace = Get-Content src/PaRuX.Server/ManagedDiffusersImageEngine.cs -Raw
    $semanticJudge = Get-Content src/PaRuX.Server/ManagedClipImageJudge.cs -Raw
    $mediaRaceTests = Get-Content tests/PaRuX.Server.Tests/MediaEngineRaceServiceTests.cs -Raw
    $mediaCoordinator = Get-Content src/PaRuX.Server/MediaExecutionCoordinator.cs -Raw
    $textQuiescence = Get-Content src/PaRuX.Server/TextInferenceQuiescence.cs -Raw
    $localRuntime = Get-Content src/PaRuX.Server/LocalRuntimeService.cs -Raw
    $mediaGeneration = Get-Content src/PaRuX.Server/MediaGenerationService.cs -Raw
    $chatScheduling = Get-Content src/PaRuX.Server/ChatSchedulingEndpointExtensions.cs -Raw
    $localApiScheduling = Get-Content src/PaRuX.Server/LocalApiEndpointExtensions.cs -Raw
    $orchestrationCompletion = Get-Content src/PaRuX.Server/Orchestration/LlamaServerCompletionClient.cs -Raw
    $mediaCoordinatorTests = Get-Content tests/PaRuX.Server.Tests/MediaExecutionCoordinatorTests.cs -Raw
    $textQuiescenceTests = Get-Content tests/PaRuX.Server.Tests/TextInferenceQuiescenceTests.cs -Raw
    $mediaBenchmarkPath = 'scripts/benchmark-media-engine-race.ps1'
    $mediaCorpusPath = 'benchmarks/media-engine-race/parux-image-corpus.json'
    if (-not (Test-Path $mediaBenchmarkPath -PathType Leaf) -or -not (Test-Path $mediaCorpusPath -PathType Leaf)) {
        throw 'Reproducible media Engine Race benchmark runner/corpus is missing.'
    }
    $mediaBenchmark = Get-Content $mediaBenchmarkPath -Raw
    try { $mediaCorpus = Get-Content $mediaCorpusPath -Raw | ConvertFrom-Json }
    catch { throw ('Media Engine Race benchmark corpus is invalid JSON: ' + $_.Exception.Message) }

    if ($mediaEndpoints -notmatch 'RunManagedImageRaceAsync\(comfyRace, diffusers, semanticJudge' -or
        $mediaEndpoints -notmatch 'StripPayloads\(race, includeArtifact \?\? true\)' -or
        $mediaEndpoints -notmatch 'bool\? includeArtifact') {
        throw 'Production media endpoint has drifted from the real two-engine compact/evidence-only race path.'
    }
    if ($mediaExtensions -notmatch 'ManagedComfyImageRaceEngine comfyRace' -or
        $mediaExtensions -notmatch 'EstablishColdBoundaryAsync' -or
        $mediaExtensions -notmatch 'challengePrompt = request\.Prompt\.Trim\(\)' -or
        $mediaExtensions -notmatch '\(seed & 1L\)') {
        throw 'Media Engine Race exact-prompt/cold-boundary/order-balancing wiring is incomplete.'
    }
    if ($mediaRaceService -notmatch 'Phase 1: render every candidate' -or
        $mediaRaceService -notmatch 'Phase 2: judge only after every renderer' -or
        $mediaRaceService -notmatch 'QualityTieTolerance = 0\.01' -or
        $mediaRaceService -notmatch 'qualityEquivalent' -or
        $mediaRaceService -notmatch 'quality-evaluation-failed') {
        throw 'Media Engine Race phase separation, quality-equivalence or failure semantics are incomplete.'
    }
    if ($comfyRace -notmatch 'SamplerSteps = 8' -or
        $comfyRace -notmatch 'EstablishColdBoundaryAsync' -or
        $comfyRace -notmatch 'PrepareForExclusiveMediaAsync' -or
        $comfyRace -notmatch 'EnsureRendererOnlyReadyAsync' -or
        $comfyRace -match 'StartMediaAsync\(' -or
        $comfyRace -notmatch 'StopMedia\(\)' -or
        $comfyRace -notmatch 'StopText\(\)') {
        throw 'Dedicated cold ComfyUI renderer lifecycle has drifted or reintroduced media-assistant startup.'
    }
    if ($diffusersRace -notmatch 'Tongyi-MAI/Z-Image-Turbo' -or
        $diffusersRace -notmatch 'guidance_scale=0\.0' -or
        $diffusersRace -notmatch 'max_sequence_length=1024' -or
        $diffusersRace -notmatch 'local_files_only=True') {
        throw 'Pinned/offline Diffusers Z-Image-Turbo race semantics have drifted.'
    }
    if ($semanticJudge -notmatch 'JudgeVersion = "3"' -or
        $semanticJudge -notmatch 'openai/clip-vit-base-patch32' -or
        $semanticJudge -notmatch 'local_files_only=True' -or
        $semanticJudge -notmatch '\.to\("cpu"\)' -or
        $semanticJudge -notmatch 'def split_prompt' -or
        $semanticJudge -notmatch 'weakest_cosine' -or
        $semanticJudge -notmatch '0\.75 \* mean_cosine \+ 0\.25 \* weakest_cosine') {
        throw 'Pinned offline CPU semantic judge has lost long-prompt chunk coverage or aggregate-quality semantics.'
    }
    if ($mediaRaceTests -notmatch 'Every_renderer_finishes_before_any_quality_judge_runs' -or
        $mediaRaceTests -notmatch 'Effectively_equal_quality_uses_cold_latency_instead_of_score_noise' -or
        $mediaRaceTests -notmatch 'Production_image_race_overload_and_pinned_components_exist' -or
        $mediaRaceTests -notmatch 'Benchmark_api_payload_removes_all_base64_artifacts') {
        throw 'Media Engine Race regression coverage is incomplete.'
    }

    # GPU scheduling is a cross-cutting correctness invariant: text readers may batch concurrently,
    # queued media has writer priority, and no media runtime may stop a text engine until admitted
    # inference has drained. Keep both PaRuX transport leases and the defensive llama.cpp slot probe.
    if ($mediaCoordinator -notmatch 'AcquireTextAsync' -or
        $mediaCoordinator -notmatch '_waitingMedia' -or
        $mediaCoordinator -notmatch '_activeText' -or
        $mediaCoordinator -notmatch '_activeMediaOwner == null && _waitingMedia == 0') {
        throw 'Writer-priority text/media GPU coordinator semantics are missing.'
    }
    if ($localRuntime -notmatch 'LLAMA_ARG_ENDPOINT_SLOTS' -or
        $localRuntime -notmatch 'AcquireTextInferenceAsync' -or
        $localRuntime -notmatch 'PrepareForExclusiveMediaAsync' -or
        $localRuntime -notmatch 'TextInferenceQuiescence\.WaitForIdleAsync') {
        throw 'Managed runtime no longer pins slot monitoring or drains text before exclusive media takeover.'
    }
    if ($textQuiescence -notmatch '/slots' -or
        $textQuiescence -notmatch '/v1/models' -or
        $textQuiescence -notmatch 'is_processing' -or
        $textQuiescence -notmatch 'HotModelPoolCatalog\.IsActive' -or
        $textQuiescence -notmatch 'IsPortListening') {
        throw 'llama.cpp standalone/router quiescence verification has drifted from fail-closed behavior.'
    }
    if ($mediaGeneration -notmatch 'AcquireAsync\("media:"' -or
        $mediaGeneration -notmatch 'PrepareForExclusiveMediaAsync') {
        throw 'Normal media generation no longer owns and drains the process-wide exclusive GPU writer.'
    }
    if ($chatScheduling -notmatch 'AcquireTextInferenceAsync\("chat:interactive"' -or
        $localApiScheduling -notmatch 'AcquireTextInferenceAsync\("local-api:chat-completions"' -or
        $orchestrationCompletion -notmatch 'AcquireTextInferenceAsync\("orchestration:completion"') {
        throw 'A primary text-serving surface lost its shared inference lease and can be truncated by media takeover.'
    }
    if ($mediaCoordinatorTests -notmatch 'Waiting_media_writer_blocks_later_reader' -or
        $mediaCoordinatorTests -notmatch 'Cancelled_waiting_writer_releases_priority' -or
        $textQuiescenceTests -notmatch 'Missing_processing_state_fails_closed' -or
        $textQuiescenceTests -notmatch 'Any_processing_slot_keeps_media_takeover_blocked') {
        throw 'GPU scheduler/quiescence regression coverage is incomplete.'
    }

    if ([int]$mediaCorpus.schemaVersion -ne 1 -or @($mediaCorpus.cases).Count -lt 10 -or
        [int]$mediaCorpus.methodology.publicationMinimumRunsPerCase -lt 3) {
        throw 'Media Engine Race corpus no longer meets the minimum deterministic publication protocol.'
    }
    if ($mediaBenchmark -notmatch 'includeArtifact=false' -or
        $mediaBenchmark -notmatch 'p95ColdRenderMs' -or
        $mediaBenchmark -notmatch 'judgeFailures' -or
        $mediaBenchmark -notmatch 'RequirePublicationProtocol') {
        throw 'Media Engine Race benchmark runner has lost evidence-only, latency, reliability or publication-gate semantics.'
    }

    # The VS Code integration is dependency-free at runtime (VS Code provides Node).
    $vsCodeRoot = Join-Path $repo 'integrations/vscode'
    $vsCodeRequired = @('package.json', 'main.js', 'extension.js', 'hunk-review.js', 'core-client.js', 'README.md', 'install-local.ps1')
    foreach ($name in $vsCodeRequired) {
        if (-not (Test-Path (Join-Path $vsCodeRoot $name))) {
            throw ('VS Code integration is incomplete: missing integrations/vscode/' + $name)
        }
    }
    try {
        $manifest = Get-Content (Join-Path $vsCodeRoot 'package.json') -Raw | ConvertFrom-Json
        # main.js is the composition root: it activates the original extension.js entry point and
        # then layers the selected-hunk Accept/Reject commands from hunk-review.js on top. Requiring
        # package.json's declared entry to be exactly extension.js is stale now that this composition
        # exists - assert the declared entry file is real and itself wires up the native extension.
        if ($manifest.name -ne 'parux-agent') {
            throw 'VS Code integration manifest does not identify the native PaRuX Agent extension.'
        }
        $mainRelative = [string]$manifest.main
        if ([string]::IsNullOrWhiteSpace($mainRelative) -or -not (Test-Path (Join-Path $vsCodeRoot $mainRelative) -PathType Leaf)) {
            throw ('VS Code integration manifest "main" entry does not resolve to a real file: ' + $mainRelative)
        }
        $mainEntrySource = Get-Content (Join-Path $vsCodeRoot $mainRelative) -Raw
        if ($mainEntrySource -notmatch "require\(['""]\./extension['""]\)" -and $mainRelative -ne './extension.js') {
            throw 'VS Code integration manifest entry point has drifted from the native PaRuX Agent extension.'
        }
    }
    catch {
        throw ('VS Code extension manifest validation failed: ' + $_.Exception.Message)
    }

    $vsCodeClient = Get-Content (Join-Path $vsCodeRoot 'core-client.js') -Raw
    if ($vsCodeClient -notmatch '/resume' -or $vsCodeClient -notmatch 'replaceRepositorySet') {
        throw 'VS Code client has drifted from Core-authoritative resume or atomic repository-set APIs.'
    }
    if ($vsCodeClient -match 'resumeTask[\s\S]{0,400}/status') {
        throw 'VS Code resume must never fall back to the generic /status mutation.'
    }

    if (Get-Command node -ErrorAction SilentlyContinue) {
        node --test integrations/vscode/test/*.test.js
        if ($LASTEXITCODE -ne 0) { throw 'VS Code Core-client tests failed.' }
    }
    else {
        Write-Warning 'Node executable not found; VS Code JS test sub-gate was not rerun on this PC. The same suite is independently runnable with `node --test integrations/vscode/test/*.test.js`.'
    }

    if ($IncludeAndroid) {
        $gradlew = if (Test-Path 'android/gradlew.bat') { 'android/gradlew.bat' }
            elseif (Test-Path 'src/PaRuX.Android/gradlew.bat') { 'src/PaRuX.Android/gradlew.bat' }
            else { $null }
        if (-not $gradlew) { throw 'Android validation requested but gradlew.bat was not found.' }
        $gradleDir = Split-Path $gradlew -Parent
        Push-Location $gradleDir
        try {
            & .\gradlew.bat --no-daemon clean test lint assembleDebug assembleRelease
            if ($LASTEXITCODE -ne 0) { throw 'Android test/lint/debug+release assemble validation failed.' }
        }
        finally { Pop-Location }
    }

    Write-Host ''
    Write-Host 'SOFTWARE-COMPLETE GATE PASSED.' -ForegroundColor Green
    Write-Host ('Validation artifacts: ' + $validationRoot)
    Write-Host 'Source/software closeout is complete. The sole blocking acceptance is the target Windows-PC procedure: MSI/WinUI/OS integration plus live GPU/NPU/runtime/media/training/stress and same-machine competitive evidence. Physical Android hardware is optional QA, and code signing is a separate publication credential.'
}
finally {
    Pop-Location
}
