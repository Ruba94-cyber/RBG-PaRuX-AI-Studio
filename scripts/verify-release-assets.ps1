<#
.SYNOPSIS
    Verifies a PaRuX release candidate locally without GitHub Actions.

.DESCRIPTION
    Checks the exact Windows MSI, detached signed envelope and machine-readable provenance against
    one version/source commit. The RSA signature, canonical update URL, manifest schema, MSI size and
    MSI SHA-256 are validated by PaRuX.ReleaseVerifier, which compiles the same verifier/trust anchor
    used by Desktop and the elevated Update Helper.

    With -RequireAndroid, the exact signed APK and Android provenance must also exist, match the same
    version/source commit/hash, and pass Android apksigner verification. -RequireTag additionally
    requires vX.Y.Z to resolve to the same source commit. This is an offline/local release gate; it
    creates or dispatches no GitHub Actions workflow.

    Payload binding: provenance records the SHA-256 of every file that was harvested into the MSI.
    The verifier extracts the MSI with an administrative image (msiexec /a into a temp directory; this
    installs nothing), requires that tree to equal the provenance payload exactly, requires the four
    primary binaries inside it to embed the source commit, and, when -PublishDirectory is given,
    requires that directory to equal the same payload. A publish directory from another commit paired
    with a valid MSI therefore fails.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string]$Version,
    [string]$SourceCommit = "",
    [string]$InstallerDirectory = "",
    [string]$AndroidDirectory = "",
    [switch]$RequireAndroid,
    [switch]$RequireTag,
    # Optional: the published (pre-MSI) tree, normally artifacts\publish\win-x64. It must match the
    # provenance payload hashes (and therefore the MSI) file for file.
    [string]$PublishDirectory = "",
    # CycloneDX SBOM bound into the manifest. Defaults to artifacts\sbom\SBOM.windows.cyclonedx.json
    # when that file exists and -InstallerDirectory was not overridden.
    [string]$SbomPath = "",
    # Android version checks: versionName must equal -Version; versionCode must exceed the previous
    # release's. The previous code comes from -PreviousAndroidVersionCode, else -PreviousManifest
    # (an earlier release-candidate manifest), else the Git history of the Gradle file.
    [string]$AndroidGradleFile = "",
    [string]$PreviousManifest = "",
    [int]$PreviousAndroidVersionCode = -1,
    # When set, a single release-candidate manifest binding commit, version and every verified asset
    # hash is written here after all checks passed. Local file only; nothing is uploaded.
    [string]$ManifestOutput = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot

if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Version must be canonical major.minor.patch." }
$parts = $Version.Split('.')
$canonical = "$([int]$parts[0]).$([int]$parts[1]).$([int]$parts[2])"
if ($Version -cne $canonical) { throw "Version must not contain leading zeroes." }

if ([string]::IsNullOrWhiteSpace($SourceCommit)) {
    $headLines = @(& git -C $root rev-parse HEAD 2>$null)
    $headExit = $LASTEXITCODE
    if ($headExit -ne 0 -or $headLines.Count -eq 0) { throw "Could not resolve current Git HEAD." }
    $SourceCommit = $headLines[0]
}
$SourceCommit = ([string]$SourceCommit).Trim().ToLowerInvariant()
if ($SourceCommit -notmatch '^[0-9a-f]{40}$') { throw "SourceCommit must be a canonical 40-hex Git SHA." }

if ($RequireTag) {
    $tag = "v$Version"
    $tagLines = @(& git -C $root rev-parse --verify -q "$tag^{commit}" 2>$null)
    $tagExit = $LASTEXITCODE
    $tagCommit = if ($tagLines.Count -gt 0) { $tagLines[0] } else { '' }
    if ($tagExit -ne 0 -or [string]::IsNullOrWhiteSpace([string]$tagCommit)) {
        throw "Required release tag $tag is unavailable."
    }
    $tagCommit = ([string]$tagCommit).Trim().ToLowerInvariant()
    if ($tagCommit -ne $SourceCommit) {
        throw "Release tag $tag resolves to $tagCommit, not expected source $SourceCommit."
    }
}

