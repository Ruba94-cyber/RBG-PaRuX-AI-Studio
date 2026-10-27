param(
    [string]$OutputPath = "",
    [string]$Configuration = "Release",
    [string]$ProductVersion = "1.5.1",
    [ValidateSet("windows", "android", "all")][string]$Scope = "all"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $repoRoot ("artifacts\sbom\SBOM.{0}.cyclonedx.json" -f $Scope)
}
# PowerShell's own provider location ($PWD, tracked by Get-Location/Set-Location) can diverge from
# .NET's [Environment]::CurrentDirectory - most reliably after this same session has invoked a
# native executable - so resolving a relative -OutputPath through [System.IO.Path]::GetFullPath
# can silently land the SBOM in the wrong directory with no error. GetUnresolvedProviderPathFromPSPath
# resolves relative to PowerShell's actual current location instead.
if (-not [System.IO.Path]::IsPathRooted($OutputPath)) {
    $OutputPath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputPath)
}
$androidRoot = Join-Path $repoRoot "src\PaRuX.Android"
$gradle = Join-Path $androidRoot "gradlew.bat"
$runtimeLock = Join-Path $repoRoot "packaging\windows-baseline-runtime.lock.json"

# These are the .NET projects whose publish output is actually harvested into the Windows MSI.
# Tests are intentionally excluded: an MSI SBOM must describe shipped software, not every package
# needed to develop/test the repository. ProjectReference dependencies are traversed by dotnet's
# resolved package graph, so PaRuX.Contracts does not need to be listed separately here.
$windowsProjects = @(
    (Join-Path $repoRoot "src\PaRuX.App\PaRuX.App.csproj"),
    (Join-Path $repoRoot "src\PaRuX.Server\PaRuX.Server.csproj"),
    (Join-Path $repoRoot "src\PaRuX.Cli\PaRuX.Cli.csproj"),
    (Join-Path $repoRoot "src\PaRuX.UpdateHelper\PaRuX.UpdateHelper.csproj"),
    (Join-Path $repoRoot "src\PaRuX.WinMlWorker\PaRuX.WinMlWorker.csproj")
)

if ($Scope -in @("windows", "all")) {
    foreach ($project in $windowsProjects) {
        if (-not (Test-Path -LiteralPath $project -PathType Leaf)) { throw "Windows release project not found: $project" }
    }
    if (-not (Test-Path -LiteralPath $runtimeLock -PathType Leaf)) { throw "Runtime lock not found: $runtimeLock" }
}
if ($Scope -in @("android", "all")) {
    if (-not (Test-Path -LiteralPath $gradle -PathType Leaf)) { throw "Android Gradle wrapper not found: $gradle" }
}

# Use PowerShell's call operator rather than ProcessStartInfo.ArgumentList. The MSI build invokes
# powershell.exe and a clean Windows machine may therefore run Windows PowerShell 5.1/.NET
# Framework, where ProcessStartInfo.ArgumentList does not exist. stderr stays separate so restore
# warnings cannot corrupt JSON/text captured from stdout.
function Invoke-Captured([string]$FileName, [string[]]$Arguments, [string]$WorkingDirectory) {
    $stderrFile = [System.IO.Path]::GetTempFileName()
    Push-Location $WorkingDirectory
    try {
        $stdoutLines = @(& $FileName @Arguments 2> $stderrFile)
        $exitCode = $LASTEXITCODE
        $stdout = $stdoutLines -join [Environment]::NewLine
        $stderr = if (Test-Path -LiteralPath $stderrFile) { Get-Content -LiteralPath $stderrFile -Raw } else { "" }
        if ($exitCode -ne 0) {
            throw "$FileName failed with exit code $exitCode`:`n$stderr`n$stdout"
        }
        return $stdout
    }
    finally {
        Pop-Location
        Remove-Item -LiteralPath $stderrFile -Force -ErrorAction SilentlyContinue
    }
}

$components = @{}
function Add-Component([string]$Key, $Component) {
    if (-not $components.ContainsKey($Key)) { $components[$Key] = $Component }
}

