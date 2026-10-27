<##
.SYNOPSIS
    Exercises the local release-asset/provenance gate with isolated synthetic assets.

.DESCRIPTION
    The production RSA/APK signature checks are already covered by the shared C# verifier and
    Android signer respectively. This script stubs only those executable boundaries so it can
    test the PowerShell gate's required-file, identity, uniqueness, size and hash checks without
    a release private key, a real release artifact, network access or publication side effects.
##>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
    Write-Host '[release asset tests] SKIP (Windows command shims are required)'
    return
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$validator = Join-Path $repoRoot 'scripts\verify-release-assets.ps1'
$androidScript = Join-Path $repoRoot 'scripts\publish-android-apk.ps1'
if (-not (Test-Path -LiteralPath $validator -PathType Leaf)) {
    throw "Release asset verifier was not found: $validator"
}

$tempBase = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
$testRoot = [IO.Path]::GetFullPath((Join-Path $tempBase ('parux-release-asset-tests-' + [Guid]::NewGuid().ToString('N'))))
if (-not $testRoot.StartsWith($tempBase, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Refusing to create test fixtures outside the system temporary directory.'
}
$shimRoot = Join-Path $testRoot 'shims'
$installerRoot = Join-Path $testRoot 'windows'
$androidRoot = Join-Path $testRoot 'android'
$version = '1.9.4'
$sourceCommit = '0123456789abcdef0123456789abcdef01234567'
$msiName = "PaRuX-$version-win-x64.msi"
$envelopeName = "PaRuX-$version-win-x64.update.json"
$winProvenanceName = "PaRuX-$version-release-provenance.json"
$apkName = "PaRuX-$version-android.apk"
$androidProvenanceName = "PaRuX-$version-android-provenance.json"
$oldPath = $env:Path

function New-StubExe([string]$Commit, [string]$Destination) {
    # Windows PowerShell 5.1 can compile a console exe carrying a real AssemblyInformationalVersion,
    # which is what the verifier reads through FileVersionInfo on the payload binaries.
    $code = 'Add-Type -TypeDefinition ''[assembly: System.Reflection.AssemblyInformationalVersion("' + $version + '+' + $Commit + '")] public static class P { public static int Main() { return 0; } }'' -OutputType ConsoleApplication -OutputAssembly ''' + $Destination + ''''
    # -EncodedCommand avoids native-argument quote stripping differences between PowerShell 5.1 and 7.
    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($code))
    & powershell.exe -NoProfile -EncodedCommand $encoded
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $Destination)) { throw 'Could not compile the stub payload executable.' }
}

function New-PayloadTree([string]$Directory, [string]$ExePath) {
    foreach ($relative in @('PaRuX.exe', 'PaRuX.Server\PaRuX.Server.exe', 'Cli\parux.exe', 'UpdateHelper\PaRuX.UpdateHelper.exe')) {
        $target = Join-Path $Directory $relative
        New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
        Copy-Item -LiteralPath $ExePath -Destination $target
    }
    New-Item -ItemType Directory -Path (Join-Path $Directory 'Assets') -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $Directory 'Assets\parux.ico'), 'synthetic icon', [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText((Join-Path $Directory 'LICENSE.txt'), 'synthetic license', [Text.UTF8Encoding]::new($false))

    # The release verifier requires the bundled CUDA 13.4 baseline: its lock, staging manifest, files and NVIDIA notice.
    $cudaRelative = 'runtime-baseline/llama.cpp-b11871-cuda13.4'
    $cudaDir = Join-Path $Directory ('PaRuX.Server\' + $cudaRelative.Replace('/', '\'))
    New-Item -ItemType Directory -Path $cudaDir -Force | Out-Null
    $manifestFiles = @()
    foreach ($name in @('llama-server.exe', 'cudart64_13.dll', 'cublas64_13.dll', 'cublasLt64_13.dll')) {
        $file = Join-Path $cudaDir $name
        [IO.File]::WriteAllText($file, ('synthetic ' + $name), [Text.UTF8Encoding]::new($false))
        $manifestFiles += [ordered]@{ path = ($cudaRelative + '/' + $name); sizeBytes = (Get-Item -LiteralPath $file).Length; sha256 = (Get-Hash $file) }
    }
    $dlls = @('cudart64_13.dll', 'cublas64_13.dll', 'cublasLt64_13.dll')
    $destination = 'PaRuX.Server/' + $cudaRelative
    $lock = [ordered]@{
        schemaVersion = 1; platform = 'win-x64'
        artifacts = @(
            [ordered]@{ id = 'llama.cpp-cuda13'; version = 'b11871'; license = 'MIT'; sizeBytes = 160060670
                sha256 = ('a' * 64); destination = $destination; requiredFiles = @('llama-server.exe') },
            [ordered]@{ id = 'cuda-runtime-13.4'; version = '13.4'; license = 'NVIDIA CUDA Toolkit EULA'
                licenseUrl = 'https://docs.nvidia.com/cuda/eula/index.html'; sizeBytes = 423535356; sha256 = ('b' * 64)
                destination = $destination; requiredFiles = $dlls; redistributableFiles = $dlls }
        )
    }
    $baselineDir = Join-Path $Directory 'PaRuX.Server\runtime-baseline'
    Write-Json (Join-Path $baselineDir 'baseline-lock.json') $lock
    Write-Json (Join-Path $baselineDir 'staging-manifest.json') ([ordered]@{ schemaVersion = 1; platform = 'win-x64'; files = $manifestFiles })
    [IO.File]::WriteAllText((Join-Path $Directory 'NVIDIA_CUDA_NOTICE.txt'),
        ('Attachment A https://docs.nvidia.com/cuda/eula/index.html ' + ($dlls -join ' ')), [Text.UTF8Encoding]::new($false))
}

function Get-PayloadEntries([string]$Directory) {
    $full = [IO.Path]::GetFullPath($Directory).TrimEnd('\')
    return @(Get-ChildItem -LiteralPath $full -Recurse -File | Sort-Object FullName | ForEach-Object {
        [ordered]@{ path = $_.FullName.Substring($full.Length + 1).Replace('\', '/'); sizeBytes = $_.Length; sha256 = (Get-Hash $_.FullName) }
    })
}

function Get-Hash([string]$Path) {
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Write-Json([string]$Path, [object]$Value) {
    [IO.File]::WriteAllText($Path, ($Value | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
}

function New-Fixture {
    $windowsDir = Join-Path $testRoot ([Guid]::NewGuid().ToString('N'))
    $winDir = Join-Path $windowsDir 'windows'
    $andDir = Join-Path $windowsDir 'android'
    New-Item -ItemType Directory -Path $winDir, $andDir -Force | Out-Null

    $msiPath = Join-Path $winDir $msiName
    $envelopePath = Join-Path $winDir $envelopeName
    [IO.File]::WriteAllBytes($msiPath, [byte[]](1, 2, 3, 4, 5, 6))
    [IO.File]::WriteAllText($envelopePath, '{"payloadBase64":"synthetic","signatureBase64":"synthetic"}', [Text.UTF8Encoding]::new($false))
    $msiPayload = Join-Path $windowsDir 'msi-payload'
    $publishPayload = Join-Path $windowsDir 'publish-payload'
    Copy-Item -LiteralPath $script:goodPayload -Destination $msiPayload -Recurse
    Copy-Item -LiteralPath $script:goodPayload -Destination $publishPayload -Recurse
    $gradlePath = Join-Path $windowsDir 'build.gradle.kts'
    [IO.File]::WriteAllText($gradlePath, "android { defaultConfig { versionCode = 7`n versionName = `"$version`" } }", [Text.UTF8Encoding]::new($false))
    $windowsProvenance = [ordered]@{
        schemaVersion = 1
        repository = 'Ruba94-cyber/RBG-PaRuX-AI-Studio'
        version = $version
        sourceCommit = $sourceCommit
        signingVerification = 'rsa-sha256-self-verified'
        buildMode = 'release'
        sourceTreeClean = $true
        payloadFiles = (Get-PayloadEntries $msiPayload)
        artifacts = @(
            [ordered]@{ fileName = $msiName; sizeBytes = (Get-Item -LiteralPath $msiPath).Length; sha256 = (Get-Hash $msiPath) },
            [ordered]@{ fileName = $envelopeName; sizeBytes = (Get-Item -LiteralPath $envelopePath).Length; sha256 = (Get-Hash $envelopePath) }
        )
    }
    Write-Json (Join-Path $winDir $winProvenanceName) $windowsProvenance

    $apkPath = Join-Path $andDir $apkName
    [IO.File]::WriteAllBytes($apkPath, [byte[]](9, 8, 7, 6, 5, 4))
    $androidProvenance = [ordered]@{
        schemaVersion = 1
        repository = 'Ruba94-cyber/RBG-PaRuX-AI-Studio'
        version = $version
        sourceCommit = $sourceCommit
        signingVerification = 'apksigner-verified'
        artifacts = @(
            [ordered]@{ fileName = $apkName; sizeBytes = (Get-Item -LiteralPath $apkPath).Length; sha256 = (Get-Hash $apkPath) }
        )
    }
    Write-Json (Join-Path $andDir $androidProvenanceName) $androidProvenance

    return @{
        Root = $windowsDir
        Windows = $winDir
        Android = $andDir
        Msi = $msiPath
        MsiPayload = $msiPayload
        PublishPayload = $publishPayload
        Gradle = $gradlePath
        Envelope = $envelopePath
        WindowsProvenance = (Join-Path $winDir $winProvenanceName)
        Apk = $apkPath
        AndroidProvenance = (Join-Path $andDir $androidProvenanceName)
    }
}

function Invoke-Validator([hashtable]$Fixture, [switch]$RequireAndroid, [string]$PublishDirectory = '', [hashtable]$Extra = @{}) {
    $env:PARUX_TEST_MSI_PAYLOAD = $Fixture.MsiPayload
    $arguments = @{
        Version = $version
        SourceCommit = $sourceCommit
        InstallerDirectory = $Fixture.Windows
    }
    if ($PublishDirectory) { $arguments.PublishDirectory = $PublishDirectory }
    if ($RequireAndroid) {
        $arguments.RequireAndroid = $true
        $arguments.AndroidDirectory = $Fixture.Android
        $arguments.AndroidGradleFile = $Fixture.Gradle
        $arguments.PreviousAndroidVersionCode = 6
    }
    foreach ($key in $Extra.Keys) { $arguments[$key] = $Extra[$key] }
    try {
        $output = & $validator @arguments 2>&1
        return @{ Passed = $true; Output = ($output | Out-String) }
    }
    catch {
        return @{ Passed = $false; Output = $_.Exception.Message }
    }
}

function Assert-Passed([hashtable]$Result, [string]$Description) {
    if (-not $Result.Passed) { throw "$Description unexpectedly failed: $($Result.Output)" }
}

function Assert-Rejected([hashtable]$Result, [string]$Description, [string]$ExpectedMessage) {
    if ($Result.Passed) { throw "$Description unexpectedly passed." }
    if ($Result.Output -notmatch [regex]::Escape($ExpectedMessage)) {
        throw "$Description failed for the wrong reason. Expected '$ExpectedMessage'; got '$($Result.Output)'"
    }
}

try {
    New-Item -ItemType Directory -Path $shimRoot -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $shimRoot 'dotnet.cmd'), "@echo off`r`nexit /b 0`r`n", [Text.Encoding]::ASCII)
    [IO.File]::WriteAllText((Join-Path $shimRoot 'apksigner.bat'), "@echo off`r`nexit /b 0`r`n", [Text.Encoding]::ASCII)
    $msiexecShim = "@echo off`r`nrem cmd splits TARGETDIR=path into two tokens; %~5 is the unquoted path.`r`nset TD=%~5`r`nxcopy `"%PARUX_TEST_MSI_PAYLOAD%\*`" `"%TD%\PFiles64\PaRuX\`" /E /I /Q /Y >nul`r`nexit /b %ERRORLEVEL%`r`n"
    [IO.File]::WriteAllText((Join-Path $shimRoot 'msiexec.cmd'), $msiexecShim, [Text.Encoding]::ASCII)
    $aaptShim = "@echo off`r`necho package: name='com.example' versionCode='%PARUX_TEST_APK_CODE%' versionName='%PARUX_TEST_APK_NAME%'`r`nexit /b 0`r`n"
    [IO.File]::WriteAllText((Join-Path $shimRoot 'aapt2.cmd'), $aaptShim, [Text.Encoding]::ASCII)
    $env:PARUX_TEST_APK_CODE = '7'
    $env:PARUX_TEST_APK_NAME = $version
    $env:Path = $shimRoot + [IO.Path]::PathSeparator + $oldPath
    $exeGood = Join-Path $testRoot 'stub-good.exe'
    $exeOther = Join-Path $testRoot 'stub-other.exe'
    New-StubExe $sourceCommit $exeGood
    New-StubExe '1123456789abcdef0123456789abcdef01234567' $exeOther
    $script:goodPayload = Join-Path $testRoot 'template-good'
    $script:otherPayload = Join-Path $testRoot 'template-other'
    New-PayloadTree $script:goodPayload $exeGood
    New-PayloadTree $script:otherPayload $exeOther
    if ((Get-Command dotnet).Source -ne (Join-Path $shimRoot 'dotnet.cmd')) {
        throw 'Could not put the release-verifier test shim first on PATH.'
    }

    $fixture = New-Fixture
    Assert-Passed (Invoke-Validator $fixture) 'Consistent Windows MSI/envelope/provenance set'

    $fixture = New-Fixture
    [IO.File]::WriteAllBytes($fixture.Msi, [byte[]](6, 5, 4, 3, 2, 1))
    Assert-Rejected (Invoke-Validator $fixture) 'Modified MSI bytes' 'SHA-256 mismatch'

    $fixture = New-Fixture
    $provenance = Get-Content -LiteralPath $fixture.WindowsProvenance -Raw | ConvertFrom-Json
    $provenance.sourceCommit = '1123456789abcdef0123456789abcdef01234567'
    Write-Json $fixture.WindowsProvenance $provenance
    Assert-Rejected (Invoke-Validator $fixture) 'Wrong Windows source commit' 'source commit mismatch'

    $fixture = New-Fixture
    $provenance = Get-Content -LiteralPath $fixture.WindowsProvenance -Raw | ConvertFrom-Json
    $provenance.artifacts = @($provenance.artifacts) + @($provenance.artifacts[0])
    Write-Json $fixture.WindowsProvenance $provenance
    Assert-Rejected (Invoke-Validator $fixture) 'Duplicate MSI provenance entries' 'exactly one entry'

    $fixture = New-Fixture
    Assert-Passed (Invoke-Validator $fixture -RequireAndroid) 'Consistent Windows and Android release assets'

    $fixture = New-Fixture
    [IO.File]::WriteAllBytes($fixture.Apk, [byte[]](4, 3, 2, 1, 9, 8))
    Assert-Rejected (Invoke-Validator $fixture -RequireAndroid) 'Modified APK bytes' 'APK SHA-256 mismatch'

    $fixture = New-Fixture
    Remove-Item -LiteralPath $fixture.Apk -Force
    Assert-Rejected (Invoke-Validator $fixture -RequireAndroid) 'Missing required APK' 'Required Android release artifact is missing'

    # Candidate manifest binds commit + version + every verified asset hash.
    $fixture = New-Fixture
    $manifestPath = Join-Path $fixture.Root 'candidate\manifest.json'
    $manifestArgs = @{ Version = $version; SourceCommit = $sourceCommit; InstallerDirectory = $fixture.Windows
        RequireAndroid = $true; AndroidDirectory = $fixture.Android; ManifestOutput = $manifestPath
        AndroidGradleFile = $fixture.Gradle; PreviousAndroidVersionCode = 6 }
    $env:PARUX_TEST_MSI_PAYLOAD = $fixture.MsiPayload
    & $validator @manifestArgs | Out-Null
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if ([string]$manifest.sourceCommit -cne $sourceCommit -or [string]$manifest.version -cne $version) { throw 'Candidate manifest does not bind version/commit.' }
    $expected = @{ $msiName = (Get-Hash $fixture.Msi); $envelopeName = (Get-Hash $fixture.Envelope); $apkName = (Get-Hash $fixture.Apk) }
    if (@($manifest.assets).Count -ne 3) { throw 'Candidate manifest must list MSI, envelope and APK.' }
    foreach ($asset in @($manifest.assets)) {
        if ($expected[[string]$asset.fileName] -cne [string]$asset.sha256) { throw "Candidate manifest hash mismatch for $($asset.fileName)." }
    }

    # No manifest is produced for a rejected set.
    $fixture = New-Fixture
    [IO.File]::WriteAllBytes($fixture.Msi, [byte[]](6, 5, 4, 3, 2, 1))
    $manifestPath = Join-Path $fixture.Root 'candidate\manifest.json'
    $env:PARUX_TEST_MSI_PAYLOAD = $fixture.MsiPayload
    try { & $validator -Version $version -SourceCommit $sourceCommit -InstallerDirectory $fixture.Windows -ManifestOutput $manifestPath | Out-Null } catch { }
    if (Test-Path -LiteralPath $manifestPath) { throw 'A candidate manifest was written for a rejected asset set.' }

    # --- payload binding ---------------------------------------------------------------------
    $fixture = New-Fixture
    Assert-Passed (Invoke-Validator $fixture -PublishDirectory $fixture.PublishPayload) 'Publish tree equal to the MSI payload'

    # Publish directory from another commit + a valid MSI must fail.
    $fixture = New-Fixture
    Remove-Item -LiteralPath $fixture.PublishPayload -Recurse -Force
    Copy-Item -LiteralPath $script:otherPayload -Destination $fixture.PublishPayload -Recurse
    Assert-Rejected (Invoke-Validator $fixture -PublishDirectory $fixture.PublishPayload) 'Publish dir from another commit' 'Publish directory differs from the provenance payload'

    # An MSI whose payload binaries come from another commit fails even if provenance matches that payload.
    $fixture = New-Fixture
    Remove-Item -LiteralPath $fixture.MsiPayload -Recurse -Force
    Copy-Item -LiteralPath $script:otherPayload -Destination $fixture.MsiPayload -Recurse
    $provenance = Get-Content -LiteralPath $fixture.WindowsProvenance -Raw | ConvertFrom-Json
    $provenance.payloadFiles = (Get-PayloadEntries $fixture.MsiPayload)
    Write-Json $fixture.WindowsProvenance $provenance
    Assert-Rejected (Invoke-Validator $fixture) 'MSI binaries embedding another commit' 'embeds source commit'

    # MSI payload differing from provenance (swapped binary / extra file) is refused.
    $fixture = New-Fixture
    [IO.File]::WriteAllText((Join-Path $fixture.MsiPayload 'extra.dll'), 'smuggled', [Text.UTF8Encoding]::new($false))
    Assert-Rejected (Invoke-Validator $fixture) 'Extra file inside the MSI' 'unexpected extra.dll'
    $fixture = New-Fixture
    [IO.File]::WriteAllText((Join-Path $fixture.MsiPayload 'LICENSE.txt'), 'tampered', [Text.UTF8Encoding]::new($false))
    Assert-Rejected (Invoke-Validator $fixture) 'Modified file inside the MSI' 'hash mismatch LICENSE.txt'

    # Legacy provenance without payload hashes, a dirty tree, or a non-release build is refused.
    $fixture = New-Fixture
    $provenance = Get-Content -LiteralPath $fixture.WindowsProvenance -Raw | ConvertFrom-Json
    $provenance.PSObject.Properties.Remove('payloadFiles')
    Write-Json $fixture.WindowsProvenance $provenance
    Assert-Rejected (Invoke-Validator $fixture) 'Provenance without payloadFiles' 'carries no payloadFiles'
    $fixture = New-Fixture
    $provenance = Get-Content -LiteralPath $fixture.WindowsProvenance -Raw | ConvertFrom-Json
    $provenance.sourceTreeClean = $false
    Write-Json $fixture.WindowsProvenance $provenance
    Assert-Rejected (Invoke-Validator $fixture) 'Provenance of a dirty tree' 'clean source tree'
    $fixture = New-Fixture
    $provenance = Get-Content -LiteralPath $fixture.WindowsProvenance -Raw | ConvertFrom-Json
    $provenance.buildMode = 'debug'
    Write-Json $fixture.WindowsProvenance $provenance
    Assert-Rejected (Invoke-Validator $fixture) 'Non-release provenance' 'release-mode build'

    # --- SBOM binding and manifest facts ---------------------------------------------------------
    $fixture = New-Fixture
    $sbomPath = Join-Path $fixture.Root 'SBOM.windows.cyclonedx.json'
    Write-Json $sbomPath ([ordered]@{ bomFormat = 'CycloneDX'; specVersion = '1.5'; components = @() })
    $manifestPath = Join-Path $fixture.Root 'sbom-manifest.json'
    Assert-Passed (Invoke-Validator $fixture -Extra @{ SbomPath = $sbomPath; ManifestOutput = $manifestPath }) 'Valid SBOM'
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    $sbomEntry = @($manifest.assets | Where-Object { [string]$_.fileName -ceq 'SBOM.windows.cyclonedx.json' })
    if ($sbomEntry.Count -ne 1 -or [string]$sbomEntry[0].sha256 -cne (Get-Hash $sbomPath)) { throw 'Manifest does not bind the SBOM hash.' }
    if ([string]$manifest.buildMode -cne 'release' -or $manifest.sourceTreeClean -ne $true) { throw 'Manifest does not record release mode / clean tree.' }
    if ([int]$manifest.payloadFileCount -lt 5 -or [string]$manifest.payloadTreeSha256 -notmatch '^[0-9a-f]{64}$') { throw 'Manifest does not bind the payload tree.' }
    Write-Json $sbomPath ([ordered]@{ bomFormat = 'NotABom' })
    Assert-Rejected (Invoke-Validator $fixture -Extra @{ SbomPath = $sbomPath }) 'Invalid SBOM' 'not a CycloneDX SBOM'

    # --- Android version rules -------------------------------------------------------------------
    $fixture = New-Fixture
    [IO.File]::WriteAllText($fixture.Gradle, "versionCode = 7`nversionName = `"1.9.3`"", [Text.UTF8Encoding]::new($false))
    Assert-Rejected (Invoke-Validator $fixture -RequireAndroid) 'Gradle versionName mismatch' 'does not equal release version'
    $fixture = New-Fixture
    $env:PARUX_TEST_APK_NAME = '1.9.3'
    Assert-Rejected (Invoke-Validator $fixture -RequireAndroid) 'APK versionName mismatch' 'APK versionName'
    $env:PARUX_TEST_APK_NAME = $version
    $env:PARUX_TEST_APK_CODE = '9'
    Assert-Rejected (Invoke-Validator $fixture -RequireAndroid) 'APK versionCode differs from Gradle' 'does not match Gradle versionCode'
    $env:PARUX_TEST_APK_CODE = '7'
    Assert-Rejected (Invoke-Validator $fixture -RequireAndroid -Extra @{ PreviousAndroidVersionCode = 7 }) 'versionCode not newer' 'must be greater than the previous'
    $prevManifest = Join-Path $fixture.Root 'previous-manifest.json'
    Write-Json $prevManifest ([ordered]@{ android = [ordered]@{ versionCode = 6 } })
    Assert-Passed (Invoke-Validator $fixture -RequireAndroid -Extra @{ PreviousAndroidVersionCode = -1; PreviousManifest = $prevManifest }) 'versionCode newer than previous manifest'
    Write-Json $prevManifest ([ordered]@{ android = [ordered]@{ versionCode = 7 } })
    Assert-Rejected (Invoke-Validator $fixture -RequireAndroid -Extra @{ PreviousAndroidVersionCode = -1; PreviousManifest = $prevManifest }) 'versionCode equal to previous manifest' 'must be greater than the previous'

    # publish-android-apk -LocalOnly: builds+verifies, never calls gh, needs no tag.
    $fakeRepo = Join-Path $testRoot 'android-repo'
    New-Item -ItemType Directory -Path (Join-Path $fakeRepo 'scripts'), (Join-Path $fakeRepo 'src\PaRuX.App'), (Join-Path $fakeRepo 'src\PaRuX.Android') -Force | Out-Null
    Copy-Item -LiteralPath $androidScript -Destination (Join-Path $fakeRepo 'scripts\publish-android-apk.ps1')
    [IO.File]::WriteAllText((Join-Path $fakeRepo 'src\PaRuX.App\PaRuX.App.csproj'), "<Project><PropertyGroup><Version>$version</Version></PropertyGroup></Project>", [Text.UTF8Encoding]::new($false))
    $gradleStub = "@echo off`r`nmkdir app\build\outputs\apk\release 2>nul`r`necho synthetic-apk> app\build\outputs\apk\release\app-release.apk`r`nexit /b 0`r`n"
    [IO.File]::WriteAllText((Join-Path $fakeRepo 'src\PaRuX.Android\gradlew.bat'), $gradleStub, [Text.Encoding]::ASCII)
    $ghMarker = Join-Path $testRoot 'gh-called.txt'
    [IO.File]::WriteAllText((Join-Path $shimRoot 'gh.cmd'), "@echo off`r`necho called> `"$ghMarker`"`r`nexit /b 0`r`n", [Text.Encoding]::ASCII)
    $keystore = Join-Path $testRoot 'synthetic.keystore'
    [IO.File]::WriteAllText($keystore, 'synthetic', [Text.UTF8Encoding]::new($false))
    & git -C $fakeRepo init -q
    & git -C $fakeRepo add -A
    & git -C $fakeRepo -c user.name=test -c user.email=test@example.invalid commit -q -m synthetic
    $fakeHead = (& git -C $fakeRepo rev-parse HEAD | Select-Object -First 1).Trim()
    $oldEnv = @{}
    foreach ($n in 'PARUX_ANDROID_KEYSTORE', 'PARUX_ANDROID_STORE_PASSWORD', 'PARUX_ANDROID_KEY_PASSWORD') { $oldEnv[$n] = [Environment]::GetEnvironmentVariable($n) }
    try {
        $env:PARUX_ANDROID_KEYSTORE = $keystore
        $env:PARUX_ANDROID_STORE_PASSWORD = 'synthetic'
        $env:PARUX_ANDROID_KEY_PASSWORD = 'synthetic'
        $fakeScript = Join-Path $fakeRepo 'scripts\publish-android-apk.ps1'
        try { & $fakeScript -Tag "v$version" | Out-Null; $uploadRefused = $false } catch { $uploadRefused = ($_.Exception.Message -match 'unavailable locally') }
        if (-not $uploadRefused) { throw 'Upload mode must refuse when the release tag is missing.' }
        if (Test-Path -LiteralPath $ghMarker) { throw 'gh was invoked although the tag precondition failed.' }
        & $fakeScript -Tag "v$version" -LocalOnly | Out-Null
        if (Test-Path -LiteralPath $ghMarker) { throw '-LocalOnly invoked gh.' }
        $apkProvenance = Get-Content -LiteralPath (Join-Path $fakeRepo "src\PaRuX.Android\app\build\outputs\apk\release\PaRuX-$version-android-provenance.json") -Raw | ConvertFrom-Json
        if ([string]$apkProvenance.sourceCommit -cne $fakeHead) { throw '-LocalOnly provenance does not bind the exact HEAD commit.' }
        # An existing tag at another commit is still refused even locally.
        [IO.File]::WriteAllText((Join-Path $fakeRepo 'later.txt'), 'x')
        & git -C $fakeRepo add -A
        & git -C $fakeRepo -c user.name=test -c user.email=test@example.invalid commit -q -m later
        & git -C $fakeRepo tag "v$version" HEAD~1
        try { & $fakeScript -Tag "v$version" -LocalOnly | Out-Null; $mismatchRefused = $false } catch { $mismatchRefused = ($_.Exception.Message -match 'points to') }
        if (-not $mismatchRefused) { throw '-LocalOnly accepted a tag pointing at a different commit.' }
    }
    finally {
        foreach ($n in $oldEnv.Keys) { [Environment]::SetEnvironmentVariable($n, $oldEnv[$n]) }
    }

    Write-Host '[release asset tests] PASS (28 scenarios; signature boundaries stubbed and tested separately)'
}
finally {
    $env:Path = $oldPath
    $fullTestRoot = [IO.Path]::GetFullPath($testRoot)
    if ($fullTestRoot.StartsWith($tempBase, [StringComparison]::OrdinalIgnoreCase) -and (Test-Path -LiteralPath $fullTestRoot)) {
        Remove-Item -LiteralPath $fullTestRoot -Recurse -Force
    }
}
