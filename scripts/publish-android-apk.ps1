<#
.SYNOPSIS
    Builds the signed PaRuX Android APK and attaches it to an existing GitHub Release.

.DESCRIPTION
    Split out from the rest of the release so the signing password never has to travel anywhere it
    should not. The keystore and its passwords are read from the environment of the shell you run
    this in: they are not written to disk, not passed on the command line where they would land in
    your shell history, and not printed.

    Publication is fail-closed around source identity: the repository must be clean, the requested
    vX.Y.Z tag must point to the exact current HEAD, and that version must match the desktop/app
    product version. PARUX_SOURCE_COMMIT is injected only for the Gradle build so the resulting APK
    exposes the same commit in BuildConfig. Android's apksigner then verifies the produced APK before
    upload. A machine-readable provenance JSON with the APK hash/size/source commit is uploaded beside
    it.

    -LocalOnly performs the identical clean-tree, version, signing, apksigner and provenance steps but
    never touches the network or GitHub: it needs no pre-existing release tag (if vX.Y.Z already
    exists locally it must still point at HEAD) and ends after writing the local APK + provenance.

    Without all three signing variables set, Gradle produces app-release-unsigned.apk, which no
    phone will install. This script refuses to upload that rather than attaching a file that looks
    like a deliverable and is not one.

.EXAMPLE
    $env:PARUX_ANDROID_KEYSTORE = "$env:USERPROFILE\Documents\PaRuX\PaRuX-android-release.keystore"
    $env:PARUX_ANDROID_STORE_PASSWORD = Read-Host -AsSecureString | ConvertFrom-SecureString -AsPlainText
    $env:PARUX_ANDROID_KEY_PASSWORD = $env:PARUX_ANDROID_STORE_PASSWORD
    ./scripts/publish-android-apk.ps1 -Tag v1.8.0
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string]$Tag,
    [string]$Repository = "Ruba94-cyber/RBG-PaRuX-AI-Studio",
    [switch]$LocalOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$missing = @()
