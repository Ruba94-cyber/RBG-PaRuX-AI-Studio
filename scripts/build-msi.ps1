[CmdletBinding()]
param(
    [string]$Version = "",
    [switch]$SkipPublish,
    [switch]$Release,
    [switch]$AllowUnsignedRelease
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

if ([System.Environment]::OSVersion.Platform -ne [System.PlatformID]::Win32NT) {
    throw "PaRuX MSI packages must be built on Windows."
}

if ($env:PARUX_ACCEPT_WIX7_EULA -ne "1") {
    throw @"
WiX v7 requires explicit acceptance of its EULA/OSMF terms.
Read https://docs.firegiant.com/wix/osmf/ and, if you accept them for this build, set:
  `$env:PARUX_ACCEPT_WIX7_EULA = "1"
Then run this script again. The repository deliberately does not auto-accept the EULA for you.
"@
}

if ($AllowUnsignedRelease -and -not $Release) {
    throw "-AllowUnsignedRelease is meaningful only together with -Release."
}
if ($Release -and $SkipPublish) {
    throw "Release mode refuses -SkipPublish: source provenance requires the MSI to be rebuilt from the exact clean HEAD being signed."
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$appProject = Join-Path $repoRoot "src\PaRuX.App\PaRuX.App.csproj"
$coreProject = Join-Path $repoRoot "src\PaRuX.Server\PaRuX.Server.csproj"
$cliProject = Join-Path $repoRoot "src\PaRuX.Cli\PaRuX.Cli.csproj"
$updateHelperProject = Join-Path $repoRoot "src\PaRuX.UpdateHelper\PaRuX.UpdateHelper.csproj"
$releaseToolProject = Join-Path $repoRoot "tools\PaRuX.ReleaseTool\PaRuX.ReleaseTool.csproj"
$trustAnchorPath = Join-Path $repoRoot "src\PaRuX.App\Services\ReleaseTrustAnchor.cs"
$wixProject = Join-Path $repoRoot "installer\PaRuX.Setup\PaRuX.Setup.wixproj"
$publishDir = Join-Path $repoRoot "artifacts\publish\win-x64"
$corePublishDir = Join-Path $publishDir "PaRuX.Server"
$cliPublishDir = Join-Path $publishDir "Cli"
$updateHelperPublishDir = Join-Path $publishDir "UpdateHelper"
$installerDir = Join-Path $repoRoot "artifacts\installer"

# Collect the whole output before reading the exit code. "| Select-Object -First 1" stops the
# pipeline as soon as it has its line, which can leave $LASTEXITCODE unassigned - and under
# Set-StrictMode reading an unassigned $LASTEXITCODE is a terminating error, so this script failed
# on its third line in any freshly started shell.
$commitLines = @(& git -C $repoRoot rev-parse HEAD 2>$null)
$gitExitCode = $LASTEXITCODE
$sourceCommit = if ($commitLines.Count -gt 0) { $commitLines[0] } else { '' }
if ($gitExitCode -ne 0 -or [string]::IsNullOrWhiteSpace([string]$sourceCommit)) {
    throw "Could not resolve the Git source commit for this MSI build."
}
$sourceCommit = ([string]$sourceCommit).Trim().ToLowerInvariant()
if ($sourceCommit -notmatch '^[0-9a-f]{40}$') {
    throw "Resolved source commit is not a canonical 40-hex Git SHA: $sourceCommit"
}
if ($Release) {
    $dirty = @(& git -C $repoRoot status --porcelain=v1 --untracked-files=all 2>$null)
    if ($LASTEXITCODE -ne 0) { throw "Could not verify release checkout cleanliness." }
    if ($dirty.Count -gt 0) {
        throw "Release mode requires a clean Git checkout so artifact provenance cannot be assigned to an uncommitted source tree."
    }
}
Write-Host "Source commit: $sourceCommit"

if ([string]::IsNullOrWhiteSpace($Version)) {
    [xml]$projectXml = Get-Content -LiteralPath $appProject -Raw
    $Version = [string]($projectXml.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ } | Select-Object -First 1)
}

if ($Version -notmatch '^\d+\.\d+\.\d+$') {
    throw "MSI ProductVersion must be exactly major.minor.patch (for example 1.5.0). Got '$Version'."
}
$versionParts = $Version.Split('.')
$major = [int]$versionParts[0]
$minor = [int]$versionParts[1]
$patch = [int]$versionParts[2]
$canonicalVersion = "$major.$minor.$patch"
if ($Version -cne $canonicalVersion) {
    throw "MSI ProductVersion must be canonical without leading zeroes. Got '$Version'."
}
if ($major -gt 255 -or $minor -gt 255 -or $patch -gt 65535) {
    throw "Windows Installer ProductVersion cannot exceed 255.255.65535. Got '$Version'."
}

$releasePrivateKey = $null
if ($Release) {
    if (-not (Test-Path -LiteralPath $trustAnchorPath -PathType Leaf)) {
        throw "Release trust anchor source is missing: $trustAnchorPath"
    }
    $anchorSource = Get-Content -LiteralPath $trustAnchorPath -Raw
    $anchorMatch = [regex]::Match($anchorSource, 'public const string PublicKeyPem\s*=\s*"(?<value>(?:\\.|[^"])*)";')
    if (-not $anchorMatch.Success -or $anchorMatch.Groups['value'].Value -notmatch 'BEGIN PUBLIC KEY') {
        throw @"
Release mode is blocked because ReleaseTrustAnchor.PublicKeyPem is empty.
Generate the PaRuX release-manifest key pair locally, keep the encrypted PRIVATE key outside the repository,
and embed/commit only the generated PUBLIC key before creating a public MSI release.
"@
    }

    $releasePrivateKey = $env:PARUX_RELEASE_PRIVATE_KEY
    if ([string]::IsNullOrWhiteSpace($releasePrivateKey) -or -not (Test-Path -LiteralPath $releasePrivateKey -PathType Leaf)) {
        throw @"
Release mode requires PARUX_RELEASE_PRIVATE_KEY to point to the encrypted manifest-signing private PEM outside the repository.
PARUX_RELEASE_KEY_PASSWORD must also be set; PaRuX.ReleaseTool validates it and refuses private keys stored inside the repo.
"@
    }
}

function Find-SignTool {
    $command = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }

    $programFilesX86 = [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFilesX86)
    if (-not [string]::IsNullOrWhiteSpace($programFilesX86)) {
        $kitsRoot = Join-Path $programFilesX86 "Windows Kits\10\bin"
        if (Test-Path $kitsRoot) {
            $candidate = Get-ChildItem $kitsRoot -Directory -ErrorAction SilentlyContinue |
                Sort-Object Name -Descending |
                ForEach-Object { Join-Path $_.FullName "x64\signtool.exe" } |
                Where-Object { Test-Path $_ } |
                Select-Object -First 1
            if ($candidate) { return $candidate }
        }
    }
    return $null
}

$pfx = $env:PARUX_SIGN_PFX
$pfxPassword = $env:PARUX_SIGN_PFX_PASSWORD
$signTool = $null
if (-not [string]::IsNullOrWhiteSpace($pfx)) {
    if (-not (Test-Path -LiteralPath $pfx -PathType Leaf)) { throw "PARUX_SIGN_PFX does not point to a file." }
    if ([string]::IsNullOrWhiteSpace($pfxPassword)) { throw "PARUX_SIGN_PFX_PASSWORD is required when PARUX_SIGN_PFX is set." }
    $signTool = Find-SignTool
    if (-not $signTool) { throw "signtool.exe was not found. Install the Windows SDK or add signtool to PATH." }
} elseif ($Release -and -not $AllowUnsignedRelease) {
    throw @"
Release mode has no Authenticode certificate configured.
Set PARUX_SIGN_PFX + PARUX_SIGN_PFX_PASSWORD, or deliberately add -AllowUnsignedRelease.
An unsigned public MSI/update helper can trigger Microsoft Defender SmartScreen / Unknown Publisher warnings.
The detached PaRuX release-manifest signature protects in-app update authenticity but does not replace Windows publisher trust.
"@
}

$fourPartVersion = "$Version.0"
$commonVersionArgs = @(
    "-p:Version=$Version",
    "-p:FileVersion=$fourPartVersion",
    "-p:AssemblyVersion=$fourPartVersion",
    # Only the plain version here. The SDK appends "+$(SourceRevisionId)" itself, so spelling the
    # commit out in both places produced "1.8.0+<sha>.<sha>" in the shipped file properties.
    "-p:InformationalVersion=$Version",
    "-p:SourceRevisionId=$sourceCommit",
    "-p:DebugSymbols=false",
    "-p:DebugType=None"
)

if (-not $SkipPublish) {
    if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
    New-Item -ItemType Directory -Path $publishDir -Force | Out-Null

    Write-Host "Publishing PaRuX Desktop $Version win-x64 from $sourceCommit..."
    & dotnet publish $appProject -c Release -r win-x64 --self-contained true -o $publishDir $commonVersionArgs
    if ($LASTEXITCODE -ne 0) { throw "PaRuX Desktop dotnet publish failed with exit code $LASTEXITCODE." }

    Write-Host "Publishing PaRuX Server $Version self-contained win-x64..."
    & dotnet publish $coreProject -c Release -r win-x64 --self-contained true -o $corePublishDir $commonVersionArgs
    if ($LASTEXITCODE -ne 0) { throw "PaRuX Server dotnet publish failed with exit code $LASTEXITCODE." }

    Write-Host "Publishing PaRuX CLI $Version self-contained win-x64..."
    & dotnet publish $cliProject -c Release -r win-x64 --self-contained true -o $cliPublishDir $commonVersionArgs
    if ($LASTEXITCODE -ne 0) { throw "PaRuX CLI dotnet publish failed with exit code $LASTEXITCODE." }

    # The UAC boundary is a tiny single-file helper. It is installed under Program Files and
    # re-verifies the RSA envelope + MSI bytes after copying them into protected staging.
    Write-Host "Publishing PaRuX protected Update Helper $Version self-contained win-x64..."
    & dotnet publish $updateHelperProject -c Release -r win-x64 --self-contained true -o $updateHelperPublishDir $commonVersionArgs `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
    if ($LASTEXITCODE -ne 0) { throw "PaRuX Update Helper dotnet publish failed with exit code $LASTEXITCODE." }

    Copy-Item -LiteralPath (Join-Path $repoRoot "LICENSE") -Destination (Join-Path $publishDir "LICENSE.txt") -Force
    Copy-Item -LiteralPath (Join-Path $repoRoot "NVIDIA_CUDA_NOTICE.txt") -Destination (Join-Path $publishDir "NVIDIA_CUDA_NOTICE.txt") -Force
    if (Test-Path -LiteralPath (Join-Path $repoRoot "THIRD_PARTY_NOTICES.md")) {
        Copy-Item -LiteralPath (Join-Path $repoRoot "THIRD_PARTY_NOTICES.md") -Destination (Join-Path $publishDir "THIRD_PARTY_NOTICES.md") -Force
    }
    if (Test-Path -LiteralPath (Join-Path $repoRoot "OPEN_SOURCE_PROVENANCE.md")) {
        Copy-Item -LiteralPath (Join-Path $repoRoot "OPEN_SOURCE_PROVENANCE.md") -Destination (Join-Path $publishDir "OPEN_SOURCE_PROVENANCE.md") -Force
    }
}

$desktopExe = Join-Path $publishDir "PaRuX.exe"
$coreExe = Join-Path $corePublishDir "PaRuX.Server.exe"
$cliExe = Join-Path $cliPublishDir "parux.exe"
$updateHelperExe = Join-Path $updateHelperPublishDir "PaRuX.UpdateHelper.exe"
$requiredPublishFiles = @(
    $desktopExe,
    (Join-Path $publishDir "PaRuX.pri"),
    (Join-Path $publishDir "Assets\parux.ico"),
    $coreExe,
    (Join-Path $corePublishDir "PaRuX.Server.dll"),
    $cliExe,
    $updateHelperExe,
    (Join-Path $publishDir "LICENSE.txt"),
    (Join-Path $publishDir "THIRD_PARTY_NOTICES.md"),
    (Join-Path $publishDir "NVIDIA_CUDA_NOTICE.txt")
)
foreach ($required in $requiredPublishFiles) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
        throw "Publish output is incomplete; required file is missing: $required"
    }
}

