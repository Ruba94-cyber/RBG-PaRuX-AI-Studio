param(
    [string]$BundleRoot = "",
    [switch]$Force
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

if ([string]::IsNullOrWhiteSpace($BundleRoot)) { $BundleRoot = $PSScriptRoot }
$BundleRoot = [System.IO.Path]::GetFullPath($BundleRoot)
$verify = Join-Path $BundleRoot "verify-offline-bundle.ps1"
$assetLock = Join-Path $BundleRoot "provenance\full-offline-assets.lock.json"
if (-not (Test-Path -LiteralPath $verify -PathType Leaf)) { throw "Bundle verifier is missing." }
if (-not (Test-Path -LiteralPath $assetLock -PathType Leaf)) { throw "Full Offline asset lock is missing." }

# Refuse to install even one byte from a bundle whose complete inventory does not verify.
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $verify -BundleRoot $BundleRoot
if ($LASTEXITCODE -ne 0) { throw "Full Offline bundle verification failed." }

function Resolve-LocalAiRoot {
    $localAppData = [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)
    $config = Join-Path $localAppData "PaRuX\localai-paths.json"
    try {
        if (Test-Path -LiteralPath $config -PathType Leaf) {
            $json = Get-Content -LiteralPath $config -Raw | ConvertFrom-Json
            $configured = $json.Root
            if ([string]::IsNullOrWhiteSpace([string]$configured)) { $configured = $json.root }
            if (-not [string]::IsNullOrWhiteSpace([string]$configured) -and (Test-Path -LiteralPath $configured -PathType Container)) {
                return [System.IO.Path]::GetFullPath([string]$configured)
            }
        }
    }
    catch { }

    $documents = [Environment]::GetFolderPath([Environment+SpecialFolder]::MyDocuments)
    $legacy = Join-Path $documents "LocalAI"
    if ((Test-Path -LiteralPath (Join-Path $legacy "runtime") -PathType Container) -or
        (Test-Path -LiteralPath (Join-Path $legacy "models") -PathType Container)) {
        return [System.IO.Path]::GetFullPath($legacy)
    }
    return [System.IO.Path]::GetFullPath((Join-Path $localAppData "PaRuX\LocalAI"))
}

$localAppData = [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)
$roots = @{
    "local-ai" = Resolve-LocalAiRoot
    "ai-runtimes" = [System.IO.Path]::GetFullPath((Join-Path $localAppData "PaRuX\ai-runtimes"))
}
$lock = Get-Content -LiteralPath $assetLock -Raw | ConvertFrom-Json
if ($lock.schemaVersion -ne 1 -or $null -eq $lock.assets) { throw "Unsupported Full Offline asset lock." }

foreach ($asset in @($lock.assets)) {
    $scope = ([string]$asset.destinationScope).ToLowerInvariant()
    if (-not $roots.ContainsKey($scope)) { throw "Unknown destinationScope in asset lock: $scope" }
    $bundlePath = ([string]$asset.bundlePath).Replace('/', '\')
    $destinationPath = ([string]$asset.destinationPath).Replace('/', '\')
    if ([string]::IsNullOrWhiteSpace($bundlePath) -or [string]::IsNullOrWhiteSpace($destinationPath) -or
        [System.IO.Path]::IsPathRooted($bundlePath) -or [System.IO.Path]::IsPathRooted($destinationPath) -or
        $bundlePath.Contains('..\') -or $destinationPath.Contains('..\')) {
        throw "Unsafe Full Offline asset path for role $($asset.role)."
    }

    $source = [System.IO.Path]::GetFullPath((Join-Path $BundleRoot $bundlePath))
    $root = [string]$roots[$scope]
    $target = [System.IO.Path]::GetFullPath((Join-Path $root $destinationPath))
    $rootPrefix = $root.TrimEnd('\') + '\'
    if (-not $target.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Asset destination escaped its managed root: $destinationPath"
    }
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "Bundle asset is missing: $bundlePath" }

    $expected = ([string]$asset.sha256).ToLowerInvariant()
    $sourceHash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($sourceHash -ne $expected) { throw "Locked asset hash mismatch before install: $bundlePath" }

    if (Test-Path -LiteralPath $target -PathType Leaf) {
        $existingHash = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($existingHash -eq $expected) {
            Write-Host "Already installed: $destinationPath"
            continue
        }
        if (-not $Force) {
            throw "A different file already exists at $target. Re-run with -Force only if replacement is intentional."
        }
    }

    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null
    $partial = "$target.parux-offline.partial"
    Remove-Item -LiteralPath $partial -Force -ErrorAction SilentlyContinue
    Copy-Item -LiteralPath $source -Destination $partial -Force
    $copiedHash = (Get-FileHash -LiteralPath $partial -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($copiedHash -ne $expected) {
        Remove-Item -LiteralPath $partial -Force -ErrorAction SilentlyContinue
        throw "Asset hash changed while copying: $destinationPath"
    }
    Move-Item -LiteralPath $partial -Destination $target -Force
    Write-Host "Installed [$($asset.role)]: $target"
}

Write-Host "PaRuX Full Offline assets installed and verified."
