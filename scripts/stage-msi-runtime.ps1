param(
    [Parameter(Mandatory = $true)][string]$PublishDir,
    [string]$LockFile = "",
    [string]$CacheDir = ""
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($LockFile)) {
    $LockFile = Join-Path $repoRoot "packaging\windows-baseline-runtime.lock.json"
}
if ([string]::IsNullOrWhiteSpace($CacheDir)) {
    $CacheDir = Join-Path $repoRoot "artifacts\cache\runtime"
}

$PublishDir = [System.IO.Path]::GetFullPath($PublishDir)
$LockFile = [System.IO.Path]::GetFullPath($LockFile)
$CacheDir = [System.IO.Path]::GetFullPath($CacheDir)

if (-not (Test-Path -LiteralPath $PublishDir -PathType Container)) {
    throw "PublishDir does not exist: $PublishDir"
}
if (-not (Test-Path -LiteralPath $LockFile -PathType Leaf)) {
    throw "Baseline runtime lock file does not exist: $LockFile"
}

$lock = Get-Content -LiteralPath $LockFile -Raw | ConvertFrom-Json
if ($lock.schemaVersion -ne 1 -or $lock.platform -ne "win-x64") {
    throw "Unsupported baseline runtime lock schema/platform."
}
if ($null -eq $lock.artifacts -or $lock.artifacts.Count -lt 1) {
    throw "Baseline runtime lock contains no artifacts."
}

New-Item -ItemType Directory -Force -Path $CacheDir | Out-Null

function Get-Sha256([string]$Path) {
    # Computed directly via .NET rather than Get-FileHash: that cmdlet ships in the
    # Microsoft.PowerShell.Utility module and is resolved through PSModulePath-based autoloading,
    # which is not reliably populated in every process this script runs under (observed: the WiX
    # MSI build's MSBuild <Exec>-launched powershell.exe child reports "Get-FileHash non
    # riconosciuto" even though the same command succeeds in an interactive shell). SHA256 is a
    # fail-closed verification step for the staged runtime, so it must not depend on that.
    $sha256 = [System.Security.Cryptography.SHA256]::Create()
    try {
        $stream = [System.IO.File]::OpenRead($Path)
        try { $hashBytes = $sha256.ComputeHash($stream) }
        finally { $stream.Dispose() }
    }
    finally { $sha256.Dispose() }
    return [System.BitConverter]::ToString($hashBytes).Replace("-", "").ToLowerInvariant()
}

function Assert-Archive([string]$Path, $Artifact) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Runtime archive is missing: $Path"
    }
    $file = Get-Item -LiteralPath $Path
    if ($null -ne $Artifact.sizeBytes -and [int64]$Artifact.sizeBytes -gt 0 -and $file.Length -ne [int64]$Artifact.sizeBytes) {
        throw "Size verification failed for $($Artifact.id): expected $($Artifact.sizeBytes), got $($file.Length)."
    }
    $actual = Get-Sha256 $Path
    $expected = ([string]$Artifact.sha256).ToLowerInvariant()
    if ($actual -ne $expected) {
        throw "SHA-256 verification failed for $($Artifact.id): expected $expected, got $actual."
    }
}

function Get-VerifiedArchive($Artifact) {
    $archive = Join-Path $CacheDir ([string]$Artifact.archiveName)
    if (Test-Path -LiteralPath $archive -PathType Leaf) {
        try {
            Assert-Archive $archive $Artifact
            return $archive
        }
        catch {
            Remove-Item -LiteralPath $archive -Force -ErrorAction SilentlyContinue
        }
    }

    $partial = "$archive.partial"
    Remove-Item -LiteralPath $partial -Force -ErrorAction SilentlyContinue
    Write-Host "Downloading locked runtime artifact $($Artifact.id) $($Artifact.version)..."
    Invoke-WebRequest -Uri ([string]$Artifact.url) -OutFile $partial -UseBasicParsing
    Assert-Archive $partial $Artifact
    Move-Item -LiteralPath $partial -Destination $archive -Force
    return $archive
}