# ---------------------------------------------------------------------------------------------
# Licence resolution.
#
# Without this every component was emitted with no `licenses` entry at all - 225 of 225 - which
# makes the document unable to answer the main question an SBOM exists to answer for an
# source-available product: what may be redistributed, and under what terms. The data was already on
# this machine: every restored NuGet package carries its own .nuspec in the global packages
# folder, declaring either an SPDX expression, a bundled licence file, or a licence URL.
#
# Read locally and offline, never fetched. A package that is not restored, or that declares no
# licence at all, gets NO licences entry rather than a guessed one - an SBOM that invents a
# licence is worse than one that admits it does not know.
# ---------------------------------------------------------------------------------------------
$script:NuGetPackagesRoot = $null
function Get-NuGetPackagesRoot {
    if ($null -ne $script:NuGetPackagesRoot) { return $script:NuGetPackagesRoot }
    $candidate = $env:NUGET_PACKAGES
    if ([string]::IsNullOrWhiteSpace($candidate)) {
        $candidate = Join-Path $env:USERPROFILE ".nuget\packages"
    }
    $script:NuGetPackagesRoot = $candidate
    return $script:NuGetPackagesRoot
}

$script:LicensesResolved = 0
$script:LicensesUnknown = 0

# The Maven counterpart of the .nuspec lookup: Gradle keeps each resolved module's POM in its own
# module cache, and a POM declares <licenses>. Same rule - read locally, never fetched, and a
# module whose POM is not cached or declares nothing gets no licences entry rather than a guess.
$script:GradleModulesRoot = Join-Path $env:USERPROFILE ".gradle\caches\modules-2\files-2.1"

function Get-MavenLicense([string]$Group, [string]$Artifact, [string]$Version) {
    $license = Resolve-MavenLicenseNode $Group $Artifact $Version 0
    if ($null -eq $license) { $script:LicensesUnknown++; return $null }
    $script:LicensesResolved++
    return $license
}

# Maven POMs inherit <licenses> from a <parent>, and the parent POM is cached by Gradle alongside
# the child. Not following it left the whole Guava family - guava, failureaccess,
# listenablefuture, annotations, auto-value-annotations, core - with no licence at all, which is
# the difference between "we could not determine this" and "we did not look".
function Resolve-MavenLicenseNode([string]$Group, [string]$Artifact, [string]$Version, [int]$Depth) {
    # Parent chains are short; the cap only stops a malformed or circular one from looping.
    if ($Depth -gt 8) { return $null }
    $moduleDir = Join-Path (Join-Path (Join-Path $script:GradleModulesRoot $Group) $Artifact) $Version
    if (-not (Test-Path -LiteralPath $moduleDir)) { return $null }

    # Gradle stores each artifact under a per-file hash directory, so the POM's exact path is not
    # predictable - find it rather than constructing it.
    $pom = Get-ChildItem -LiteralPath $moduleDir -Recurse -Filter "*.pom" -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if ($null -eq $pom) { return $null }

    try { [xml]$xml = Get-Content -LiteralPath $pom.FullName -Raw }
    catch { return $null }

    $licenseNode = $xml.SelectSingleNode("//*[local-name()='licenses']/*[local-name()='license']")
    if ($null -eq $licenseNode) {
        # No licences of its own: inherit from the parent POM, exactly as Maven itself would.
        $parent = $xml.SelectSingleNode("/*[local-name()='project']/*[local-name()='parent']")
        if ($null -eq $parent) { return $null }
        $parentGroup = $parent.SelectSingleNode("*[local-name()='groupId']")
        $parentArtifact = $parent.SelectSingleNode("*[local-name()='artifactId']")
        $parentVersion = $parent.SelectSingleNode("*[local-name()='version']")
        if ($null -eq $parentGroup -or $null -eq $parentArtifact -or $null -eq $parentVersion) { return $null }
        return Resolve-MavenLicenseNode `
            ([string]$parentGroup.InnerText).Trim() `
            ([string]$parentArtifact.InnerText).Trim() `
            ([string]$parentVersion.InnerText).Trim() `
            ($Depth + 1)
    }

    $nameNode = $licenseNode.SelectSingleNode("*[local-name()='name']")
    $urlNode = $licenseNode.SelectSingleNode("*[local-name()='url']")
    $name = if ($null -ne $nameNode) { ([string]$nameNode.InnerText).Trim() } else { "" }
    $url = if ($null -ne $urlNode) { ([string]$urlNode.InnerText).Trim() } else { "" }
    if ([string]::IsNullOrWhiteSpace($name) -and [string]::IsNullOrWhiteSpace($url)) { return $null }
    # A POM declares a free-text name, not an SPDX id, so it is recorded as a name. The one
    # exception is the Apache 2.0 wording every AndroidX POM uses verbatim, which maps to a real
    # SPDX id unambiguously - mapping anything less certain would be inventing precision.
    if ($name -match '^The Apache Software License, Version 2\.0$' -or $name -match '^Apache License, Version 2\.0$') {
        return [ordered]@{ expression = "Apache-2.0" }
    }
    $license = [ordered]@{}
    if (-not [string]::IsNullOrWhiteSpace($name)) { $license["name"] = $name }
    if (-not [string]::IsNullOrWhiteSpace($url)) { $license["url"] = $url }
    return [ordered]@{ license = $license }
}