$installerDirectoryOverridden = -not [string]::IsNullOrWhiteSpace($InstallerDirectory)
if ([string]::IsNullOrWhiteSpace($InstallerDirectory)) {
    $InstallerDirectory = Join-Path $root "artifacts\installer"
}
$InstallerDirectory = [IO.Path]::GetFullPath($InstallerDirectory)
if (-not (Test-Path $InstallerDirectory -PathType Container)) {
    throw "Installer directory not found: $InstallerDirectory"
}

$msi = Join-Path $InstallerDirectory "PaRuX-$Version-win-x64.msi"
$envelope = Join-Path $InstallerDirectory "PaRuX-$Version-win-x64.update.json"
$provenancePath = Join-Path $InstallerDirectory "PaRuX-$Version-release-provenance.json"
foreach ($required in @($msi, $envelope, $provenancePath)) {
    if (-not (Test-Path $required -PathType Leaf)) { throw "Required release artifact is missing: $required" }
}

Write-Host "[release verify] authenticating signed Windows update + MSI bytes"
& dotnet run --project (Join-Path $root "tools\PaRuX.ReleaseVerifier\PaRuX.ReleaseVerifier.csproj") -c Release -- `
    --msi $msi `
    --envelope $envelope `
    --version $Version `
    --source-commit $SourceCommit
if ($LASTEXITCODE -ne 0) { throw "PaRuX.ReleaseVerifier rejected the Windows release candidate." }

