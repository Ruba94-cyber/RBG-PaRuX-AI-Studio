[CmdletBinding()]
param(
    [string]$CopyPath,
    [string]$PromptPath,
    [string]$AgentCommandTemplate,
    [int]$TimeoutSeconds = 0,
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
        throw 'CopyPath overlaps the primary checkout; refusing to run an agent there.'
    }
}

function Test-PathHasReparsePoint([string]$Path) {
    $current = Get-FullPath $Path
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
    if (Test-PathHasReparsePoint $sidecar) { throw 'The sibling .dogfood path contains a reparse point.' }

    foreach ($outputPath in $OutputPaths) {
        $output = Get-FullPath $outputPath
        if (-not (Test-PathWithin $output $sidecar $false)) { throw 'A dogfood output path escapes the sibling .dogfood directory.' }
        if ((Test-PathWithin $output $primary $true) -or (Test-PathWithin $primary $output $true)) {
            throw 'A dogfood output path overlaps the primary checkout.'
        }
        if (Test-PathHasReparsePoint $output) { throw 'A dogfood output path traverses a reparse point.' }
    }
}

function Invoke-Git([string]$WorkingDirectory, [string[]]$Arguments) {
    $previousPreference = $ErrorActionPreference
    $ErrorActionPreference = 'SilentlyContinue'
    try { $output = & git -C $WorkingDirectory @Arguments 2>$null; $gitExitCode = $LASTEXITCODE }
    finally { $ErrorActionPreference = $previousPreference }
    if ($gitExitCode -ne 0) { throw 'The disposable repository could not be validated with git.' }
    return (@($output) -join "`n").Trim()
}

