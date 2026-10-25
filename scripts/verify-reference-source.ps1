<#
.SYNOPSIS
    Local-only source/build gate for PaRuX reference-grade changes.

.DESCRIPTION
    This intentionally does not depend on GitHub Actions. Run it on the development machine before
    release or after a substantial change. It checks critical production wiring, rejects obvious
    implementation stubs, and can restore/build/test the x64 solution locally.
#>
[CmdletBinding()]
param(
    [switch]$SkipBuild,
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Push-Location $repo
try {
    Write-Host 'PaRuX local reference verification' -ForegroundColor Cyan

    if (-not $SkipBuild) {
        Write-Host 'Restoring solution…'
        dotnet restore PaRuX.sln -p:Platform=x64
        if ($LASTEXITCODE -ne 0) { throw 'dotnet restore failed.' }

        Write-Host 'Building Release x64…'
        dotnet build PaRuX.sln -c Release -p:Platform=x64 --no-restore --nologo
        if ($LASTEXITCODE -ne 0) { throw 'Release x64 build failed.' }
    }

    if (-not $SkipTests) {
        Write-Host 'Running server regression suite…'
        dotnet test tests/PaRuX.Server.Tests/PaRuX.Server.Tests.csproj -c Release --no-restore --nologo
        if ($LASTEXITCODE -ne 0) { throw 'Server regression suite failed.' }
    }

    Write-Host 'Checking production source for implementation stubs…'
    $stubHits = Get-ChildItem src -Recurse -File -Include *.cs |
        Select-String -Pattern 'NotImplementedException' -SimpleMatch
    if ($stubHits) {
        $stubHits | ForEach-Object {
            Write-Error ("{0}:{1}: {2}" -f $_.Path, $_.LineNumber, $_.Line.Trim())
        }
        throw 'Production source contains NotImplementedException.'
    }

    $checks = @(
        @{ Path = 'src/PaRuX.Server/MediaGenerationRaceExtensions.cs'; Pattern = 'RunManagedImageRaceAsync' },
        @{ Path = 'src/PaRuX.Server/LocalProtocolCompatibilityEndpoints.cs'; Pattern = 'anthropic' },
        @{ Path = 'src/PaRuX.Server/WindowsMlPerformancePackService.cs'; Pattern = 'EnsureProviderAsync' },
        @{ Path = 'src/PaRuX.Server/SpeculativeDraftDiscovery.cs'; Pattern = 'Mtp' },
        @{ Path = 'src/PaRuX.App/Services/HistoryStore.cs'; Pattern = 'RetainSupersededPath' },
        @{ Path = 'src/PaRuX.App/Services/HistoryStore.cs'; Pattern = 'BranchComparable' },
        @{ Path = 'src/PaRuX.App/Models/ConversationBranch.cs'; Pattern = 'ConversationBranch' },
        @{ Path = 'scripts/benchmark-reference-stream.ps1'; Pattern = 'completion_tokens' },
        @{ Path = 'src/PaRuX.App/Services/CodingCommandPreflight.cs'; Pattern = 'ContainedUnknown' },
        @{ Path = 'src/PaRuX.App/Services/CodingAgentTools.cs'; Pattern = 'CodingCommandPreflight.DenyReasonFor' },
        @{ Path = 'src/PaRuX.App/Models/RouteKind.cs'; Pattern = 'VisionDelegated' },
        @{ Path = 'src/PaRuX.App/Services/RouterService.cs'; Pattern = 'RouteKind.VisionDelegated' },
        @{ Path = 'src/PaRuX.Server/VisionSubagentService.cs'; Pattern = 'MainModelEvidenceGuard' },
        @{ Path = 'src/PaRuX.Server/ChatSchedulingEndpointExtensions.cs'; Pattern = 'DelegateIfNeededAsync' },
        @{ Path = 'tests/PaRuX.Server.Tests/VisionSubagentTests.cs'; Pattern = 'Scheduling_contract_runs_visual_delegation_before_the_chat_reader_lease' }
    )

    Write-Host 'Checking reference-critical wiring…'
    foreach ($check in $checks) {
        if (-not (Test-Path $check.Path)) {
            throw "Missing critical source file: $($check.Path)"
        }
        if (-not (Select-String -Path $check.Path -Pattern $check.Pattern -SimpleMatch -Quiet)) {
            throw "Critical marker '$($check.Pattern)' missing from $($check.Path)."
        }
    }

    # Ordering is semantic here, not cosmetic. Vision delegation may need an exclusive text-model
    # transition, so the interactive reader lease must be acquired afterwards or the helper can
    # wait forever on the reader owned by its own request.
    $chatScheduling = Get-Content 'src/PaRuX.Server/ChatSchedulingEndpointExtensions.cs' -Raw
    $delegateIndex = $chatScheduling.IndexOf('DelegateIfNeededAsync', [StringComparison]::Ordinal)
    $readerIndex = $chatScheduling.IndexOf('AcquireTextInferenceAsync("chat:interactive"', [StringComparison]::Ordinal)
    if ($delegateIndex -lt 0 -or $readerIndex -le $delegateIndex) {
        throw 'Vision delegation must run before the interactive chat reader lease.'
    }

    if (Test-Path '.github/workflows/reference-quality-gate.yml') {
        throw 'The deprecated GitHub Actions reference gate is present; use this local gate instead.'
    }

    Write-Host 'REFERENCE SOURCE GATE: PASS' -ForegroundColor Green
}
finally {
    Pop-Location
}