function Get-NuGetLicense([string]$Id, [string]$Version) {
    $root = Get-NuGetPackagesRoot
    $lower = $Id.ToLowerInvariant()
    $nuspec = Join-Path (Join-Path (Join-Path $root $lower) $Version) "$lower.nuspec"
    if (-not (Test-Path -LiteralPath $nuspec)) { $script:LicensesUnknown++; return $null }

    try { [xml]$xml = Get-Content -LiteralPath $nuspec -Raw }
    catch { $script:LicensesUnknown++; return $null }

    $metadata = $xml.package.metadata
    if ($null -eq $metadata) { $script:LicensesUnknown++; return $null }

    $licenseNode = $metadata.SelectSingleNode("*[local-name()='license']")
    if ($null -ne $licenseNode) {
        $type = [string]$licenseNode.GetAttribute("type")
        $value = ([string]$licenseNode.InnerText).Trim()
        if (-not [string]::IsNullOrWhiteSpace($value)) {
            $script:LicensesResolved++
            # An SPDX expression is exactly CycloneDX's `expression`; a bundled licence file can
            # only be named, not interpreted, so it is recorded as a name rather than an id.
            if ($type -eq "expression") { return [ordered]@{ expression = $value } }
            return [ordered]@{ license = [ordered]@{ name = "See bundled license file: $value" } }
        }
    }

    $urlNode = $metadata.SelectSingleNode("*[local-name()='licenseUrl']")
    if ($null -ne $urlNode) {
        $url = ([string]$urlNode.InnerText).Trim()
        if (-not [string]::IsNullOrWhiteSpace($url)) {
            $script:LicensesResolved++
            return [ordered]@{ license = [ordered]@{ url = $url } }
        }
    }

    $script:LicensesUnknown++
    return $null
}

# `dotnet list package --format json` omits the "topLevelPackages"/"transitivePackages" keys
# entirely for a framework that has zero packages in that category (e.g. a thin CLI project with
# no NuGet references at all). Under Set-StrictMode -Version Latest, a plain $framework.topLevelPackages
# access then throws PropertyNotFoundException instead of returning $null. This accessor treats a
# missing key the same as an empty array.
function Get-JsonArrayProperty($JsonObject, [string]$Name) {
    $property = $JsonObject.PSObject.Properties[$Name]
    if ($null -eq $property -or $null -eq $property.Value) { return @() }
    return @($property.Value)
}