function Get-CanonicalHash([string]$path) {
    return (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
}

$provenance = Get-Content -LiteralPath $provenancePath -Raw | ConvertFrom-Json
if ([int]$provenance.schemaVersion -ne 1) { throw "Unsupported Windows release provenance schema." }
if ([string]$provenance.repository -cne "Ruba94-cyber/RBG-PaRuX-AI-Studio") { throw "Windows provenance repository mismatch." }
if ([string]$provenance.version -cne $Version) { throw "Windows provenance version mismatch." }
if ([string]$provenance.sourceCommit -cne $SourceCommit) { throw "Windows provenance source commit mismatch." }
if ([string]$provenance.signingVerification -cne "rsa-sha256-self-verified") { throw "Windows provenance does not record signer self-verification." }

$winArtifacts = @($provenance.artifacts)
$manifestAssets = @()
$androidVersionReport = $null
foreach ($path in @($msi, $envelope)) {
    $name = [IO.Path]::GetFileName($path)
    $entry = @($winArtifacts | Where-Object { [string]$_.fileName -ceq $name })
    if ($entry.Count -ne 1) { throw "Windows provenance must contain exactly one entry for $name." }
    $actualSize = (Get-Item -LiteralPath $path).Length
    $actualHash = Get-CanonicalHash $path
    if ([long]$entry[0].sizeBytes -ne $actualSize) { throw "Windows provenance size mismatch for $name." }
    if ([string]$entry[0].sha256 -cne $actualHash) { throw "Windows provenance SHA-256 mismatch for $name." }
    $manifestAssets += [ordered]@{ fileName = $name; sizeBytes = $actualSize; sha256 = $actualHash }
}
Write-Host "[release verify] Windows provenance: PASS"

# ---- source-tree/build-mode facts and payload binding -------------------------------------------
if (-not ($provenance.PSObject.Properties.Name -contains 'buildMode') -or [string]$provenance.buildMode -cne "release") { throw "Windows provenance does not record a release-mode build." }
if (-not ($provenance.PSObject.Properties.Name -contains 'sourceTreeClean') -or $provenance.sourceTreeClean -ne $true) {
    throw "Windows provenance does not record a clean source tree."
}
if (-not ($provenance.PSObject.Properties.Name -contains 'payloadFiles') -or @($provenance.payloadFiles).Count -eq 0) {
    throw "Windows provenance carries no payloadFiles; the MSI contents are not bound to the build output. Rebuild with the current build-msi.ps1."
}
$expectedPayload = @{}
foreach ($entry in @($provenance.payloadFiles)) {
    $key = ([string]$entry.path).Replace('\', '/')
    if ($expectedPayload.ContainsKey($key)) { throw "Windows provenance payload lists $key more than once." }
    $expectedPayload[$key] = [string]$entry.sha256
}
$primaryBinaries = @("PaRuX.exe", "PaRuX.Server/PaRuX.Server.exe", "Cli/parux.exe", "UpdateHelper/PaRuX.UpdateHelper.exe")
foreach ($binary in $primaryBinaries) {
    if (-not $expectedPayload.ContainsKey($binary)) { throw "Windows provenance payload does not list primary binary $binary." }
}

function Get-TreeHashes([string]$directory) {
    $directory = [IO.Path]::GetFullPath($directory).TrimEnd('\')
    $result = @{}
    foreach ($file in Get-ChildItem -LiteralPath $directory -Recurse -File) {
        $relative = $file.FullName.Substring($directory.Length + 1).Replace('\', '/')
        $result[$relative] = Get-CanonicalHash $file.FullName
    }
    return $result
}

function Assert-TreeMatchesProvenance([hashtable]$actual, [string]$what) {
    $problems = @()
    foreach ($key in $expectedPayload.Keys) {
        if (-not $actual.ContainsKey($key)) { $problems += "missing $key" }
        elseif ($actual[$key] -cne $expectedPayload[$key]) { $problems += "hash mismatch $key" }
    }
    foreach ($key in $actual.Keys) {
        if (-not $expectedPayload.ContainsKey($key)) { $problems += "unexpected $key" }
    }
    if ($problems.Count -gt 0) {
        $shown = ($problems | Select-Object -First 5) -join '; '
        throw "$what differs from the provenance payload ($($problems.Count) problem(s)): $shown"
    }
}

function Test-EmbeddedCommit([string]$payloadRoot) {
    $report = @()
    foreach ($relative in $primaryBinaries) {
        $binary = Join-Path $payloadRoot ($relative.Replace('/', '\'))
        $productVersion = [string][Diagnostics.FileVersionInfo]::GetVersionInfo($binary).ProductVersion
        # >= 12 hex characters: a 7-9 character prefix is too short to identify a commit.
        $match = [regex]::Match($productVersion, '^(?<v>\d+\.\d+\.\d+)\+(?<c>[0-9a-fA-F]{12,40})$')
        if (-not $match.Success) { throw "$relative does not embed a source commit (>= 12 hex characters) in its product version ('$productVersion')." }
        if ([string]$match.Groups['v'].Value -cne $Version) { throw "$relative embeds version $($match.Groups['v'].Value), expected $Version." }
        $embedded = $match.Groups['c'].Value.ToLowerInvariant()
        if (-not $SourceCommit.StartsWith($embedded, [StringComparison]::Ordinal)) {
            throw "$relative embeds source commit $embedded, expected $SourceCommit."
        }
        $report += [ordered]@{ file = $relative; productVersion = $productVersion }
    }
    return $report
}

$msiexec = Get-Command msiexec -ErrorAction SilentlyContinue
if (-not $msiexec) { throw "msiexec is required to extract the MSI payload for verification (administrative image; nothing is installed)." }
$extractRoot = Join-Path ([IO.Path]::GetTempPath()) ("parux-msi-payload-" + [Guid]::NewGuid().ToString('N'))
$embeddedCommitReport = @()
try {
    New-Item -ItemType Directory -Path $extractRoot -Force | Out-Null
    $process = Start-Process -FilePath $msiexec.Source -ArgumentList @('/a', "`"$msi`"", '/qn', "TARGETDIR=`"$extractRoot`"") -Wait -PassThru -NoNewWindow
    if ($process.ExitCode -ne 0) { throw "msiexec administrative extraction of the MSI failed with exit code $($process.ExitCode)." }
    $primaryHits = @(Get-ChildItem -LiteralPath $extractRoot -Recurse -File -Filter "PaRuX.exe" | Where-Object { $_.Name -ceq "PaRuX.exe" })
    if ($primaryHits.Count -ne 1) { throw "Expected exactly one PaRuX.exe in the extracted MSI payload, found $($primaryHits.Count)." }
    $payloadRoot = $primaryHits[0].DirectoryName
    Assert-TreeMatchesProvenance (Get-TreeHashes $payloadRoot) "MSI payload"
    Write-Host "[release verify] MSI payload ($($expectedPayload.Count) files) equals provenance: PASS"
    $embeddedCommitReport = Test-EmbeddedCommit $payloadRoot
    Write-Host "[release verify] embedded source commit in $($primaryBinaries.Count) primary binaries of the MSI: PASS"
}
finally {
    if ((Test-Path -LiteralPath $extractRoot) -and $extractRoot.StartsWith([IO.Path]::GetTempPath(), [StringComparison]::OrdinalIgnoreCase)) {
        Remove-Item -LiteralPath $extractRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
if (-not [string]::IsNullOrWhiteSpace($PublishDirectory)) {
    if (-not (Test-Path -LiteralPath $PublishDirectory -PathType Container)) { throw "Publish directory not found: $PublishDirectory" }
    Assert-TreeMatchesProvenance (Get-TreeHashes $PublishDirectory) "Publish directory"
    Write-Host "[release verify] publish directory equals provenance payload: PASS"
}

# ---- SBOM -----------------------------------------------------------------------------------------
$sbomAsset = $null
if ([string]::IsNullOrWhiteSpace($SbomPath) -and -not $installerDirectoryOverridden) {
    $defaultSbom = Join-Path $root "artifacts\sbom\SBOM.windows.cyclonedx.json"
    if (Test-Path -LiteralPath $defaultSbom -PathType Leaf) { $SbomPath = $defaultSbom }
}
if (-not [string]::IsNullOrWhiteSpace($SbomPath)) {
    if (-not (Test-Path -LiteralPath $SbomPath -PathType Leaf)) { throw "SBOM not found: $SbomPath" }
    $sbom = Get-Content -LiteralPath $SbomPath -Raw | ConvertFrom-Json
    if ([string]$sbom.bomFormat -ne "CycloneDX" -or [string]::IsNullOrWhiteSpace([string]$sbom.specVersion)) { throw "SbomPath is not a CycloneDX SBOM." }
    $sbomAsset = [ordered]@{ fileName = [IO.Path]::GetFileName($SbomPath); sizeBytes = (Get-Item -LiteralPath $SbomPath).Length; sha256 = (Get-CanonicalHash $SbomPath) }
    $manifestAssets += $sbomAsset
    Write-Host "[release verify] SBOM bound: $($sbomAsset.fileName) $($sbomAsset.sha256)"
}

function Find-ApkSigner {
    $command = Get-Command apksigner.bat -ErrorAction SilentlyContinue
    if (-not $command) { $command = Get-Command apksigner -ErrorAction SilentlyContinue }
    if ($command) { return $command.Source }
    $roots = @($env:ANDROID_SDK_ROOT, $env:ANDROID_HOME)
    if ($env:LOCALAPPDATA) { $roots += (Join-Path $env:LOCALAPPDATA "Android\Sdk") }
    foreach ($sdkRoot in @($roots | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -Unique)) {
        $buildTools = Join-Path $sdkRoot "build-tools"
        if (-not (Test-Path $buildTools -PathType Container)) { continue }
        $candidate = Get-ChildItem $buildTools -Directory -ErrorAction SilentlyContinue |
            Sort-Object Name -Descending |
            ForEach-Object {
                $bat = Join-Path $_.FullName "apksigner.bat"
                $plain = Join-Path $_.FullName "apksigner"
                if (Test-Path $bat -PathType Leaf) { $bat }
                elseif (Test-Path $plain -PathType Leaf) { $plain }
            } | Select-Object -First 1
        if ($candidate) { return $candidate }
    }
    return $null
}

if ($RequireAndroid) {
    if ([string]::IsNullOrWhiteSpace($AndroidDirectory)) {
        $AndroidDirectory = Join-Path $root "src\PaRuX.Android\app\build\outputs\apk\release"
    }
    $AndroidDirectory = [IO.Path]::GetFullPath($AndroidDirectory)
    $apk = Join-Path $AndroidDirectory "PaRuX-$Version-android.apk"
    $androidProvenancePath = Join-Path $AndroidDirectory "PaRuX-$Version-android-provenance.json"
    foreach ($required in @($apk, $androidProvenancePath)) {
        if (-not (Test-Path $required -PathType Leaf)) { throw "Required Android release artifact is missing: $required" }
    }

    $apkSigner = Find-ApkSigner
    if (-not $apkSigner) { throw "apksigner is required to independently verify the Android release artifact." }
    Write-Host "[release verify] authenticating Android APK signature"
    & $apkSigner verify --verbose --print-certs $apk
    if ($LASTEXITCODE -ne 0) { throw "apksigner rejected $apk" }

    $android = Get-Content -LiteralPath $androidProvenancePath -Raw | ConvertFrom-Json
    if ([int]$android.schemaVersion -ne 1) { throw "Unsupported Android provenance schema." }
    if ([string]$android.repository -cne "Ruba94-cyber/RBG-PaRuX-AI-Studio") { throw "Android provenance repository mismatch." }
    if ([string]$android.version -cne $Version) { throw "Android provenance version mismatch." }
    if ([string]$android.sourceCommit -cne $SourceCommit) { throw "Android provenance source commit mismatch." }
    if ([string]$android.signingVerification -cne "apksigner-verified") { throw "Android provenance does not record apksigner verification." }
    $apkEntries = @($android.artifacts | Where-Object { [string]$_.fileName -ceq [IO.Path]::GetFileName($apk) })
    if ($apkEntries.Count -ne 1) { throw "Android provenance must contain exactly one APK entry." }
    if ([long]$apkEntries[0].sizeBytes -ne (Get-Item -LiteralPath $apk).Length) { throw "Android provenance APK size mismatch." }
    if ([string]$apkEntries[0].sha256 -cne (Get-CanonicalHash $apk)) { throw "Android provenance APK SHA-256 mismatch." }
    # versionName/versionCode: the Gradle source, the APK's own manifest and -Version must agree, and
    # the code must be strictly newer than the previous release so Android accepts it as an upgrade.
    if ([string]::IsNullOrWhiteSpace($AndroidGradleFile)) { $AndroidGradleFile = Join-Path $root "src\PaRuX.Android\app\build.gradle.kts" }
    function Read-GradleVersion([string]$text) {
        $n = [regex]::Match($text, 'versionName\s*=\s*"(?<v>[^"]+)"')
        $c = [regex]::Match($text, 'versionCode\s*=\s*(?<v>\d+)')
        if (-not $n.Success -or -not $c.Success) { return $null }
        return @{ Name = $n.Groups['v'].Value; Code = [int]$c.Groups['v'].Value }
    }
    if (-not (Test-Path -LiteralPath $AndroidGradleFile -PathType Leaf)) { throw "Android Gradle file not found: $AndroidGradleFile" }
    $gradle = Read-GradleVersion (Get-Content -LiteralPath $AndroidGradleFile -Raw)
    if ($null -eq $gradle) { throw "Could not read versionName/versionCode from $AndroidGradleFile." }
    if ($gradle.Name -cne $Version) { throw "Android versionName '$($gradle.Name)' does not equal release version $Version." }

    $aapt2 = Get-Command aapt2 -ErrorAction SilentlyContinue
    $aapt2Path = if ($aapt2) { $aapt2.Source } else { $null }
    if (-not $aapt2Path) {
        foreach ($sdkRoot in @($env:ANDROID_SDK_ROOT, $env:ANDROID_HOME, $(if ($env:LOCALAPPDATA) { Join-Path $env:LOCALAPPDATA "Android\Sdk" }))) {
            if ([string]::IsNullOrWhiteSpace($sdkRoot) -or -not (Test-Path (Join-Path $sdkRoot "build-tools"))) { continue }
            $hit = Get-ChildItem (Join-Path $sdkRoot "build-tools") -Directory | Sort-Object Name -Descending |
                ForEach-Object { Join-Path $_.FullName "aapt2.exe" } | Where-Object { Test-Path $_ } | Select-Object -First 1
            if ($hit) { $aapt2Path = $hit; break }
        }
    }
    if (-not $aapt2Path) { throw "aapt2 is required to read versionName/versionCode from the APK itself." }
    $badging = @(& $aapt2Path dump badging $apk 2>&1) -join "`n"
    $apkInfo = [regex]::Match($badging, "versionCode='(?<c>\d+)'\s+versionName='(?<n>[^']*)'")
    if (-not $apkInfo.Success) { throw "Could not read versionCode/versionName from the APK ($apk)." }
    if ($apkInfo.Groups['n'].Value -cne $Version) { throw "APK versionName '$($apkInfo.Groups['n'].Value)' does not equal release version $Version." }
    if ([int]$apkInfo.Groups['c'].Value -ne $gradle.Code) { throw "APK versionCode $($apkInfo.Groups['c'].Value) does not match Gradle versionCode $($gradle.Code)." }

    $previousCode = $PreviousAndroidVersionCode
    $previousSource = "parameter"
    if ($previousCode -lt 0 -and -not [string]::IsNullOrWhiteSpace($PreviousManifest)) {
        if (-not (Test-Path -LiteralPath $PreviousManifest -PathType Leaf)) { throw "Previous manifest not found: $PreviousManifest" }
        $prev = Get-Content -LiteralPath $PreviousManifest -Raw | ConvertFrom-Json
        if ($prev.PSObject.Properties.Name -contains 'android' -and $null -ne $prev.android) { $previousCode = [int]$prev.android.versionCode; $previousSource = "previous manifest" }
    }
    if ($previousCode -lt 0) {
        $fullGradle = [IO.Path]::GetFullPath($AndroidGradleFile)
        $fullRoot = [IO.Path]::GetFullPath($root).TrimEnd('\') + '\'
        if ($fullGradle.StartsWith($fullRoot, [StringComparison]::OrdinalIgnoreCase)) {
            $relativeGradle = $fullGradle.Substring($fullRoot.Length).Replace('\', '/')
            $commits = @(& git -C $root log --format=%H -n 400 -- $relativeGradle 2>$null)
            if ($LASTEXITCODE -eq 0) {
                foreach ($commit in $commits) {
                    $old = @(& git -C $root show "${commit}:$relativeGradle" 2>$null) -join "`n"
                    if ($LASTEXITCODE -ne 0) { continue }
                    $oldVersion = Read-GradleVersion $old
                    if ($null -ne $oldVersion -and $oldVersion.Name -cne $Version) { $previousCode = $oldVersion.Code; $previousSource = "git history ($($oldVersion.Name) @ $($commit.Substring(0, 12)))"; break }
                }
            }
        }
    }
    if ($previousCode -lt 0) { throw "Could not determine the previous Android versionCode; pass -PreviousAndroidVersionCode or -PreviousManifest." }
    if ($gradle.Code -le $previousCode) { throw "Android versionCode $($gradle.Code) must be greater than the previous release's $previousCode ($previousSource)." }
    $androidVersionReport = [ordered]@{ versionName = $gradle.Name; versionCode = $gradle.Code; previousVersionCode = $previousCode; previousSource = $previousSource }
    Write-Host "[release verify] Android versionName=$($gradle.Name) versionCode=$($gradle.Code) > previous $previousCode ($previousSource): PASS"

    $manifestAssets += [ordered]@{ fileName = [IO.Path]::GetFileName($apk); sizeBytes = (Get-Item -LiteralPath $apk).Length; sha256 = (Get-CanonicalHash $apk) }
    Write-Host "[release verify] Android provenance + signing: PASS"
} else {
    Write-Host "[release verify] Android artifact not required by this invocation; signed Android publication remains a separate explicit gate."
}

Write-Host "[release verify] PASS - release assets are internally consistent for $Version @ $SourceCommit"

if (-not [string]::IsNullOrWhiteSpace($ManifestOutput)) {
    $treeLines = ($expectedPayload.Keys | Sort-Object { $_ } -CaseSensitive | ForEach-Object { "$_`:$($expectedPayload[$_])" }) -join "`n"
    $payloadTreeHash = [BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash([Text.Encoding]::UTF8.GetBytes($treeLines))).Replace('-', '').ToLowerInvariant()
    $manifest = [ordered]@{
        schemaVersion = 1
        version = $Version
        sourceCommit = $SourceCommit
        tagVerified = [bool]$RequireTag
        androidIncluded = [bool]$RequireAndroid
        buildMode = [string]$provenance.buildMode
        sourceTreeClean = [bool]$provenance.sourceTreeClean
        payloadFileCount = $expectedPayload.Count
        payloadTreeSha256 = $payloadTreeHash
        android = $androidVersionReport
        embeddedCommitBinaries = $embeddedCommitReport
        assets = $manifestAssets
    }
    $manifestPath = [IO.Path]::GetFullPath($ManifestOutput)
    New-Item -ItemType Directory -Path (Split-Path -Parent $manifestPath) -Force | Out-Null
    [IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json -Depth 6), [Text.UTF8Encoding]::new($false))
    Write-Host "[release verify] candidate manifest written: $manifestPath"
}
