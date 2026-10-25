[CmdletBinding()]
param(
    [string]$ScratchRoot,
    [string]$AllowedRoot,
    [string]$TaskPromptPath,
    [string]$BaseCommit,
    [string]$AgentCommandTemplate,
    [int]$TimeoutSeconds = 0,
    [string]$BuildProjectPath,
    [string]$TestProjectPath,
    [string]$TestFilter,
    [int]$VerificationTimeoutMinutes = 30,
    [switch]$SelfTest
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

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

function Get-CurrentPowerShellPath {
    $path = [System.Diagnostics.Process]::GetCurrentProcess().MainModule.FileName
    if ([string]::IsNullOrWhiteSpace($path) -or -not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw 'Could not resolve the current PowerShell executable.'
    }
    return $path
}

function Stop-ChildTree([System.Diagnostics.Process]$Process) {
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

function Test-TargetedTestFilter([string]$Filter) {
    if ($Filter -match '\*' -or $Filter -notmatch '(?i)^\s*FullyQualifiedName\s*=\s*([^\s|&]+)\s*$') { return $false }
    $target = $Matches[1]
    $parts = @($target.Split('.') | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    return $parts.Count -ge 3
}

function Invoke-PowerShellChild([string]$ScriptPath, [string[]]$ScriptArguments, [int]$TimeoutSeconds) {
    $hostPath = Get-CurrentPowerShellPath
    $allArguments = @('-NoProfile', '-NonInteractive', '-File', $ScriptPath) + @($ScriptArguments)
    $startInfo = New-Object System.Diagnostics.ProcessStartInfo
    $startInfo.FileName = $hostPath
    $startInfo.Arguments = (@($allArguments | ForEach-Object { ConvertTo-WindowsArgument ([string]$_) }) -join ' ')
    $startInfo.WorkingDirectory = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true

    $process = New-Object System.Diagnostics.Process
    $process.StartInfo = $startInfo
    if (-not $process.Start()) { throw 'Could not start a dogfood stage.' }
    $stdoutTask = $process.StandardOutput.ReadToEndAsync()
    $stderrTask = $process.StandardError.ReadToEndAsync()
    $timedOut = -not $process.WaitForExit($TimeoutSeconds * 1000)
    if ($timedOut) { [void](Stop-ChildTree $process) }
    $captureTimer = [System.Diagnostics.Stopwatch]::StartNew()
    $stdoutReady = $stdoutTask.Wait(5000)
    $remainingMs = [Math]::Max(0, 5000 - [int]$captureTimer.ElapsedMilliseconds)
    $stderrReady = $stderrTask.Wait($remainingMs)
    if (-not $stdoutReady) { try { $process.StandardOutput.Close() } catch {} }
    if (-not $stderrReady) { try { $process.StandardError.Close() } catch {} }
    $stdout = if ($stdoutReady) { $stdoutTask.Result } else { '' }
    $stderr = if ($stderrReady) { $stderrTask.Result } else { '' }
    $captureFailed = -not ($stdoutReady -and $stderrReady)
    $exitCode = if ($timedOut) { 124 } elseif ($captureFailed) { 125 } elseif ($process.HasExited) { $process.ExitCode } else { 125 }
    $process.Dispose()
    $causes = @()
    if ($timedOut) {
        $causes = @('Dogfood stage exceeded its bounded wait.')
    }
    elseif ($captureFailed) {
        $causes = @('Dogfood stage output streams did not close within the bounded cleanup wait.')
    }
    elseif ($exitCode -ne 0) {
        $lines = @((($stdout + "`n" + $stderr) -split "`r?`n") | Where-Object { $_ -match '^FAIL:' })
        if ($lines.Count -gt 0) { $causes = @($lines | ForEach-Object { $_ -replace '^FAIL:\s*', '' }) }
        else { $causes = @('Dogfood stage exited with code ' + $exitCode + '.') }
    }
    return [pscustomobject]@{ ExitCode = $exitCode; TimedOut = $timedOut; Causes = $causes; Output = $stdout }
}

function Assert-TaskInputs([string]$RequestedScratch, [string]$RequestedAllowed, [string]$PromptFile, [string]$AgentTemplate, [int]$AgentTimeout, [string]$BuildProject, [string]$TestProject, [string]$Filter, [int]$VerifyTimeout) {
    if ([string]::IsNullOrWhiteSpace($RequestedScratch)) { throw 'ScratchRoot must be supplied explicitly.' }
    if ([string]::IsNullOrWhiteSpace($RequestedAllowed)) { throw 'AllowedRoot must be supplied explicitly.' }
    if ([string]::IsNullOrWhiteSpace($PromptFile)) { throw 'TaskPromptPath is required.' }
    if ($PromptFile -notmatch '^(?:[A-Za-z]:[\\/]|\\\\[^\\/]+[\\/][^\\/]+(?:[\\/]|$))') { throw 'TaskPromptPath must be absolute.' }
    if ([string]::IsNullOrWhiteSpace($AgentTemplate)) { throw 'AgentCommandTemplate is required; there is no default agent.' }
    if ($AgentTimeout -lt 1 -or $AgentTimeout -gt 86400) { throw 'TimeoutSeconds must be between 1 and 86400.' }
    if ([string]::IsNullOrWhiteSpace($BuildProject)) { throw 'BuildProjectPath is required.' }
    if ([string]::IsNullOrWhiteSpace($TestProject)) { throw 'TestProjectPath is required.' }
    if ([string]::IsNullOrWhiteSpace($Filter)) { throw 'TestFilter is required; refusing a full-suite run.' }
    if (-not (Test-TargetedTestFilter $Filter)) { throw 'TestFilter must be an exact dotted FullyQualifiedName for a test method; substring, namespace-wide, and full-suite filters are refused.' }
    if ($VerifyTimeout -lt 1 -or $VerifyTimeout -gt 1440) { throw 'VerificationTimeoutMinutes must be between 1 and 1440.' }
}

function Invoke-TaskSequence([scriptblock]$StartStep, [scriptblock]$AgentStep, [scriptblock]$AcceptanceStep) {
    $stages = @(
        [pscustomobject]@{ Name = 'copy creation'; Action = $StartStep },
        [pscustomobject]@{ Name = 'agent execution'; Action = $AgentStep },
        [pscustomobject]@{ Name = 'independent acceptance'; Action = $AcceptanceStep }
    )
    foreach ($stage in $stages) {
        $result = & $stage.Action
        if ($null -eq $result -or [int]$result.ExitCode -ne 0) {
            $causes = if ($null -ne $result -and $result.Causes.Count -gt 0) { @($result.Causes) } else { @($stage.Name + ' failed.') }
            return [pscustomobject]@{ ExitCode = 1; FailedStage = $stage.Name; Causes = $causes }
        }
    }
    return [pscustomobject]@{ ExitCode = 0; FailedStage = $null; Causes = @() }
}

function New-SelfTestStep([string]$Name, [System.Collections.Generic.List[string]]$Calls, [pscustomobject]$Failure, [string]$FixtureRoot) {
    $stageName = $Name
    return {
        if (-not (Test-Path -LiteralPath $FixtureRoot -PathType Container)) { throw 'Self-test fixture disappeared.' }
        [void]$Calls.Add($stageName)
        if ($Failure.Value -eq $stageName) {
            return [pscustomobject]@{ ExitCode = 9; Causes = @($stageName + ' fixture failure') }
        }
        return [pscustomobject]@{ ExitCode = 0; Causes = @() }
    }.GetNewClosure()
}

function Invoke-TaskSelfTest {
    $fixtureRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('parux-dogfood-task-' + [Guid]::NewGuid().ToString('N'))
    $copy = Join-Path $fixtureRoot 'repo'
    $dogfood = Join-Path $fixtureRoot '.dogfood'
    New-Item -ItemType Directory -Path $fixtureRoot, $copy, $dogfood -Force | Out-Null
    try {
        Set-Content -LiteralPath (Join-Path $dogfood 'task.md') -Value 'fixture prompt'
        Set-Content -LiteralPath (Join-Path $dogfood 'base-commit.txt') -Value ('a' * 40)

        $calls = New-Object 'System.Collections.Generic.List[string]'
        $failure = [pscustomobject]@{ Value = '' }
        $start = New-SelfTestStep 'copy creation' $calls $failure $fixtureRoot
        $agent = New-SelfTestStep 'agent execution' $calls $failure $fixtureRoot
        $acceptance = New-SelfTestStep 'independent acceptance' $calls $failure $fixtureRoot

        Assert-TaskInputs (Join-Path $fixtureRoot 'scratch') $fixtureRoot (Join-Path $dogfood 'task.md') '["agent.exe","{prompt}"]' 60 'src/app.csproj' 'tests/test.csproj' 'FullyQualifiedName=Example.Tests.ContractTests.AcceptancePasses' 30
        $passed = Invoke-TaskSequence $start $agent $acceptance
        if ($passed.ExitCode -ne 0 -or ($calls -join ',') -ne 'copy creation,agent execution,independent acceptance') {
            throw 'The PASS path did not call copy, agent, and acceptance in order.'
        }

        foreach ($stageName in @('copy creation', 'agent execution', 'independent acceptance')) {
            $calls.Clear()
            $failure.Value = $stageName
            $failed = Invoke-TaskSequence $start $agent $acceptance
            if ($failed.ExitCode -eq 0 -or $failed.FailedStage -ne $stageName -or $calls[$calls.Count - 1] -ne $stageName) {
                throw ('The sequence did not stop at the failing stage: ' + $stageName)
            }
            $expectedCount = @('copy creation', 'agent execution', 'independent acceptance').IndexOf($stageName) + 1
            if ($calls.Count -ne $expectedCount) { throw ('A later stage ran after failure at ' + $stageName) }
        }

        $failure.Value = ''
        $calls.Clear()
        $missingAgentRejected = $false
        try { Assert-TaskInputs (Join-Path $fixtureRoot 'scratch') $fixtureRoot (Join-Path $dogfood 'task.md') '' 60 'src/app.csproj' 'tests/test.csproj' 'FullyQualifiedName=Example.Tests.ContractTests.AcceptancePasses' 30 }
        catch { $missingAgentRejected = $true }
        if (-not $missingAgentRejected) { throw 'Refusal check failed for a missing explicit agent command.' }

        $missingFilterRejected = $false
        try { Assert-TaskInputs (Join-Path $fixtureRoot 'scratch') $fixtureRoot (Join-Path $dogfood 'task.md') '["agent.exe","{prompt}"]' 60 'src/app.csproj' 'tests/test.csproj' '' 30 }
        catch { $missingFilterRejected = $true }
        if (-not $missingFilterRejected) { throw 'Refusal check failed for a missing test filter.' }

        $broadFilterRejected = $false
        try { Assert-TaskInputs (Join-Path $fixtureRoot 'scratch') $fixtureRoot (Join-Path $dogfood 'task.md') '["agent.exe","{prompt}"]' 60 'src/app.csproj' 'tests/test.csproj' 'Category=Unit' 30 }
        catch { $broadFilterRejected = $true }
        if (-not $broadFilterRejected) { throw 'Refusal check failed for a non-targeted test filter.' }

        $namespaceFilterRejected = $false
        try { Assert-TaskInputs (Join-Path $fixtureRoot 'scratch') $fixtureRoot (Join-Path $dogfood 'task.md') '["agent.exe","{prompt}"]' 60 'src/app.csproj' 'tests/test.csproj' 'FullyQualifiedName~PaRuX.Server.Tests' 30 }
        catch { $namespaceFilterRejected = $true }
        if (-not $namespaceFilterRejected) { throw 'Refusal check failed for a namespace-wide test filter.' }

        $nestedNamespaceFilterRejected = $false
        try { Assert-TaskInputs (Join-Path $fixtureRoot 'scratch') $fixtureRoot (Join-Path $dogfood 'task.md') '["agent.exe","{prompt}"]' 60 'src/app.csproj' 'tests/test.csproj' 'FullyQualifiedName~PaRuX.Server.Tests.Integration' 30 }
        catch { $nestedNamespaceFilterRejected = $true }
        if (-not $nestedNamespaceFilterRejected) { throw 'Refusal check failed for a nested namespace-wide test filter.' }

        foreach ($substringFilter in @('FullyQualifiedName~Tests', 'FullyQualifiedName~IntegrationTests', 'FullyQualifiedName~ContractTests')) {
            $substringRejected = $false
            try { Assert-TaskInputs (Join-Path $fixtureRoot 'scratch') $fixtureRoot (Join-Path $dogfood 'task.md') '["agent.exe","{prompt}"]' 60 'src/app.csproj' 'tests/test.csproj' $substringFilter 30 }
            catch { $substringRejected = $true }
            if (-not $substringRejected) { throw ('Refusal check failed for a substring test filter: ' + $substringFilter) }
        }

        $timeoutScript = Join-Path $fixtureRoot 'timeout-child.ps1'
        [System.IO.File]::WriteAllText($timeoutScript, 'Start-Sleep -Seconds 30', (New-Object System.Text.UTF8Encoding($false)))
        $timeoutResult = Invoke-PowerShellChild $timeoutScript @() 1
        if (-not $timeoutResult.TimedOut -or $timeoutResult.ExitCode -ne 124) { throw 'The bounded stage timeout did not terminate and report its child.' }
    }
    finally {
        if (Test-Path -LiteralPath $fixtureRoot) {
            $tempRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath()).TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
            $fullFixture = [System.IO.Path]::GetFullPath($fixtureRoot)
            if ($fullFixture.StartsWith($tempRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
                Remove-Item -LiteralPath $fixtureRoot -Recurse -Force -ErrorAction SilentlyContinue
            }
        }
    }
}

function Invoke-DogfoodTask {
    Assert-TaskInputs $ScratchRoot $AllowedRoot $TaskPromptPath $AgentCommandTemplate $TimeoutSeconds $BuildProjectPath $TestProjectPath $TestFilter $VerificationTimeoutMinutes
    $scratch = [System.IO.Path]::GetFullPath($ScratchRoot)
    $copy = Join-Path $scratch 'repo'
    $dogfood = Join-Path $scratch '.dogfood'
    $scriptDirectory = $PSScriptRoot
    $startScript = Join-Path $scriptDirectory 'Start-DogfoodCopy.ps1'
    $agentScript = Join-Path $scriptDirectory 'Invoke-DogfoodAgent.ps1'
    $acceptanceScript = Join-Path $scriptDirectory 'Test-DogfoodAcceptance.ps1'

    $startArguments = @('-ScratchRoot', $scratch, '-AllowedRoot', [System.IO.Path]::GetFullPath($AllowedRoot), '-TaskPromptPath', [System.IO.Path]::GetFullPath($TaskPromptPath))
    if (-not [string]::IsNullOrWhiteSpace($BaseCommit)) { $startArguments += @('-BaseCommit', $BaseCommit) }
    $startStep = {
        $result = Invoke-PowerShellChild $startScript $startArguments 120
        return [pscustomobject]@{ ExitCode = $result.ExitCode; Causes = $result.Causes }
    }.GetNewClosure()

    $agentStep = {
        $prompt = Join-Path $dogfood 'task.md'
        if (-not (Test-Path -LiteralPath $prompt -PathType Leaf)) { return [pscustomobject]@{ ExitCode = 1; Causes = @('The copy stage did not write the task prompt.') } }
        $arguments = @('-CopyPath', $copy, '-PromptPath', $prompt, '-AgentCommandTemplate', $AgentCommandTemplate, '-TimeoutSeconds', [string]$TimeoutSeconds)
        $result = Invoke-PowerShellChild $agentScript $arguments ($TimeoutSeconds + 30)
        return [pscustomobject]@{ ExitCode = $result.ExitCode; Causes = $result.Causes }
    }.GetNewClosure()

    $acceptanceStep = {
        $basePath = Join-Path $dogfood 'base-commit.txt'
        if (-not (Test-Path -LiteralPath $basePath -PathType Leaf)) { return [pscustomobject]@{ ExitCode = 1; Causes = @('The copy stage did not record the base commit.') } }
        $resolvedBase = (Get-Content -LiteralPath $basePath -Raw).Trim()
        if ($resolvedBase -notmatch '^[0-9a-fA-F]{40,64}$') { return [pscustomobject]@{ ExitCode = 1; Causes = @('The recorded base commit is invalid.') } }
        $arguments = @(
            '-CopyPath', $copy,
            '-BaseCommit', $resolvedBase,
            '-BuildProjectPath', $BuildProjectPath,
            '-TestProjectPath', $TestProjectPath,
            '-TestFilter', $TestFilter,
            '-TimeoutMinutes', [string]$VerificationTimeoutMinutes
        )
        $result = Invoke-PowerShellChild $acceptanceScript $arguments (($VerificationTimeoutMinutes * 60) + 30)
        return [pscustomobject]@{ ExitCode = $result.ExitCode; Causes = $result.Causes }
    }.GetNewClosure()

    $result = Invoke-TaskSequence $startStep $agentStep $acceptanceStep
    if ($result.ExitCode -ne 0) {
        foreach ($cause in $result.Causes) { Write-Output ('FAIL: ' + $cause) }
        return $false
    }
    return $true
}

try {
    if ($SelfTest) {
        Invoke-TaskSelfTest
        Write-Output 'PASS'
        exit 0
    }
    if (Invoke-DogfoodTask) {
        Write-Output 'PASS'
        exit 0
    }
    exit 1
}
catch {
    Write-Output ('FAIL: ' + $_.Exception.Message)
    exit 1
}