$publisherSigned = $false
if ($signTool) {
    # Sign the binaries that cross user/security boundaries before WiX harvests them. In particular,
    # the update helper must show the same trusted publisher at the UAC prompt as the MSI itself.
    foreach ($binary in @($desktopExe, $coreExe, $cliExe, $updateHelperExe)) {
        Write-Host "Authenticode signing $([IO.Path]::GetFileName($binary))..."
        & $signTool sign /fd SHA256 /td SHA256 /tr "http://timestamp.digicert.com" /f $pfx /p $pfxPassword $binary
        if ($LASTEXITCODE -ne 0) { throw "Authenticode signing failed for $binary with exit code $LASTEXITCODE." }
        & $signTool verify /pa /v $binary
        if ($LASTEXITCODE -ne 0) { throw "Authenticode verification failed for $binary." }
    }
    $publisherSigned = $true
} elseif ($Release) {
    Write-Warning "Building an explicitly UNSIGNED public release. Expect Windows SmartScreen/Unknown Publisher warnings."
} else {
    Write-Warning "Built application binaries are unsigned for local testing."
}

if (Test-Path $installerDir) { Remove-Item $installerDir -Recurse -Force }
New-Item -ItemType Directory -Path $installerDir -Force | Out-Null

Write-Host "Building PaRuX MSI with WiX v7..."
& dotnet build $wixProject `
    -c Release `
    "-p:ProductVersion=$Version" `
    "-p:PublishDir=$publishDir" `
    -p:AcceptEula=wix7 `
    "-p:OutputPath=$installerDir"
if ($LASTEXITCODE -ne 0) { throw "WiX MSI build failed with exit code $LASTEXITCODE." }

$msi = Get-ChildItem -LiteralPath $installerDir -Recurse -File -Filter "PaRuX-$Version-win-x64.msi" |
    Select-Object -First 1
if ($null -eq $msi) {
    $msi = Get-ChildItem -LiteralPath $installerDir -Recurse -File -Filter "*.msi" | Select-Object -First 1
}
if ($null -eq $msi) { throw "WiX completed but no MSI was found under $installerDir." }

if ($signTool) {
    Write-Host "Authenticode signing final MSI..."
    & $signTool sign /fd SHA256 /td SHA256 /tr "http://timestamp.digicert.com" /f $pfx /p $pfxPassword $msi.FullName
    if ($LASTEXITCODE -ne 0) { throw "Authenticode signing failed with exit code $LASTEXITCODE." }
    & $signTool verify /pa /v $msi.FullName
    if ($LASTEXITCODE -ne 0) { throw "Authenticode verification failed for the MSI." }
}

# Hash only the final MSI bytes (after Authenticode), because this is the digest authenticated by
# the detached PaRuX release envelope and later re-checked inside protected elevated staging.
$hash = Get-FileHash -Algorithm SHA256 -LiteralPath $msi.FullName
$hashFile = Join-Path $installerDir ("PaRuX-{0}-win-x64.sha256.txt" -f $Version)
("{0}  {1}" -f $hash.Hash.ToLowerInvariant(), $msi.Name) | Set-Content -LiteralPath $hashFile -Encoding ascii

$envelopePath = $null
$provenancePath = $null
if ($Release) {
    $envelopePath = Join-Path $installerDir ("PaRuX-{0}-win-x64.update.json" -f $Version)
    $provenancePath = Join-Path $installerDir ("PaRuX-{0}-release-provenance.json" -f $Version)
    Write-Host "Creating detached RSA-signed PaRuX update envelope over final MSI bytes..."
    & dotnet run --project $releaseToolProject -c Release -- sign `
        --msi $msi.FullName `
        --version $Version `
        --source-commit $sourceCommit `
        --private $releasePrivateKey `
        --output $envelopePath `
        --payload-dir $publishDir `
        --provenance $provenancePath
    if ($LASTEXITCODE -ne 0) { throw "PaRuX release-manifest signing failed with exit code $LASTEXITCODE." }
    if (-not (Test-Path -LiteralPath $envelopePath -PathType Leaf)) {
        throw "Release-manifest signing completed without producing $envelopePath."
    }
    if (-not (Test-Path -LiteralPath $provenancePath -PathType Leaf)) {
        throw "Release provenance generation completed without producing $provenancePath."
    }
}