function Stage-ZipArtifact($Artifact) {
    $archive = Get-VerifiedArchive $Artifact
    $destination = [System.IO.Path]::GetFullPath(
        (Join-Path $PublishDir (([string]$Artifact.destination).Replace('/', '\'))))
    $publishPrefix = $PublishDir.TrimEnd([System.IO.Path]::DirectorySeparatorChar) +
        [System.IO.Path]::DirectorySeparatorChar
    if (-not $destination.StartsWith($publishPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "The locked runtime destination escaped the MSI publish directory: $($Artifact.id)"
    }

    $suffix = [Guid]::NewGuid().ToString('N')
    # Prepare next to the target: directory rename is atomic on the same filesystem.
    $staged = "$destination.parux-staging-$suffix"
    $backup = "$destination.parux-backup-$suffix"
    $temp = Join-Path ([System.IO.Path]::GetTempPath()) ("parux-stage-" + $suffix)
    $oldMoved = $false
    New-Item -ItemType Directory -Force -Path $temp | Out-Null
    try {
        Expand-Archive -LiteralPath $archive -DestinationPath $temp -Force

        $required = @($Artifact.requiredFiles)
        if ($required.Count -lt 1) { throw "Artifact $($Artifact.id) has no requiredFiles gate." }

        $anchor = $null
        foreach ($requiredFile in $required) {
            $matches = @(Get-ChildItem -LiteralPath $temp -Recurse -File | Where-Object { $_.Name -ieq [string]$requiredFile })
            if ($matches.Count -ne 1) {
                throw "Verified archive $($Artifact.id) must contain exactly one $requiredFile; found $($matches.Count)."
            }
            if ($null -eq $anchor) { $anchor = $matches[0].Directory.FullName }
            elseif ($anchor -ne $matches[0].Directory.FullName) {
                throw "Required files for $($Artifact.id) do not share one payload root."
            }
        }

        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $destination) | Out-Null
        New-Item -ItemType Directory -Force -Path $staged | Out-Null
        Get-ChildItem -LiteralPath $anchor -Force | ForEach-Object {
            Copy-Item -LiteralPath $_.FullName -Destination $staged -Recurse -Force
        }

        foreach ($requiredFile in $required) {
            $candidate = Join-Path $staged ([string]$requiredFile)
            if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) {
                throw "Staging failed for $($Artifact.id): required payload file missing at $candidate"
            }
        }

        if (Test-Path -LiteralPath $destination) {
            if (-not (Test-Path -LiteralPath $destination -PathType Container)) {
                throw "Runtime staging destination is not a directory: $destination"
            }
            [System.IO.Directory]::Move($destination, $backup)
            $oldMoved = $true
        }

        # The verified payload is fully prepared before the active publish tree changes.
        [System.IO.Directory]::Move($staged, $destination)
        if ($oldMoved) {
            Remove-Item -LiteralPath $backup -Recurse -Force -ErrorAction Stop
            $oldMoved = $false
        }
    }
    catch {
        $failure = $_
        if ($oldMoved -and -not (Test-Path -LiteralPath $destination)) {
            try {
                [System.IO.Directory]::Move($backup, $destination)
                $oldMoved = $false
            }
            catch {
                throw "Runtime staging failed for $($Artifact.id) and rollback failed. Previous payload is retained at $backup. Original failure: $($failure.Exception.Message). Rollback: $($_.Exception.Message)"
            }
        }
        throw $failure
    }
    finally {
        if (Test-Path -LiteralPath $staged) {
            Remove-Item -LiteralPath $staged -Recurse -Force -ErrorAction SilentlyContinue
        }
        if (Test-Path -LiteralPath $temp) {
            Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue
        }
        # Never erase a backup retained because rollback failed.
    }
}

foreach ($artifact in $lock.artifacts) {
    if (-not ([string]$artifact.archiveName).EndsWith(".zip", [StringComparison]::OrdinalIgnoreCase)) {
        throw "Only ZIP baseline artifacts are supported by this staging script: $($artifact.archiveName)"
    }
    Stage-ZipArtifact $artifact
}

$serverDir = Join-Path $PublishDir "PaRuX.Server"
$baselineDir = Join-Path $serverDir "runtime-baseline"
New-Item -ItemType Directory -Force -Path $baselineDir | Out-Null
Copy-Item -LiteralPath $LockFile -Destination (Join-Path $baselineDir "baseline-lock.json") -Force

$manifestPath = Join-Path $baselineDir "staging-manifest.json"
$lockHash = Get-Sha256 $LockFile
$manifestFiles = @()
$serverPrefix = [System.IO.Path]::GetFullPath($serverDir).TrimEnd('\') + '\'
$rootsToInventory = @(
    (Join-Path $serverDir "runtime-baseline"),
    (Join-Path $serverDir "runtime-tools\uv")
)
foreach ($root in $rootsToInventory) {
    if (-not (Test-Path -LiteralPath $root -PathType Container)) { continue }
    Get-ChildItem -LiteralPath $root -Recurse -File |
        Where-Object { $_.FullName -ne $manifestPath } |
        Sort-Object FullName |
        ForEach-Object {
            $full = [System.IO.Path]::GetFullPath($_.FullName)
            if (-not $full.StartsWith($serverPrefix, [StringComparison]::OrdinalIgnoreCase)) {
                throw "Staged runtime inventory escaped PaRuX.Server: $full"
            }
            # Avoid Path.GetRelativePath: powershell.exe on clean Windows may be Windows PowerShell
            # 5.1/.NET Framework, where that API does not exist. Prefix stripping is safe here
            # because every inventory root is already a child of the fully-normalized serverDir.
            $relative = $full.Substring($serverPrefix.Length).Replace('\', '/')
            $manifestFiles += [ordered]@{
                path = $relative
                sizeBytes = $_.Length
                sha256 = Get-Sha256 $_.FullName
            }
        }
}

$manifest = [ordered]@{
    schemaVersion = 1
    generatedUtc = [DateTime]::UtcNow.ToString("o")
    platform = "win-x64"
    lockSha256 = $lockHash
    files = $manifestFiles
}
$manifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $manifestPath -Encoding UTF8

Write-Host "PaRuX MSI baseline runtime staged and verified:"
Write-Host "  $manifestPath"
