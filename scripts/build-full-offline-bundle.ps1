param(
    [Parameter(Mandatory = $true)][string]$MsiPath,
    [Parameter(Mandatory = $true)][string]$AssetsRoot,
    [Parameter(Mandatory = $true)][string]$AssetsLock,
    [Parameter(Mandatory = $true)][string]$SbomPath,
    [string]$OutputDir = "",
    [string]$ProductVersion = "1.5.1",
    [switch]$RequireSpeech
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutputDir)) { $OutputDir = Join-Path $repoRoot "artifacts\offline" }
$MsiPath = [System.IO.Path]::GetFullPath($MsiPath)
$AssetsRoot = [System.IO.Path]::GetFullPath($AssetsRoot)
$AssetsLock = [System.IO.Path]::GetFullPath($AssetsLock)
$SbomPath = [System.IO.Path]::GetFullPath($SbomPath)
$OutputDir = [System.IO.Path]::GetFullPath($OutputDir)

foreach ($required in @($MsiPath, $AssetsLock, $SbomPath)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw "Required Full Offline input is missing: $required" }
}
if (-not (Test-Path -LiteralPath $AssetsRoot -PathType Container)) { throw "AssetsRoot does not exist: $AssetsRoot" }
if ([string]::IsNullOrWhiteSpace($ProductVersion) -or $ProductVersion.IndexOfAny([System.IO.Path]::GetInvalidFileNameChars()) -ge 0) {
    throw "ProductVersion is unsafe for an artifact name."
}