if ($Scope -in @("windows", "all")) {
    # Ask the SDK for each shipped project's resolved graph. This keeps test-only packages such as
    # xUnit out of the MSI SBOM while still including every top-level/transitive NuGet resolved for
    # the actual Desktop/Server/CLI/UpdateHelper/Windows-ML-worker release surfaces.
    foreach ($projectPath in $windowsProjects) {
        $dotnetJson = Invoke-Captured "dotnet" @(
            "list", $projectPath, "package", "--include-transitive", "--format", "json"
        ) $repoRoot | ConvertFrom-Json

        foreach ($project in @($dotnetJson.projects)) {
            foreach ($framework in @($project.frameworks)) {
                # Each call is wrapped in @(...) at the call site, not only inside the function: a
                # single-element (or empty) pipeline result collapses to a scalar/$null when used
                # directly as a "+" operand instead of staying an array, which is exactly the
                # PropertyNotFoundException-adjacent pitfall this helper exists to avoid.
                $topLevelPackages = @(Get-JsonArrayProperty $framework "topLevelPackages")
                $transitivePackages = @(Get-JsonArrayProperty $framework "transitivePackages")
                foreach ($package in $topLevelPackages + $transitivePackages) {
                    if ($null -eq $package -or [string]::IsNullOrWhiteSpace([string]$package.id)) { continue }
                    $version = [string]$package.resolvedVersion
                    if ([string]::IsNullOrWhiteSpace($version)) { $version = [string]$package.requestedVersion }
                    if ([string]::IsNullOrWhiteSpace($version)) { continue }
                    $id = [string]$package.id
                    $key = "nuget:$id@$version"
                    $component = [ordered]@{
                        type = "library"
                        name = $id
                        version = $version
                        purl = "pkg:nuget/$id@$version"
                        properties = @([ordered]@{ name = "parux:ecosystem"; value = "nuget" })
                    }
                    $license = Get-NuGetLicense $id $version
                    if ($null -ne $license) { $component["licenses"] = @($license) }
                    Add-Component $key $component
                }
            }
        }
    }

    # Runtime components are distributed by the Windows MSI but do not occur in a NuGet graph.
    $lock = Get-Content -LiteralPath $runtimeLock -Raw | ConvertFrom-Json
    foreach ($artifact in @($lock.artifacts)) {
        $id = [string]$artifact.id
        $version = [string]$artifact.version
        $sha = ([string]$artifact.sha256).ToLowerInvariant()
        $url = [string]$artifact.url
        $key = "runtime:$id@$version"
        $runtimeComponent = [ordered]@{
            type = "application"
            name = $id
            version = $version
            hashes = @([ordered]@{ alg = "SHA-256"; content = $sha })
            externalReferences = @([ordered]@{ type = "distribution"; url = $url })
            properties = @(
                [ordered]@{ name = "parux:ecosystem"; value = "runtime" },
                [ordered]@{ name = "parux:lockedArchive"; value = [string]$artifact.archiveName }
            )
        }
        # Taken from the lock file's own declaration, not assumed here: the lock is where these
        # artifacts are pinned by version and hash, so it is also where their terms belong. An
        # entry without a license is emitted without one rather than guessed at.
        $licenseProperty = $artifact.PSObject.Properties["license"]
        $licenseUrlProperty = $artifact.PSObject.Properties["licenseUrl"]
        if ($null -ne $licenseProperty -and -not [string]::IsNullOrWhiteSpace([string]$licenseProperty.Value)) {
            if ($null -ne $licenseUrlProperty -and -not [string]::IsNullOrWhiteSpace([string]$licenseUrlProperty.Value)) {
                $runtimeComponent["licenses"] = @([ordered]@{ license = [ordered]@{
                    name = [string]$licenseProperty.Value
                    url = [string]$licenseUrlProperty.Value
                } })
            } else {
                $runtimeComponent["licenses"] = @([ordered]@{ expression = [string]$licenseProperty.Value })
            }
            $script:LicensesResolved++
        } else {
            $script:LicensesUnknown++
        }
        $redistributablesProperty = $artifact.PSObject.Properties["redistributableFiles"]
        if ($null -ne $redistributablesProperty -and $null -ne $redistributablesProperty.Value) {
            $properties = @($runtimeComponent["properties"])
            foreach ($file in @($redistributablesProperty.Value)) {
                $properties += [ordered]@{ name = "parux:redistributableFile"; value = [string]$file }
            }
            $runtimeComponent["properties"] = $properties
        }
        Add-Component $key $runtimeComponent
    }

    # Vendored llama.cpp conversion scripts are copied next to PaRuX Server and therefore belong
    # in the Windows distribution SBOM even though they are not package-manager dependencies.
    Add-Component "vendored:llama-cpp-conversion" ([ordered]@{
        type = "library"
        name = "llama.cpp LoRA conversion scripts"
        version = "vendored"
        # The exact upstream text is vendored alongside the scripts, so this is a fact about files
        # in this repository rather than an assumption about an upstream project.
        licenses = @([ordered]@{ expression = "MIT" })
        properties = @(
            [ordered]@{ name = "parux:ecosystem"; value = "vendored" },
            [ordered]@{ name = "parux:path"; value = "llama-cpp-conversion/" },
            [ordered]@{ name = "parux:licenseFile"; value = "llama-cpp-conversion/LICENSE-llama.cpp" }
        )
    })
}