Write-Host ""
Write-Host "MSI ready: $($msi.FullName)"
Write-Host "SHA-256:   $($hash.Hash.ToLowerInvariant())"
Write-Host "Source:    $sourceCommit"
Write-Host "Authenticode: $(if ($publisherSigned) { 'verified on primary binaries + MSI' } else { 'not present' })"
Write-Host "Desktop:   PaRuX.exe"
Write-Host "Server:    PaRuX.Server\PaRuX.Server.exe"
Write-Host "CLI:       Cli\parux.exe"
Write-Host "Updater:   UpdateHelper\PaRuX.UpdateHelper.exe"
Write-Host "Size:      $($msi.Length) bytes"
if ($Release) {
    Write-Host "Envelope:  $envelopePath"
    Write-Host "Provenance:$provenancePath"
}
Write-Host ""
Write-Host "Local smoke install:"
Write-Host "  msiexec /i `"$($msi.FullName)`" /l*v `"$installerDir\install.log`""
Write-Host "Upgrade smoke: install an older MSI first, then this one; the stable UpgradeCode must replace it."
Write-Host "Uninstall must remove binaries/shortcuts/PATH registration only; PaRuX state under LOCALAPPDATA is intentionally preserved."
if ($Release) {
    Write-Host "Publish the exact MSI, .update.json envelope and release-provenance.json to GitHub Release tag v$Version."
}