foreach ($name in @("PARUX_ANDROID_KEYSTORE", "PARUX_ANDROID_STORE_PASSWORD", "PARUX_ANDROID_KEY_PASSWORD")) {
    if ([string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable($name))) { $missing += $name }
}
if ($missing.Count -gt 0) {
    throw @"
Signing material is missing: $($missing -join ', ').

Set them in this shell and run again. They are read from the environment on purpose, so the
password stays in your terminal:

  `$env:PARUX_ANDROID_KEYSTORE = "C:\path\to\PaRuX-android-release.keystore"
  `$env:PARUX_ANDROID_STORE_PASSWORD = "..."
  `$env:PARUX_ANDROID_KEY_PASSWORD = "..."
"@
}

if (-not (Test-Path $env:PARUX_ANDROID_KEYSTORE -PathType Leaf)) {
    throw "Keystore not found at $env:PARUX_ANDROID_KEYSTORE"
}

$root = Split-Path -Parent $PSScriptRoot
$androidRoot = Join-Path $root "src\PaRuX.Android"
$gradlew = Join-Path $androidRoot "gradlew.bat"
if (-not (Test-Path $gradlew -PathType Leaf)) { throw "gradlew.bat not found at $gradlew" }

$headLines = @(& git -C $root rev-parse HEAD 2>$null)
$headExit = $LASTEXITCODE
$sourceCommit = if ($headLines.Count -gt 0) { $headLines[0] } else { '' }
if ($headExit -ne 0 -or [string]::IsNullOrWhiteSpace([string]$sourceCommit)) {
    throw "Could not resolve repository HEAD for Android release provenance."
}
$sourceCommit = ([string]$sourceCommit).Trim().ToLowerInvariant()
if ($sourceCommit -notmatch '^[0-9a-f]{40}$') {
    throw "Repository HEAD is not a canonical 40-hex Git commit: $sourceCommit"
}
$dirty = @(& git -C $root status --porcelain=v1 --untracked-files=all 2>$null)
if ($LASTEXITCODE -ne 0) { throw "Could not verify repository cleanliness." }
if ($dirty.Count -gt 0) {
    throw "Signed Android publication requires a clean Git checkout; commit/stash/remove all tracked and untracked changes first."
}

$version = ([xml](Get-Content (Join-Path $root "src\PaRuX.App\PaRuX.App.csproj"))).Project.PropertyGroup.Version |
    Where-Object { $_ } | Select-Object -First 1
if ([string]::IsNullOrWhiteSpace([string]$version) -or $version -notmatch '^\d+\.\d+\.\d+$') {
    throw "Could not resolve canonical PaRuX product version from PaRuX.App.csproj."
}
$version = [string]$version
$expectedTag = "v$version"
if ($Tag -cne $expectedTag) {
    throw "Android release tag must be exactly $expectedTag for app version $version; got '$Tag'."
}
# Collect all output before reading the exit code, and use --verify -q: plain rev-parse echoes an
# unresolved name to stdout, which would otherwise be mistaken for a commit.
$tagLines = @(& git -C $root rev-parse --verify -q "$Tag^{commit}" 2>$null)
$tagExit = $LASTEXITCODE
$tagCommit = if ($tagLines.Count -gt 0) { $tagLines[0] } else { '' }
$tagResolved = ($tagExit -eq 0 -and ([string]$tagCommit).Trim() -match '^[0-9a-fA-F]{40}$')
if (-not $tagResolved -and -not $LocalOnly) {
    throw "Git tag $Tag is unavailable locally. Fetch the intended release tag before publishing."
}
if ($tagResolved) {
    $tagCommit = ([string]$tagCommit).Trim().ToLowerInvariant()
    if ($tagCommit -ne $sourceCommit) {
        throw "Git tag $Tag points to $tagCommit, but the signed APK would be built from HEAD $sourceCommit. Refusing mismatched provenance."
    }
}

function Find-ApkSigner {
    $command = Get-Command apksigner.bat -ErrorAction SilentlyContinue
    if (-not $command) { $command = Get-Command apksigner -ErrorAction SilentlyContinue }
    if ($command) { return $command.Source }

    $roots = New-Object System.Collections.Generic.List[string]
    foreach ($candidate in @($env:ANDROID_SDK_ROOT, $env:ANDROID_HOME)) {
        if (-not [string]::IsNullOrWhiteSpace($candidate)) { $roots.Add($candidate) }
    }
    if ($env:LOCALAPPDATA) { $roots.Add((Join-Path $env:LOCALAPPDATA "Android\Sdk")) }

    foreach ($sdkRoot in $roots | Select-Object -Unique) {
        $buildTools = Join-Path $sdkRoot "build-tools"
        if (-not (Test-Path $buildTools -PathType Container)) { continue }
        $tool = Get-ChildItem $buildTools -Directory -ErrorAction SilentlyContinue |
            Sort-Object Name -Descending |
            ForEach-Object {
                $bat = Join-Path $_.FullName "apksigner.bat"
                $plain = Join-Path $_.FullName "apksigner"
                if (Test-Path $bat -PathType Leaf) { $bat }
                elseif (Test-Path $plain -PathType Leaf) { $plain }
            } |
            Select-Object -First 1
        if ($tool) { return $tool }
    }
    return $null
}

$apkSigner = Find-ApkSigner
if (-not $apkSigner) {
    throw "Android apksigner was not found in PATH or installed Android SDK build-tools; signed publication cannot be independently verified."
}

Write-Host "Building signed Android $version from $sourceCommit..."
$previousSourceCommit = $env:PARUX_SOURCE_COMMIT
$env:PARUX_SOURCE_COMMIT = $sourceCommit
Push-Location $androidRoot
try {
    & $gradlew --no-daemon :app:assembleRelease
    if ($LASTEXITCODE -ne 0) { throw "Gradle assembleRelease failed with exit code $LASTEXITCODE." }
}
finally {
    Pop-Location
    if ($null -eq $previousSourceCommit) { Remove-Item Env:PARUX_SOURCE_COMMIT -ErrorAction SilentlyContinue }
    else { $env:PARUX_SOURCE_COMMIT = $previousSourceCommit }
}

$outputDir = Join-Path $androidRoot "app\build\outputs\apk\release"
$signed = Get-ChildItem -Path $outputDir -Filter "*.apk" |
    Where-Object { $_.Name -notlike "*unsigned*" } |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1

if (-not $signed) {
    throw @"
Gradle produced only an unsigned APK, which no phone will install.

That means the signing configuration did not activate: build.gradle.kts creates it only when all
three environment variables are non-empty in the process that runs Gradle. Check them and re-run.
"@
}

$assetName = "PaRuX-$version-android.apk"
$staged = Join-Path $outputDir $assetName
Copy-Item $signed.FullName $staged -Force

Write-Host "Verifying Android signing block and certificate..."
& $apkSigner verify --verbose --print-certs $staged
if ($LASTEXITCODE -ne 0) {
    throw "apksigner rejected the produced APK; nothing will be uploaded."
}

$sha = (Get-FileHash $staged -Algorithm SHA256).Hash.ToLowerInvariant()
$size = (Get-Item -LiteralPath $staged).Length
$provenanceName = "PaRuX-$version-android-provenance.json"
$provenancePath = Join-Path $outputDir $provenanceName
$provenance = [ordered]@{
    schemaVersion = 1
    repository = $Repository
    version = $version
    sourceCommit = $sourceCommit
    generatedUtc = [DateTime]::UtcNow.ToString("o")
    signingVerification = "apksigner-verified"
    artifacts = @(
        [ordered]@{
            kind = "android-apk"
            fileName = $assetName
            sizeBytes = $size
            sha256 = $sha
        }
    )
}
$json = $provenance | ConvertTo-Json -Depth 6
[IO.File]::WriteAllText($provenancePath, $json, [Text.UTF8Encoding]::new($false))

Write-Host "Signed APK:    $assetName"
Write-Host "Source commit:$sourceCommit"
Write-Host "SHA-256:       $sha"
Write-Host "Provenance:    $provenanceName"

if ($LocalOnly) {
    Write-Host "Local-only: APK and provenance stay under $outputDir; nothing was uploaded."
    return
}

Write-Host "Attaching source-proven APK + provenance to release $Tag..."
& gh release upload $Tag $staged $provenancePath --repo $Repository --clobber
if ($LASTEXITCODE -ne 0) { throw "gh release upload failed with exit code $LASTEXITCODE." }

Write-Host "Done. $assetName and $provenanceName are attached to $Tag."