if ($Scope -in @("android", "all")) {
    # Android's releaseRuntimeClasspath is the dependency graph that is actually packaged into the
    # release APK. Keeping this separate from the MSI scope avoids forcing Java/Gradle onto a
    # Windows-only MSI build and avoids claiming Android libraries are installed by the MSI.
    $gradleText = Invoke-Captured $gradle @(
        "-p", $androidRoot,
        ":app:dependencies",
        "--configuration", "releaseRuntimeClasspath",
        "--console=plain",
        "--no-daemon"
    ) $androidRoot

    $dependencyRegex = [regex]'(?<group>[A-Za-z0-9_.-]+):(?<artifact>[A-Za-z0-9_.-]+):(?<declared>[^\s()]+)(?:\s+->\s+(?<resolved>[^\s()]+))?'
    foreach ($line in ($gradleText -split "`r?`n")) {
        $match = $dependencyRegex.Match($line)
        if (-not $match.Success) { continue }
        $group = $match.Groups['group'].Value
        $artifact = $match.Groups['artifact'].Value
        $version = if ($match.Groups['resolved'].Success) { $match.Groups['resolved'].Value } else { $match.Groups['declared'].Value }
        if ([string]::IsNullOrWhiteSpace($version) -or $version -match '^(project|unspecified)$') { continue }
        $name = "$group`:$artifact"
        $key = "maven:$name@$version"
        $mavenComponent = [ordered]@{
            type = "library"
            group = $group
            name = $artifact
            version = $version
            purl = "pkg:maven/$group/$artifact@$version"
            properties = @([ordered]@{ name = "parux:ecosystem"; value = "maven" })
        }
        $mavenLicense = Get-MavenLicense $group $artifact $version
        if ($null -ne $mavenLicense) { $mavenComponent["licenses"] = @($mavenLicense) }
        Add-Component $key $mavenComponent
    }
}

$orderedComponents = @($components.GetEnumerator() | Sort-Object Name | ForEach-Object { $_.Value })
$componentName = switch ($Scope) {
    "windows" { "PaRuX Windows" }
    "android" { "PaRuX Android" }
    default { "PaRuX release surfaces" }
}
$metadataProperties = @(
    [ordered]@{ name = "parux:scope"; value = $Scope },
    [ordered]@{ name = "parux:configuration"; value = $Configuration }
)
if ($Scope -in @("windows", "all")) {
    $metadataProperties += [ordered]@{
        name = "parux:dotnetSource"
        value = "resolved package graphs for shipped Desktop/Server/CLI/UpdateHelper/WinMlWorker projects; test projects excluded"
    }
}
if ($Scope -in @("android", "all")) {
    $metadataProperties += [ordered]@{ name = "parux:androidSource"; value = "Gradle releaseRuntimeClasspath" }
}

$sbom = [ordered]@{
    bomFormat = "CycloneDX"
    specVersion = "1.5"
    version = 1
    metadata = [ordered]@{
        timestamp = [DateTime]::UtcNow.ToString("o")
        tools = [ordered]@{
            components = @([ordered]@{
                type = "application"
                name = "PaRuX resolved-graph SBOM generator"
                version = "2"
            })
        }
        component = [ordered]@{
            type = "application"
            name = $componentName
            version = $ProductVersion
            licenses = @([ordered]@{ license = [ordered]@{ id = "FSL-1.1-MIT" } })
        }
        properties = $metadataProperties
    }
    components = $orderedComponents
}

$parent = Split-Path -Parent $OutputPath
if (-not [string]::IsNullOrWhiteSpace($parent)) { New-Item -ItemType Directory -Force -Path $parent | Out-Null }
$sbom | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $OutputPath -Encoding UTF8
Write-Host "Generated CycloneDX SBOM ($Scope) from resolved shipped dependency graphs: $OutputPath"
Write-Host "Components: $($orderedComponents.Count)"
# Reported rather than left implicit: a reader has to be able to tell how much of this document is
# actually licence-attributed. Maven components are not covered by the NuGet .nuspec lookup, so
# they count as unknown here by design rather than by omission.
Write-Host "Licences resolved from local package caches: $script:LicensesResolved (unknown: $script:LicensesUnknown)"
