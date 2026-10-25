[CmdletBinding()]
param(
    [string]$ScratchRoot,
    [string]$AllowedRoot,
    [string]$TaskPrompt,
    [string]$TaskPromptPath,
    [string]$BaseCommit,
    [switch]$SelfTest
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Get-FullPath([string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path)) { throw 'A path is required.' }
    if ($Path -notmatch '^(?:[A-Za-z]:[\\/]|\\\\[^\\/]+[\\/][^\\/]+(?:[\\/]|$))') { throw 'Paths must be absolute.' }
    return [System.IO.Path]::GetFullPath($Path)
}

function Get-PathKey([string]$Path) {
    $full = Get-FullPath $Path
    $root = [System.IO.Path]::GetPathRoot($full)
    if ($full.Length -gt $root.Length) {
        return $full.TrimEnd([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar)
    }
    return $root
}

function Test-PathWithin([string]$Path, [string]$Root, [bool]$AllowEqual = $true) {
    $candidate = Get-PathKey $Path
    $base = Get-PathKey $Root
    if ($candidate.Equals($base, [System.StringComparison]::OrdinalIgnoreCase)) { return $AllowEqual }
    $prefix = $base
    if (-not $prefix.EndsWith([System.IO.Path]::DirectorySeparatorChar)) {
        $prefix += [System.IO.Path]::DirectorySeparatorChar
    }
    return $candidate.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)
}

function Assert-NoReparsePath([string]$Path) {
    $full = Get-FullPath $Path
    $root = [System.IO.Path]::GetPathRoot($full)
    $relative = $full.Substring($root.Length)
    $current = $root
    foreach ($part in @($relative -split '[\\/]+' | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })) {
        $current = Join-Path $current $part
        if (Test-Path -LiteralPath $current) {
            $attributes = [System.IO.File]::GetAttributes($current)
            if (($attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw 'A path component is a reparse point; refusing an ambiguous destination.'
            }
        }
    }
}