function Get-Sha256([string]$Path) {
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

# The SBOM must be generated CycloneDX, not the old manually maintained placeholder.
$sbom = Get-Content -LiteralPath $SbomPath -Raw | ConvertFrom-Json
if ([string]$sbom.bomFormat -ne "CycloneDX" -or [string]::IsNullOrWhiteSpace([string]$sbom.specVersion)) {
    throw "SbomPath is not a CycloneDX SBOM generated for this release."
}

$lock = Get-Content -LiteralPath $AssetsLock -Raw | ConvertFrom-Json
if ($lock.schemaVersion -ne 1 -or $null -eq $lock.assets -or @($lock.assets).Count -lt 1) {
    throw "Unsupported or empty Full Offline asset lock."
}

# Lock schema, one record per immutable file:
# { role, sourcePath, destinationScope: local-ai|ai-runtimes, destinationPath,
#   sha256, sourceUrl, license }
# sourcePath is relative to AssetsRoot. The builder never discovers or downloads a model by name:
# release inputs must already be intentionally curated and cryptographically locked.
$allowedScopes = @("local-ai", "ai-runtimes")
$roles = @{}
$validated = @()
$assetRootPrefix = $AssetsRoot.TrimEnd('\') + '\'
$destinations = @{}

foreach ($asset in @($lock.assets)) {
    $role = ([string]$asset.role).Trim().ToLowerInvariant()
    $sourcePath = ([string]$asset.sourcePath).Replace('/', '\')
    $destinationScope = ([string]$asset.destinationScope).Trim().ToLowerInvariant()
    $destinationPath = ([string]$asset.destinationPath).Replace('/', '\')
    $expectedHash = ([string]$asset.sha256).Trim().ToLowerInvariant()
    $sourceUrl = ([string]$asset.sourceUrl).Trim()
    $license = ([string]$asset.license).Trim()

    if ([string]::IsNullOrWhiteSpace($role) -or [string]::IsNullOrWhiteSpace($sourcePath) -or
        [string]::IsNullOrWhiteSpace($destinationPath) -or [System.IO.Path]::IsPathRooted($sourcePath) -or
        [System.IO.Path]::IsPathRooted($destinationPath) -or $sourcePath.Contains('..\') -or
        $destinationPath.Contains('..\')) {
        throw "Unsafe or incomplete Full Offline asset record."
    }
    if ($allowedScopes -notcontains $destinationScope) { throw "Unknown destinationScope: $destinationScope" }
    if ($expectedHash -notmatch '^[0-9a-f]{64}$') { throw "Invalid SHA-256 for asset $sourcePath" }
    if ([string]::IsNullOrWhiteSpace($sourceUrl) -or [string]::IsNullOrWhiteSpace($license)) {
        throw "Every Full Offline asset requires sourceUrl and license provenance: $sourcePath"
    }

    $source = [System.IO.Path]::GetFullPath((Join-Path $AssetsRoot $sourcePath))
    if (-not $source.StartsWith($assetRootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Asset source escaped AssetsRoot: $sourcePath"
    }
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "Locked Full Offline asset is missing: $source" }
    $actualHash = Get-Sha256 $source
    if ($actualHash -ne $expectedHash) {
        throw "Full Offline asset SHA-256 mismatch for $sourcePath`: expected $expectedHash, got $actualHash"
    }

    $destinationKey = ($destinationScope + "/" + $destinationPath.Replace('\', '/')).ToLowerInvariant()
    if ($destinations.ContainsKey($destinationKey)) { throw "Duplicate Full Offline destination: $destinationKey" }
    $destinations[$destinationKey] = $true
    $roles[$role] = $true

    $validated += [ordered]@{
        role = $role
        source = $source
        sourcePath = $sourcePath.Replace('\', '/')
        destinationScope = $destinationScope
        destinationPath = $destinationPath.Replace('\', '/')
        sha256 = $expectedHash
        sourceUrl = $sourceUrl
        license = $license
        sizeBytes = (Get-Item -LiteralPath $source).Length
    }
}

foreach ($requiredRole in @("text-baseline", "embedding", "reranker")) {
    if (-not $roles.ContainsKey($requiredRole)) { throw "Full Offline asset lock is missing required role: $requiredRole" }
}
if ($RequireSpeech -and -not $roles.ContainsKey("speech")) { throw "-RequireSpeech was requested but the asset lock has no speech role." }

# Enforce the actual paths consumed by the current PaRuX baseline. This turns "bundle contains a
# model" into the stronger guarantee "a fresh install can find the bundled model without network
# or a manual role assignment".
$requiredDestinations = @(
    "local-ai/models/qwen3.8-27b-ud-iq4_xs.gguf",
    "ai-runtimes/embeddings/model.onnx",
    "ai-runtimes/embeddings/vocab.txt",
    "ai-runtimes/reranker/model.onnx",
    "ai-runtimes/reranker/vocab.txt"
)
foreach ($requiredDestination in $requiredDestinations) {
    if (-not $destinations.ContainsKey($requiredDestination)) {
        throw "Full Offline asset lock is missing runtime-required destination: $requiredDestination"
    }
}

New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
$bundleName = "PaRuX-FullOffline-$ProductVersion-win-x64"
$stage = Join-Path $OutputDir $bundleName
$zipPath = Join-Path $OutputDir ($bundleName + ".zip")
$zipHashPath = $zipPath + ".sha256"
if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
if (Test-Path -LiteralPath $zipHashPath) { Remove-Item -LiteralPath $zipHashPath -Force }
New-Item -ItemType Directory -Force -Path $stage | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $stage "payload\local-ai") | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $stage "payload\ai-runtimes") | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $stage "provenance") | Out-Null

Copy-Item -LiteralPath $MsiPath -Destination (Join-Path $stage (Split-Path -Leaf $MsiPath)) -Force
Copy-Item -LiteralPath $SbomPath -Destination (Join-Path $stage "provenance\SBOM.cyclonedx.json") -Force
Copy-Item -LiteralPath (Join-Path $repoRoot "OPEN_SOURCE_PROVENANCE.md") -Destination (Join-Path $stage "provenance\OPEN_SOURCE_PROVENANCE.md") -Force
Copy-Item -LiteralPath (Join-Path $repoRoot "THIRD_PARTY_NOTICES.md") -Destination (Join-Path $stage "provenance\THIRD_PARTY_NOTICES.md") -Force
Copy-Item -LiteralPath (Join-Path $repoRoot "packaging\windows-baseline-runtime.lock.json") -Destination (Join-Path $stage "provenance\windows-baseline-runtime.lock.json") -Force
Copy-Item -LiteralPath (Join-Path $repoRoot "scripts\verify-offline-bundle.ps1") -Destination (Join-Path $stage "verify-offline-bundle.ps1") -Force
Copy-Item -LiteralPath (Join-Path $repoRoot "scripts\install-offline-assets.ps1") -Destination (Join-Path $stage "install-offline-assets.ps1") -Force

$bundleLockAssets = @()
foreach ($asset in $validated) {
    $bundleRelative = "payload/" + $asset.destinationScope + "/" + $asset.destinationPath
    $target = Join-Path $stage ($bundleRelative.Replace('/', '\'))
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null
    Copy-Item -LiteralPath $asset.source -Destination $target -Force
    $copiedHash = Get-Sha256 $target
    if ($copiedHash -ne $asset.sha256) { throw "Asset hash changed while staging: $($asset.sourcePath)" }
    $bundleLockAssets += [ordered]@{
        role = $asset.role
        bundlePath = $bundleRelative
        destinationScope = $asset.destinationScope
        destinationPath = $asset.destinationPath
        sha256 = $asset.sha256
        sizeBytes = $asset.sizeBytes
        sourceUrl = $asset.sourceUrl
        license = $asset.license
    }
}

$bundleLock = [ordered]@{
    schemaVersion = 1
    product = "PaRuX"
    productVersion = $ProductVersion
    platform = "win-x64"
    generatedUtc = [DateTime]::UtcNow.ToString("o")
    sourceAssetLockSha256 = Get-Sha256 $AssetsLock
    assets = $bundleLockAssets
}
$bundleLock | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $stage "provenance\full-offline-assets.lock.json") -Encoding UTF8

$readme = @"
PaRuX Full Offline $ProductVersion (win-x64)

1. Run verify-offline-bundle.ps1 before installation or redistribution.
2. Install the included PaRuX MSI.
3. Run install-offline-assets.ps1 to place the locked baseline model, embedding and reranker assets into PaRuX-managed local storage.
4. The asset installer never overwrites a different existing model unless -Force is explicitly supplied.

This bundle contains no cloud API requirement. Every packaged file is covered by MANIFEST.sha256; model/runtime provenance and the generated CycloneDX SBOM are under provenance\.
"@
Set-Content -LiteralPath (Join-Path $stage "README-OFFLINE.txt") -Value $readme -Encoding UTF8

# Manifest covers every bundle file except itself, including the verifier and installers.
$rootPrefix = $stage.TrimEnd('\') + '\'
$manifestLines = @()
Get-ChildItem -LiteralPath $stage -Recurse -File |
    Where-Object { $_.Name -ne "MANIFEST.sha256" } |
    Sort-Object FullName |
    ForEach-Object {
        $full = [System.IO.Path]::GetFullPath($_.FullName)
        if (-not $full.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw "Bundle inventory escaped staging root." }
        $relative = $full.Substring($rootPrefix.Length).Replace('\', '/')
        $manifestLines += (Get-Sha256 $full) + " *" + $relative
    }
Set-Content -LiteralPath (Join-Path $stage "MANIFEST.sha256") -Value $manifestLines -Encoding ASCII

& powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $stage "verify-offline-bundle.ps1") -BundleRoot $stage
if ($LASTEXITCODE -ne 0) { throw "Freshly built Full Offline bundle failed self-verification." }

# Do not use Compress-Archive here: the Standard 27B GGUF is multi-GB and Windows PowerShell's
# archive cmdlet is not a safe ZIP64 path for such payloads. Windows 10/11 ships bsdtar as tar.exe;
# libarchive emits ZIP64 when required and does not impose Compress-Archive's large-file ceiling.
$tar = Get-Command tar.exe -ErrorAction SilentlyContinue
if ($null -eq $tar) {
    throw "tar.exe is required to create the ZIP64 Full Offline bundle and was not found on this Windows build machine."
}
& $tar.Source -a -c -f $zipPath -C $OutputDir $bundleName
if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $zipPath -PathType Leaf)) {
    throw "tar.exe failed to create the Full Offline ZIP64 archive."
}
$zipHash = Get-Sha256 $zipPath
Set-Content -LiteralPath $zipHashPath -Value ($zipHash + " *" + (Split-Path -Leaf $zipPath)) -Encoding ASCII

Write-Host "PaRuX Full Offline bundle built and verified:"
Write-Host "  $zipPath"
Write-Host "  $zipHashPath"
