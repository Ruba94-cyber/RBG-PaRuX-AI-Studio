param(
    [switch]$Restore,
    [switch]$SkipWindowsAppBuild,
    [switch]$SkipVsCodeTests,
    [switch]$SkipAndroidTests,
    [switch]$SkipReleaseAssetHarness
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root
$isWindowsHost = [System.Environment]::OSVersion.Platform -eq [System.PlatformID]::Win32NT
# Self-test seam only: PARUX_RELEASE_GATE_ASSUME_NON_WINDOWS=1 takes the non-Windows branches below so
# tests/PaRuX.Server.Tests can exercise them on a Windows host. Each such branch fails unless its explicit
# -Skip* diagnostic switch is also passed, so the seam by itself can never produce a pass.
if ($env:PARUX_RELEASE_GATE_ASSUME_NON_WINDOWS -eq '1') { $isWindowsHost = $false }

function Write-Gate([string]$text) { Write-Host "[PaRuX gate] $text" }
function Fail-Gate([string]$text) { throw "PaRuX release gate failed: $text" }

Write-Gate "repository: $root"

$workflowRoot = Join-Path $root '.github\workflows'
if (Test-Path $workflowRoot) {
    $workflowFiles = @(Get-ChildItem $workflowRoot -File -Recurse -ErrorAction SilentlyContinue)
    if ($workflowFiles.Count -gt 0) {
        $workflowNames = ($workflowFiles | ForEach-Object { $_.FullName.Substring($root.Length + 1) }) -join ', '
        Fail-Gate ("GitHub Actions are disabled by project policy, but workflow file(s) exist: " + $workflowNames)
    }
}
Write-Gate "GitHub Actions policy: PASS (no workflow files)"

$planPath = Join-Path $root 'PARUX_EXECUTION_PLAN.md'
if (-not (Test-Path $planPath -PathType Leaf)) {
    Fail-Gate 'Canonical PARUX_EXECUTION_PLAN.md is missing.'
}
$plan = Get-Content $planPath -Raw -Encoding UTF8
$statusMatch = [regex]::Match($plan, '(?ms)^## Current status\s+(?<body>.*?)(?=^## )')
if (-not $statusMatch.Success) {
    Fail-Gate 'Canonical plan has no parseable Current status section.'
}
$currentStatus = $statusMatch.Groups['body'].Value
$currentStatusLine = @($currentStatus -split "`r?`n" | Where-Object { $_.Trim().Length -gt 0 })[0]
if ($currentStatusLine -notmatch 'PHYSICAL_PC_ACCEPTANCE_REQUIRED') {
    Fail-Gate 'Canonical Current status no longer preserves required physical Windows-PC acceptance.'
}
$backlogMatch = [regex]::Match($plan, '(?ms)^## Active competitive-gap backlog — 2026-09-10\s+(?<body>.*?)(?=^## |\z)')
if (-not $backlogMatch.Success) {
    Fail-Gate 'Canonical active competitive-gap backlog is missing.'
}
$activeBacklog = $backlogMatch.Groups['body'].Value
$bicRows = [regex]::Matches($activeBacklog, '(?m)^\|\s*(?<id>BIC-\d{2})\s*\|\s*(?<priority>P[012])\s*\|\s*(?<status>DONE|PARTIAL|MISSING|BLOCKED-EXTERNAL)\s*\|')
$expectedBicIds = @(1..17 | ForEach-Object { 'BIC-' + $_.ToString('00') })
$actualBicIds = @($bicRows | ForEach-Object { $_.Groups['id'].Value })
$uniqueBicIds = @($actualBicIds | Sort-Object -Unique)
if ($bicRows.Count -ne $expectedBicIds.Count -or
    $uniqueBicIds.Count -ne $expectedBicIds.Count -or
    (($uniqueBicIds -join ',') -ne ($expectedBicIds -join ','))) {
    Fail-Gate 'Canonical backlog must contain exactly one well-formed table row for every BIC-01 through BIC-17, with a valid priority and lifecycle status.'
}
$bic16 = @($bicRows | Where-Object { $_.Groups['id'].Value -eq 'BIC-16' })[0]
if ($bic16.Groups['priority'].Value -ne 'P0') {
    Fail-Gate 'BIC-16 must retain its canonical P0 priority.'
}
$openBic = @($bicRows | Where-Object { $_.Groups['status'].Value -ne 'DONE' }).Count
if ($openBic -gt 0) {
    if ($currentStatusLine -notmatch '\bPARTIAL\b') {
        Fail-Gate 'Open BIC rows exist but Current status is not PARTIAL.'
    }
    if ($currentStatusLine -match 'SOURCE_SOFTWARE_COMPLETE|REFERENCE_VERIFIED') {
        Fail-Gate 'Open BIC rows cannot coexist with a current source-complete/reference-verified claim.'
    }
    Write-Gate ("canonical status: PASS (truthful PARTIAL; " + $openBic + " active BIC row(s) still require closure)")
} else {
    Write-Gate 'canonical status: PASS (no open BIC rows in the active backlog; physical-PC acceptance still required)'
}

Write-Gate 'testing coding-agent competitive verdict rules'
try { & (Join-Path $root 'scripts\benchmark-coding-agent-competitors.ps1') -VerdictSelfTest }
catch { Fail-Gate ("coding-agent competitive verdict self-test failed: " + $_.Exception.Message) }
Write-Gate 'coding-agent competitive verdict self-test: PASS'

if ($isWindowsHost) {
    Write-Gate 'testing local release-asset provenance and completeness validation'
    & (Join-Path $root 'tools\test-release-asset-verifier.ps1')
    Write-Gate 'local release-asset verifier regression tests: PASS'
} elseif ($SkipReleaseAssetHarness) {
    Write-Gate 'local PowerShell release-asset regression harness: NOT_RUN (-SkipReleaseAssetHarness diagnostic run only)'
} else {
    Fail-Gate 'the local PowerShell release-asset regression harness needs Windows command shims and cannot run on this host. Use -SkipReleaseAssetHarness only for a non-release diagnostic run.'
}

# The predecessor brand must not appear anywhere in the tracked tree. The name is built from
# character codes so this guard does not itself contain it.
$archivedBrand = [string]::new([char[]](78, 101, 120, 111, 114, 97))
$legacyHits = @(& git -C $root grep -n -i -I -F -e $archivedBrand -- . 2>$null)
if ($legacyHits.Count -gt 0) {
    Fail-Gate ("tracked tree still contains archived predecessor-brand references: " + (($legacyHits | Select-Object -First 12 | ForEach-Object { ($_ -split ':')[0..1] -join ':' }) -join ', '))
}
Write-Gate "archived-brand sweep: PASS"

# Reference-grade runtime invariants added during the September closeout. These checks are source
# sentinels, not substitutes for unit tests; their job is to stop a future refactor from silently
# deleting the production wiring while leaving old tests/docs behind.
$chatProxy = (Get-Content 'src\PaRuX.Server\ChatProxyService.cs' -Raw) + (Get-Content 'src\PaRuX.Server\StreamingMcpToolLoop.cs' -Raw)
$mcpBatch = Get-Content 'src\PaRuX.Server\McpToolCallBatchExecutor.cs' -Raw
$winMlEngine = Get-Content 'src\PaRuX.Server\WindowsMlGenAiLocalInferenceEngine.cs' -Raw
$winMlProtocol = Get-Content 'src\PaRuX.Server\WindowsMlGenAiProtocol.cs' -Raw
$winMlRace = Get-Content 'src\PaRuX.Server\WindowsMlGenAiEngineBenchmarkAdapter.cs' -Raw
$engineCoordinator = Get-Content 'src\PaRuX.Server\LocalInferenceEngineCoordinator.cs' -Raw
$winMlProject = Get-Content 'src\PaRuX.WinMlWorker\PaRuX.WinMlWorker.csproj' -Raw
$winMlWorker = Get-Content 'src\PaRuX.WinMlWorker\WinMlGenAiServer.cs' -Raw
if ($chatProxy -notmatch 'McpToolCallBatchExecutor\.ExecuteAsync' -or $mcpBatch -notmatch 'Task\.WhenAll') {
    Fail-Gate 'MCP sibling tool calls are no longer wired to the concurrent deterministic batch executor.'
}
if ($winMlProject -notmatch 'Microsoft\.ML\.OnnxRuntimeGenAI\.WinML' -or
    $winMlWorker -notmatch 'RegisterExecutionProviderLibrary' -or
    $winMlWorker -notmatch 'ApplyChatTemplate' -or
    $winMlEngine -notmatch 'ILocalInferenceEngine' -or
    $winMlProtocol -notmatch 'LibrarySha256' -or
    $winMlRace -notmatch 'WindowsMlGenAiEngineBenchmarkAdapter' -or
    $engineCoordinator -notmatch 'SelectWindowsMlWinnerAsync') {
    Fail-Gate 'Windows ML GenAI production serving/race/AUTO wiring is incomplete.'
}
Write-Gate 'MCP parallelism + Windows ML GenAI source invariants: PASS'

$managedDotnet = if ($env:LOCALAPPDATA) { Join-Path $env:LOCALAPPDATA 'PaRuX\dev\dotnet\dotnet.exe' } else { $null }
$dotnet = if ($managedDotnet -and (Test-Path $managedDotnet)) { $managedDotnet } else { 'dotnet' }
Write-Gate "dotnet: $dotnet"
& $dotnet --version
if ($LASTEXITCODE -ne 0) { Fail-Gate 'dotnet SDK is unavailable.' }

$restoreArg = @()
if ($Restore) {
    Write-Gate 'restoring solution dependencies locally'
    & $dotnet restore 'PaRuX.sln' '-p:Platform=x64'
    if ($LASTEXITCODE -ne 0) { Fail-Gate 'solution restore failed.' }

    foreach ($publishProject in @(
        'src\PaRuX.App\PaRuX.App.csproj',
        'src\PaRuX.Server\PaRuX.Server.csproj',
        'src\PaRuX.Cli\PaRuX.Cli.csproj',
        'src\PaRuX.UpdateHelper\PaRuX.UpdateHelper.csproj',
        'src\PaRuX.WinMlWorker\PaRuX.WinMlWorker.csproj'
    )) {
        & $dotnet restore $publishProject '-r' 'win-x64'
        if ($LASTEXITCODE -ne 0) { Fail-Gate ("win-x64 restore failed for " + $publishProject) }
    }
} else {
    $restoreArg = @('--no-restore')
    Write-Gate 'dependency restore skipped (use -Restore explicitly when local caches are not ready)'
}

Write-Gate 'building Core/CLI/contracts/test dependency graph'
& $dotnet build 'tests\PaRuX.Server.Tests\PaRuX.Server.Tests.csproj' '-c' 'Release' @restoreArg
if ($LASTEXITCODE -ne 0) { Fail-Gate 'Core/test dependency build failed.' }

Write-Gate 'running Core regression suite'
& $dotnet test 'tests\PaRuX.Server.Tests\PaRuX.Server.Tests.csproj' '-c' 'Release' @restoreArg '--no-build'
if ($LASTEXITCODE -ne 0) { Fail-Gate 'Core regression suite failed.' }

if (-not $SkipWindowsAppBuild) {
    if (-not $isWindowsHost) { Fail-Gate 'Windows App build was requested but the gate is not running on Windows. Use -SkipWindowsAppBuild only for a non-release diagnostic run.' }
    Write-Gate 'building full Windows x64 solution including WinUI surfaces'
    & $dotnet build 'PaRuX.sln' '-c' 'Release' '-p:Platform=x64' @restoreArg
    if ($LASTEXITCODE -ne 0) { Fail-Gate 'full Windows solution build failed.' }

    $publishBase = Join-Path $root 'artifacts\validation'
    $publishRoot = Join-Path $publishBase 'release-gate-publish'
    if (Test-Path -LiteralPath $publishRoot) {
        # Preserve earlier ignored release evidence instead of recursively deleting it. Give this
        # gate run a fresh directory when the conventional destination already contains artifacts.
        $runId = (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N')
        $publishRoot = Join-Path $publishBase ('release-gate-publish-' + $runId)
        Write-Gate ("preserving existing publish evidence; this run writes to " + $publishRoot)
    }
    if (Test-Path -LiteralPath $publishRoot) {
        Fail-Gate ("publish output destination already exists; refusing to overwrite it: " + $publishRoot)
    }
    New-Item -ItemType Directory -Path $publishRoot -Force | Out-Null

    $publishChecks = @(
        @{ Project = 'src\PaRuX.App\PaRuX.App.csproj'; Output = 'app'; Exe = 'PaRuX.exe'; Args = @('-p:Platform=x64') },
        @{ Project = 'src\PaRuX.Server\PaRuX.Server.csproj'; Output = 'server'; Exe = 'PaRuX.Server.exe'; Args = @() },
        @{ Project = 'src\PaRuX.Cli\PaRuX.Cli.csproj'; Output = 'cli'; Exe = 'parux.exe'; Args = @() },
        @{ Project = 'src\PaRuX.UpdateHelper\PaRuX.UpdateHelper.csproj'; Output = 'update-helper'; Exe = 'PaRuX.UpdateHelper.exe'; Args = @('-p:PublishSingleFile=true','-p:IncludeNativeLibrariesForSelfExtract=true') },
        @{ Project = 'src\PaRuX.WinMlWorker\PaRuX.WinMlWorker.csproj'; Output = 'winml'; Exe = 'PaRuX.WinMlWorker.exe'; Args = @() }
    )
    foreach ($check in $publishChecks) {
        $outDir = Join-Path $publishRoot $check.Output
        Write-Gate ("publishing shipped Windows project: " + $check.Project)
        & $dotnet publish $check.Project '-c' 'Release' '-r' 'win-x64' '--self-contained' 'true' @restoreArg '-o' $outDir @($check.Args)
        if ($LASTEXITCODE -ne 0) { Fail-Gate ("shipped-project publish failed: " + $check.Project) }
        if (-not (Test-Path (Join-Path $outDir $check.Exe) -PathType Leaf)) {
            Fail-Gate ("shipped-project publish did not produce required executable " + $check.Exe)
        }
    }
    Write-Gate 'Windows shipped-project publishability: PASS'
}

if (-not $SkipVsCodeTests) {
    $vscodeDir = Join-Path $root 'integrations\vscode'
    if (Test-Path (Join-Path $vscodeDir 'package.json')) {
        $node = Get-Command node -ErrorAction SilentlyContinue
        if (-not $node) { Fail-Gate 'Node.js is required for the VS Code integration tests. Use -SkipVsCodeTests only for a non-release diagnostic run.' }
        $npm = Get-Command npm -ErrorAction SilentlyContinue
        if (-not $npm) { Fail-Gate 'npm is required for the VS Code integration tests. Use -SkipVsCodeTests only for a non-release diagnostic run.' }
        Push-Location $vscodeDir
        try {
            Write-Gate 'running VS Code V2 integration and editor-context contract tests'
            & npm test
            if ($LASTEXITCODE -ne 0) { Fail-Gate 'VS Code integration tests failed.' }
        }
        finally { Pop-Location }
    }
}

# Android is a first-class companion but not a second physical release target. Source/API parity,
# unit tests, lint and APK production are mandatory software checks on the Windows PC. Real-phone
# camera/radio/OEM-power behavior is useful exploratory QA, never a blocking acceptance condition.
$mobilePolicy = Get-Content 'src\PaRuX.Server\PairedDeviceEndpointPolicy.cs' -Raw
$mobileV2 = Get-Content 'src\PaRuX.Server\MobileProductV2Endpoints.cs' -Raw
$androidApi = Get-Content 'src\PaRuX.Android\app\src\main\java\com\parux\android\core\network\PaRuXApi.kt' -Raw
$androidBoundary = Get-Content 'src\PaRuX.Android\app\src\main\java\com\parux\android\core\network\MobileEndpointBoundary.kt' -Raw
$androidMain = Get-Content 'src\PaRuX.Android\app\src\main\java\com\parux\android\MainActivity.kt' -Raw

foreach ($sentinel in @(
    '/api/v2/mobile/execution-profile',
    '/api/v2/mobile/observability/dashboard',
    '/api/v2/mobile/goals/tasks'
)) {
    if ($mobilePolicy -notmatch [regex]::Escape($sentinel) -or
        $mobileV2 -notmatch [regex]::Escape($sentinel) -or
        $androidApi -notmatch [regex]::Escape($sentinel.TrimStart('/'))) {
        Fail-Gate ("Android safe V2 parity is incomplete for " + $sentinel)
    }
}
if ($mobilePolicy -match '"/api/v2/execution-profiles"' -or $mobilePolicy -match '"/api/v2/observability/dashboard"') {
    Fail-Gate 'Android must use sanitized /api/v2/mobile projections, not generic desktop V2 endpoints.'
}
if ($mobileV2 -notmatch 'GrantedCapabilities = new List<string>\(\)') {
    Fail-Gate 'Mobile Goal Mode must not allow a paired device to self-grant Core capabilities.'
}
foreach ($desktopOnly in @('/runtime/', '/training/', '/mcp/', '/backup/', '/models/benchmarks')) {
    if ($androidBoundary -notmatch [regex]::Escape($desktopOnly)) {
        Fail-Gate ("Android desktop-only boundary lost required protection for " + $desktopOnly)
    }
}
if ($androidMain -notmatch 'GoalsScreen' -or $androidMain -notmatch 'PaRuXTab\.Goals') {
    Fail-Gate 'Android Goal Mode V2 is not wired into the mobile UI.'
}
Write-Gate 'Android safe-parity static boundary: PASS'

if (-not $SkipAndroidTests) {
    if (-not $isWindowsHost) { Fail-Gate 'Android companion software validation is expected on the Windows development host. Use -SkipAndroidTests only for a non-release diagnostic run.' }
    $androidDir = Join-Path $root 'src\PaRuX.Android'
    $gradlew = Join-Path $androidDir 'gradlew.bat'
    if (-not (Test-Path $gradlew -PathType Leaf)) { Fail-Gate 'src\PaRuX.Android\gradlew.bat is missing.' }
    if (-not (Get-Command java -ErrorAction SilentlyContinue) -and -not $env:JAVA_HOME) {
        Fail-Gate 'A JDK is required for Android companion test/lint/build validation.'
    }
    Push-Location $androidDir
    try {
        Write-Gate 'running clean Android JVM tests, lint, debug APK and unsigned release APK build'
        & .\gradlew.bat --no-daemon clean test lint assembleDebug assembleRelease
        if ($LASTEXITCODE -ne 0) { Fail-Gate 'Android clean test/lint/debug+release assemble validation failed.' }
    }
    finally { Pop-Location }
}

Write-Gate 'PASS - executable repository/software checks completed for this host.'
if ($openBic -gt 0) {
    Write-Host ("Release remains blocked by " + $openBic + " active BIC row(s) plus PHYSICAL_PC_ACCEPTANCE_REQUIRED. Passing this gate is evidence, not a source-complete claim.")
} else {
    Write-Host 'Software backlog is closed in the canonical plan; PHYSICAL_PC_ACCEPTANCE_REQUIRED still remains before release/reference acceptance.'
}
Write-Host 'Physical Android hardware remains optional non-blocking QA unless the canonical plan explicitly changes that boundary.'