function Assert-ProtectedTarget([string]$Path) {
    $full = Get-PathKey $Path
    $driveRoot = Get-PathKey ([System.IO.Path]::GetPathRoot($full))
    if ($full.Equals($driveRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw 'The destination cannot be a drive root.'
    }

    $profileRoots = @(
        [Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile),
        $env:USERPROFILE
    ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -Unique
    foreach ($profileRoot in $profileRoots) {
        if ($full.Equals((Get-PathKey $profileRoot), [System.StringComparison]::OrdinalIgnoreCase)) {
            throw 'The destination cannot be a user profile root.'
        }
    }
}

function Assert-ScratchLayout([string]$RequestedScratch, [string]$RequestedAllowedRoot, [string]$PrimaryRoot) {
    if ([string]::IsNullOrWhiteSpace($RequestedScratch)) { throw 'ScratchRoot must be supplied explicitly.' }
    if ([string]::IsNullOrWhiteSpace($RequestedAllowedRoot)) { throw 'AllowedRoot must be supplied explicitly.' }

    $scratch = Get-FullPath $RequestedScratch
    $allowed = Get-FullPath $RequestedAllowedRoot
    $primary = Get-PathKey $PrimaryRoot
    if (-not (Test-Path -LiteralPath $allowed -PathType Container)) { throw 'AllowedRoot must already exist as a directory.' }
    if (-not (Test-PathWithin $scratch $allowed $true)) { throw 'ScratchRoot must be under AllowedRoot.' }

    $copy = [System.IO.Path]::GetFullPath((Join-Path $scratch 'repo'))
    $dogfood = [System.IO.Path]::GetFullPath((Join-Path $scratch '.dogfood'))
    Assert-ProtectedTarget $scratch
    Assert-ProtectedTarget $copy
    if ((Test-PathWithin $scratch $primary $true) -or (Test-PathWithin $copy $primary $true)) {
        throw 'ScratchRoot and its copy must resolve outside the primary checkout.'
    }

    Assert-NoReparsePath $allowed
    Assert-NoReparsePath $scratch
    Assert-NoReparsePath $copy
    Assert-NoReparsePath $dogfood
    if (Test-Path -LiteralPath $copy) {
        if (-not (Test-Path -LiteralPath $copy -PathType Container)) { throw 'The copy target exists and is not a directory.' }
        $firstChild = Get-ChildItem -LiteralPath $copy -Force | Select-Object -First 1
        if ($null -ne $firstChild) { throw 'The copy target already exists and is not empty.' }
    }
    return [pscustomobject]@{ Scratch = $scratch; Allowed = $allowed; Copy = $copy; Dogfood = $dogfood; Primary = $primary }
}

function Invoke-Git([string]$WorkingDirectory, [string[]]$Arguments) {
    $previousPreference = $ErrorActionPreference
    $ErrorActionPreference = 'SilentlyContinue'
    try { $output = & git -C $WorkingDirectory @Arguments 2>$null; $gitExitCode = $LASTEXITCODE }
    finally { $ErrorActionPreference = $previousPreference }
    if ($gitExitCode -ne 0) { throw ('git failed: ' + ($Arguments -join ' ')) }
    return (@($output) -join "`n").Trim()
}

function Invoke-GitAtSource([string[]]$Arguments) {
    $previousPreference = $ErrorActionPreference
    $ErrorActionPreference = 'SilentlyContinue'
    try { $output = & git @Arguments 2>$null; $gitExitCode = $LASTEXITCODE }
    finally { $ErrorActionPreference = $previousPreference }
    if ($gitExitCode -ne 0) { throw ('git failed: ' + ($Arguments -join ' ')) }
    return (@($output) -join "`n").Trim()
}

function Invoke-DogfoodCopy([string]$RequestedScratch, [string]$RequestedAllowedRoot, [string]$PromptText, [string]$PromptFile, [string]$RequestedBaseCommit, [string]$PrimaryRoot) {
    if ([string]::IsNullOrWhiteSpace($PromptText) -eq [string]::IsNullOrWhiteSpace($PromptFile)) {
        throw 'Supply exactly one of TaskPrompt or TaskPromptPath.'
    }
    $layout = Assert-ScratchLayout $RequestedScratch $RequestedAllowedRoot $PrimaryRoot
    if (-not [string]::IsNullOrWhiteSpace($PromptFile)) {
        $promptSource = Get-FullPath $PromptFile
        if (-not (Test-PathWithin $promptSource $layout.Scratch $false) -or (Test-PathWithin $promptSource $layout.Copy $true)) {
            throw 'TaskPromptPath must be a file in ScratchRoot outside the repository copy.'
        }
        Assert-NoReparsePath $promptSource
        if (-not (Test-Path -LiteralPath $promptSource -PathType Leaf)) { throw 'TaskPromptPath must identify an existing file.' }
        $PromptText = [System.IO.File]::ReadAllText($promptSource, [System.Text.Encoding]::UTF8)
    }
    if ([string]::IsNullOrWhiteSpace($PromptText)) { throw 'The task prompt cannot be empty.' }

    if (-not (Test-Path -LiteralPath $layout.Scratch)) {
        New-Item -ItemType Directory -Path $layout.Scratch -Force | Out-Null
    }
    $promptDestination = Join-Path $layout.Dogfood 'task.md'
    $baseDestination = Join-Path $layout.Dogfood 'base-commit.txt'
    if ((Test-Path -LiteralPath $promptDestination) -or (Test-Path -LiteralPath $baseDestination)) {
        throw 'The .dogfood task metadata already exists; choose a fresh scratch root.'
    }

    if (-not [string]::IsNullOrWhiteSpace($RequestedBaseCommit) -and $RequestedBaseCommit -notmatch '^[0-9a-fA-F]{40,64}$') {
        throw 'BaseCommit must be a full commit hash.'
    }

    $baseExpression = if ([string]::IsNullOrWhiteSpace($RequestedBaseCommit)) { 'refs/heads/main^{commit}' } else { $RequestedBaseCommit + '^{commit}' }
    $resolvedBase = Invoke-GitAtSource @('-C', $PrimaryRoot, 'rev-parse', '--verify', $baseExpression)
    if ($resolvedBase -notmatch '^[0-9a-fA-F]{40,64}$') { throw 'The base commit did not resolve to a commit hash.' }

    $previousPreference = $ErrorActionPreference
    $ErrorActionPreference = 'SilentlyContinue'
    try { $cloneOutput = & git clone --quiet --no-hardlinks --no-checkout $PrimaryRoot $layout.Copy 2>$null; $cloneExitCode = $LASTEXITCODE }
    finally { $ErrorActionPreference = $previousPreference }
    if ($cloneExitCode -ne 0) { throw 'git clone from the primary checkout failed.' }
    [void](Invoke-Git $layout.Copy @('checkout', '--quiet', '--detach', $resolvedBase))

    $origin = Invoke-Git $layout.Copy @('remote', 'get-url', 'origin')
    if ([string]::IsNullOrWhiteSpace($origin)) { throw 'The disposable copy did not contain the expected origin remote.' }
    [void](Invoke-Git $layout.Copy @('remote', 'remove', 'origin'))
    $remainingRemotes = Invoke-Git $layout.Copy @('remote', '-v')
    if (-not [string]::IsNullOrWhiteSpace($remainingRemotes)) { throw 'The disposable copy still has a configured remote.' }

    if (-not (Test-Path -LiteralPath $layout.Dogfood -PathType Container)) {
        New-Item -ItemType Directory -Path $layout.Dogfood -Force | Out-Null
    }
    $utf8 = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText($promptDestination, $PromptText, $utf8)
    [System.IO.File]::WriteAllText($baseDestination, $resolvedBase + "`n", $utf8)

    return [pscustomobject]@{ CopyPath = $layout.Copy; TaskPromptPath = $promptDestination; BaseCommit = $resolvedBase }
}

function Invoke-StartSelfTest {
    $fixtureRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('parux-dogfood-start-' + [Guid]::NewGuid().ToString('N'))
    $primary = Join-Path $fixtureRoot 'primary'
    $allowed = Join-Path $fixtureRoot 'allowed'
    $outside = Join-Path $fixtureRoot 'outside'
    New-Item -ItemType Directory -Path $primary, $allowed, $outside -Force | Out-Null
    $previousGlobalConfig = $env:GIT_CONFIG_GLOBAL
    $previousNoSystem = $env:GIT_CONFIG_NOSYSTEM
    $previousXdgConfig = $env:XDG_CONFIG_HOME
    $previousHome = $env:HOME
    $env:GIT_CONFIG_GLOBAL = Join-Path $fixtureRoot 'gitconfig'
    $env:GIT_CONFIG_NOSYSTEM = '1'
    $env:XDG_CONFIG_HOME = Join-Path $fixtureRoot 'xdg-config'
    $env:HOME = $fixtureRoot
    try {
        & git init --quiet --initial-branch=main $primary 2>$null
        if ($LASTEXITCODE -ne 0) { throw 'Self-test could not initialize its temporary git fixture.' }
        Set-Content -LiteralPath (Join-Path $primary 'README.md') -Value 'fixture' -Encoding UTF8
        & git -C $primary add README.md 2>$null
        if ($LASTEXITCODE -ne 0) { throw 'Self-test could not stage its temporary git fixture.' }
        & git -C $primary -c user.name=Harness -c user.email=example@example.com commit --quiet -m fixture 2>$null
        if ($LASTEXITCODE -ne 0) { throw 'Self-test could not commit its temporary git fixture.' }

        $rejected = 0
        try { [void](Assert-ScratchLayout (Join-Path $primary 'nested') $fixtureRoot $primary) } catch { $rejected++ }
        if ($rejected -ne 1) { throw 'Refusal check failed for a target inside the primary checkout.' }

        $rejected = 0
        try { [void](Assert-ScratchLayout (Join-Path $outside 'nested') $allowed $primary) } catch { $rejected++ }
        if ($rejected -ne 1) { throw 'Refusal check failed for a target outside the allowed root.' }

        $rejected = 0
        try { [void](Assert-ScratchLayout '' $allowed $primary) } catch { $rejected++ }
        if ($rejected -ne 1) { throw 'Refusal check failed when ScratchRoot was omitted.' }

        $rejected = 0
        try { [void](Assert-ScratchLayout 'relative-scratch' $allowed $primary) } catch { $rejected++ }
        if ($rejected -ne 1) { throw 'Refusal check failed for a relative ScratchRoot.' }
        $rejected = 0
        try { [void](Assert-ScratchLayout 'C:drive-relative-scratch' $allowed $primary) } catch { $rejected++ }
        if ($rejected -ne 1) { throw 'Refusal check failed for a drive-relative ScratchRoot.' }

        $nonEmptyScratch = Join-Path $allowed 'nonempty'
        $nonEmptyCopy = Join-Path $nonEmptyScratch 'repo'
        New-Item -ItemType Directory -Path $nonEmptyCopy -Force | Out-Null
        Set-Content -LiteralPath (Join-Path $nonEmptyCopy 'marker.txt') -Value 'fixture'
        $rejected = 0
        try { [void](Assert-ScratchLayout $nonEmptyScratch $allowed $primary) } catch { $rejected++ }
        if ($rejected -ne 1) { throw 'Refusal check failed for a non-empty copy target.' }

        $rejected = 0
        try { Assert-ProtectedTarget ([System.IO.Path]::GetPathRoot($fixtureRoot)) } catch { $rejected++ }
        if ($rejected -ne 1) { throw 'Refusal check failed for a drive-root destination.' }

        $profile = [Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile)
        if (-not [string]::IsNullOrWhiteSpace($profile)) {
            $rejected = 0
            try { Assert-ProtectedTarget $profile } catch { $rejected++ }
            if ($rejected -ne 1) { throw 'Refusal check failed for a user-profile-root destination.' }
        }

        $successRoot = Join-Path $allowed 'success'
        $result = Invoke-DogfoodCopy $successRoot $allowed 'self-test prompt' '' '' $primary
        if (-not (Test-Path -LiteralPath $result.CopyPath -PathType Container)) { throw 'Valid copy creation did not produce a repository directory.' }
        if (Test-Path -LiteralPath (Join-Path $result.CopyPath '.git\config') -PathType Leaf) {
            $copyRemotes = Invoke-Git $result.CopyPath @('remote', '-v')
            if (-not [string]::IsNullOrWhiteSpace($copyRemotes)) { throw 'The successful copy retained a remote.' }
        }
        if (-not (Test-Path -LiteralPath $result.TaskPromptPath -PathType Leaf)) { throw 'The task prompt was not written outside the copy.' }
        if (Test-PathWithin $result.TaskPromptPath $result.CopyPath $true) { throw 'The task prompt was written inside the working tree.' }
    }
    finally {
        if ($null -eq $previousGlobalConfig) { Remove-Item Env:GIT_CONFIG_GLOBAL -ErrorAction SilentlyContinue } else { $env:GIT_CONFIG_GLOBAL = $previousGlobalConfig }
        if ($null -eq $previousNoSystem) { Remove-Item Env:GIT_CONFIG_NOSYSTEM -ErrorAction SilentlyContinue } else { $env:GIT_CONFIG_NOSYSTEM = $previousNoSystem }
        if ($null -eq $previousXdgConfig) { Remove-Item Env:XDG_CONFIG_HOME -ErrorAction SilentlyContinue } else { $env:XDG_CONFIG_HOME = $previousXdgConfig }
        if ($null -eq $previousHome) { Remove-Item Env:HOME -ErrorAction SilentlyContinue } else { $env:HOME = $previousHome }
        if (Test-Path -LiteralPath $fixtureRoot) {
            $tempRoot = Get-PathKey ([System.IO.Path]::GetTempPath())
            if (Test-PathWithin $fixtureRoot $tempRoot $false) {
                Remove-Item -LiteralPath $fixtureRoot -Recurse -Force -ErrorAction SilentlyContinue
            }
        }
    }
}

$primaryCheckout = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
try {
    if ($SelfTest) {
        Invoke-StartSelfTest
        Write-Output 'PASS'
        exit 0
    }

    $result = Invoke-DogfoodCopy $ScratchRoot $AllowedRoot $TaskPrompt $TaskPromptPath $BaseCommit $primaryCheckout
    Write-Output 'PASS'
    exit 0
}
catch {
    Write-Output ('FAIL: ' + $_.Exception.Message)
    exit 1
}
