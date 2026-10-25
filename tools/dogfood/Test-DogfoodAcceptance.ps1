[CmdletBinding()]
param(
    [string]$CopyPath,
    [string]$BaseCommit,
    [string]$BuildProjectPath,
    [string]$TestProjectPath,
    [string]$TestFilter,
    [int]$TimeoutMinutes = 30,
    [switch]$SelfTest
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Get-FullPath([string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path) -or $Path -notmatch '^(?:[A-Za-z]:[\\/]|\\\\[^\\/]+[\\/][^\\/]+(?:[\\/]|$))') {
        throw 'Paths must be supplied as absolute paths.'
    }
    return [System.IO.Path]::GetFullPath($Path)
}

function Test-PathWithin([string]$Path, [string]$Root, [bool]$AllowEqual = $true) {
    $candidate = Get-FullPath $Path
    $base = Get-FullPath $Root
    $root = [System.IO.Path]::GetPathRoot($base)
    if ($base.Length -gt $root.Length) { $base = $base.TrimEnd([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar) }
    $root = [System.IO.Path]::GetPathRoot($candidate)
    if ($candidate.Length -gt $root.Length) { $candidate = $candidate.TrimEnd([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar) }
    if ($candidate.Equals($base, [System.StringComparison]::OrdinalIgnoreCase)) { return $AllowEqual }
    $prefix = $base
    if (-not $prefix.EndsWith([System.IO.Path]::DirectorySeparatorChar)) { $prefix += [System.IO.Path]::DirectorySeparatorChar }
    return $candidate.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)
}

function Get-PrimaryCheckoutRoot {
    $scriptRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
    $previousPreference = $ErrorActionPreference
    $ErrorActionPreference = 'SilentlyContinue'
    try { $top = & git -C $scriptRoot rev-parse --show-toplevel 2>$null; $gitExitCode = $LASTEXITCODE }
    finally { $ErrorActionPreference = $previousPreference }
    if ($gitExitCode -ne 0) { throw 'Could not identify the primary checkout containing this harness.' }
    return Get-FullPath ([string]$top)
}

function Assert-DisposableCopy([string]$Copy) {
    $primary = Get-PrimaryCheckoutRoot
    if ((Test-PathWithin $Copy $primary $true) -or (Test-PathWithin $primary $Copy $true)) {
        throw 'CopyPath overlaps the primary checkout; refusing independent acceptance there.'
    }
}

function Test-TargetedTestFilter([string]$Filter) {
    if ($Filter -match '\*' -or $Filter -notmatch '(?i)^\s*FullyQualifiedName\s*=\s*([^\s|&]+)\s*$') { return $false }
    $target = $Matches[1]
    $parts = @($target.Split('.') | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    return $parts.Count -ge 3
}

function Test-PathHasReparsePoint([string]$Path, [string]$Repository) {
    $root = Get-FullPath $Repository
    $current = Get-FullPath $Path
    if (-not (Test-PathWithin $current $root $true)) { return $true }
    while ($true) {
        $item = Get-Item -LiteralPath $current -Force -ErrorAction SilentlyContinue
        if ($null -ne $item -and (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0)) { return $true }
        try {
            $attributes = [System.IO.File]::GetAttributes($current)
            if (($attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) { return $true }
        }
        catch {
            if (Test-Path -LiteralPath $current) { throw }
        }
        if ($current.Equals($root, [System.StringComparison]::OrdinalIgnoreCase)) { break }
        $parent = Split-Path -Parent $current
        if ([string]::IsNullOrWhiteSpace($parent) -or $parent.Equals($current, [System.StringComparison]::OrdinalIgnoreCase)) { break }
        $current = $parent
    }
    return $false
}

function Assert-SafeDogfoodOutputs([string]$Copy, [string]$Dogfood, [string[]]$OutputPaths) {
    $expectedDogfood = Get-FullPath (Join-Path (Split-Path -Parent $Copy) '.dogfood')
    $sidecar = Get-FullPath $Dogfood
    if (-not $sidecar.Equals($expectedDogfood, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw 'Dogfood output directory must be the sibling .dogfood directory.'
    }
    if (-not (Test-Path -LiteralPath $sidecar -PathType Container)) { throw 'The sibling .dogfood directory is missing.' }

    $primary = Get-PrimaryCheckoutRoot
    if ((Test-PathWithin $sidecar $primary $true) -or (Test-PathWithin $primary $sidecar $true)) {
        throw 'The sibling .dogfood directory overlaps the primary checkout.'
    }
    if (Test-PathHasReparsePoint $sidecar ([System.IO.Path]::GetPathRoot($sidecar))) {
        throw 'The sibling .dogfood path contains a reparse point.'
    }

    foreach ($outputPath in $OutputPaths) {
        $output = Get-FullPath $outputPath
        if (-not (Test-PathWithin $output $sidecar $false)) { throw 'A dogfood output path escapes the sibling .dogfood directory.' }
        if ((Test-PathWithin $output $primary $true) -or (Test-PathWithin $primary $output $true)) {
            throw 'A dogfood output path overlaps the primary checkout.'
        }
        if (Test-PathHasReparsePoint $output ([System.IO.Path]::GetPathRoot($output))) {
            throw 'A dogfood output path traverses a reparse point.'
        }
    }
}

function ConvertTo-WindowsArgument([string]$Value) {
    $builder = New-Object System.Text.StringBuilder
    [void]$builder.Append('"')
    $slashes = 0
    foreach ($character in $Value.ToCharArray()) {
        if ($character -eq '\') { $slashes++; continue }
        if ($character -eq '"') {
            [void]$builder.Append(('\' * (2 * $slashes + 1)))
            [void]$builder.Append('"')
            $slashes = 0
            continue
        }
        if ($slashes -gt 0) { [void]$builder.Append(('\' * $slashes)); $slashes = 0 }
        [void]$builder.Append($character)
    }
    if ($slashes -gt 0) { [void]$builder.Append(('\' * (2 * $slashes))) }
    [void]$builder.Append('"')
    return $builder.ToString()
}

function Invoke-GitCapture([string]$Repository, [string[]]$Arguments) {
    $gitCommand = Get-Command git -CommandType Application -ErrorAction Stop | Select-Object -First 1
    $startInfo = New-Object System.Diagnostics.ProcessStartInfo
    $startInfo.FileName = $gitCommand.Source
    $startInfo.Arguments = (@($Arguments | ForEach-Object { ConvertTo-WindowsArgument $_ }) -join ' ')
    $startInfo.WorkingDirectory = $Repository
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.StandardOutputEncoding = [System.Text.Encoding]::UTF8
    $startInfo.StandardErrorEncoding = [System.Text.Encoding]::UTF8
    $process = New-Object System.Diagnostics.Process
    $process.StartInfo = $startInfo
    if (-not $process.Start()) { throw 'Could not start git.' }
    $stdoutTask = $process.StandardOutput.ReadToEndAsync()
    $stderrTask = $process.StandardError.ReadToEndAsync()
    $process.WaitForExit()
    $stdout = $stdoutTask.Result
    $stderr = $stderrTask.Result
    $exitCode = $process.ExitCode
    $process.Dispose()
    if ($exitCode -ne 0) { throw ('git failed: ' + ($Arguments -join ' ') + '. ' + $stderr.Trim()) }
    return $stdout
}

function Split-NulPaths([string]$Text) {
    return @($Text.Split([char]0, [System.StringSplitOptions]::RemoveEmptyEntries))
}

function Get-GitBlobBytes([string]$Repository, [string]$ObjectPath) {
    $git = Get-Command git -CommandType Application -ErrorAction Stop | Select-Object -First 1
    $arguments = @('-C', $Repository, 'cat-file', 'blob', $ObjectPath)
    $startInfo = New-Object System.Diagnostics.ProcessStartInfo
    $startInfo.FileName = $git.Source
    $startInfo.Arguments = (@($arguments | ForEach-Object { ConvertTo-WindowsArgument ([string]$_) }) -join ' ')
    $startInfo.WorkingDirectory = $Repository
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $process = New-Object System.Diagnostics.Process
    $process.StartInfo = $startInfo
    if (-not $process.Start()) { throw 'git cat-file did not start.' }
    $errorTask = $process.StandardError.ReadToEndAsync()
    $memory = New-Object System.IO.MemoryStream
    try {
        $process.StandardOutput.BaseStream.CopyTo($memory)
        $process.WaitForExit()
        if ($process.ExitCode -ne 0) { throw 'The base version of a changed file could not be read.' }
        return ,$memory.ToArray()
    }
    finally {
        $memory.Dispose()
        $process.Dispose()
    }
}

function Get-AddedLines([string]$OldText, [string]$NewText) {
    $oldLines = if ($OldText.Length -eq 0) { @() } else { @($OldText -split "`r?`n") }
    $newLines = if ($NewText.Length -eq 0) { @() } else { @($NewText -split "`r?`n") }
    $remaining = New-Object 'System.Collections.Generic.Dictionary[string,int]' ([System.StringComparer]::Ordinal)
    foreach ($line in $oldLines) {
        if ($remaining.ContainsKey($line)) { $remaining[$line]++ } else { $remaining.Add($line, 1) }
    }
    $added = New-Object System.Collections.Generic.List[string]
    foreach ($line in $newLines) {
        if ($remaining.ContainsKey($line) -and $remaining[$line] -gt 0) { $remaining[$line]-- }
        else { [void]$added.Add($line) }
    }
    return $added.ToArray()
}

function Get-ChangedPaths([string]$Repository, [string]$InitialCommit) {
    $resolved = (Invoke-GitCapture $Repository @('rev-parse', '--verify', ($InitialCommit + '^{commit}'))).Trim()
    if ($resolved -notmatch '^[0-9a-fA-F]{40,64}$') { throw 'BaseCommit did not resolve to a commit hash.' }
    $tracked = Split-NulPaths (Invoke-GitCapture $Repository @('diff', '--name-only', '-z', '--no-renames', $resolved, '--'))
    $untracked = Split-NulPaths (Invoke-GitCapture $Repository @('ls-files', '--others', '-z'))
    $all = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($path in @($tracked) + @($untracked)) {
        $normalized = ([string]$path).Replace('\', '/')
        if (-not [string]::IsNullOrWhiteSpace($normalized)) { [void]$all.Add($normalized) }
    }
    return @($all | Sort-Object)
}

function ConvertFrom-AddedFileBytes([byte[]]$Bytes) {
    if ($Bytes.Length -ge 2 -and $Bytes[0] -eq 255 -and $Bytes[1] -eq 254) {
        return [System.Text.Encoding]::Unicode.GetString($Bytes, 2, $Bytes.Length - 2)
    }
    if ($Bytes.Length -ge 2 -and $Bytes[0] -eq 254 -and $Bytes[1] -eq 255) {
        return (New-Object System.Text.UnicodeEncoding($true, $false)).GetString($Bytes, 2, $Bytes.Length - 2)
    }

    if ($Bytes.Length -ge 8 -and ($Bytes.Length % 2) -eq 0) {
        $sampleLength = [Math]::Min($Bytes.Length, 512)
        $evenZeros = 0
        $oddZeros = 0
        for ($index = 0; $index -lt $sampleLength; $index++) {
            if ($Bytes[$index] -eq 0) {
                if (($index % 2) -eq 0) { $evenZeros++ } else { $oddZeros++ }
            }
        }
        if ($oddZeros -ge [Math]::Max(2, [int]($sampleLength * 0.2)) -and $evenZeros -lt [int]($sampleLength * 0.05)) {
            return [System.Text.Encoding]::Unicode.GetString($Bytes)
        }
        if ($evenZeros -ge [Math]::Max(2, [int]($sampleLength * 0.2)) -and $oddZeros -lt [int]($sampleLength * 0.05)) {
            return (New-Object System.Text.UnicodeEncoding($true, $false)).GetString($Bytes)
        }
    }
    return [System.Text.Encoding]::UTF8.GetString($Bytes)
}

function Get-AddedText([string]$Repository, [string]$InitialCommit, [string[]]$UntrackedPaths) {
    $resolved = (Invoke-GitCapture $Repository @('rev-parse', '--verify', ($InitialCommit + '^{commit}'))).Trim()
    $addedLines = New-Object System.Collections.Generic.List[string]
    $trackedPaths = Split-NulPaths (Invoke-GitCapture $Repository @('diff', '--name-only', '-z', '--no-renames', $resolved, '--'))
    foreach ($relativePath in $trackedPaths) {
        $candidate = [System.IO.Path]::GetFullPath((Join-Path $Repository $relativePath.Replace('/', [System.IO.Path]::DirectorySeparatorChar)))
        if (-not (Test-PathWithin $candidate $Repository $false) -or (Test-PathHasReparsePoint $candidate $Repository)) { continue }
        if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) { continue }
        $newText = ConvertFrom-AddedFileBytes ([System.IO.File]::ReadAllBytes($candidate))
        $oldText = ''
        try { $oldText = ConvertFrom-AddedFileBytes (Get-GitBlobBytes $Repository ($resolved + ':' + $relativePath)) }
        catch { $oldText = '' }
        foreach ($line in (Get-AddedLines $oldText $newText)) { [void]$addedLines.Add($line) }
    }
    foreach ($relativePath in $UntrackedPaths) {
        $candidate = [System.IO.Path]::GetFullPath((Join-Path $Repository $relativePath.Replace('/', [System.IO.Path]::DirectorySeparatorChar)))
        if (-not (Test-PathWithin $candidate $Repository $false)) { continue }
        if (Test-PathHasReparsePoint $candidate $Repository) { continue }
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            $bytes = [System.IO.File]::ReadAllBytes($candidate)
            [void]$addedLines.Add((ConvertFrom-AddedFileBytes $bytes))
        }
    }
    return $addedLines.ToArray()
}

function Get-SecretLikeCauses([string[]]$AddedText) {
    $joined = @($AddedText) -join "`n"
    $patterns = @(
        @{ Name = 'private key block'; Regex = '-----BEGIN [A-Z0-9 ]*PRIVATE KEY-----' },
        @{ Name = 'hf-style secret'; Regex = '(?i)\bhf_[A-Za-z0-9]{20,}\b' },
        @{ Name = 'sk-style secret'; Regex = '(?i)\bsk-\S{20,}' },
        @{ Name = 'credential assignment'; Regex = '(?i)["'']?\b(?:password|token|client[_-]?secret|api[_-]?key|secret[_-]?key|access[_-]?key(?:[_-]?id)?|secret[_-]?access[_-]?key)\b["'']?\s*[:=]\s*(?:"[^"\r\n]*"|''[^''\r\n]*''|[^\s,;#]+)' },
        @{ Name = 'AWS access key'; Regex = '\b(?:AKIA|ASIA)[0-9A-Z]{16}\b' },
        @{ Name = 'GitHub token'; Regex = '\bgh[pousr]_[A-Za-z0-9]{20,}\b' }
    )
    $causes = New-Object System.Collections.Generic.List[string]
    foreach ($entry in $patterns) {
        if ([System.Text.RegularExpressions.Regex]::IsMatch($joined, $entry.Regex)) {
            [void]$causes.Add('Added content contains a secret-like ' + $entry.Name + '.')
        }
    }
    return $causes.ToArray()
}

function Resolve-ProjectPath([string]$RequestedPath, [string]$Repository, [string]$Label) {
    if ([string]::IsNullOrWhiteSpace($RequestedPath)) { throw ($Label + ' is required.') }
    $candidate = if ([System.IO.Path]::IsPathRooted($RequestedPath)) { Get-FullPath $RequestedPath } else { [System.IO.Path]::GetFullPath((Join-Path $Repository $RequestedPath)) }
    if (-not (Test-PathWithin $candidate $Repository $false)) { throw ($Label + ' must be inside the disposable copy.') }
    if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) { throw ($Label + ' must identify an existing project file.') }
    if (Test-PathHasReparsePoint $candidate $Repository) { throw ($Label + ' cannot traverse a reparse point.') }
    return $candidate
}

function Invoke-DotnetCheck([string]$Kind, [string]$Project, [string]$Filter, [string]$LogPath, [int]$TimeoutMinutes, [string]$Repository) {
    $dotnet = Get-Command dotnet -CommandType Application -ErrorAction Stop | Select-Object -First 1
    $arguments = if ($Kind -eq 'build') { @('build', $Project, '--no-restore') } else { @('test', $Project, '--no-restore', '--filter', $Filter) }
    $argumentLine = (@($arguments | ForEach-Object { ConvertTo-WindowsArgument $_ }) -join ' ')
    $stderrPath = [System.IO.Path]::ChangeExtension($LogPath, '.stderr.log')
    $startInfo = New-Object System.Diagnostics.ProcessStartInfo
    $startInfo.FileName = $dotnet.Source
    $startInfo.Arguments = $argumentLine
    $startInfo.WorkingDirectory = $Repository
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.StandardOutputEncoding = [System.Text.Encoding]::UTF8
    $startInfo.StandardErrorEncoding = [System.Text.Encoding]::UTF8
    $process = New-Object System.Diagnostics.Process
    $process.StartInfo = $startInfo
    if (-not $process.Start()) { throw 'dotnet did not start.' }
    $stdoutTask = $process.StandardOutput.ReadToEndAsync()
    $stderrTask = $process.StandardError.ReadToEndAsync()
    $timedOut = -not $process.WaitForExit($TimeoutMinutes * 60000)
    if ($timedOut) {
        $treeStopped = $false
        try { $process.Kill($true); $treeStopped = $true }
        catch {
            $taskkill = Join-Path $env:SystemRoot 'System32\taskkill.exe'
            if (Test-Path -LiteralPath $taskkill -PathType Leaf) {
                try {
                    $killer = Start-Process -FilePath $taskkill -ArgumentList ('/PID ' + $process.Id + ' /T /F') -PassThru -WindowStyle Hidden
                    if (-not $killer.WaitForExit(5000)) { try { $killer.Kill() } catch {} }
                    $treeStopped = $true
                }
                catch {}
            }
            if (-not $treeStopped) { try { $process.Kill() } catch {} }
        }
        try { [void]$process.WaitForExit(5000) } catch {}
    }
    if ($timedOut) {
        [void]$stdoutTask.Wait(5000)
        [void]$stderrTask.Wait(5000)
    }
    $stdout = if ($stdoutTask.IsCompleted) { $stdoutTask.Result } else { '' }
    $stderr = if ($stderrTask.IsCompleted) { $stderrTask.Result } else { '' }
    Assert-SafeDogfoodOutputs $Repository (Join-Path (Split-Path -Parent $Repository) '.dogfood') @($LogPath, $stderrPath)
    [System.IO.File]::WriteAllText($LogPath, $stdout, (New-Object System.Text.UTF8Encoding($false)))
    [System.IO.File]::WriteAllText($stderrPath, $stderr, (New-Object System.Text.UTF8Encoding($false)))
    if (-not $timedOut) { [void]$process.WaitForExit(); return [pscustomobject]@{ ExitCode = $process.ExitCode; TimedOut = $false } }
    return [pscustomobject]@{ ExitCode = 124; TimedOut = $true }
}

function Write-Verdict([string]$Path, [object]$Verdict) {
    $json = ConvertTo-Json -InputObject $Verdict -Depth 10
    [System.IO.File]::WriteAllText($Path, $json, (New-Object System.Text.UTF8Encoding($false)))
}

function Invoke-AcceptanceCore([string]$RequestedCopyPath, [string]$RequestedBaseCommit, [string]$RequestedBuildProject, [string]$RequestedTestProject, [string]$RequestedFilter, [int]$LimitMinutes, [scriptblock]$Runner) {
    $causes = New-Object System.Collections.Generic.List[string]
    $changedPaths = @()
    $started = [DateTime]::UtcNow
    $buildResult = [pscustomobject]@{ Status = 'not-run'; ExitCode = $null; TimedOut = $false }
    $testResult = [pscustomobject]@{ Status = 'not-run'; ExitCode = $null; TimedOut = $false }
    $copy = $null
    $dogfood = $null
    $verdictPath = $null

    try {
        $copy = Get-FullPath $RequestedCopyPath
        Assert-DisposableCopy $copy
        if (-not (Test-Path -LiteralPath $copy -PathType Container)) { throw 'CopyPath must identify an existing directory.' }
        $dogfood = Join-Path (Split-Path -Parent $copy) '.dogfood'
        Assert-SafeDogfoodOutputs $copy $dogfood @(
            (Join-Path $dogfood 'verdict.json'),
            (Join-Path $dogfood 'build.log'),
            (Join-Path $dogfood 'build.stderr.log'),
            (Join-Path $dogfood 'test.log'),
            (Join-Path $dogfood 'test.stderr.log')
        )
        $verdictPath = Join-Path $dogfood 'verdict.json'

        if ([string]::IsNullOrWhiteSpace($RequestedBaseCommit)) { throw 'BaseCommit is required.' }
        if ($RequestedBaseCommit -notmatch '^[0-9a-fA-F]{40,64}$') { throw 'BaseCommit must be a commit hash.' }
        if ($LimitMinutes -lt 1 -or $LimitMinutes -gt 1440) { throw 'TimeoutMinutes must be between 1 and 1440.' }
        if ([string]::IsNullOrWhiteSpace($RequestedFilter)) { throw 'A targeted TestFilter is required; refusing to run tests without a filter.' }
        if (-not (Test-TargetedTestFilter $RequestedFilter)) { throw 'TestFilter must be an exact dotted FullyQualifiedName for a test method; substring, namespace-wide, and full-suite filters are refused.' }

        $top = (Invoke-GitCapture $copy @('rev-parse', '--show-toplevel')).Trim()
        if (-not (Get-FullPath $top).Equals($copy, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw 'CopyPath must be the root of the disposable git working tree.'
        }
        $buildProject = Resolve-ProjectPath $RequestedBuildProject $copy 'BuildProjectPath'
        $testProject = Resolve-ProjectPath $RequestedTestProject $copy 'TestProjectPath'
        $changedPaths = @(Get-ChangedPaths $copy $RequestedBaseCommit)

        foreach ($path in $changedPaths) {
            if ($path -notmatch '^(?i:src|tests|docs)/') { [void]$causes.Add('Changed path is outside src/, tests/, or docs/: ' + $path) }
            $candidate = [System.IO.Path]::GetFullPath((Join-Path $copy $path.Replace('/', [System.IO.Path]::DirectorySeparatorChar)))
            if (Test-PathHasReparsePoint $candidate $copy) { [void]$causes.Add('Changed path traverses a reparse point: ' + $path) }
            $leaf = [System.IO.Path]::GetFileName($path)
            if ($leaf -match '^(?i:LICENSE|NOTICE|COPYING)' -or $path -match '(?i)licen|third[ _.-]*party[ _.-]*notices?') {
                [void]$causes.Add('A licence or notice file changed: ' + $path)
            }
        }

        $untrackedPaths = Split-NulPaths (Invoke-GitCapture $copy @('ls-files', '--others', '-z'))
        $addedText = Get-AddedText $copy $RequestedBaseCommit $untrackedPaths
        foreach ($cause in (Get-SecretLikeCauses $addedText)) { [void]$causes.Add($cause) }
    }
    catch {
        [void]$causes.Add($_.Exception.Message)
    }

    if ($causes.Count -eq 0) {
        $buildLog = Join-Path $dogfood 'build.log'
        $buildStderr = [System.IO.Path]::ChangeExtension($buildLog, '.stderr.log')
        try {
            $build = & $Runner 'build' $buildProject '' $buildLog $LimitMinutes $copy
            $buildResult = [pscustomobject]@{ Status = if ($build.TimedOut) { 'timed-out' } elseif ($build.ExitCode -eq 0) { 'passed' } else { 'failed' }; ExitCode = $build.ExitCode; TimedOut = [bool]$build.TimedOut }
            if ($build.TimedOut) { [void]$causes.Add('Build timed out.') }
            elseif ($build.ExitCode -ne 0) { [void]$causes.Add('Build failed with exit code ' + $build.ExitCode + '.') }
        }
        catch { [void]$causes.Add('Build could not run: ' + $_.Exception.Message) }

        if ($causes.Count -eq 0) {
            $testLog = Join-Path $dogfood 'test.log'
            try {
                $test = & $Runner 'test' $testProject $RequestedFilter $testLog $LimitMinutes $copy
                $testResult = [pscustomobject]@{ Status = if ($test.TimedOut) { 'timed-out' } elseif ($test.ExitCode -eq 0) { 'passed' } else { 'failed' }; ExitCode = $test.ExitCode; TimedOut = [bool]$test.TimedOut }
                if ($test.TimedOut) { [void]$causes.Add('Targeted test run timed out.') }
                elseif ($test.ExitCode -ne 0) { [void]$causes.Add('Targeted test run failed with exit code ' + $test.ExitCode + '.') }
                else {
                    $testStderr = [System.IO.Path]::ChangeExtension($testLog, '.stderr.log')
                    $testOutput = ''
                    if (Test-Path -LiteralPath $testLog -PathType Leaf) { $testOutput += [System.IO.File]::ReadAllText($testLog) }
                    if (Test-Path -LiteralPath $testStderr -PathType Leaf) { $testOutput += [System.IO.File]::ReadAllText($testStderr) }
                    if ($testOutput -notmatch '(?im)\bPassed:\s*[1-9][0-9]*\b') {
                        $testResult.Status = 'failed'
                        [void]$causes.Add('The targeted test command passed without reporting a passed test.')
                    }
                }
            }
            catch { [void]$causes.Add('Targeted test run could not run: ' + $_.Exception.Message) }
        }
    }

    $status = if ($causes.Count -eq 0) { 'PASS' } else { 'FAIL' }
    $record = [ordered]@{
        schemaVersion = 1
        status = $status
        startedUtc = $started.ToString('o')
        completedUtc = [DateTime]::UtcNow.ToString('o')
        baseCommit = $RequestedBaseCommit
        changedPaths = $changedPaths
        build = $buildResult
        targetedTests = $testResult
        causes = $causes.ToArray()
    }
    if (-not [string]::IsNullOrWhiteSpace($verdictPath)) {
        try {
            Assert-SafeDogfoodOutputs $copy $dogfood @($verdictPath)
            Write-Verdict $verdictPath $record
        }
        catch {
            [void]$causes.Add('Verdict output refused: ' + $_.Exception.Message)
            $status = 'FAIL'
            $record.status = $status
            $record.causes = $causes.ToArray()
            $verdictPath = $null
        }
    }
    return [pscustomobject]@{ Status = $status; Causes = $causes.ToArray(); VerdictPath = $verdictPath; Record = $record }
}

function Invoke-AcceptanceSelfTest {
    $fixtureRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('parux-dogfood-accept-' + [Guid]::NewGuid().ToString('N'))
    $copy = Join-Path $fixtureRoot 'repo'
    $dogfood = Join-Path $fixtureRoot '.dogfood'
    New-Item -ItemType Directory -Path $fixtureRoot, $copy, $dogfood -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $copy 'src'), (Join-Path $copy 'tests'), (Join-Path $copy 'docs') -Force | Out-Null
    $previousGlobalConfig = $env:GIT_CONFIG_GLOBAL
    $previousNoSystem = $env:GIT_CONFIG_NOSYSTEM
    $previousXdgConfig = $env:XDG_CONFIG_HOME
    $previousHome = $env:HOME
    $env:GIT_CONFIG_GLOBAL = Join-Path $fixtureRoot 'gitconfig'
    $env:GIT_CONFIG_NOSYSTEM = '1'
    $env:XDG_CONFIG_HOME = Join-Path $fixtureRoot 'xdg-config'
    $env:HOME = $fixtureRoot
    try {
        & git init --quiet --initial-branch=main $copy 2>$null
        if ($LASTEXITCODE -ne 0) { throw 'Self-test could not initialize its temporary git fixture.' }
        Set-Content -LiteralPath (Join-Path $copy 'src/app.csproj') -Value '<Project />' -Encoding UTF8
        Set-Content -LiteralPath (Join-Path $copy 'src/tracked-secret.cs') -Value 'fixture' -Encoding UTF8
        Set-Content -LiteralPath (Join-Path $copy 'tests/test.csproj') -Value '<Project />' -Encoding UTF8
        Set-Content -LiteralPath (Join-Path $copy 'docs/readme.md') -Value 'fixture' -Encoding UTF8
        & git -C $copy add -- . 2>$null
        & git -C $copy -c user.name=Harness -c user.email=example@example.com commit --quiet -m fixture 2>$null
        if ($LASTEXITCODE -ne 0) { throw 'Self-test could not commit its temporary git fixture.' }
        $base = (Invoke-GitCapture $copy @('rev-parse', 'HEAD')).Trim()
        $primaryResult = Invoke-AcceptanceCore (Get-PrimaryCheckoutRoot) $base 'src/app.csproj' 'tests/test.csproj' 'FullyQualifiedName=Example.Tests.DogfoodHarnessContractTests.AcceptanceContract' 1 { throw 'The runner must not be reached for the primary checkout.' }
        if ($primaryResult.Status -ne 'FAIL' -or -not ($primaryResult.Causes -join ' ' -match 'primary checkout')) {
            throw 'Refusal check failed when acceptance was pointed at the primary checkout.'
        }
        $calls = New-Object 'System.Collections.Generic.List[string]'
        $mode = [pscustomobject]@{ Value = 'pass' }
        $fakeRunner = {
            param($kind, $project, $filter, $logPath, $timeoutMinutes, $repository)
            [void]$calls.Add([string]$kind)
            if ($kind -eq 'test' -and $mode.Value -eq 'no-tests') { [System.IO.File]::WriteAllText($logPath, 'No test matches the filter.') }
            elseif ($kind -eq 'test') { [System.IO.File]::WriteAllText($logPath, 'Passed! - Failed: 0, Passed: 1, Skipped: 0, Total: 1') }
            else { [System.IO.File]::WriteAllText($logPath, 'Build succeeded.') }
            if ($kind -eq 'test' -and [string]::IsNullOrWhiteSpace([string]$filter)) { throw 'Test filter did not reach the runner.' }
            if ($mode.Value -eq 'build-fail' -and $kind -eq 'build') { return [pscustomobject]@{ ExitCode = 23; TimedOut = $false } }
            if ($mode.Value -eq 'test-fail' -and $kind -eq 'test') { return [pscustomobject]@{ ExitCode = 24; TimedOut = $false } }
            return [pscustomobject]@{ ExitCode = 0; TimedOut = $false }
        }.GetNewClosure()

        $missingFilter = Invoke-AcceptanceCore $copy $base 'src/app.csproj' 'tests/test.csproj' '' 1 $fakeRunner
        if ($missingFilter.Status -ne 'FAIL' -or -not ($missingFilter.Causes -join ' ' -match 'TestFilter') -or $calls.Count -ne 0) {
            throw 'Refusal check failed when TestFilter was missing.'
        }

        $mode.Value = 'pass'
        $calls.Clear()
        $passed = Invoke-AcceptanceCore $copy $base 'src/app.csproj' 'tests/test.csproj' 'FullyQualifiedName=Example.Tests.DogfoodHarnessContractTests.AcceptanceContract' 1 $fakeRunner
        if ($passed.Status -ne 'PASS' -or $calls.Count -ne 2 -or $calls[0] -ne 'build' -or $calls[1] -ne 'test') {
            throw ('The acceptance PASS path failed: status=' + $passed.Status + '; calls=' + ($calls -join ',') + '; causes=' + ($passed.Causes -join '; '))
        }

        $reset = {
            & git -C $copy checkout -- . 2>$null
            & git -C $copy clean -fdx -- 2>$null | Out-Null
            if ($LASTEXITCODE -ne 0) { throw 'Self-test could not reset its temporary fixture.' }
            $calls.Clear()
        }.GetNewClosure()

        & $reset
        New-Item -ItemType Directory -Path (Join-Path $copy 'tools') -Force | Out-Null
        Set-Content -LiteralPath (Join-Path $copy 'tools/out-of-scope.txt') -Value 'fixture'
        $scopeFailure = Invoke-AcceptanceCore $copy $base 'src/app.csproj' 'tests/test.csproj' 'FullyQualifiedName=Example.Tests.DogfoodHarnessContractTests.AcceptanceContract' 1 $fakeRunner
        if ($scopeFailure.Status -ne 'FAIL' -or $calls.Count -ne 0 -or -not ($scopeFailure.Causes -join ' ' -match 'outside src/')) {
            throw 'Refusal check failed for a changed path outside the allowed directories.'
        }

        & $reset
        Set-Content -LiteralPath (Join-Path $copy 'docs/LICENSE-policy.txt') -Value 'fixture'
        $licenceFailure = Invoke-AcceptanceCore $copy $base 'src/app.csproj' 'tests/test.csproj' 'FullyQualifiedName=Example.Tests.DogfoodHarnessContractTests.AcceptanceContract' 1 $fakeRunner
        if ($licenceFailure.Status -ne 'FAIL' -or $calls.Count -ne 0 -or -not ($licenceFailure.Causes -join ' ' -match 'licence or notice')) {
            throw 'Refusal check failed for a changed licence file.'
        }

        foreach ($noticeName in @('ThirdPartyNotices.md', 'COPYING.txt')) {
            & $reset
            Set-Content -LiteralPath (Join-Path $copy ('docs/' + $noticeName)) -Value 'fixture'
            $noticeFailure = Invoke-AcceptanceCore $copy $base 'src/app.csproj' 'tests/test.csproj' 'FullyQualifiedName=Example.Tests.DogfoodHarnessContractTests.AcceptanceContract' 1 $fakeRunner
            if ($noticeFailure.Status -ne 'FAIL' -or $calls.Count -ne 0 -or -not ($noticeFailure.Causes -join ' ' -match 'licence or notice')) {
                throw ('Refusal check failed for a notice file: ' + $noticeName)
            }
        }

        $secretCases = @(
            @{ Name = 'hf-style'; Text = 'class Fixture { string key = "hf' + '_' + ('Q' * 20) + '"; }' },
            @{ Name = 'sk-style'; Text = 'class Fixture { string key = "sk-' + ('R' * 20) + '"; }' },
            @{ Name = 'sk-project-style'; Text = 'class Fixture { string key = "sk' + '-proj-' + ('U' * 20) + '"; }' },
            @{ Name = 'private-key'; Text = '-----BEGIN PRIVATE KEY-----' },
            @{ Name = 'password-assignment'; Text = ('pass' + 'word' + '=' + 'example') },
            @{ Name = 'token-assignment'; Text = 'to' + 'ken=' + 'example' },
            @{ Name = 'api-key-assignment'; Text = 'api' + '_key=' + 'example' },
            @{ Name = 'client-secret-assignment'; Text = 'client' + '_' + 'secret' + '=' + 'example' },
            @{ Name = 'quoted-password-key'; Text = '{"' + 'pass' + 'word' + '": "example"}' },
            @{ Name = 'quoted-client-secret-key'; Text = '{"client' + '_secret": "example"}' },
            @{ Name = 'single-quoted-token-key'; Text = '{''to' + 'ken'': ''example''}' },
            @{ Name = 'AWS-access-key'; Text = 'AKIA' + ('S' * 16) },
            @{ Name = 'AWS-session-key'; Text = 'ASIA' + ('V' * 16) },
            @{ Name = 'AWS-access-key-id-property'; Text = '{"AccessKeyId":"example"}' },
            @{ Name = 'AWS-secret-access-key-property'; Text = '{"SecretAccessKey":"example"}' },
            @{ Name = 'GitHub-token'; Text = 'gh' + 'p_' + ('T' * 20) }
        )
        foreach ($secretCase in $secretCases) {
            & $reset
            Set-Content -LiteralPath (Join-Path $copy 'src/added.cs') -Value $secretCase.Text
            $secretFailure = Invoke-AcceptanceCore $copy $base 'src/app.csproj' 'tests/test.csproj' 'FullyQualifiedName=Example.Tests.DogfoodHarnessContractTests.AcceptanceContract' 1 $fakeRunner
            if ($secretFailure.Status -ne 'FAIL' -or $calls.Count -ne 0 -or -not ($secretFailure.Causes -join ' ' -match 'secret-like')) {
                throw ('Refusal check failed for secret-like content: ' + $secretCase.Name)
            }
        }

        & $reset
        $utf16Path = Join-Path $copy 'src/utf16-bom.cs'
        [System.IO.File]::WriteAllText($utf16Path, 'api_key=example', [System.Text.Encoding]::Unicode)
        $utf16Failure = Invoke-AcceptanceCore $copy $base 'src/app.csproj' 'tests/test.csproj' 'FullyQualifiedName=Example.Tests.DogfoodHarnessContractTests.AcceptanceContract' 1 $fakeRunner
        if ($utf16Failure.Status -ne 'FAIL' -or $calls.Count -ne 0 -or -not ($utf16Failure.Causes -join ' ' -match 'secret-like')) {
            throw 'Refusal check failed for a UTF-16LE file with a byte-order mark.'
        }

        & $reset
        $utf16NoBomPath = Join-Path $copy 'src/utf16-no-bom.cs'
        [System.IO.File]::WriteAllBytes($utf16NoBomPath, [System.Text.Encoding]::Unicode.GetBytes('token=example'))
        $utf16NoBomFailure = Invoke-AcceptanceCore $copy $base 'src/app.csproj' 'tests/test.csproj' 'FullyQualifiedName=Example.Tests.DogfoodHarnessContractTests.AcceptanceContract' 1 $fakeRunner
        if ($utf16NoBomFailure.Status -ne 'FAIL' -or $calls.Count -ne 0 -or -not ($utf16NoBomFailure.Causes -join ' ' -match 'secret-like')) {
            throw 'Refusal check failed for a UTF-16LE file without a byte-order mark.'
        }

        & $reset
        $trackedUtf16Path = Join-Path $copy 'src/tracked-secret.cs'
        $trackedSecretText = 'pass' + 'word' + '=' + 'example'
        [System.IO.File]::WriteAllText($trackedUtf16Path, $trackedSecretText, [System.Text.Encoding]::Unicode)
        $trackedUtf16Failure = Invoke-AcceptanceCore $copy $base 'src/app.csproj' 'tests/test.csproj' 'FullyQualifiedName=Example.Tests.DogfoodHarnessContractTests.AcceptanceContract' 1 $fakeRunner
        if ($trackedUtf16Failure.Status -ne 'FAIL' -or $calls.Count -ne 0 -or -not ($trackedUtf16Failure.Causes -join ' ' -match 'secret-like')) {
            throw 'Refusal check failed for a tracked UTF-16LE file with a byte-order mark.'
        }

        & $reset
        [System.IO.File]::WriteAllBytes($trackedUtf16Path, [System.Text.Encoding]::Unicode.GetBytes('token=example'))
        $trackedUtf16NoBomFailure = Invoke-AcceptanceCore $copy $base 'src/app.csproj' 'tests/test.csproj' 'FullyQualifiedName=Example.Tests.DogfoodHarnessContractTests.AcceptanceContract' 1 $fakeRunner
        if ($trackedUtf16NoBomFailure.Status -ne 'FAIL' -or $calls.Count -ne 0 -or -not ($trackedUtf16NoBomFailure.Causes -join ' ' -match 'secret-like')) {
            throw 'Refusal check failed for a tracked UTF-16LE file without a byte-order mark.'
        }

        & $reset
        $danglingLink = Join-Path $copy 'tests/dangling-link.txt'
        $linkCreated = $false
        try {
            New-Item -ItemType SymbolicLink -Path $danglingLink -Target (Join-Path $fixtureRoot 'missing-target.txt') -ErrorAction Stop | Out-Null
            $linkCreated = $true
        }
        catch {}
        if ($linkCreated) {
            $linkFailure = Invoke-AcceptanceCore $copy $base 'src/app.csproj' 'tests/test.csproj' 'FullyQualifiedName=Example.Tests.DogfoodHarnessContractTests.AcceptanceContract' 1 $fakeRunner
            if ($linkFailure.Status -ne 'FAIL' -or $calls.Count -ne 0 -or -not ($linkFailure.Causes -join ' ' -match 'reparse point')) {
                throw 'A dangling reparse point was not refused.'
            }
        }

        & $reset
        $mode.Value = 'build-fail'
        $calls.Clear()
        $buildFailure = Invoke-AcceptanceCore $copy $base 'src/app.csproj' 'tests/test.csproj' 'FullyQualifiedName=Example.Tests.DogfoodHarnessContractTests.AcceptanceContract' 1 $fakeRunner
        if ($buildFailure.Status -ne 'FAIL' -or $calls.Count -ne 1 -or $calls[0] -ne 'build') {
            throw 'The build FAIL path did not stop before the targeted test run.'
        }

        $mode.Value = 'test-fail'
        $calls.Clear()
        $testFailure = Invoke-AcceptanceCore $copy $base 'src/app.csproj' 'tests/test.csproj' 'FullyQualifiedName=Example.Tests.DogfoodHarnessContractTests.AcceptanceContract' 1 $fakeRunner
        if ($testFailure.Status -ne 'FAIL' -or $calls.Count -ne 2 -or $calls[1] -ne 'test') {
            throw 'The targeted test FAIL path was not reported.'
        }

        $mode.Value = 'no-tests'
        $calls.Clear()
        $emptyTestFailure = Invoke-AcceptanceCore $copy $base 'src/app.csproj' 'tests/test.csproj' 'FullyQualifiedName=Example.Tests.DogfoodHarnessContractTests.AcceptanceContract' 1 $fakeRunner
        if ($emptyTestFailure.Status -ne 'FAIL' -or $calls.Count -ne 2 -or -not ($emptyTestFailure.Causes -join ' ' -match 'passed test')) {
            throw 'A targeted test run with zero passed tests was not refused.'
        }

        $wildcardFailure = Invoke-AcceptanceCore $copy $base 'src/app.csproj' 'tests/test.csproj' '*' 1 $fakeRunner
        if ($wildcardFailure.Status -ne 'FAIL') { throw 'Refusal check failed for a full-suite wildcard filter.' }
        $broadFilterFailure = Invoke-AcceptanceCore $copy $base 'src/app.csproj' 'tests/test.csproj' 'Category=Unit' 1 $fakeRunner
        if ($broadFilterFailure.Status -ne 'FAIL') { throw 'Refusal check failed for a non-targeted category filter.' }
        $namespaceFilterFailure = Invoke-AcceptanceCore $copy $base 'src/app.csproj' 'tests/test.csproj' 'FullyQualifiedName~PaRuX.Server.Tests' 1 $fakeRunner
        if ($namespaceFilterFailure.Status -ne 'FAIL') { throw 'Refusal check failed for a namespace-wide test filter.' }
        $nestedNamespaceFilterFailure = Invoke-AcceptanceCore $copy $base 'src/app.csproj' 'tests/test.csproj' 'FullyQualifiedName~PaRuX.Server.Tests.Integration' 1 $fakeRunner
        if ($nestedNamespaceFilterFailure.Status -ne 'FAIL') { throw 'Refusal check failed for a nested namespace-wide test filter.' }
        foreach ($substringFilter in @('FullyQualifiedName~Tests', 'FullyQualifiedName~IntegrationTests', 'FullyQualifiedName~DogfoodHarnessContractTests')) {
            $substringFailure = Invoke-AcceptanceCore $copy $base 'src/app.csproj' 'tests/test.csproj' $substringFilter 1 $fakeRunner
            if ($substringFailure.Status -ne 'FAIL') { throw ('Refusal check failed for a substring test filter: ' + $substringFilter) }
        }

        $outputTarget = Join-Path $fixtureRoot 'output-target'
        New-Item -ItemType Directory -Path $outputTarget -Force | Out-Null
        $verdictLink = Join-Path $dogfood 'verdict.json'
        Remove-Item -LiteralPath $verdictLink -Force -ErrorAction SilentlyContinue
        if (-not (New-TestJunction $verdictLink $outputTarget)) { throw 'Self-test could not create a temporary junction for the output-file refusal check.' }
        $calls.Clear()
        $redirectedOutputFailure = Invoke-AcceptanceCore $copy $base 'src/app.csproj' 'tests/test.csproj' 'FullyQualifiedName=Example.Tests.DogfoodHarnessContractTests.AcceptanceContract' 1 $fakeRunner
        if ($redirectedOutputFailure.Status -ne 'FAIL' -or $calls.Count -ne 0 -or $null -ne $redirectedOutputFailure.VerdictPath -or (Get-ChildItem -LiteralPath $outputTarget -Force | Measure-Object).Count -ne 0) {
            throw 'The acceptance stage wrote through or accepted a redirected output file.'
        }
        Remove-TestJunction $verdictLink

        $redirectTarget = Join-Path $fixtureRoot 'redirect-target'
        New-Item -ItemType Directory -Path $redirectTarget -Force | Out-Null
        Remove-Item -LiteralPath $dogfood -Recurse -Force
        if (-not (New-TestJunction $dogfood $redirectTarget)) { throw 'Self-test could not create a temporary junction for the sidecar refusal check.' }
        $calls.Clear()
        $redirectedSidecarFailure = Invoke-AcceptanceCore $copy $base 'src/app.csproj' 'tests/test.csproj' 'FullyQualifiedName=Example.Tests.DogfoodHarnessContractTests.AcceptanceContract' 1 $fakeRunner
        if ($redirectedSidecarFailure.Status -ne 'FAIL' -or $calls.Count -ne 0 -or $null -ne $redirectedSidecarFailure.VerdictPath -or (Test-Path -LiteralPath (Join-Path $redirectTarget 'verdict.json'))) {
            throw 'The acceptance stage wrote through or accepted a redirected .dogfood sidecar.'
        }
    }
    finally {
        if ($null -eq $previousGlobalConfig) { Remove-Item Env:GIT_CONFIG_GLOBAL -ErrorAction SilentlyContinue } else { $env:GIT_CONFIG_GLOBAL = $previousGlobalConfig }
        if ($null -eq $previousNoSystem) { Remove-Item Env:GIT_CONFIG_NOSYSTEM -ErrorAction SilentlyContinue } else { $env:GIT_CONFIG_NOSYSTEM = $previousNoSystem }
        if ($null -eq $previousXdgConfig) { Remove-Item Env:XDG_CONFIG_HOME -ErrorAction SilentlyContinue } else { $env:XDG_CONFIG_HOME = $previousXdgConfig }
        if ($null -eq $previousHome) { Remove-Item Env:HOME -ErrorAction SilentlyContinue } else { $env:HOME = $previousHome }
        if (Test-Path -LiteralPath $fixtureRoot) {
            $tempRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath()).TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
            $fullFixture = [System.IO.Path]::GetFullPath($fixtureRoot)
            if ($fullFixture.StartsWith($tempRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
                Remove-Item -LiteralPath $fixtureRoot -Recurse -Force -ErrorAction SilentlyContinue
            }
        }
    }
}

function New-TestJunction([string]$LinkPath, [string]$TargetPath) {
    if ([string]::IsNullOrWhiteSpace($env:ComSpec)) { return $false }
    $command = 'mklink /J "' + $LinkPath + '" "' + $TargetPath + '"'
    $process = Start-Process -FilePath $env:ComSpec -ArgumentList ('/d /c ' + $command) -PassThru -Wait -WindowStyle Hidden
    return ($process.ExitCode -eq 0)
}

function Remove-TestJunction([string]$LinkPath) {
    $attributes = [System.IO.File]::GetAttributes($LinkPath)
    if (($attributes -band [System.IO.FileAttributes]::ReparsePoint) -eq 0) { throw 'Self-test cleanup expected a junction.' }
    $command = 'rmdir "' + $LinkPath + '"'
    $process = Start-Process -FilePath $env:ComSpec -ArgumentList ('/d /c ' + $command) -PassThru -Wait -WindowStyle Hidden
    if ($process.ExitCode -ne 0 -or (Test-Path -LiteralPath $LinkPath)) { throw 'Self-test could not remove its temporary junction.' }
}

try {
    if ($SelfTest) {
        Invoke-AcceptanceSelfTest
        Write-Output 'PASS'
        exit 0
    }

    $runner = { param($kind, $project, $filter, $logPath, $timeoutMinutes, $repository) Invoke-DotnetCheck $kind $project $filter $logPath $timeoutMinutes $repository }
    $result = Invoke-AcceptanceCore $CopyPath $BaseCommit $BuildProjectPath $TestProjectPath $TestFilter $TimeoutMinutes $runner
    if ($result.Status -eq 'PASS') {
        Write-Output 'PASS'
        exit 0
    }
    foreach ($cause in $result.Causes) { Write-Output ('FAIL: ' + $cause) }
    exit 1
}
catch {
    Write-Output ('FAIL: ' + $_.Exception.Message)
    exit 1
}
