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

function Stage-ZipArtifactGroup([object[]]$Artifacts) {
    if ($Artifacts.Count -lt 1) { throw "A baseline runtime staging group must contain at least one artifact." }
    $destinationRelative = ([string]$Artifacts[0].destination).Replace('/', '\')
    foreach ($artifact in $Artifacts) {
        if (([string]$artifact.destination).Replace('/', '\') -ine $destinationRelative) {
            throw "Artifacts in one staging group must share the same destination."
        }
        if (-not ([string]$artifact.archiveName).EndsWith(".zip", [StringComparison]::OrdinalIgnoreCase)) {
            throw "Only ZIP baseline artifacts are supported by this staging script: $($artifact.archiveName)"
        }
    }

    $destination = [System.IO.Path]::GetFullPath((Join-Path $PublishDir $destinationRelative))
    $publishPrefix = $PublishDir.TrimEnd([System.IO.Path]::DirectorySeparatorChar) +
        [System.IO.Path]::DirectorySeparatorChar
    if (-not $destination.StartsWith($publishPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "The locked runtime destination escaped the MSI publish directory: $destinationRelative"
    }

    $suffix = [Guid]::NewGuid().ToString('N')
    $staged = "$destination.parux-staging-$suffix"
    $backup = "$destination.parux-backup-$suffix"
    $tempRoots = New-Object 'System.Collections.Generic.List[string]'
    $oldMoved = $false
    New-Item -ItemType Directory -Force -Path $staged | Out-Null
    try {
        foreach ($artifact in $Artifacts) {
            Write-Host "Staging locked runtime artifact $($artifact.id) $($artifact.version)..."
            $archive = Get-VerifiedArchive $artifact
            $temp = Join-Path ([System.IO.Path]::GetTempPath()) ("parux-stage-" + [Guid]::NewGuid().ToString('N'))
            $tempRoots.Add($temp)
            New-Item -ItemType Directory -Force -Path $temp | Out-Null
            Expand-Archive -LiteralPath $archive -DestinationPath $temp -Force

            $required = @($artifact.requiredFiles)
            if ($required.Count -lt 1) { throw "Artifact $($artifact.id) has no requiredFiles gate." }

            $anchor = $null
            foreach ($requiredFile in $required) {
                $matches = @(Get-ChildItem -LiteralPath $temp -Recurse -File | Where-Object { $_.Name -ieq [string]$requiredFile })
                if ($matches.Count -ne 1) {
                    throw "Verified archive $($artifact.id) must contain exactly one $requiredFile; found $($matches.Count)."
                }
                if ($null -eq $anchor) { $anchor = $matches[0].Directory.FullName }
                elseif ($anchor -ne $matches[0].Directory.FullName) {
                    throw "Required files for $($artifact.id) do not share one payload root."
                }
            }

            $anchorRoot = [System.IO.Path]::GetFullPath($anchor).TrimEnd([System.IO.Path]::DirectorySeparatorChar) +
                [System.IO.Path]::DirectorySeparatorChar
            foreach ($file in Get-ChildItem -LiteralPath $anchor -Recurse -File) {
                $full = [System.IO.Path]::GetFullPath($file.FullName)
                if (-not $full.StartsWith($anchorRoot, [StringComparison]::OrdinalIgnoreCase)) {
                    throw "Extracted runtime file escaped its archive payload root: $($artifact.id)"
                }
                $relative = $full.Substring($anchorRoot.Length)
                $destinationFile = [System.IO.Path]::GetFullPath((Join-Path $staged $relative))
                $stagedPrefix = $staged.TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
                if (-not $destinationFile.StartsWith($stagedPrefix, [StringComparison]::OrdinalIgnoreCase)) {
                    throw "Runtime file escaped the staging directory: $($artifact.id)"
                }
                if (Test-Path -LiteralPath $destinationFile) {
                    throw "Runtime archives contain a duplicate staged path: $relative"
                }
                New-Item -ItemType Directory -Force -Path (Split-Path -Parent $destinationFile) | Out-Null
                Copy-Item -LiteralPath $full -Destination $destinationFile -Force
            }

            foreach ($requiredFile in $required) {
                $candidate = Join-Path $staged ([string]$requiredFile)
                if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) {
                    throw "Staging failed for $($artifact.id): required payload file missing at $candidate"
                }
            }
        }

        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $destination) | Out-Null
        if (Test-Path -LiteralPath $destination) {
            if (-not (Test-Path -LiteralPath $destination -PathType Container)) {
                throw "Runtime staging destination is not a directory: $destination"
            }
            [System.IO.Directory]::Move($destination, $backup)
            $oldMoved = $true
        }

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
                throw "Runtime staging failed for $destinationRelative and rollback failed. Previous payload is retained at $backup. Original failure: $($failure.Exception.Message). Rollback: $($_.Exception.Message)"
            }
        }
        throw $failure
    }
    finally {
        if (Test-Path -LiteralPath $staged) {
            Remove-Item -LiteralPath $staged -Recurse -Force -ErrorAction SilentlyContinue
        }
        foreach ($temp in $tempRoots) {
            if (Test-Path -LiteralPath $temp) {
                Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue
            }
        }
    }
}

 # Artifacts with the same destination are co-staged into one immutable runtime folder. CUDA's
 # llama-server build and NVIDIA's separate cudart/cuBLAS redistributable archive are one payload.
$destinationGroups = [ordered]@{}
foreach ($artifact in @($lock.artifacts)) {
    $key = ([string]$artifact.destination).Replace('/', '\').ToLowerInvariant()
    if ([string]::IsNullOrWhiteSpace($key)) { throw "Baseline artifact $($artifact.id) has no destination." }
    if (-not $destinationGroups.Contains($key)) {
        $destinationGroups[$key] = New-Object 'System.Collections.Generic.List[object]'
    }
    $destinationGroups[$key].Add($artifact)
}
foreach ($key in $destinationGroups.Keys) {
    Stage-ZipArtifactGroup -Artifacts $destinationGroups[$key].ToArray()
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