function ConvertTo-WindowsArgument([string]$Value) {
    $builder = New-Object System.Text.StringBuilder
    [void]$builder.Append('"')
    $slashes = 0
    foreach ($character in $Value.ToCharArray()) {
        if ($character -eq '\') { $slashes++; continue }
        if ($character -eq '"') {
            if ($slashes -gt 0) { [void]$builder.Append(('\' * (2 * $slashes + 1))) } else { [void]$builder.Append('\') }
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

function Resolve-AgentExecutable([string]$Executable) {
    if ([string]::IsNullOrWhiteSpace($Executable)) { throw 'The command template must start with an executable.' }
    if ([System.IO.Path]::IsPathRooted($Executable)) {
        $resolved = [System.IO.Path]::GetFullPath($Executable)
        if (-not (Test-Path -LiteralPath $resolved -PathType Leaf)) { throw 'The requested agent executable does not exist.' }
        return $resolved
    }
    if ($Executable.Contains('\') -or $Executable.Contains('/')) { throw 'Use an absolute executable path or an executable found on PATH.' }
    $application = Get-Command -Name $Executable -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($null -eq $application) { throw 'The requested agent executable was not found on PATH.' }
    return $application.Source
}

function Stop-ProcessTree([System.Diagnostics.Process]$Process) {
    try {
        $Process.Kill($true)
        [void]$Process.WaitForExit(5000)
        return -not $Process.HasExited
    }
    catch {
        $taskkill = Join-Path $env:SystemRoot 'System32\taskkill.exe'
        if (Test-Path -LiteralPath $taskkill -PathType Leaf) {
            try {
                $killer = Start-Process -FilePath $taskkill -ArgumentList ('/PID ' + $Process.Id + ' /T /F') -PassThru -WindowStyle Hidden
                if (-not $killer.WaitForExit(5000)) { try { $killer.Kill() } catch {} }
                [void]$Process.WaitForExit(5000)
                if ($Process.HasExited) { return $false }
            }
            catch {}
        }
        try { $Process.Kill() } catch {}
        try { [void]$Process.WaitForExit(5000) } catch {}
        return -not $Process.HasExited
    }
}

function Invoke-AgentCore([string]$RequestedCopyPath, [string]$RequestedPromptPath, [string]$CommandTemplate, [int]$LimitSeconds) {
    if ([string]::IsNullOrWhiteSpace($RequestedCopyPath)) { throw 'CopyPath is required.' }
    if ([string]::IsNullOrWhiteSpace($RequestedPromptPath)) { throw 'PromptPath is required.' }
    if ([string]::IsNullOrWhiteSpace($CommandTemplate)) { throw 'AgentCommandTemplate is required; there is no default agent.' }
    if ($LimitSeconds -lt 1 -or $LimitSeconds -gt 86400) { throw 'TimeoutSeconds must be between 1 and 86400.' }

    $copy = Get-FullPath $RequestedCopyPath
    Assert-DisposableCopy $copy
    $prompt = Get-FullPath $RequestedPromptPath
    if (-not (Test-Path -LiteralPath $copy -PathType Container)) { throw 'CopyPath must identify an existing directory.' }
    if (-not (Test-Path -LiteralPath $prompt -PathType Leaf)) { throw 'PromptPath must identify an existing file.' }
    $top = Invoke-Git $copy @('rev-parse', '--show-toplevel')
    if (-not (Get-FullPath $top).Equals($copy, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw 'CopyPath must be the root of the disposable git working tree.'
    }
    if (Test-PathWithin $prompt $copy $true) { throw 'The prompt file must be outside the disposable working tree.' }

    $scratch = Split-Path -Parent $copy
    $dogfood = Join-Path $scratch '.dogfood'
    if (-not (Test-Path -LiteralPath $dogfood -PathType Container)) { throw 'The sibling .dogfood directory is missing.' }
    $expectedPrompt = Get-FullPath (Join-Path $dogfood 'task.md')
    if (-not $prompt.Equals($expectedPrompt, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw 'PromptPath must be the task.md file in the sibling .dogfood directory.'
    }
    Assert-SafeDogfoodOutputs $copy $dogfood @()
    if (Test-PathHasReparsePoint $prompt) { throw 'PromptPath traverses a reparse point.' }

    $parsedTemplate = ConvertFrom-Json -InputObject $CommandTemplate -ErrorAction Stop
    if ($parsedTemplate -is [string] -or $parsedTemplate -isnot [System.Array]) {
        throw 'AgentCommandTemplate must be a JSON array of executable and argument strings.'
    }
    $templateParts = @($parsedTemplate)
    if ($templateParts.Count -lt 2) { throw 'AgentCommandTemplate must include an executable and a prompt argument.' }
    foreach ($part in $templateParts) {
        if ($part -isnot [string]) { throw 'Every command template element must be a string.' }
    }
    $hasPrompt = $false
    $expandedParts = New-Object System.Collections.Generic.List[string]
    foreach ($part in $templateParts) {
        if ($part.Contains('{prompt}')) { $hasPrompt = $true }
        [void]$expandedParts.Add($part.Replace('{prompt}', $prompt))
    }
    if (-not $hasPrompt) { throw 'AgentCommandTemplate must contain the {prompt} placeholder.' }

    $executable = Resolve-AgentExecutable $expandedParts[0]
    $arguments = @()
    if ($expandedParts.Count -gt 1) { $arguments = @($expandedParts.GetRange(1, $expandedParts.Count - 1)) }
    $argumentLine = (@($arguments | ForEach-Object { ConvertTo-WindowsArgument $_ }) -join ' ')

    $stdoutPath = Join-Path $dogfood 'agent.stdout.log'
    $stderrPath = Join-Path $dogfood 'agent.stderr.log'
    $runPath = Join-Path $dogfood 'run.json'
    Assert-SafeDogfoodOutputs $copy $dogfood @($stdoutPath, $stderrPath, $runPath)
    $started = [DateTime]::UtcNow
    $ended = $null
    $exitCode = -1
    $timedOut = $false
    $failure = $null
    $process = $null
    $processStarted = $false
    $stdout = ''
    $stderr = ''
    try {
        $startInfo = New-Object System.Diagnostics.ProcessStartInfo
        $startInfo.FileName = $executable
        $startInfo.Arguments = $argumentLine
        $startInfo.WorkingDirectory = $copy
        $startInfo.UseShellExecute = $false
        $startInfo.CreateNoWindow = $true
        $startInfo.RedirectStandardOutput = $true
        $startInfo.RedirectStandardError = $true
        $startInfo.StandardOutputEncoding = [System.Text.Encoding]::UTF8
        $startInfo.StandardErrorEncoding = [System.Text.Encoding]::UTF8
        $process = New-Object System.Diagnostics.Process
        $process.StartInfo = $startInfo
        $processStarted = $process.Start()
        if (-not $processStarted) { throw 'The agent process did not start.' }
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit($LimitSeconds * 1000)) {
            $timedOut = $true
            $treeStillRunning = Stop-ProcessTree $process
            if ($treeStillRunning) { $failure = 'The agent timed out and its process tree could not be fully stopped.' }
        }
        if ($process.HasExited) {
            [void]$process.WaitForExit()
            $exitCode = $process.ExitCode
        }
        elseif ($timedOut) { $exitCode = 124 }
        if ($timedOut) {
            [void]$stdoutTask.Wait(5000)
            [void]$stderrTask.Wait(5000)
        }
        if ($stdoutTask.IsCompleted) { $stdout = $stdoutTask.Result }
        if ($stderrTask.IsCompleted) { $stderr = $stderrTask.Result }
    }
    catch {
        $failure = $_.Exception.Message
        $exitCode = 127
    }
    finally {
        $ended = [DateTime]::UtcNow
        Assert-SafeDogfoodOutputs $copy $dogfood @($stdoutPath, $stderrPath, $runPath)
        [System.IO.File]::WriteAllText($stdoutPath, $stdout, (New-Object System.Text.UTF8Encoding($false)))
        [System.IO.File]::WriteAllText($stderrPath, $stderr, (New-Object System.Text.UTF8Encoding($false)))
        $record = [ordered]@{
            schemaVersion = 1
            startedUtc = $started.ToString('o')
            endedUtc = $ended.ToString('o')
            exitCode = $exitCode
            timedOut = $timedOut
            failure = $failure
        }
        $json = ConvertTo-Json -InputObject $record -Depth 5
        [System.IO.File]::WriteAllText($runPath, $json, (New-Object System.Text.UTF8Encoding($false)))
    }

    if ($timedOut) { throw 'The agent exceeded TimeoutSeconds.' }
    if ($null -ne $failure) { throw ('The agent process could not complete: ' + $failure) }
    if ($exitCode -ne 0) { throw ('The agent exited with code ' + $exitCode + '.') }
    return [pscustomobject]@{ ExitCode = $exitCode; RunPath = $runPath; StartedUtc = $started.ToString('o'); EndedUtc = $ended.ToString('o') }
}

function Invoke-AgentSelfTest {
    $fixtureRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('parux-dogfood-agent-' + [Guid]::NewGuid().ToString('N'))
    $copy = Join-Path $fixtureRoot 'repo'
    $dogfood = Join-Path $fixtureRoot '.dogfood'
    New-Item -ItemType Directory -Path $copy, $dogfood -Force | Out-Null
    $previousGlobalConfig = $env:GIT_CONFIG_GLOBAL
    $previousNoSystem = $env:GIT_CONFIG_NOSYSTEM
    $previousXdgConfig = $env:XDG_CONFIG_HOME
    $previousHome = $env:HOME
    $env:GIT_CONFIG_GLOBAL = Join-Path $fixtureRoot 'gitconfig'
    $env:GIT_CONFIG_NOSYSTEM = '1'
    $env:XDG_CONFIG_HOME = Join-Path $fixtureRoot 'xdg-config'
    $env:HOME = $fixtureRoot
    try {
        & git init --quiet $copy 2>$null
        if ($LASTEXITCODE -ne 0) { throw 'Self-test could not initialize its temporary git fixture.' }
        Set-Content -LiteralPath (Join-Path $copy 'README.md') -Value 'fixture' -Encoding UTF8
        & git -C $copy add README.md 2>$null
        & git -C $copy -c user.name=Harness -c user.email=example@example.com commit --quiet -m fixture 2>$null
        if ($LASTEXITCODE -ne 0) { throw 'Self-test could not commit its temporary git fixture.' }
        $prompt = Join-Path $dogfood 'task.md'
        Set-Content -LiteralPath $prompt -Value 'fixture prompt' -Encoding UTF8

        $primaryRejected = $false
        try { [void](Invoke-AgentCore (Get-PrimaryCheckoutRoot) $prompt '[]' 5) }
        catch { $primaryRejected = $_.Exception.Message -match 'primary checkout' }
        if (-not $primaryRejected) { throw 'Refusal check failed when the agent was pointed at the primary checkout.' }

        $rejected = 0
        try { [void](Invoke-AgentCore $copy $prompt '["powershell.exe","-NoProfile","-Command","Write-Output x"]' 5) } catch { $rejected++ }
        if ($rejected -ne 1) { throw 'Refusal check failed when the command template omitted {prompt}.' }

        $agentScript = Join-Path $dogfood 'fake-agent.ps1'
        $agentSource = @'
param([string]$PromptFile)
$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath $PromptFile -PathType Leaf)) { exit 7 }
Set-Content -LiteralPath (Join-Path (Get-Location) 'agent-output.txt') -Value 'fixture result'
'@
        [System.IO.File]::WriteAllText($agentScript, $agentSource, (New-Object System.Text.UTF8Encoding($false)))
        $hostPath = [System.Diagnostics.Process]::GetCurrentProcess().MainModule.FileName
        $successTemplate = ConvertTo-Json -InputObject @($hostPath, '-NoProfile', '-NonInteractive', '-File', $agentScript, '{prompt}') -Compress
        $success = Invoke-AgentCore $copy $prompt $successTemplate 15
        if ($success.ExitCode -ne 0 -or -not (Test-Path -LiteralPath (Join-Path $copy 'agent-output.txt') -PathType Leaf)) {
            throw 'The successful fake agent did not write its result inside the copy.'
        }
        $successRecord = Get-Content -LiteralPath $success.RunPath -Raw | ConvertFrom-Json
        if ($null -eq $successRecord.startedUtc -or $null -eq $successRecord.endedUtc -or $successRecord.exitCode -ne 0) {
            throw 'run.json did not retain start, end, and exit information.'
        }

        $failedScript = Join-Path $dogfood 'fake-failure.ps1'
        [System.IO.File]::WriteAllText($failedScript, 'exit 17', (New-Object System.Text.UTF8Encoding($false)))
        $failedTemplate = ConvertTo-Json -InputObject @($hostPath, '-NoProfile', '-NonInteractive', '-File', $failedScript, '{prompt}') -Compress
        $rejected = 0
        try { [void](Invoke-AgentCore $copy $prompt $failedTemplate 15) } catch { $rejected++ }
        if ($rejected -ne 1) { throw 'A non-zero fake-agent exit was not rejected.' }
        $failureRecord = Get-Content -LiteralPath $success.RunPath -Raw | ConvertFrom-Json
        if ($failureRecord.exitCode -ne 17) { throw ('run.json did not retain the fake agent exit code: ' + (ConvertTo-Json $failureRecord -Compress)) }

        $sleepScript = Join-Path $dogfood 'fake-timeout.ps1'
        [System.IO.File]::WriteAllText($sleepScript, 'Start-Sleep -Seconds 10', (New-Object System.Text.UTF8Encoding($false)))
        $timeoutTemplate = ConvertTo-Json -InputObject @($hostPath, '-NoProfile', '-NonInteractive', '-File', $sleepScript, '{prompt}') -Compress
        $rejected = 0
        try { [void](Invoke-AgentCore $copy $prompt $timeoutTemplate 1) } catch { $rejected++ }
        if ($rejected -ne 1) { throw 'The hard timeout did not reject the sleeping fake agent.' }
        $timeoutRecord = Get-Content -LiteralPath $success.RunPath -Raw | ConvertFrom-Json
        if (-not $timeoutRecord.timedOut) { throw 'run.json did not record the timeout.' }

        $outputTarget = Join-Path $fixtureRoot 'output-target'
        New-Item -ItemType Directory -Path $outputTarget -Force | Out-Null
        Remove-Item -LiteralPath $success.RunPath -Force
        $danglingTarget = Join-Path $outputTarget 'not-created.txt'
        if (New-TestFileSymlink $success.RunPath $danglingTarget) {
            $danglingRejected = $false
            try { [void](Invoke-AgentCore $copy $prompt $successTemplate 5) }
            catch { $danglingRejected = $_.Exception.Message -match 'output path.*reparse point' }
            if (-not $danglingRejected -or (Test-Path -LiteralPath $danglingTarget)) {
                throw 'The agent stage wrote through or accepted a dangling output symlink.'
            }
            Remove-TestFileSymlink $success.RunPath
        }
        if (-not (New-TestJunction $success.RunPath $outputTarget)) { throw 'Self-test could not create a temporary junction for the output-file refusal check.' }
        $outputRejected = $false
        try { [void](Invoke-AgentCore $copy $prompt $successTemplate 5) }
        catch { $outputRejected = $_.Exception.Message -match 'output path.*reparse point' }
        if (-not $outputRejected -or (Get-ChildItem -LiteralPath $outputTarget -Force | Measure-Object).Count -ne 0) {
            throw 'The agent stage wrote through or accepted a redirected output file.'
        }
        Remove-TestJunction $success.RunPath

        $redirectTarget = Join-Path $fixtureRoot 'redirect-target'
        New-Item -ItemType Directory -Path $redirectTarget -Force | Out-Null
        Set-Content -LiteralPath (Join-Path $redirectTarget 'task.md') -Value 'fixture prompt' -Encoding UTF8
        Remove-Item -LiteralPath $dogfood -Recurse -Force
        if (-not (New-TestJunction $dogfood $redirectTarget)) { throw 'Self-test could not create a temporary junction for the sidecar refusal check.' }
        $sidecarRejected = $false
        $sidecarFailureMessage = ''
        try { [void](Invoke-AgentCore $copy $prompt '[]' 5) }
        catch { $sidecarFailureMessage = $_.Exception.Message; $sidecarRejected = $sidecarFailureMessage -match 'reparse point|overlaps the primary checkout' }
        if (-not $sidecarRejected -or (Test-Path -LiteralPath (Join-Path $redirectTarget 'run.json'))) {
            $sidecarAttributes = [System.IO.File]::GetAttributes($dogfood)
            throw ('The agent stage wrote through or accepted a redirected .dogfood sidecar. Failure: ' + $sidecarFailureMessage + '; reparse=' + (Test-PathHasReparsePoint $dogfood) + '; attributes=' + $sidecarAttributes)
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

function New-TestFileSymlink([string]$LinkPath, [string]$TargetPath) {
    if ([string]::IsNullOrWhiteSpace($env:ComSpec)) { return $false }
    $command = 'mklink "' + $LinkPath + '" "' + $TargetPath + '"'
    $process = Start-Process -FilePath $env:ComSpec -ArgumentList ('/d /c ' + $command) -PassThru -Wait -WindowStyle Hidden
    return ($process.ExitCode -eq 0)
}

function Remove-TestFileSymlink([string]$LinkPath) {
    $attributes = [System.IO.File]::GetAttributes($LinkPath)
    if (($attributes -band [System.IO.FileAttributes]::ReparsePoint) -eq 0) { throw 'Self-test cleanup expected a file symlink.' }
    Remove-Item -LiteralPath $LinkPath -Force
    if (Test-Path -LiteralPath $LinkPath) { throw 'Self-test could not remove its temporary file symlink.' }
}

try {
    if ($SelfTest) {
        Invoke-AgentSelfTest
        Write-Output 'PASS'
        exit 0
    }
    $result = Invoke-AgentCore $CopyPath $PromptPath $AgentCommandTemplate $TimeoutSeconds
    Write-Output 'PASS'
    exit 0
}
catch {
    Write-Output ('FAIL: ' + $_.Exception.Message)
    exit 1
}
